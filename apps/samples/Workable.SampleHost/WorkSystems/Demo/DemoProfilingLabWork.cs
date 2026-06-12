using Microsoft.Data.SqlClient;
using Workable;

namespace SampleHost.Demo;

public sealed record DemoProfilingLabInput(
    string Scenario = "baseline",
    int SectionCount = 4,
    int StepsPerSection = 3,
    int DelayMilliseconds = 35,
    bool AddDiscoveredIdentifier = true);

public sealed record DemoProfilingLabOutput(
    string Scenario,
    int SectionCount,
    int StepCount,
    string ActivationId,
    DateTimeOffset CompletedAt);

internal sealed record DemoProfilingPlan(
    string Scenario,
    IReadOnlyList<DemoProfilingSectionPlan> Sections);

internal sealed record DemoProfilingSectionPlan(
    int Ordinal,
    string Label,
    string Phase,
    IReadOnlyList<DemoProfilingStepPlan> Steps);

internal sealed record DemoProfilingStepPlan(
    int Ordinal,
    string Label,
    string Category,
    int DelayMilliseconds);

internal sealed record DemoProfilingSectionResult(
    string Label,
    int StepCount,
    int TotalDelayMilliseconds);

internal sealed record DemoProfilingSqlConnection(string ConnectionString);

internal sealed record DemoProfilingSqlSnapshot(
    string DatabaseName,
    int SessionId,
    int MatchingDurableEntries);

internal sealed class DemoProfilingLabWork(
    DemoProfilingActivationMarker activationMarker,
    DemoProfilingPlanner planner,
    DemoProfilingPipeline pipeline) : IWorkExecutor<DemoProfilingLabInput, DemoProfilingLabOutput>
{
    public async Task<WorkExecutionResult<DemoProfilingLabOutput>> Execute(
        IWorkExecutionContext context,
        DemoProfilingLabInput input,
        CancellationToken cancellationToken)
    {
        var normalizedInput = NormalizeInput(input);
        context.Profile.AddInfo("Execution context", new
        {
            context.WorkerId.Value,
            context.Definition.Name,
            context.Options.ProfilingEnabled,
        });

        if (normalizedInput.AddDiscoveredIdentifier)
        {
            context.AddIdentifier(new WorkIdentifier("profile-demo", normalizedInput.Scenario));
        }

        using var scenarioScope = context.Profile.CreateScope("Run profiling demo", normalizedInput);
        var plan = planner.BuildPlan(normalizedInput);
        var output = await pipeline.RunAsync(plan, activationMarker.ActivationId, cancellationToken);
        scenarioScope.SetResult(new
        {
            output.Scenario,
            output.SectionCount,
            output.StepCount,
            output.ActivationId,
        });

        return WorkExecutionResult<DemoProfilingLabOutput>.Success(
            output,
            [
                WorkMessage.Info(
                    "sample.demo.profiling-lab.completed",
                    $"Profiling demo '{output.Scenario}' completed with {output.SectionCount} sections and {output.StepCount} timed steps."),
            ]);
    }

    private static DemoProfilingLabInput NormalizeInput(DemoProfilingLabInput input)
        => input with
        {
            Scenario = string.IsNullOrWhiteSpace(input.Scenario)
                ? "baseline"
                : input.Scenario.Trim(),
            SectionCount = Math.Clamp(input.SectionCount, 1, 6),
            StepsPerSection = Math.Clamp(input.StepsPerSection, 1, 5),
            DelayMilliseconds = Math.Clamp(input.DelayMilliseconds, 5, 150),
        };
}

internal sealed class DemoProfilingActivationMarker
{
    public DemoProfilingActivationMarker(IWorkProfiler profiler)
    {
        this.ActivationId = Guid.NewGuid().ToString("N")[..8];

        // This runs during service activation, so it lands at the profile root.
        profiler.AddInfo("Service activation", new
        {
            this.ActivationId,
            Service = nameof(DemoProfilingActivationMarker),
            Stage = "constructor",
        });
    }

    public string ActivationId { get; }
}

internal sealed class DemoProfilingPlanner(IWorkProfiler profiler)
{
    private static readonly string[] SectionLabels =
    [
        "Gather worker context",
        "Validate operator request",
        "Hydrate supporting data",
        "Assemble retained response",
        "Publish audit artifacts",
        "Finalize sample output",
    ];

    private static readonly string[] StepLabels =
    [
        "Load cached state",
        "Call injected dependency",
        "Shape intermediate result",
        "Attach profile metadata",
        "Serialize retained sample",
    ];

    private static readonly string[] Categories =
    [
        "cache",
        "dependency",
        "projection",
        "annotation",
        "serialization",
    ];

    internal DemoProfilingPlan BuildPlan(DemoProfilingLabInput input)
    {
        using var scope = profiler.CreateMethodScope<DemoProfilingPlanner>(new
        {
            input.Scenario,
            input.SectionCount,
            input.StepsPerSection,
            input.DelayMilliseconds,
        });

        profiler.AddInfo("Planner heuristics", new
        {
            TemplateCount = SectionLabels.Length,
            StepTemplateCount = StepLabels.Length,
            DelayStrategy = "section and step weighted",
        });

        var sections = new List<DemoProfilingSectionPlan>(input.SectionCount);
        for (var sectionIndex = 0; sectionIndex < input.SectionCount; sectionIndex++)
        {
            var steps = new List<DemoProfilingStepPlan>(input.StepsPerSection);
            for (var stepIndex = 0; stepIndex < input.StepsPerSection; stepIndex++)
            {
                steps.Add(new DemoProfilingStepPlan(
                    stepIndex + 1,
                    StepLabels[stepIndex % StepLabels.Length],
                    Categories[stepIndex % Categories.Length],
                    input.DelayMilliseconds + (sectionIndex * 10) + (stepIndex * 5)));
            }

            sections.Add(new DemoProfilingSectionPlan(
                sectionIndex + 1,
                SectionLabels[sectionIndex % SectionLabels.Length],
                sectionIndex < 2 ? "Preparation" : sectionIndex < 4 ? "Execution" : "Finalization",
                steps));
        }

        scope.SetResult(new
        {
            Sections = sections.Count,
            Steps = sections.Sum(section => section.Steps.Count),
        });

        return new DemoProfilingPlan(input.Scenario, sections);
    }
}

internal sealed class DemoProfilingPipeline(
    DemoProfilingSectionWorker sectionWorker,
    DemoProfilingOutputComposer outputComposer,
    IWorkProfiler profiler) 
{
    internal async Task<DemoProfilingLabOutput> RunAsync(
        DemoProfilingPlan plan,
        string activationId,
        CancellationToken cancellationToken)
    {
        using var scope = profiler.CreateMethodScope<DemoProfilingPipeline>(new
        {
            plan.Scenario,
            SectionCount = plan.Sections.Count,
            activationId,
        });

        var results = new List<DemoProfilingSectionResult>(plan.Sections.Count);
        foreach (var section in plan.Sections)
        {
            results.Add(await sectionWorker.RunAsync(section, cancellationToken));
        }

        var output = outputComposer.Compose(plan, results, activationId);
        scope.SetResult(new
        {
            output.SectionCount,
            output.StepCount,
            output.ActivationId,
        });

        return output;
    }
}

internal sealed class DemoProfilingSectionWorker(
    DemoProfilingSqlProbe sqlProbe,
    IWorkProfiler profiler,
    ILogger<DemoProfilingSectionWorker> logger)
{
    internal async Task<DemoProfilingSectionResult> RunAsync(
        DemoProfilingSectionPlan section,
        CancellationToken cancellationToken)
    {
        using var scope = profiler.CreateMethodScope<DemoProfilingSectionWorker>(new
        {
            section.Ordinal,
            section.Label,
            section.Phase,
            StepCount = section.Steps.Count,
        });

        logger.LogInformation(
            "Profiling demo section {SectionOrdinal}: {SectionLabel} started with {StepCount} steps.",
            section.Ordinal,
            section.Label,
            section.Steps.Count);

        using (var normalizationScope = profiler.CreateScope("Normalize section context", new
        {
            section.Label,
            section.Phase,
        }))
        {
            profiler.AddInfo("Section metadata", new
            {
                section.Ordinal,
                section.Label,
                section.Phase,
            });
            normalizationScope.SetResult(new
            {
                Normalized = true,
                section.Steps.Count,
            });
        }

        using (var sqlScope = profiler.CreateScope("Capture SQL sample", new
        {
            section.Ordinal,
            section.Label,
            Purpose = "show SQL nodes in the profiling tree",
        }))
        {
            var sqlSnapshot = await sqlProbe.CaptureAsync(section, cancellationToken);
            sqlScope.SetResult(sqlSnapshot);
        }

        var totalDelayMilliseconds = 0;
        using (var executionScope = profiler.CreateScope("Execute retained steps", new
        {
            section.Label,
            section.Phase,
        }))
        {
            foreach (var step in section.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();

                logger.LogInformation(
                    "Profiling demo section {SectionOrdinal}: step {StepOrdinal} ({StepLabel}) running for {DelayMilliseconds}ms.",
                    section.Ordinal,
                    step.Ordinal,
                    step.Label,
                    step.DelayMilliseconds);

                using var timing = profiler.StartTiming(step.Label, new
                {
                    step.Ordinal,
                    step.Category,
                    step.DelayMilliseconds,
                });

                if (step.Ordinal % 2 == 0)
                {
                    profiler.AddInfo("Step annotations", new
                    {
                        SectionLabel = section.Label,
                        StepLabel = step.Label,
                        step.Category,
                    });
                }

                await Task.Delay(step.DelayMilliseconds, cancellationToken);
                totalDelayMilliseconds += step.DelayMilliseconds;
            }

            executionScope.SetResult(new
            {
                section.Label,
                ExecutedSteps = section.Steps.Count,
                totalDelayMilliseconds,
            });
        }

        logger.LogInformation(
            "Profiling demo section {SectionOrdinal}: {SectionLabel} completed after {TotalDelayMilliseconds}ms.",
            section.Ordinal,
            section.Label,
            totalDelayMilliseconds);

        scope.SetResult(new
        {
            section.Label,
            ExecutedSteps = section.Steps.Count,
            totalDelayMilliseconds,
        });

        return new DemoProfilingSectionResult(
            section.Label,
            section.Steps.Count,
            totalDelayMilliseconds);
    }
}

internal sealed class DemoProfilingSqlProbe(
    DemoProfilingSqlConnection connection,
    IWorkProfiler profiler)
{
    internal async Task<DemoProfilingSqlSnapshot> CaptureAsync(
        DemoProfilingSectionPlan section,
        CancellationToken cancellationToken)
    {
        using var scope = profiler.CreateMethodScope<DemoProfilingSqlProbe>(new
        {
            section.Ordinal,
            section.Label,
            section.Phase,
            ConnectionTarget = "sample persistence SQL Server",
        });

        await using var sqlConnection = new SqlConnection(connection.ConnectionString);
        await sqlConnection.OpenAsync(cancellationToken);

        string databaseName;
        int sessionId;
        await using (var metadataCommand = sqlConnection.CreateCommand())
        {
            metadataCommand.CommandText = """
SELECT
    CAST(DB_NAME() AS nvarchar(128)) AS DatabaseName,
    CAST(@@SPID AS int) AS SessionId,
    @SectionOrdinal AS SectionOrdinal,
    @SectionLabel AS SectionLabel,
    @Phase AS Phase;
""";
            metadataCommand.Parameters.AddWithValue("@SectionOrdinal", section.Ordinal);
            metadataCommand.Parameters.AddWithValue("@SectionLabel", section.Label);
            metadataCommand.Parameters.AddWithValue("@Phase", section.Phase);

            await using var reader = await metadataCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Expected sample profiling SQL metadata query to return one row.");
            }

            databaseName = reader.GetString(0);
            sessionId = reader.GetInt32(1);
        }

        await using var countCommand = sqlConnection.CreateCommand();
        countCommand.CommandText = """
SELECT COUNT(*)
FROM workable.WorkEntries
WHERE WorkSystemName = @WorkSystemName
  AND DefinitionName LIKE @DefinitionPattern;
""";
        countCommand.Parameters.AddWithValue("@WorkSystemName", "default");
        countCommand.Parameters.AddWithValue("@DefinitionPattern", "sample.demo.%");

        var matchingDurableEntries = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));
        var snapshot = new DemoProfilingSqlSnapshot(
            databaseName,
            sessionId,
            matchingDurableEntries);

        scope.SetResult(snapshot);
        return snapshot;
    }
}

internal sealed class DemoProfilingOutputComposer(IWorkProfiler profiler)
{
    internal DemoProfilingLabOutput Compose(
        DemoProfilingPlan plan,
        IReadOnlyList<DemoProfilingSectionResult> sections,
        string activationId)
    {
        using var scope = profiler.CreateMethodScope<DemoProfilingOutputComposer>(new
        {
            plan.Scenario,
            activationId,
            SectionCount = sections.Count,
        });

        using (var summaryScope = profiler.CreateScope("Summarize profiling output"))
        {
            summaryScope.SetResult(new
            {
                SectionLabels = sections.Select(section => section.Label).ToArray(),
                TotalDelayMilliseconds = sections.Sum(section => section.TotalDelayMilliseconds),
            });
        }

        var completedAt = DateTimeOffset.UtcNow;
        var output = new DemoProfilingLabOutput(
            plan.Scenario,
            sections.Count,
            sections.Sum(section => section.StepCount),
            activationId,
            completedAt);

        scope.SetResult(new
        {
            output.Scenario,
            output.SectionCount,
            output.StepCount,
            output.CompletedAt,
        });

        return output;
    }
}
