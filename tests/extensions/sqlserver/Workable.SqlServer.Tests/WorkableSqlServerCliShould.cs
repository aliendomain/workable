namespace Workable.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class WorkableSqlServerCliShould
{
    private static readonly SemaphoreSlim ConsoleLock = new(1, 1);

    [Fact]
    public async Task ReturnFailureForUnknownCommand()
    {
        var result = await RunCli("unknown");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Unknown command 'unknown'.", result.Error);
        Assert.Contains("Workable SQL Server CLI", result.Output);
    }

    [Fact]
    public async Task GenerateSchemaScriptToStandardOutputWithoutDiscoveryInput()
    {
        var result = await RunCli("schema", "generate", "--schema", "custom");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        Assert.Contains("CREATE SCHEMA [custom]", result.Output);
        Assert.Contains("[custom].[WorkEntries]", result.Output);
    }

    [Fact]
    public async Task SkipGenerateWhenDiscoveryFindsNoPersistenceFeatures()
    {
        using var workspace = SqlServerCliTestWorkspace.Create();
        var projectPath = workspace.WriteProject("src/App/App.csproj");
        workspace.WriteFile("src/App/Program.cs", "namespace App;");

        var result = await RunCli("schema", "generate", "--project", projectPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains("Scanned 1 project(s)", result.Error);
        Assert.Contains("No Workable SQL Server persistence features were detected", result.Error);
    }

    [Fact]
    public async Task RequireConnectionStringBeforeApplyingDiscoveredSchema()
    {
        using var workspace = SqlServerCliTestWorkspace.Create();
        var projectPath = workspace.WriteProject("src/App/App.csproj");
        workspace.WriteFile("src/App/Program.cs", """
using Workable;

configuration => configuration.QueueDurably();
""");
        var previousConnectionString = Environment.GetEnvironmentVariable("WORKABLE_SQLSERVER_CONNECTION_STRING");
        Environment.SetEnvironmentVariable("WORKABLE_SQLSERVER_CONNECTION_STRING", null);
        try
        {
            var result = await RunCli("schema", "apply", "--project", projectPath);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("Usage:", result.Output);
            Assert.Contains("Scanned 1 project(s)", result.Error);
            Assert.Contains("Detected SQL Server persistence features: DurableQueue", result.Error);
            Assert.Contains("A connection string is required.", result.Error);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WORKABLE_SQLSERVER_CONNECTION_STRING", previousConnectionString);
        }
    }

    private static async Task<CliResult> RunCli(params string[] args)
    {
        await ConsoleLock.WaitAsync();
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await WorkableSqlServerCli.Run(args);

            return new CliResult(exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
            ConsoleLock.Release();
        }
    }

    private sealed record CliResult(int ExitCode, string Output, string Error);
}
