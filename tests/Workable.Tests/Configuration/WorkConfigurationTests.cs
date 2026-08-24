using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Configuration")]
public sealed class WorkConfigurationTests
{
    [Fact]
    public void ValidatorRejectsEveryUndefinedConfigurationEnum()
    {
        const int undefined = 999;
        var cases = new (WorkConfiguration Configuration, string Code)[]
        {
            (WorkConfiguration.Default with
            {
                Start = WorkStartConfiguration.Default with { Policy = (WorkStartPolicy)undefined },
            }, "workable.configuration.start.policy.invalid"),
            (WorkConfiguration.Default with
            {
                TransientRetry = WorkTransientRetryConfiguration.Default with { Backoff = (WorkRetryBackoff)undefined },
            }, "workable.configuration.transient_retry.backoff.invalid"),
            (WorkConfiguration.Default with
            {
                FailedWorker = WorkFailedWorkerConfiguration.Default with { Handling = (WorkFailedWorkerHandling)undefined },
            }, "workable.configuration.failed_worker.handling.invalid"),
            (WorkConfiguration.Default with
            {
                Logging = WorkLoggingConfiguration.Default with { Level = (LogLevel)undefined },
            }, "workable.configuration.logging.level.invalid"),
            (WorkConfiguration.Default with
            {
                ExecutionDiagnostics = WorkExecutionDiagnosticsPersistenceConfiguration.Default with
                {
                    MinimumLogLevel = (LogLevel)undefined,
                },
            }, "workable.configuration.execution_diagnostics.log_level_invalid"),
            (WorkConfiguration.Default with
            {
                ExecutionDiagnostics = WorkExecutionDiagnosticsPersistenceConfiguration.Default with
                {
                    ProfileCaptureMode = (WorkProfileCaptureMode)undefined,
                },
            }, "workable.configuration.execution_diagnostics.profile_capture_mode_invalid"),
            (WorkConfiguration.Default with
            {
                Coordination = WorkCoordinationConfiguration.Default with { Storage = (WorkCoordinationStorage)undefined },
            }, "workable.configuration.coordination.storage.invalid"),
            (WorkConfiguration.Default with
            {
                Coordination = WorkCoordinationConfiguration.Default with
                {
                    Idempotency = WorkIdempotencyConfiguration.Default with
                    {
                        ConflictPolicy = (WorkIdempotencyConflictPolicy)undefined,
                    },
                },
            }, "workable.configuration.idempotency.conflict_policy.invalid"),
            (WithConcurrency(WorkConcurrencyConfiguration.Default with { Scope = (WorkConcurrencyScope)undefined }),
                "workable.configuration.concurrency.scope.invalid"),
            (WithConcurrency(WorkConcurrencyConfiguration.Default with { BlockingMode = (WorkConcurrencyBlockingMode)undefined }),
                "workable.configuration.concurrency.blocking_mode.invalid"),
            (WithConcurrency(WorkConcurrencyConfiguration.Default with
                {
                    LimitReachedBehavior = (WorkConcurrencyLimitReachedBehavior)undefined,
                }), "workable.configuration.concurrency.limit_reached_behavior.invalid"),
            (WithConcurrency(WorkConcurrencyConfiguration.Default with
                {
                    OverrideBehavior = (WorkConcurrencyOverrideBehavior)undefined,
                }), "workable.configuration.concurrency.override_behavior.invalid"),
            (WorkConfiguration.Default with
            {
                Invocation = WorkInvocationConfiguration.Allow((WorkInvocationChannel)undefined),
            }, "workable.configuration.invocation.channel.invalid"),
        };

        foreach (var (configuration, code) in cases)
        {
            Assert.Contains(
                WorkConfigurationValidator.Validate(configuration),
                message => message.Code == code);
        }
    }

    [Fact]
    public void WorkerOptionsValidatorRejectsUndefinedProfilingCaptureMode()
    {
        var messages = WorkConfigurationValidator.ValidateWorkerOptions(
            new WorkerOptions
            {
                ProfilingCaptureMode = (WorkProfileCaptureMode)999,
            });

        var message = Assert.Single(messages);
        Assert.Equal("workable.options.profiling_capture_mode.invalid", message.Code);
        Assert.Equal("options.profilingCaptureMode", message.Target);
    }

    [Fact]
    public void ValidatorRejectsEveryNumericCollectionAndRepositoryBoundary()
    {
        var invalid = WorkConfiguration.Default with
        {
            Recurrence = WorkRecurrenceConfiguration.Default with
            {
                CircuitBreakerFailureThreshold = 0,
                RetainedIterations = 0,
            },
            TransientRetry = WorkTransientRetryConfiguration.Disabled with
            {
                Jitter = TimeSpan.FromTicks(-1),
                MaximumDelay = TimeSpan.Zero,
                InitialDelay = TimeSpan.FromSeconds(1),
            },
            FailedWorker = WorkFailedWorkerConfiguration.Default with
            {
                AutoCancelAfter = TimeSpan.Zero,
            },
            Coordination = WorkCoordinationConfiguration.Default with
            {
                IsEnabled = true,
                Concurrency = WorkConcurrencyConfiguration.Default with
                {
                    MaximumCapacity = -1,
                },
            },
            Invocation = WorkInvocationConfiguration.Allow(),
            ChildExecution = WorkChildExecutionConfiguration.Default with
            {
                AllowedDefinitionNames = new HashSet<string> { " " },
            },
        };

        var codes = WorkConfigurationValidator.Validate(invalid)
            .Select(message => message.Code)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("workable.configuration.recurrence.circuit_breaker_failure_threshold_required", codes);
        Assert.Contains("workable.configuration.recurrence.retained_iterations_required", codes);
        Assert.Contains("workable.configuration.transient_retry.jitter_negative", codes);
        Assert.Contains("workable.configuration.transient_retry.maximum_delay_required", codes);
        Assert.Contains("workable.configuration.transient_retry.initial_delay_exceeds_maximum", codes);
        Assert.Contains("workable.configuration.failed_worker.auto_cancel_after_required", codes);
        Assert.Contains("workable.configuration.concurrency.maximum_capacity_negative", codes);
        Assert.Contains("workable.configuration.invocation.channels_required", codes);
        Assert.Contains("workable.configuration.child_execution.definition_name_required", codes);

        Assert.Empty(WorkConfigurationValidator.ValidateExecutionDiagnosticsRepository(
            WorkConfiguration.Default,
            repositoryAvailable: false));
        Assert.Empty(WorkConfigurationValidator.ValidateExecutionDiagnosticsRepository(
            WorkConfiguration.Default with
            {
                ExecutionDiagnostics = WorkExecutionDiagnosticsPersistenceConfiguration.Default with { IsEnabled = true },
            },
            repositoryAvailable: true));
        Assert.Single(WorkConfigurationValidator.ValidateExecutionDiagnosticsRepository(
            WorkConfiguration.Default with
            {
                ExecutionDiagnostics = WorkExecutionDiagnosticsPersistenceConfiguration.Default with { IsEnabled = true },
            },
            repositoryAvailable: false));
    }

    [Fact]
    public void DisabledCoordinationReportsEveryIndividuallyEnabledNestedFeature()
    {
        var configurations = new[]
        {
            WorkCoordinationConfiguration.Default with
            {
                Idempotency = WorkIdempotencyConfiguration.Default with { IsEnabled = true },
            },
            WorkCoordinationConfiguration.Default with
            {
                Concurrency = WorkConcurrencyConfiguration.Default with { IsEnabled = true },
            },
            WorkCoordinationConfiguration.Default with
            {
                Durability = WorkQueueDurabilityConfiguration.Default with { IsEnabled = true },
            },
            WorkCoordinationConfiguration.Default with
            {
                Durability = WorkQueueDurabilityConfiguration.Default with { CompleteDurably = true },
            },
        };

        Assert.All(configurations, coordination => Assert.Contains(
            WorkConfigurationValidator.Validate(WorkConfiguration.Default with { Coordination = coordination }),
            message => message.Code == "workable.configuration.coordination.disabled_with_features"));
    }

    [Fact]
    public void ContributedWorkCanBeConfiguredAtBootstrap()
    {
        var definition = WorkDefinition.Create("contributed-config", "Configured while contributed.");
        var system = new ServiceCollection()
            .AddWorkableWork(
                definition,
                SuccessfulWork,
                configuration => configuration.RecurEvery(TimeSpan.FromMinutes(3)))
            .AddWorkableSystem(builder => builder.StartWithHost())
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        var configured = RequiredDefinition(system, "contributed-config");

        Assert.True(configured.Configuration.Recurrence.IsEnabled);
        Assert.Equal(TimeSpan.FromMinutes(3), configured.Configuration.Recurrence.Interval);
    }

    [Fact]
    public async Task ExecutionContextReceivesEffectiveConfiguration()
    {
        var observed = new TaskCompletionSource<RecurrenceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var definition = WorkDefinition.Create("context-config", "Executor can read effective configuration.",
            configuration: WorkConfiguration.Default with
            {
                Recurrence = WorkRecurrenceConfiguration.Every(TimeSpan.FromMinutes(4)),
            });
        var system = CreateSystem(definition, (context, input, cancellationToken) =>
        {
            observed.TrySetResult(new RecurrenceResult(
                context.Configuration.Recurrence.IsEnabled,
                context.Configuration.Recurrence.Interval));
            return Task.FromResult(WorkExecutionResult.Success());
        });

        await system.Start();

        var handle = await system.Queue.Enqueue("context-config");
        var result = await observed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var worker = await system.Query.Worker(handle.WorkerId ?? throw new InvalidOperationException("Expected worker id."));
        await system.Workers.Execute(RequiredWorker(worker).Version, WorkAction.Cancel);
        await handle.WaitForCompletion();

        Assert.True(result.IsEnabled);
        Assert.Equal(TimeSpan.FromMinutes(4), result.Interval);
    }

    private static IWorkSystem CreateSystem(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute)
        => new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(definition, execute))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

    private static WorkConfiguration WithConcurrency(WorkConcurrencyConfiguration concurrency)
        => WorkConfiguration.Default with
        {
            Coordination = WorkCoordinationConfiguration.Default with
            {
                Concurrency = concurrency,
            },
        };

    private static WorkDefinition RequiredDefinition(IWorkSystem system, string name)
        => system.Catalog.TryGet(name, out var definition)
            ? definition
            : throw new InvalidOperationException($"Expected work definition '{name}' to exist.");

    private static WorkerSnapshot RequiredWorker(WorkerSnapshot? worker)
        => worker ?? throw new InvalidOperationException("Expected worker.");

    private static Task<WorkExecutionResult> SuccessfulWork(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    private sealed record RecurrenceResult(bool IsEnabled, TimeSpan Interval);
}
