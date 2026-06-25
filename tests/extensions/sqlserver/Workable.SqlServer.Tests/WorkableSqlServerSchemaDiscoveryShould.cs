namespace Workable.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class WorkableSqlServerSchemaDiscoveryShould
{
    [Fact]
    public async Task DiscoverConfiguredSqlServerPersistenceFeaturesAndTargets()
    {
        using var workspace = SqlServerCliTestWorkspace.Create();
        var projectPath = workspace.WriteProject("src/App/App.csproj");
        workspace.WriteFile("src/App/Program.cs", """
using Microsoft.Extensions.DependencyInjection;
using Workable;
using Workable.SqlServer;

var services = new ServiceCollection();
services.AddWorkableSqlServerDurableQueue(
    "Server=(localdb)\\MSSQLLocalDB;Database=Workable;Integrated Security=true",
    schemaName: "ops");
services.AddWorkableSystem(builder => builder.AddWork(
    WorkDefinition.Create("sample", "Sample work."),
    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
    configuration => configuration.CoordinatePersistently().QueueDurably()));
""");

        var result = await WorkableSqlServerSchemaDiscovery.Discover(new WorkableSqlServerSchemaDiscoveryRequest(
            SolutionPaths: [],
            ProjectPaths: [projectPath],
            IncludeTests: false));

        Assert.True(result.RequiresSchema);
        Assert.Equal(1, result.ProjectsScanned);
        Assert.Equal(1, result.FilesScanned);
        Assert.Contains(result.Features, feature => feature.Feature == WorkableSqlServerSchemaFeature.DurableQueue);
        Assert.Contains(result.Features, feature => feature.Feature == WorkableSqlServerSchemaFeature.PersistenceBackedIdempotency);
        Assert.Contains(result.Features, feature => feature.Feature == WorkableSqlServerSchemaFeature.PersistenceBackedConcurrency);
        var target = Assert.Single(result.Targets);
        Assert.Equal(@"Server=(localdb)\MSSQLLocalDB;Database=Workable;Integrated Security=true", target.ConnectionString);
        Assert.Equal("ops", target.SchemaName);
    }

    [Fact]
    public async Task IgnorePersistenceReferencesInsideCommentsAndStringLiterals()
    {
        using var workspace = SqlServerCliTestWorkspace.Create();
        var projectPath = workspace.WriteProject("src/App/App.csproj");
        workspace.WriteFile("src/App/Program.cs", """
using Microsoft.Extensions.DependencyInjection;

var ignoredString = ".QueueDurably().CoordinatePersistently().AddWorkableSqlServerDurableQueue(\"fake\", schemaName: \"ignored\")";
// services.AddWorkableSqlServerDurableQueue("comment", schemaName: "commented");
/*
builder.AddWork(
    WorkDefinition.Create("sample", "Sample work."),
    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
    configuration => configuration.CoordinatePersistently().QueueDurably());
*/
""");

        var result = await WorkableSqlServerSchemaDiscovery.Discover(new WorkableSqlServerSchemaDiscoveryRequest(
            SolutionPaths: [],
            ProjectPaths: [projectPath],
            IncludeTests: false));

        Assert.False(result.RequiresSchema);
        Assert.Empty(result.Features);
        Assert.Empty(result.Targets);
    }

    [Fact]
    public async Task ExcludeTestProjectsFromSolutionDiscoveryByDefault()
    {
        using var workspace = SqlServerCliTestWorkspace.Create();
        workspace.WriteProject("src/App/App.csproj");
        workspace.WriteFile("src/App/Program.cs", "namespace App;");
        workspace.WriteProject("tests/App.Tests/App.Tests.csproj");
        workspace.WriteFile("tests/App.Tests/DurableTests.cs", """
using Microsoft.Extensions.DependencyInjection;
using Workable.SqlServer;

new ServiceCollection().AddWorkableSqlServerDurableQueue("Server=test;", "testschema");
configuration => configuration.QueueDurably();
""");
        var solutionPath = workspace.WriteSolution("""
<Solution>
  <Project Path="src/App/App.csproj" />
  <Project Path="tests/App.Tests/App.Tests.csproj" />
</Solution>
""");

        var withoutTests = await WorkableSqlServerSchemaDiscovery.Discover(new WorkableSqlServerSchemaDiscoveryRequest(
            SolutionPaths: [solutionPath],
            ProjectPaths: [],
            IncludeTests: false));
        var withTests = await WorkableSqlServerSchemaDiscovery.Discover(new WorkableSqlServerSchemaDiscoveryRequest(
            SolutionPaths: [solutionPath],
            ProjectPaths: [],
            IncludeTests: true));

        Assert.Equal(1, withoutTests.ProjectsScanned);
        Assert.False(withoutTests.RequiresSchema);
        Assert.Empty(withoutTests.Targets);
        Assert.Equal(2, withTests.ProjectsScanned);
        Assert.True(withTests.RequiresSchema);
        var target = Assert.Single(withTests.Targets);
        Assert.Equal("Server=test;", target.ConnectionString);
        Assert.Equal("testschema", target.SchemaName);
    }

    [Fact]
    public async Task DiscoverDurableWorkflowConfigurationAsRequiringSqlSchema()
    {
        using var workspace = SqlServerCliTestWorkspace.Create();
        var projectPath = workspace.WriteProject("src/App/App.csproj");
        workspace.WriteFile("src/App/Program.cs", """
using Microsoft.Extensions.DependencyInjection;
using Workable;
using Workable.SqlServer;

var services = new ServiceCollection();
services.AddWorkableSqlServerDurableQueue(
    "Server=(localdb)\\MSSQLLocalDB;Database=Workable;Integrated Security=true",
    schemaName: "ops");
services.AddWorkableSystem("workflow-tests", builder =>
{
    builder.AddWork(
        WorkDefinition.Create("sample.dispatch"),
        (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));
    builder.AddWorkflow(
        WorkflowDefinition.Create(
            "workflow.durable",
            coordination: WorkflowCoordinationConfiguration.Durable),
        workflow => workflow.DispatchWork("dispatch", "sample.dispatch"));
});
""");

        var result = await WorkableSqlServerSchemaDiscovery.Discover(new WorkableSqlServerSchemaDiscoveryRequest(
            SolutionPaths: [],
            ProjectPaths: [projectPath],
            IncludeTests: false));

        Assert.True(result.RequiresSchema);
        Assert.Contains(result.Features, feature => feature.Feature == WorkableSqlServerSchemaFeature.DurableWorkflow);
        var target = Assert.Single(result.Targets);
        Assert.Equal(@"Server=(localdb)\MSSQLLocalDB;Database=Workable;Integrated Security=true", target.ConnectionString);
        Assert.Equal("ops", target.SchemaName);
    }

}
