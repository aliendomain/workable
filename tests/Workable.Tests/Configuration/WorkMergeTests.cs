using Microsoft.Extensions.Logging;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Configuration")]
public sealed class WorkMergeTests
{
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
        };

        var merged = original.MergeRuntimeOptions(overrides);

        Assert.Equal(overrides.Start, merged.Start);
        Assert.Equal(overrides.Coordination, merged.Coordination);
        Assert.Equal(overrides.Recurrence, merged.Recurrence);
        Assert.Equal(overrides.TransientRetry, merged.TransientRetry);
        Assert.Equal(overrides.Logging, merged.Logging);
        Assert.Equal(overrides.Retention, merged.Retention);
        Assert.Equal(original.Invocation, merged.Invocation);
    }

    [Fact]
    public void DefaultInvocationAllowsInProcessAndHttpApi()
    {
        Assert.True(WorkConfiguration.Default.Invocation.Allows(WorkInvocationChannel.InProcess));
        Assert.True(WorkConfiguration.Default.Invocation.Allows(WorkInvocationChannel.HttpApi));
        Assert.False(WorkConfiguration.Default.Invocation.Allows(WorkInvocationChannel.Mcp));
    }
}
