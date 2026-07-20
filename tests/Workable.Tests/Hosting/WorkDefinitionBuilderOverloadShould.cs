using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Hosting")]
public sealed class WorkDefinitionBuilderOverloadShould
{
    [Fact]
    public void PreserveSharedDefaultsAcrossEveryDocumentedRegistrationShape()
    {
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder => builder
            .RequireAuthorization(false)
            .WithWorkDefaults(
                register: work =>
                {
                    work.AddWork(Definition("builder.raw"), Raw);
                    work.AddWork(Definition("builder.raw.config"), Raw, PerWorkConfiguration);
                    work.AddWork(Definition("builder.raw.authorized"), Raw, PerWorkConfiguration, PerWorkAuthorization);

                    work.AddWork<BuilderInput>(Definition("builder.input"), Typed);
                    work.AddWork<BuilderInput>(Definition("builder.input.config"), Typed, PerWorkConfiguration);
                    work.AddWork<BuilderInput>(Definition("builder.input.authorized"), Typed, PerWorkConfiguration, PerWorkAuthorization);

                    work.AddWork<BuilderInput, BuilderOutput>(Definition("builder.output"), TypedOutput);
                    work.AddWork<BuilderInput, BuilderOutput>(Definition("builder.output.config"), TypedOutput, PerWorkConfiguration);
                    work.AddWork<BuilderInput, BuilderOutput>(Definition("builder.output.authorized"), TypedOutput, PerWorkConfiguration, PerWorkAuthorization);

                    work.AddWork<BuilderExecutor>(Definition("builder.executor"));
                    work.AddWork<BuilderExecutor>(Definition("builder.executor.config"), PerWorkConfiguration);
                    work.AddWork<BuilderExecutor>(Definition("builder.executor.authorized"), PerWorkConfiguration, PerWorkAuthorization);

                    work.AddWork<AttributedBuilderExecutor>();
                    work.AddWork<ConfiguredAttributedBuilderExecutor>(PerWorkConfiguration);
                    work.AddWork<AuthorizedAttributedBuilderExecutor>(PerWorkConfiguration, PerWorkAuthorization);

                    work.WithWorkDefaults(
                        nested => nested.AddWork(Definition("builder.nested"), Raw),
                        configure => configure.ConfigureLogging(level: LogLevel.Critical, maximumBufferedEntries: 2),
                        authorize => authorize.AllowOperateToGroups("nested.operator"));
                },
                configure: configure => configure.ConfigureLogging(
                    level: LogLevel.Warning,
                    maximumBufferedEntries: 12),
                authorize: authorize => authorize.AllowOperateToGroups("default.operator")));

        using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<IWorkSystemRegistry>().Default.Catalog;

        Assert.Equal(16, catalog.Definitions.Count);
        Assert.All(
            catalog.Definitions.Where(definition =>
                !definition.Name.Contains(".config", StringComparison.Ordinal) &&
                !definition.Name.Contains(".authorized", StringComparison.Ordinal) &&
                definition.Name != "builder.nested"),
            definition =>
            {
                Assert.Equal(LogLevel.Warning, definition.Configuration.Logging.Level);
                Assert.Equal(12, definition.Configuration.Logging.MaximumBufferedEntries);
                Assert.Equal(["default.operator"], definition.Authorization.Operate.Groups);
            });

        Assert.True(catalog.TryGet("builder.raw.authorized", out var authorized));
        Assert.Equal(LogLevel.Error, authorized.Configuration.Logging.Level);
        Assert.Equal(3, authorized.Configuration.Logging.MaximumBufferedEntries);
        Assert.Equal(["work.operator"], authorized.Authorization.Operate.Groups);

        Assert.True(catalog.TryGet("builder.nested", out var nested));
        Assert.Equal(LogLevel.Critical, nested.Configuration.Logging.Level);
        Assert.Equal(2, nested.Configuration.Logging.MaximumBufferedEntries);
        Assert.Equal(["nested.operator"], nested.Authorization.Operate.Groups);
    }

    [Fact]
    public void RejectNullNestedRegistrationCallbacks()
    {
        var systemAdapter = new SystemWorkDefinitionBuilderAdapter(null!);
        var defaulting = new DefaultingWorkDefinitionBuilder(systemAdapter, null, null);

        Assert.Throws<ArgumentNullException>(() => systemAdapter.WithWorkDefaults(null!));
        Assert.Throws<ArgumentNullException>(() => defaulting.WithWorkDefaults(null!));
    }

    private static WorkDefinition Definition(string name) => WorkDefinition.Create(name, category: "Builder:Overloads");

    private static Task<WorkExecutionResult> Raw(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    private static Task<WorkExecutionResult> Typed(
        IWorkExecutionContext context,
        BuilderInput input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    private static Task<WorkExecutionResult<BuilderOutput>> TypedOutput(
        IWorkExecutionContext context,
        BuilderInput input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult<BuilderOutput>.Success(new BuilderOutput(input.Value)));

    private static void PerWorkConfiguration(IWorkConfigurationBuilder builder)
        => builder.ConfigureLogging(level: LogLevel.Error, maximumBufferedEntries: 3);

    private static void PerWorkAuthorization(IWorkAuthorizationBuilder builder)
        => builder.AllowOperateToGroups("work.operator");

    private sealed record BuilderInput(string Value);

    private sealed record BuilderOutput(string Value);

    private class BuilderExecutor : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            WorkInput? input,
            CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }

    [WorkMetadata("builder.attributed", "Builder:Overloads")]
    private sealed class AttributedBuilderExecutor : BuilderExecutor;

    [WorkMetadata("builder.attributed.config", "Builder:Overloads")]
    private sealed class ConfiguredAttributedBuilderExecutor : BuilderExecutor;

    [WorkMetadata("builder.attributed.authorized", "Builder:Overloads")]
    private sealed class AuthorizedAttributedBuilderExecutor : BuilderExecutor;
}
