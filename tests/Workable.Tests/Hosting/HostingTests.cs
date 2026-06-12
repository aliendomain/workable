using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Hosting")]
public sealed class HostingTests
{
    [Fact]
    public void DefaultAndNamedSystemsCanBeRegisteredSideBySide()
    {
        var services = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(WorkDefinition.Create("default", "Default work."), SuccessfulWork))
            .AddWorkableSystem("background", builder => builder.AddWork(WorkDefinition.Create("background", "Background work."), SuccessfulWork));

        var registry = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>();

        Assert.Null(registry.Default.Name);
        Assert.True(registry.TryGet("background", out var background));
        Assert.Equal("background", background.Name);
        Assert.Equal(2, registry.Systems.Count);
    }

    [Fact]
    public void DefaultWorkSystemCanBeInjectedDirectly()
    {
        var services = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(WorkDefinition.Create("default", "Default work."), SuccessfulWork))
            .AddWorkableSystem("background", builder => builder.AddWork(WorkDefinition.Create("background", "Background work."), SuccessfulWork));

        var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkSystemRegistry>();
        var system = provider.GetRequiredService<IWorkSystem>();

        Assert.Same(registry.Default, system);
        Assert.Null(system.Name);
    }

    [Fact]
    public async Task WorkExecutionReceivesScopedServicesFromHostContainer()
    {
        var definition = WorkDefinition.Create("scoped", "Uses a scoped service.");
        var services = new ServiceCollection();
        services.AddScoped<ScopedMarker>();
        services.AddWorkableSystem(builder => builder.AddWork<ScopedExecutor>(definition));

        var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;

        await system.Start();

        var handle = await system.Queue.Enqueue("scoped");
        var completion = await handle.WaitForCompletion();

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.True(completion.Output?.ToValue<ScopedResult>()?.Resolved);
    }

    [Fact]
    public async Task WorkExecutionDisposesAsyncScopedServicesAfterExecution()
    {
        var definition = WorkDefinition.Create("async-scoped", "Disposes async scoped services.");
        var services = new ServiceCollection();
        services.AddSingleton<AsyncDisposeTracker>();
        services.AddScoped<AsyncScopedMarker>();
        services.AddWorkableSystem(builder => builder.AddWork<AsyncScopedExecutor>(definition));

        var provider = services.BuildServiceProvider();
        var tracker = provider.GetRequiredService<AsyncDisposeTracker>();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;

        await system.Start();

        var handle = await system.Queue.Enqueue("async-scoped");
        var completion = await handle.WaitForCompletion();

        Assert.True(completion.IsCompletedSuccessfully);
        await tracker.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task NonProductionHostEnvironmentEnablesProfilingByDefaultWhenWorkDoesNotSetIt()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(Environments.Development));
        services.AddWorkableSystem(builder => builder.AddWork(
            WorkDefinition.Create("profile.default.dev", "Uses implicit non-production profiling."),
            SuccessfulWork));

        await using var provider = services.BuildServiceProvider();
        await using var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var completion = await (await system.Queue.Enqueue("profile.default.dev")).WaitForCompletion();

        Assert.True(completion.IsCompletedSuccessfully);
        var worker = completion.Worker ?? throw new InvalidOperationException("Expected worker snapshot.");
        Assert.True(worker.Options.ProfilingEnabled);
        Assert.NotNull(worker.Profile);
    }

    [Fact]
    public async Task ExplicitProfilingDisableOverridesNonProductionDefault()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(Environments.Development));
        services.AddWorkableSystem(builder => builder.AddWork(
            WorkDefinition.Create(
                "profile.default.dev.off",
                "Explicitly disables profiling.",
                defaultOptions: new WorkerOptions(ProfilingEnabled: false)),
            SuccessfulWork));

        await using var provider = services.BuildServiceProvider();
        await using var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var completion = await (await system.Queue.Enqueue("profile.default.dev.off")).WaitForCompletion();

        Assert.True(completion.IsCompletedSuccessfully);
        var worker = completion.Worker ?? throw new InvalidOperationException("Expected worker snapshot.");
        Assert.False(worker.Options.ProfilingEnabled);
        Assert.Null(worker.Profile);
    }

    [Fact]
    public async Task ConfigurationOnlyDefaultOptionsStillInheritNonProductionProfilingDefault()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(Environments.Development));
        services.AddWorkableSystem(builder => builder.AddWork(
            WorkDefinition.Create(
                "profile.default.dev.configuration-only",
                "Uses configuration-only worker defaults.",
                defaultOptions: new WorkerOptions(
                    Configuration: WorkConfiguration.Default with
                    {
                        Start = WorkStartConfiguration.DoNotStart,
                    })),
            SuccessfulWork));

        await using var provider = services.BuildServiceProvider();
        await using var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var completion = await (await system.Queue.Enqueue(
            "profile.default.dev.configuration-only",
            options: new WorkerOptions(Configuration: WorkConfiguration.Default))).WaitForCompletion();

        Assert.True(completion.IsCompletedSuccessfully);
        var worker = completion.Worker ?? throw new InvalidOperationException("Expected worker snapshot.");
        Assert.True(worker.Options.ProfilingEnabled);
        Assert.NotNull(worker.Profile);
    }

    [Fact]
    public void WorkCanBeContributedWithoutConfiguringASystem()
    {
        var contributed = WorkDefinition.Create("feature.work", "Registered by a feature assembly.");

        var services = new ServiceCollection()
            .AddWorkableWork(contributed, SuccessfulWork)
            .AddWorkableSystem(builder => builder.StartWithHost());

        var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;

        Assert.True(system.Catalog.TryGet("feature.work", out var definition));
        Assert.Equal(contributed.Id, definition.Id);
    }

    [Fact]
    public void AttributeOnlyContributedWorkUsesMetadataForDefinition()
    {
        var services = new ServiceCollection()
            .AddWorkableWork<AttributedContributionWork>()
            .AddWorkableSystem(builder => builder.StartWithHost());

        var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;

        Assert.True(system.Catalog.TryGet("feature.attributed", out var definition));
        Assert.Equal("Features:Attributed", definition.Category);
        Assert.Equal("Registered entirely through attributes.", definition.Description);
        Assert.Equal(WorkStartPolicy.DoNotStart, definition.Configuration.Start.Policy);
    }

    [Fact]
    public void AttributeOnlySystemWorkUsesMetadataForDefinition()
    {
        var services = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork<AttributedSystemWork>());

        var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;

        Assert.True(system.Catalog.TryGet("system.attributed", out var definition));
        Assert.Equal("Systems:Attributed", definition.Category);
        Assert.Null(definition.Description);
    }

    [Fact]
    public void AttributeOnlyRegistrationRequiresWorkMetadata()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() => services.AddWorkableWork<MissingMetadataWork>());

        Assert.Contains(nameof(WorkMetadataAttribute), exception.Message);
    }

    [Fact]
    public void ContributionsCanTargetNamedSystems()
    {
        var shared = WorkDefinition.Create("shared.work", "Available everywhere.");
        var targeted = WorkDefinition.Create("targeted.work", "Available to one system.");

        var services = new ServiceCollection()
            .AddWorkableWork(shared, SuccessfulWork)
            .AddWorkableWork(targeted, SuccessfulWork, systemName: "remote")
            .AddWorkableSystem(builder => builder.StartWithHost())
            .AddWorkableSystem("remote", builder => builder.StartWithHost());

        var registry = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>();
        Assert.True(registry.TryGet("remote", out var remote));

        Assert.True(registry.Default.Catalog.TryGet("shared.work", out _));
        Assert.False(registry.Default.Catalog.TryGet("targeted.work", out _));
        Assert.True(remote.Catalog.TryGet("shared.work", out _));
        Assert.True(remote.Catalog.TryGet("targeted.work", out _));
    }

    [Fact]
    public void SystemsCanOptOutOfContributedWork()
    {
        var contributed = WorkDefinition.Create("feature.work", "Registered by a feature assembly.");

        var services = new ServiceCollection()
            .AddWorkableWork(contributed, SuccessfulWork)
            .AddWorkableSystem(builder => builder.IncludeContributedWork(false));

        var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;

        Assert.False(system.Catalog.TryGet("feature.work", out _));
    }

    [Fact]
    public void WorkDefaultsApplySharedConfigurationAndAuthorizationToGroupedRegistrations()
    {
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder
                .RequireAuthorization(false)
                .WithWorkDefaults(
                    register: work => work
                        .AddWork(WorkDefinition.Create("grouped.first"), SuccessfulWork)
                        .AddWork(WorkDefinition.Create("grouped.second"), SuccessfulWork),
                    configure: configure => configure.ConfigureLogging(level: LogLevel.Warning, maximumBufferedEntries: 12),
                    authorize: authorize => authorize.AllowOperateToGroups("grouped.admin")))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        Assert.True(system.Catalog.TryGet("grouped.first", out var first));
        Assert.True(system.Catalog.TryGet("grouped.second", out var second));
        Assert.Equal(LogLevel.Warning, first.Configuration.Logging.Level);
        Assert.Equal(12, first.Configuration.Logging.MaximumBufferedEntries);
        Assert.Equal(["grouped.admin"], first.Authorization.Operate.Groups.OrderBy(group => group).ToArray());
        Assert.Equal(LogLevel.Warning, second.Configuration.Logging.Level);
        Assert.Equal(12, second.Configuration.Logging.MaximumBufferedEntries);
        Assert.Equal(["grouped.admin"], second.Authorization.Operate.Groups.OrderBy(group => group).ToArray());
    }

    [Fact]
    public void WorkDefaultsCanBeOverriddenByIndividualRegistrations()
    {
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder
                .RequireAuthorization(false)
                .WithWorkDefaults(
                    register: work => work.AddWork(
                        WorkDefinition.Create("grouped.override"),
                        SuccessfulWork,
                        configure: configure => configure.ConfigureLogging(level: LogLevel.Error, maximumBufferedEntries: 3),
                        authorize: authorize => authorize.AllowOperateToGroups("grouped.support")),
                    configure: configure => configure.ConfigureLogging(level: LogLevel.Warning, maximumBufferedEntries: 12),
                    authorize: authorize => authorize.AllowOperateToGroups("grouped.admin")))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        Assert.True(system.Catalog.TryGet("grouped.override", out var definition));
        Assert.Equal(LogLevel.Error, definition.Configuration.Logging.Level);
        Assert.Equal(3, definition.Configuration.Logging.MaximumBufferedEntries);
        Assert.Equal(["grouped.support"], definition.Authorization.Operate.Groups.OrderBy(group => group).ToArray());
    }

    private static Task<WorkExecutionResult> SuccessfulWork(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    private sealed class ScopedMarker;

    private sealed record ScopedResult(bool Resolved);

    private sealed class ScopedExecutor(ScopedMarker marker) : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
        {
            var resolvedFromContext = context.Services.GetRequiredService<ScopedMarker>();
            return Task.FromResult(WorkExecutionResult.Success(WorkOutput.FromValue(new ScopedResult(ReferenceEquals(marker, resolvedFromContext)))));
        }
    }

    [WorkMetadata("feature.attributed", "Features:Attributed", "Registered entirely through attributes.")]
    [WorkStart(WorkStartPolicy.DoNotStart)]
    private sealed class AttributedContributionWork : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }

    [WorkMetadata("system.attributed", "Systems:Attributed")]
    private sealed class AttributedSystemWork : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }

    private sealed class MissingMetadataWork : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }

    private sealed class AsyncDisposeTracker
    {
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class AsyncScopedMarker(AsyncDisposeTracker tracker) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            tracker.Disposed.SetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AsyncScopedExecutor(AsyncScopedMarker marker) : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
        {
            _ = marker;
            return Task.FromResult(WorkExecutionResult.Success());
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
