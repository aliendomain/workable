using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Logging")]
public sealed class WorkExecutionDiagnosticsPersistenceTests
{
    [Fact]
    public async Task SystemStartsAndReportsDiagnosticsUnavailableWhenRepositoryCannotConnect()
    {
        var repository = new TestExecutionDiagnosticsRepository
        {
            InitializeException = new WorkPersistenceStoreUnavailableException(
                "Diagnostics store unavailable.",
                new InvalidOperationException("Simulated connection failure.")),
        };
        var services = new ServiceCollection();
        services.AddSingleton<IWorkExecutionDiagnosticsRepository>(repository);
        services.AddWorkableSystem(builder => builder
            .RequireAuthorization(false)
            .PersistExecutionDiagnostics(TimeSpan.FromHours(1), LogLevel.Information)
            .AddWork<SuccessfulExecutor>(WorkDefinition.Create("diagnostics-unavailable-startup")));
        await using var provider = services.BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;

        await system.Start();
        var session = await system.CreateSession(WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        var completion = await (await system.Queue.Enqueue("diagnostics-unavailable-startup")).WaitForCompletion();

        Assert.Equal(WorkSystemState.Started, system.State);
        Assert.True(completion.IsCompletedSuccessfully);
        Assert.False(session.Capabilities.ExecutionDiagnosticsPersistenceAvailable);
        Assert.Equal(
            WorkExecutionDiagnosticsPersistenceHealthStatus.Unhealthy,
            system.Diagnostics.ExecutionDiagnosticsPersistence.Status);
        Assert.Equal(
            WorkExecutionDiagnosticsPersistenceHealthStatus.Unhealthy,
            session.Diagnostics.ExecutionDiagnosticsPersistence.Status);
        Assert.Same(
            session.Diagnostics.ExecutionDiagnosticsPersistence,
            session.Diagnostics.ExecutionDiagnosticsPersistence);
        Assert.False(session.Diagnostics.ExecutionDiagnosticsPersistence.IsHealthy);
        Assert.NotNull(session.Diagnostics.ExecutionDiagnosticsPersistence.InitializationFailedAt);
        var capabilitySource = Assert.IsAssignableFrom<IWorkSystemCapabilitySource>(system);
        Assert.Same(capabilitySource.Capabilities, capabilitySource.Capabilities);
        Assert.False(((IWorkExecutionDiagnosticsSystem)system).ExecutionDiagnosticsPersistenceAvailable);
        Assert.Equal(1, repository.InitializeCallCount);

        await system.Stop();
    }

    [Fact]
    public async Task PersistsAllEligibleLogsIndependentlyOfTheRetainedBufferLimit()
    {
        var repository = new RecordingRepository();
        var services = new ServiceCollection();
        services.AddSingleton<IWorkExecutionDiagnosticsRepository>(repository);
        services.AddSingleton<IWorkSystemCapabilityContributor, ProfilingCapabilityContributor>();
        services.AddWorkableSystem(builder => builder.AddWork<ThreeLogExecutor>(
            WorkDefinition.Create("persistent-logs", "Persists execution evidence."),
            configuration =>
            {
                configuration.ConfigureLogging(level: LogLevel.Warning, maximumBufferedEntries: 1);
                configuration.PersistExecutionDiagnostics(TimeSpan.FromHours(1), LogLevel.Debug);
            }));
        var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        Assert.Equal(
            WorkExecutionDiagnosticsPersistenceHealthStatus.Healthy,
            system.Diagnostics.ExecutionDiagnosticsPersistence.Status);
        Assert.Same(
            system.Diagnostics.ExecutionDiagnosticsPersistence,
            system.Diagnostics.ExecutionDiagnosticsPersistence);
        Assert.True(system.Diagnostics.ExecutionDiagnosticsPersistence.IsHealthy);
        Assert.True(system.Diagnostics.ExecutionDiagnosticsPersistence.PersistenceAvailable);
        Assert.Null(system.Diagnostics.ExecutionDiagnosticsPersistence.InitializationFailedAt);

        var completion = await (await system.Queue.Enqueue("persistent-logs")).WaitForCompletion();
        var persisted = await repository.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var started = await repository.Start.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(completion.Worker!.LastIteration!.Logs);
        Assert.Equal(3, persisted.PersistedLogCount);
        Assert.Equal(0, persisted.DroppedLogCount);
        Assert.True(started.InstrumentationAvailability.SqlClientProfilingAvailable);
        Assert.True(started.InstrumentationAvailability.HttpClientProfilingAvailable);
        Assert.Collection(
            repository.Logs.OrderBy(log => log.Ordinal),
            log => Assert.Equal("debug 1", log.Message),
            log => Assert.Equal("information 2", log.Message),
            log => Assert.Equal("warning 3", log.Message));
    }

    [Fact]
    public async Task RejectsEnabledWorkConfigurationWhenNoRepositoryIsRegistered()
    {
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder => builder.AddWork<ThreeLogExecutor>(
            WorkDefinition.Create("missing-repository", "Requires persistence."),
            configuration => configuration.PersistExecutionDiagnostics(TimeSpan.FromHours(1))));
        var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => system.Start());

        Assert.Contains("IWorkExecutionDiagnosticsRepository", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnregisteredRepositoryKeepsTheDiagnosticsSurfaceUnavailable()
    {
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder => builder.AddWork<SuccessfulExecutor>(
            WorkDefinition.Create("diagnostics-unavailable", "Uses the diagnostics no-op path.")));
        await using var provider = services.BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var diagnostics = Assert.IsAssignableFrom<IWorkExecutionDiagnosticsSystem>(system);

        var query = await diagnostics.QueryExecutionDiagnostics(
            new WorkExecutionDiagnosticCriteria(system.Id),
            CancellationToken.None);
        var artifact = await diagnostics.GetExecutionDiagnostic(
            new WorkExecutionDiagnosticGetRequest(system.Id, WorkerId.New(), 1),
            CancellationToken.None);
        var deleted = await diagnostics.DeleteExecutionDiagnosticCaptureRule(Guid.NewGuid(), CancellationToken.None);

        Assert.False(diagnostics.ExecutionDiagnosticsPersistenceAvailable);
        Assert.Equal(
            WorkExecutionDiagnosticsPersistenceHealthStatus.NotConfigured,
            system.Diagnostics.ExecutionDiagnosticsPersistence.Status);
        Assert.Same(
            system.Diagnostics.ExecutionDiagnosticsPersistence,
            system.Diagnostics.ExecutionDiagnosticsPersistence);
        Assert.True(system.Diagnostics.ExecutionDiagnosticsPersistence.IsHealthy);
        Assert.False(system.Diagnostics.ExecutionDiagnosticsPersistence.PersistenceAvailable);
        Assert.Empty(query.Items);
        Assert.Null(artifact);
        Assert.Empty(diagnostics.GetExecutionDiagnosticCaptureRules());
        Assert.False(deleted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => diagnostics.CreateExecutionDiagnosticCaptureRule(
            definitionName: null,
            LogLevel.Information,
            profileCaptureMode: null,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromHours(1),
            new WorkActor("test-user"),
            CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(WorkExecutionDiagnosticCriteria.MaximumTake + 1)]
    public async Task RejectsOutOfRangeDiagnosticQueryCountsBeforeCallingTheRepository(int take)
    {
        var repository = new TestExecutionDiagnosticsRepository();
        var services = new ServiceCollection();
        services.AddSingleton<IWorkExecutionDiagnosticsRepository>(repository);
        services.AddWorkableSystem(builder => builder.AddWork<SuccessfulExecutor>(
            WorkDefinition.Create("bounded-diagnostic-query")));
        await using var provider = services.BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        var diagnostics = Assert.IsAssignableFrom<IWorkExecutionDiagnosticsSystem>(system);

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            diagnostics.QueryExecutionDiagnostics(
                new WorkExecutionDiagnosticCriteria(system.Id, Take: take),
                CancellationToken.None));

        Assert.Equal("criteria", exception.ParamName);
        Assert.Null(repository.LastCriteria);
    }

    [Fact]
    public async Task ReportsRepositoryBatchFailuresAsDroppedRatherThanPersisted()
    {
        var repository = new RecordingRepository { FailLogWrites = true };
        var services = new ServiceCollection();
        services.AddSingleton<IWorkExecutionDiagnosticsRepository>(repository);
        services.AddWorkableSystem(builder => builder.AddWork<ThreeLogExecutor>(
            WorkDefinition.Create("failed-log-batch", "Reports diagnostic loss."),
            configuration => configuration.PersistExecutionDiagnostics(TimeSpan.FromHours(1), LogLevel.Debug)));
        var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        await (await system.Queue.Enqueue("failed-log-batch")).WaitForCompletion();
        var persisted = await repository.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, persisted.PersistedLogCount);
        Assert.Equal(3, persisted.DroppedLogCount);
    }

    [Fact]
    public async Task BoundsAndTruncatesPersistentLogsWithoutChangingTheWorkerOutcome()
    {
        var repository = new RecordingRepository();
        var services = new ServiceCollection();
        services.AddSingleton<IWorkExecutionDiagnosticsRepository>(repository);
        services.AddWorkableSystem(builder => builder
            .UseExecutionDiagnosticsPersistence(new WorkSystemExecutionDiagnosticsPersistenceConfiguration
            {
                IsEnabled = true,
                Retention = TimeSpan.FromHours(1),
                MinimumLogLevel = LogLevel.Information,
                MaximumLogsPerIteration = 2,
                MaximumLogBytesPerIteration = 1_048_576,
                MaximumLogMessageLength = 12,
                MaximumLogPropertiesLength = 24,
                MaximumExceptionTextLength = 10,
            })
            .AddWork<BoundedLogExecutor>(WorkDefinition.Create("bounded-persistent-logs")));
        await using var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var completion = await (await system.Queue.Enqueue("bounded-persistent-logs")).WaitForCompletion();
        var persisted = await repository.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var logs = repository.Logs.OrderBy(log => log.Ordinal).ToArray();

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Equal(2, persisted.PersistedLogCount);
        Assert.Equal(1, persisted.DroppedLogCount);
        Assert.Equal(2, logs.Length);
        Assert.All(logs, log => Assert.True(log.Message.Length <= 12));
        Assert.Contains("Item", logs[0].PropertiesJson, StringComparison.Ordinal);
        Assert.Equal(typeof(InvalidOperationException).FullName, logs[1].ExceptionType);
        Assert.Equal("exception-", logs[1].ExceptionMessage);
    }

    [Fact]
    public async Task RepositoryBeginAndCompletionFailuresRemainBestEffort()
    {
        var repository = new RecordingRepository
        {
            FailBegins = true,
            FailCompletions = true,
        };
        var services = new ServiceCollection();
        services.AddSingleton<IWorkExecutionDiagnosticsRepository>(repository);
        services.AddWorkableSystem(builder => builder.AddWork<SuccessfulExecutor>(
            WorkDefinition.Create("best-effort-repository-failures"),
            configuration => configuration.PersistExecutionDiagnostics(TimeSpan.FromHours(1))));
        await using var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var completion = await (await system.Queue.Enqueue("best-effort-repository-failures"))
            .WaitForCompletion()
            .WaitAsync(TimeSpan.FromSeconds(5));
        await repository.BeginAttempt.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await repository.CompletionAttempt.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task StaticPersistenceDoesNotAutomaticallyEnableProfilingInProduction()
    {
        var repository = new RecordingRepository();
        var system = CreateProfileSystem(repository, Environments.Production);
        await system.Start();

        await (await system.Queue.Enqueue("profile-policy")).WaitForCompletion();
        var persisted = await repository.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(persisted.Profile);
    }

    [Fact]
    public async Task TemporaryRuleCanExplicitlyEnableProfilingInProduction()
    {
        var repository = new RecordingRepository();
        var system = CreateProfileSystem(repository, Environments.Production);
        await system.Start();
        var diagnostics = Assert.IsAssignableFrom<IWorkExecutionDiagnosticsSystem>(system);
        await diagnostics.CreateExecutionDiagnosticCaptureRule(
            "profile-policy",
            LogLevel.Information,
            WorkProfileCaptureMode.Bounded,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromHours(1),
            new WorkActor("test-user"),
            CancellationToken.None);

        await (await system.Queue.Enqueue("profile-policy")).WaitForCompletion();
        var persisted = await repository.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(persisted.Profile);
        await (await system.Queue.Enqueue("profile-policy")).WaitForCompletion();
        Assert.Equal(1, repository.ListCaptureRuleCallCount);
    }

    [Fact]
    public async Task LogsOnlyTemporaryRuleDoesNotPersistAnExplicitWorkerProfile()
    {
        var repository = new RecordingRepository();
        var services = new ServiceCollection();
        services.AddSingleton<IWorkExecutionDiagnosticsRepository>(repository);
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(Environments.Production));
        services.AddWorkableSystem(builder => builder.AddWork<SuccessfulExecutor>(
            WorkDefinition.Create("logs-only-profile-policy")));
        await using var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var diagnostics = Assert.IsAssignableFrom<IWorkExecutionDiagnosticsSystem>(system);
        await diagnostics.CreateExecutionDiagnosticCaptureRule(
            "logs-only-profile-policy",
            LogLevel.Information,
            profileCaptureMode: null,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromHours(1),
            new WorkActor("test-user"),
            CancellationToken.None);

        var completion = await (await system.Queue.Enqueue(
            "logs-only-profile-policy",
            options: new WorkerOptions(ProfilingEnabled: true)
            {
                ProfilingCaptureMode = WorkProfileCaptureMode.Full,
            })).WaitForCompletion();
        var persisted = await repository.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var started = await repository.Start.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(completion.Worker?.LastIteration?.Profile);
        Assert.Null(started.ProfileCaptureMode);
        Assert.Null(persisted.Profile);
        Assert.False(persisted.ProfileDropped);
    }

    [Fact]
    public async Task QueueTimeRuntimeConfigurationCannotDisableWorkLevelPersistence()
    {
        var repository = new RecordingRepository();
        var services = new ServiceCollection();
        services.AddSingleton<IWorkExecutionDiagnosticsRepository>(repository);
        services.AddWorkableSystem(builder => builder.AddWork<SuccessfulExecutor>(
            WorkDefinition.Create("definition-scoped-persistence"),
            configuration => configuration.PersistExecutionDiagnostics(TimeSpan.FromHours(1))));
        await using var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        await (await system.Queue.Enqueue(
            "definition-scoped-persistence",
            options: new WorkerOptions(Configuration: WorkConfiguration.Default with
            {
                ExecutionDiagnostics = new WorkExecutionDiagnosticsPersistenceConfiguration
                {
                    IsEnabled = false,
                },
            }))).WaitForCompletion();
        var started = await repository.Start.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WorkExecutionDiagnosticCaptureSource.WorkConfiguration, started.CaptureSource);
    }

    [Fact]
    public async Task TemporaryRuleCreatedAfterQueueingEnablesProfilingWhenTheIterationStarts()
    {
        var repository = new RecordingRepository();
        var system = CreateDeferredProfileSystem(repository, "rule-after-queue");
        await system.Start();
        var handle = await system.Queue.Enqueue("rule-after-queue");
        var diagnostics = Assert.IsAssignableFrom<IWorkExecutionDiagnosticsSystem>(system);
        await diagnostics.CreateExecutionDiagnosticCaptureRule(
            "rule-after-queue",
            LogLevel.Information,
            WorkProfileCaptureMode.Bounded,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromHours(1),
            new WorkActor("test-user"),
            CancellationToken.None);

        var queued = await system.Query.Worker(handle.WorkerId!.Value)
            ?? throw new InvalidOperationException("Expected the queued worker.");
        var start = await system.Workers.Execute(queued.Version, WorkAction.Start);
        var completion = await handle.WaitForCompletion();
        var persisted = await repository.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var updated = await system.Query.Worker(handle.WorkerId.Value);

        Assert.True(start.IsAccepted);
        Assert.True(completion.IsCompletedSuccessfully);
        Assert.NotNull(updated?.Profile);
        Assert.NotNull(persisted.Profile);
        Assert.Equal(WorkProfileCaptureMode.Bounded, (await repository.Start.Task).ProfileCaptureMode);
    }

    [Fact]
    public async Task DeletedTemporaryRuleDoesNotLeaveProfilingEnabledForQueuedWork()
    {
        var repository = new RecordingRepository();
        var system = CreateDeferredProfileSystem(repository, "rule-deleted-before-start");
        await system.Start();
        var diagnostics = Assert.IsAssignableFrom<IWorkExecutionDiagnosticsSystem>(system);
        var rule = await diagnostics.CreateExecutionDiagnosticCaptureRule(
            "rule-deleted-before-start",
            LogLevel.Information,
            WorkProfileCaptureMode.Full,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromHours(1),
            new WorkActor("test-user"),
            CancellationToken.None);
        var handle = await system.Queue.Enqueue("rule-deleted-before-start");
        Assert.True(await diagnostics.DeleteExecutionDiagnosticCaptureRule(rule.Id, CancellationToken.None));

        var queued = await system.Query.Worker(handle.WorkerId!.Value)
            ?? throw new InvalidOperationException("Expected the queued worker.");
        var start = await system.Workers.Execute(queued.Version, WorkAction.Start);
        var completion = await handle.WaitForCompletion();

        Assert.True(start.IsAccepted);
        Assert.Null(completion.Worker?.Profile);
        Assert.False(repository.Start.Task.IsCompleted);
    }

    [Fact]
    public async Task TemporaryCaptureRulesEnforceCountAndSelectorBounds()
    {
        var repository = new RecordingRepository();
        var services = new ServiceCollection();
        services.AddSingleton<IWorkExecutionDiagnosticsRepository>(repository);
        services.AddWorkableSystem(builder => builder
            .UseExecutionDiagnosticsPersistence(new WorkSystemExecutionDiagnosticsPersistenceConfiguration
            {
                MaximumCaptureRules = 1,
            })
            .AddWork<SuccessfulExecutor>(WorkDefinition.Create("bounded-capture-rules")));
        var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var diagnostics = Assert.IsAssignableFrom<IWorkExecutionDiagnosticsSystem>(system);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => diagnostics.CreateExecutionDiagnosticCaptureRule(
            new string('x', 451),
            LogLevel.Information,
            null,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromHours(1),
            new WorkActor("test-user"),
            CancellationToken.None));
        var original = await diagnostics.CreateExecutionDiagnosticCaptureRule(
            definitionName: null,
            LogLevel.Information,
            profileCaptureMode: null,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromHours(1),
            new WorkActor("test-user"),
            CancellationToken.None);
        var replacement = await diagnostics.CreateExecutionDiagnosticCaptureRule(
            definitionName: null,
            LogLevel.Warning,
            WorkProfileCaptureMode.Full,
            TimeSpan.FromMinutes(10),
            TimeSpan.FromHours(2),
            new WorkActor("replacement-user"),
            CancellationToken.None);

        var active = Assert.Single(diagnostics.GetExecutionDiagnosticCaptureRules());
        Assert.Equal(replacement.Id, active.Id);
        Assert.NotEqual(original.Id, active.Id);
        Assert.Equal(LogLevel.Warning, active.MinimumLogLevel);
        Assert.Equal(WorkProfileCaptureMode.Full, active.ProfileCaptureMode);
        Assert.False(await diagnostics.DeleteExecutionDiagnosticCaptureRule(original.Id, CancellationToken.None));

        await Assert.ThrowsAsync<InvalidOperationException>(() => diagnostics.CreateExecutionDiagnosticCaptureRule(
            "bounded-capture-rules",
            LogLevel.Information,
            null,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromHours(1),
            new WorkActor("test-user"),
            CancellationToken.None));
    }

    [Fact]
    public async Task TemporaryWorkRuleOverridesTheSystemRuleCaseInsensitively()
    {
        var repository = new RecordingRepository();
        var services = new ServiceCollection();
        services.AddSingleton<IWorkExecutionDiagnosticsRepository>(repository);
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(Environments.Production));
        services.AddWorkableSystem(builder => builder.AddWork<SuccessfulExecutor>(
            WorkDefinition.Create("Rule.Precedence")));
        await using var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var diagnostics = Assert.IsAssignableFrom<IWorkExecutionDiagnosticsSystem>(system);
        await diagnostics.CreateExecutionDiagnosticCaptureRule(
            definitionName: null,
            LogLevel.Warning,
            profileCaptureMode: null,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromHours(1),
            new WorkActor("system-rule-user"),
            CancellationToken.None);
        await diagnostics.CreateExecutionDiagnosticCaptureRule(
            "rule.precedence",
            LogLevel.Debug,
            WorkProfileCaptureMode.Full,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromHours(2),
            new WorkActor("work-rule-user"),
            CancellationToken.None);

        await (await system.Queue.Enqueue("Rule.Precedence")).WaitForCompletion();
        var start = await repository.Start.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WorkExecutionDiagnosticCaptureSource.TemporaryWorkRule, start.CaptureSource);
        Assert.Equal(LogLevel.Debug, start.MinimumLogLevel);
        Assert.Equal(WorkProfileCaptureMode.Full, start.ProfileCaptureMode);
        Assert.Equal(TimeSpan.FromHours(2), start.Retention);
    }

    [Fact]
    public async Task TemporaryCaptureRulesRejectInvalidLifetimesAndEnums()
    {
        var repository = new RecordingRepository();
        var services = new ServiceCollection();
        services.AddSingleton<IWorkExecutionDiagnosticsRepository>(repository);
        services.AddWorkableSystem(builder => builder.AddWork<SuccessfulExecutor>(
            WorkDefinition.Create("invalid-rule")));
        await using var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var diagnostics = Assert.IsAssignableFrom<IWorkExecutionDiagnosticsSystem>(system);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => diagnostics.CreateExecutionDiagnosticCaptureRule(
            null,
            LogLevel.None,
            null,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromHours(1),
            new WorkActor("test-user"),
            CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => diagnostics.CreateExecutionDiagnosticCaptureRule(
            null,
            LogLevel.Information,
            (WorkProfileCaptureMode)99,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromHours(1),
            new WorkActor("test-user"),
            CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => diagnostics.CreateExecutionDiagnosticCaptureRule(
            null,
            LogLevel.Information,
            null,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromHours(1),
            new WorkActor("test-user"),
            CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => diagnostics.CreateExecutionDiagnosticCaptureRule(
            null,
            LogLevel.Information,
            null,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromDays(31),
            new WorkActor("test-user"),
            CancellationToken.None));
    }

    [Fact]
    public async Task CleanupImmediatelyContinuesBoundedPassesWhileADeletionBacklogRemains()
    {
        var repository = new RecordingRepository { FullCleanupBatchesBeforeEmpty = 5 };
        var services = new ServiceCollection();
        services.AddSingleton<IWorkExecutionDiagnosticsRepository>(repository);
        services.AddWorkableSystem(builder => builder
            .UseExecutionDiagnosticsPersistence(new WorkSystemExecutionDiagnosticsPersistenceConfiguration
            {
                CleanupInterval = TimeSpan.FromMilliseconds(10),
                CleanupBacklogDelay = TimeSpan.FromMilliseconds(1),
                CleanupBatchSize = 3,
                MaximumCleanupBatchesPerInterval = 2,
            })
            .AddWork<SuccessfulExecutor>(WorkDefinition.Create("cleanup-backlog")));
        await using var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        await repository.CleanupDrained.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(repository.DeleteExpiredCallCount >= 6);
    }

    [Fact]
    public async Task WorkCompletionDoesNotWaitForPersistedProfileMaterialization()
    {
        var repository = new RecordingRepository { BlockBegins = true };
        var system = CreateBackpressuredProfileSystem<SuccessfulExecutor>(repository, "background-profile");
        await system.Start();

        var receipt = await system.Queue.Enqueue("background-profile");
        await repository.Start.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var completion = await receipt.WaitForCompletion().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(completion.Worker!.LastIteration!.Profile);

        repository.ReleaseBegins();
        var persisted = await repository.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var updatedWorker = await system.Query.Worker(completion.Worker!.Id);

        Assert.NotNull(persisted.Profile);
        Assert.False(persisted.ProfileDropped);
        Assert.NotNull(updatedWorker?.LastIteration?.Profile);
    }

    [Fact]
    public async Task CleanupProtectsACompletionWaitingBehindTheBackgroundWriter()
    {
        var repository = new RecordingRepository { BlockBegins = true };
        var system = CreateBackpressuredProfileSystem<SuccessfulExecutor>(
            repository,
            "pending-completion-cleanup",
            TimeSpan.FromMilliseconds(10));
        await system.Start();

        var receipt = await system.Queue.Enqueue("pending-completion-cleanup");
        var start = await repository.Start.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await receipt.WaitForCompletion().WaitAsync(TimeSpan.FromSeconds(5));
            while (repository.ExpirationRequests.TryDequeue(out _))
            {
                // Drain cleanup requests recorded before the pending completion was observed.
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            WorkExecutionDiagnosticsExpirationRequest? observed = null;
            while (observed is null)
            {
                while (repository.ExpirationRequests.TryDequeue(out var request))
                {
                    if (request.ActiveDiagnosticIds.Contains(start.DiagnosticId))
                    {
                        observed = request;
                        break;
                    }
                }

                if (observed is null)
                {
                    await Task.Delay(10, timeout.Token);
                }
            }

            Assert.Contains(start.DiagnosticId, observed.ActiveDiagnosticIds);
        }
        finally
        {
            repository.ReleaseBegins();
        }

        await repository.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DropsQueuedProfileInsteadOfDelayingWorkWhenTheWriterIsSaturated()
    {
        var repository = new RecordingRepository { BlockBegins = true };
        var system = CreateBackpressuredProfileSystem<OneLogExecutor>(repository, "dropped-profile");
        await system.Start();

        var receipt = await system.Queue.Enqueue("dropped-profile");
        await repository.Start.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await receipt.WaitForCompletion().WaitAsync(TimeSpan.FromSeconds(5));

        repository.ReleaseBegins();
        var persisted = await repository.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(persisted.Profile);
        Assert.True(persisted.ProfileDropped);
    }

    [Fact]
    public async Task PreservesConfiguredWorkerProfileWhenTheDiagnosticsWriterIsSaturated()
    {
        var repository = new RecordingRepository { BlockBegins = true };
        var system = CreateBackpressuredProfileSystem<OneLogExecutor>(repository, "preserved-profile");
        await system.Start();

        var receipt = await system.Queue.Enqueue(
            "preserved-profile",
            options: new WorkerOptions(ProfilingEnabled: true)
            {
                ProfilingCaptureMode = WorkProfileCaptureMode.Full,
            });
        await repository.Start.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var completion = await receipt.WaitForCompletion().WaitAsync(TimeSpan.FromSeconds(5));

        repository.ReleaseBegins();
        var persisted = await repository.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(completion.Worker!.LastIteration!.Profile);
        Assert.Null(persisted.Profile);
        Assert.True(persisted.ProfileDropped);
    }

    [Fact]
    public async Task PreservesConfiguredWorkerProfileWhenItExceedsPersistenceBounds()
    {
        var repository = new RecordingRepository();
        var services = new ServiceCollection();
        services.AddSingleton<IWorkExecutionDiagnosticsRepository>(repository);
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(Environments.Development));
        services.AddWorkableSystem(builder => builder
            .UseExecutionDiagnosticsPersistence(new WorkSystemExecutionDiagnosticsPersistenceConfiguration
            {
                IsEnabled = true,
                Retention = TimeSpan.FromHours(1),
                ProfileCaptureMode = WorkProfileCaptureMode.Full,
                MaximumProfileJsonLength = 1,
            })
            .AddWork<SuccessfulExecutor>(WorkDefinition.Create("oversized-profile")));
        await using var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var receipt = await system.Queue.Enqueue(
            "oversized-profile",
            options: new WorkerOptions(ProfilingEnabled: true)
            {
                ProfilingCaptureMode = WorkProfileCaptureMode.Full,
            });
        var completion = await receipt.WaitForCompletion().WaitAsync(TimeSpan.FromSeconds(5));
        var persisted = await repository.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var updatedWorker = await system.Query.Worker(completion.Worker!.Id);

        Assert.NotNull(updatedWorker?.LastIteration?.Profile);
        Assert.Null(persisted.Profile);
        Assert.True(persisted.ProfileDropped);
    }

    [Fact]
    public async Task BoundsCompletedProfilesWaitingForTheBackgroundWriter()
    {
        var repository = new RecordingRepository { BlockBegins = true };
        var services = new ServiceCollection();
        services.AddSingleton<IWorkExecutionDiagnosticsRepository>(repository);
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(Environments.Development));
        services.AddWorkableSystem(builder => builder
            .UseExecutionDiagnosticsPersistence(new WorkSystemExecutionDiagnosticsPersistenceConfiguration
            {
                IsEnabled = true,
                Retention = TimeSpan.FromHours(1),
                ProfileCaptureMode = WorkProfileCaptureMode.Full,
                ChannelCapacity = 10,
                MaximumPendingProfiles = 1,
                LogBatchSize = 1,
            })
            .AddWork<SuccessfulExecutor>(WorkDefinition.Create("bounded-pending-profiles")));
        await using var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var first = await system.Queue.Enqueue("bounded-pending-profiles");
        await repository.Start.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await system.Queue.Enqueue("bounded-pending-profiles");
        await Task.WhenAll(first.WaitForCompletion(), second.WaitForCompletion()).WaitAsync(TimeSpan.FromSeconds(5));

        repository.ReleaseBegins();
        await TestEventually.Until(() => Task.FromResult(repository.Completions.Count == 2));

        Assert.Single(repository.Completions, completion => completion.Profile is not null);
        Assert.Single(repository.Completions, completion => completion.ProfileDropped);
    }

    [Fact]
    public void RejectsRetentionLongerThanThirtyDays()
    {
        var configuration = WorkConfiguration.Default with
        {
            ExecutionDiagnostics = new WorkExecutionDiagnosticsPersistenceConfiguration
            {
                IsEnabled = true,
                Retention = TimeSpan.FromDays(31),
            },
        };

        var message = Assert.Single(WorkConfigurationValidator.Validate(configuration));

        Assert.Equal("workable.configuration.execution_diagnostics.retention_out_of_range", message.Code);
    }

    [Fact]
    public void ValidatesEveryWorkLevelExecutionDiagnosticsSetting()
    {
        static WorkMessage Validate(WorkExecutionDiagnosticsPersistenceConfiguration persistence)
            => Assert.Single(WorkConfigurationValidator.Validate(WorkConfiguration.Default with
            {
                ExecutionDiagnostics = persistence,
            }));

        Assert.Equal(
            "workable.configuration.execution_diagnostics.retention_out_of_range",
            Validate(new WorkExecutionDiagnosticsPersistenceConfiguration
            {
                IsEnabled = true,
                Retention = TimeSpan.FromSeconds(59),
            }).Code);
        Assert.Equal(
            "workable.configuration.execution_diagnostics.log_level_required",
            Validate(new WorkExecutionDiagnosticsPersistenceConfiguration
            {
                IsEnabled = true,
                MinimumLogLevel = LogLevel.None,
            }).Code);
        Assert.Equal(
            "workable.configuration.execution_diagnostics.profile_capture_mode_invalid",
            Validate(new WorkExecutionDiagnosticsPersistenceConfiguration
            {
                IsEnabled = true,
                ProfileCaptureMode = (WorkProfileCaptureMode)int.MaxValue,
            }).Code);
    }

    [Fact]
    public void ValidatesEverySystemLevelExecutionDiagnosticsBound()
    {
        static void Reject(WorkSystemExecutionDiagnosticsPersistenceConfiguration persistence)
            => Assert.Throws<InvalidOperationException>(() => new ServiceCollection()
                .AddWorkableSystem(builder => builder.UseExecutionDiagnosticsPersistence(persistence)));

        Reject(new() { Retention = TimeSpan.FromSeconds(59) });
        Reject(new() { IsEnabled = true, MinimumLogLevel = LogLevel.None });
        Reject(new() { IsEnabled = true, ProfileCaptureMode = (WorkProfileCaptureMode)int.MaxValue });
        Reject(new() { ChannelCapacity = 0 });
        Reject(new() { MaximumPendingLogBytes = 0 });
        Reject(new() { MaximumPendingProfiles = 0 });
        Reject(new() { MaximumCaptureRules = 0 });
        Reject(new() { LogBatchSize = 0 });
        Reject(new() { ChannelCapacity = 1, LogBatchSize = 2 });
        Reject(new() { CleanupInterval = TimeSpan.Zero });
        Reject(new() { CleanupBatchSize = 0 });
    }

    [Fact]
    public async Task SystemLevelFluentConfigurationPreservesTheRequestedPolicy()
    {
        var repository = new RecordingRepository();
        var services = new ServiceCollection();
        services.AddSingleton<IWorkExecutionDiagnosticsRepository>(repository);
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(Environments.Development));
        services.AddWorkableSystem(builder => builder
            .PersistExecutionDiagnostics(
                TimeSpan.FromHours(4),
                LogLevel.Warning,
                WorkProfileCaptureMode.Full)
            .AddWork<SuccessfulExecutor>(WorkDefinition.Create("system-diagnostic-policy")));
        await using var provider = services.BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        await (await system.Queue.Enqueue("system-diagnostic-policy")).WaitForCompletion();
        var start = await repository.Start.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(TimeSpan.FromHours(4), start.Retention);
        Assert.Equal(LogLevel.Warning, start.MinimumLogLevel);
        Assert.Equal(WorkProfileCaptureMode.Full, start.ProfileCaptureMode);
    }

    private static IWorkSystem CreateProfileSystem(RecordingRepository repository, string environmentName)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWorkExecutionDiagnosticsRepository>(repository);
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(environmentName));
        services.AddWorkableSystem(builder => builder.AddWork<SuccessfulExecutor>(
            WorkDefinition.Create("profile-policy", "Verifies production profile policy."),
            configuration => configuration.PersistExecutionDiagnostics(
                TimeSpan.FromHours(1),
                LogLevel.Information,
                WorkProfileCaptureMode.Bounded)));
        return services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
    }

    private static IWorkSystem CreateBackpressuredProfileSystem<TExecutor>(
        RecordingRepository repository,
        string workName,
        TimeSpan? cleanupInterval = null)
        where TExecutor : class, IWorkExecutor
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWorkExecutionDiagnosticsRepository>(repository);
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(Environments.Development));
        services.AddWorkableSystem(builder => builder
            .UseExecutionDiagnosticsPersistence(new WorkSystemExecutionDiagnosticsPersistenceConfiguration
            {
                IsEnabled = true,
                Retention = TimeSpan.FromHours(1),
                MinimumLogLevel = LogLevel.Debug,
                ProfileCaptureMode = WorkProfileCaptureMode.Full,
                ChannelCapacity = 1,
                LogBatchSize = 1,
                CleanupInterval = cleanupInterval ?? TimeSpan.FromMinutes(1),
            })
            .AddWork<TExecutor>(WorkDefinition.Create(workName)));
        return services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
    }

    private static IWorkSystem CreateDeferredProfileSystem(
        RecordingRepository repository,
        string workName)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWorkExecutionDiagnosticsRepository>(repository);
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(Environments.Production));
        services.AddWorkableSystem(builder => builder.AddWork<SuccessfulExecutor>(
            WorkDefinition.Create(
                workName,
                defaultOptions: WorkerOptionFixtures.DoNotStart())));
        return services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
    }

    private sealed class ThreeLogExecutor(ILogger<ThreeLogExecutor> logger) : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            WorkInput? input,
            CancellationToken cancellationToken)
        {
            logger.LogDebug("debug 1");
            logger.LogInformation("information 2");
            logger.LogWarning("warning 3");
            return Task.FromResult(WorkExecutionResult.Success());
        }
    }

    private sealed class SuccessfulExecutor : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            WorkInput? input,
            CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }

    private sealed class OneLogExecutor(ILogger<OneLogExecutor> logger) : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            WorkInput? input,
            CancellationToken cancellationToken)
        {
            logger.LogInformation("fills the diagnostics channel");
            return Task.FromResult(WorkExecutionResult.Success());
        }
    }

    private sealed class BoundedLogExecutor(ILogger<BoundedLogExecutor> logger) : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            WorkInput? input,
            CancellationToken cancellationToken)
        {
            logger.LogInformation("processed item {Item}", "abcdefghijklmnopqrstuvwxyz");
            logger.LogWarning(new InvalidOperationException("exception-message-is-long"), "warning item {Item}", 2);
            logger.LogError("this third log is dropped");
            return Task.FromResult(WorkExecutionResult.Success());
        }
    }

    private sealed class RecordingRepository : IWorkExecutionDiagnosticsRepository
    {
        private readonly ConcurrentDictionary<Guid, WorkExecutionDiagnosticIterationStart> starts = [];
        private readonly ConcurrentDictionary<Guid, WorkExecutionDiagnosticCaptureRule> rules = [];
        private int listCaptureRuleCallCount;
        private int deleteExpiredCallCount;

        public ConcurrentQueue<WorkExecutionDiagnosticLogRecord> Logs { get; } = [];

        public ConcurrentQueue<WorkExecutionDiagnosticIterationCompletion> Completions { get; } = [];

        public ConcurrentQueue<WorkExecutionDiagnosticsExpirationRequest> ExpirationRequests { get; } = [];

        public bool FailLogWrites { get; init; }

        public bool FailBegins { get; init; }

        public bool FailCompletions { get; init; }

        public bool BlockBegins { get; init; }

        public int FullCleanupBatchesBeforeEmpty { get; init; }

        public TaskCompletionSource CleanupDrained { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource BeginAttempt { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CompletionAttempt { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DeleteExpiredCallCount => Volatile.Read(ref this.deleteExpiredCallCount);

        public int ListCaptureRuleCallCount => Volatile.Read(ref this.listCaptureRuleCallCount);

        private TaskCompletionSource BeginRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<WorkExecutionDiagnosticIterationCompletion> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<WorkExecutionDiagnosticIterationStart> Start { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Initialize(WorkExecutionDiagnosticsInitializationContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public async Task BeginIteration(WorkExecutionDiagnosticIterationStart iteration, CancellationToken cancellationToken = default)
        {
            this.starts[iteration.DiagnosticId] = iteration;
            this.Start.TrySetResult(iteration);
            this.BeginAttempt.TrySetResult();
            if (this.FailBegins)
            {
                throw new InvalidOperationException("Simulated diagnostics begin failure.");
            }

            if (this.BlockBegins)
            {
                await this.BeginRelease.Task.WaitAsync(cancellationToken);
            }
        }

        public void ReleaseBegins() => this.BeginRelease.TrySetResult();

        public Task AppendLogs(IReadOnlyList<WorkExecutionDiagnosticLogRecord> logs, CancellationToken cancellationToken = default)
        {
            if (this.FailLogWrites)
            {
                throw new InvalidOperationException("Simulated diagnostics repository failure.");
            }

            foreach (var log in logs)
            {
                this.Logs.Enqueue(log);
            }

            return Task.CompletedTask;
        }

        public Task CompleteIteration(WorkExecutionDiagnosticIterationCompletion completion, CancellationToken cancellationToken = default)
        {
            this.CompletionAttempt.TrySetResult();
            if (this.FailCompletions)
            {
                throw new InvalidOperationException("Simulated diagnostics completion failure.");
            }

            this.Completions.Enqueue(completion);
            this.Completion.TrySetResult(completion);
            return Task.CompletedTask;
        }

        public Task<int> DeleteExpired(WorkExecutionDiagnosticsExpirationRequest request, CancellationToken cancellationToken = default)
        {
            this.ExpirationRequests.Enqueue(request);
            var call = Interlocked.Increment(ref this.deleteExpiredCallCount);
            if (call <= this.FullCleanupBatchesBeforeEmpty)
            {
                return Task.FromResult(request.MaximumCount);
            }

            this.CleanupDrained.TrySetResult();
            return Task.FromResult(0);
        }

        public Task<WorkExecutionDiagnosticQueryResult> Query(WorkExecutionDiagnosticCriteria criteria, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkExecutionDiagnosticQueryResult([]));

        public Task<WorkExecutionDiagnosticArtifact?> Get(WorkExecutionDiagnosticGetRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkExecutionDiagnosticArtifact?>(null);

        public Task<IReadOnlyList<WorkExecutionDiagnosticCaptureRule>> ListCaptureRules(
            WorkExecutionDiagnosticsInitializationContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref this.listCaptureRuleCallCount);
            return Task.FromResult<IReadOnlyList<WorkExecutionDiagnosticCaptureRule>>([.. this.rules.Values]);
        }

        public Task UpsertCaptureRule(WorkExecutionDiagnosticCaptureRule rule, int maximumActiveRules, CancellationToken cancellationToken = default)
        {
            foreach (var existing in this.rules.Values.Where(existing =>
                existing.Id != rule.Id &&
                StringComparer.OrdinalIgnoreCase.Equals(existing.DefinitionName, rule.DefinitionName)))
            {
                this.rules.TryRemove(existing.Id, out _);
            }
            this.rules[rule.Id] = rule;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteCaptureRule(
            WorkExecutionDiagnosticCaptureRuleDeleteRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(this.rules.TryRemove(request.RuleId, out _));
    }

    private sealed class ProfilingCapabilityContributor : IWorkSystemCapabilityContributor
    {
        public void ConfigureCapabilities(WorkSystemCapabilitiesBuilder capabilities)
        {
            capabilities.SqlProfilingAvailable = true;
            capabilities.HttpClientProfilingAvailable = true;
        }
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Workable.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
