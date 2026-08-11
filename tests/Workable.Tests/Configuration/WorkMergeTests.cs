using Microsoft.Extensions.Logging;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Configuration")]
public sealed class WorkMergeTests
{
    [Fact]
    public void WorkerOptionsMergePreservesAndOverridesProfilingCaptureMode()
    {
        var full = new WorkerOptions
        {
            ProfilingCaptureMode = WorkProfileCaptureMode.Full,
        };

        var inherited = full.Merge(new WorkerOptions());
        var bounded = full.Merge(new WorkerOptions
        {
            ProfilingCaptureMode = WorkProfileCaptureMode.Bounded,
        });

        Assert.Equal(WorkProfileCaptureMode.Full, inherited.ProfilingCaptureMode);
        Assert.True(inherited.HasExplicitProfilingCaptureMode);
        Assert.Equal(WorkProfileCaptureMode.Bounded, bounded.ProfilingCaptureMode);
        Assert.True(bounded.HasExplicitProfilingCaptureMode);
    }

    [Fact]
    public void WorkerOptionsMergeWithNullOverridesReturnsSameOptions()
    {
        var options = new WorkerOptions(
            ProfilingEnabled: true,
            Configuration: WorkConfiguration.Default);

        Assert.Same(options, options.Merge(null));
    }

    [Fact]
    public void WorkerOptionsMergeUsesOverrideValuesWhenProvided()
    {
        var original = new WorkerOptions(
            ProfilingEnabled: true,
            Configuration: WorkConfiguration.Default with
            {
                Logging = new WorkLoggingConfiguration
                {
                    Level = LogLevel.Warning,
                },
            });
        var overrides = new WorkerOptions(
            ProfilingEnabled: false,
            Configuration: WorkConfiguration.Default with
            {
                Logging = new WorkLoggingConfiguration
                {
                    IsEnabled = false,
                    Level = LogLevel.Error,
                },
            });

        var merged = original.Merge(overrides);

        Assert.False(merged.ProfilingEnabled);
        Assert.False(merged.Configuration?.Logging.IsEnabled);
        Assert.Equal(LogLevel.Error, merged.Configuration?.Logging.Level);
    }

    [Fact]
    public void WorkerOptionsMergePreservesProfilingWhenOverridesDoNotExplicitlySetIt()
    {
        var original = new WorkerOptions(
            ProfilingEnabled: true,
            Configuration: WorkConfiguration.Default with
            {
                Logging = new WorkLoggingConfiguration
                {
                    Level = LogLevel.Warning,
                },
            });
        var overrides = new WorkerOptions(
            Configuration: WorkConfiguration.Default with
            {
                Logging = new WorkLoggingConfiguration
                {
                    IsEnabled = false,
                    Level = LogLevel.Error,
                },
            });

        var merged = original.Merge(overrides);

        Assert.True(merged.ProfilingEnabled);
        Assert.True(merged.HasExplicitProfilingEnabled);
        Assert.False(merged.Configuration?.Logging.IsEnabled);
        Assert.Equal(LogLevel.Error, merged.Configuration?.Logging.Level);
    }

    [Fact]
    public void WorkerOptionsMergePreservesInheritedProfilingStateWhenNeitherSideSetsItExplicitly()
    {
        var original = new WorkerOptions(
            Configuration: WorkConfiguration.Default with
            {
                Logging = new WorkLoggingConfiguration
                {
                    Level = LogLevel.Information,
                },
            });
        var overrides = new WorkerOptions(
            Configuration: WorkConfiguration.Default with
            {
                Start = WorkStartConfiguration.DoNotStart,
            });

        var merged = original.Merge(overrides);

        Assert.False(merged.ProfilingEnabled);
        Assert.False(merged.HasExplicitProfilingEnabled);
        Assert.Equal(WorkStartPolicy.DoNotStart, merged.Configuration?.Start.Policy);
        Assert.Equal(LogLevel.Information, merged.Configuration?.Logging.Level);
    }

    [Fact]
    public void WorkConfigurationMergeWithNullOverridesReturnsSameConfiguration()
    {
        var configuration = WorkConfiguration.Default;

        Assert.Same(configuration, configuration.MergeRuntimeOptions(null));
    }

    [Fact]
    public void WorkConfigurationRuntimeOptionsMergeUsesOverrideFacetsExceptInvocation()
    {
        var original = WorkConfiguration.Default with
        {
            Start = WorkStartConfiguration.DoNotStart,
            Logging = new WorkLoggingConfiguration
            {
                Level = LogLevel.Warning,
            },
            ExecutionDiagnostics = new WorkExecutionDiagnosticsPersistenceConfiguration
            {
                IsEnabled = true,
                Retention = TimeSpan.FromHours(2),
                MinimumLogLevel = LogLevel.Debug,
                ProfileCaptureMode = WorkProfileCaptureMode.Full,
            },
            ChildExecution = WorkChildExecutionConfiguration.Default.AllowAdditional("original.child"),
        };
        var overrides = WorkConfiguration.Default with
        {
            Start = new WorkStartConfiguration
            {
                Policy = WorkStartPolicy.StartAndReturnAfterCompleted,
            },
            Coordination = WorkCoordinationConfiguration.Default with
            {
                IsEnabled = true,
                Idempotency = new WorkIdempotencyConfiguration
                {
                    IsEnabled = true,
                },
                Concurrency = WorkConcurrencyConfiguration.Default with
                {
                    IsEnabled = true,
                    MaximumCapacity = 2,
                },
            },
            Recurrence = WorkRecurrenceConfiguration.Every(TimeSpan.FromMinutes(1)),
            TransientRetry = WorkTransientRetryConfiguration.Default with
            {
                Count = 2,
            },
            FailedWorker = new WorkFailedWorkerConfiguration
            {
                Handling = WorkFailedWorkerHandling.AutoCancel,
                AutoCancelAfter = TimeSpan.FromMinutes(3),
            },
            Logging = new WorkLoggingConfiguration
            {
                IsEnabled = false,
                Level = LogLevel.Error,
            },
            Retention = new WorkRetentionConfiguration
            {
                PurgeInterval = TimeSpan.FromSeconds(30),
            },
            Invocation = WorkInvocationConfiguration.Allow(WorkInvocationChannel.InProcess),
            ExecutionDiagnostics = new WorkExecutionDiagnosticsPersistenceConfiguration
            {
                IsEnabled = false,
            },
            ChildExecution = WorkChildExecutionConfiguration.Default.AllowAdditional("override.child"),
        };

        var merged = original.MergeRuntimeOptions(overrides);

        Assert.Equal(overrides.Start, merged.Start);
        Assert.Equal(overrides.Coordination, merged.Coordination);
        Assert.Equal(overrides.Recurrence, merged.Recurrence);
        Assert.Equal(overrides.TransientRetry, merged.TransientRetry);
        Assert.Equal(overrides.FailedWorker, merged.FailedWorker);
        Assert.Equal(overrides.Logging, merged.Logging);
        Assert.Equal(overrides.Retention, merged.Retention);
        Assert.Equal(original.ExecutionDiagnostics, merged.ExecutionDiagnostics);
        Assert.Equal(original.Invocation, merged.Invocation);
        Assert.Equal(original.ChildExecution, merged.ChildExecution);
    }

    [Fact]
    public void DefaultInvocationAllowsInProcessAndHttpApi()
    {
        Assert.True(WorkConfiguration.Default.Invocation.Allows(WorkInvocationChannel.InProcess));
        Assert.True(WorkConfiguration.Default.Invocation.Allows(WorkInvocationChannel.HttpApi));
        Assert.False(WorkConfiguration.Default.Invocation.Allows(WorkInvocationChannel.Mcp));
    }
}
