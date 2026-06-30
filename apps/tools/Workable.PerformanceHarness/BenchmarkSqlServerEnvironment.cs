using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Workable.SqlServer;

namespace Workable.PerformanceHarness;

internal sealed class BenchmarkSqlServerEnvironment : IAsyncDisposable
{
    private const string ConnectionStringEnvironmentVariable = "WORKABLE_SQLSERVER_TEST_CONNECTION_STRING";
    private const string ContainerRuntimeEnvironmentVariable = "WORKABLE_SQLSERVER_TEST_CONTAINER_RUNTIME";
    private const string ContainerImageEnvironmentVariable = "WORKABLE_SQLSERVER_TEST_CONTAINER_IMAGE";
    private const string ContainerReuseEnvironmentVariable = "WORKABLE_SQLSERVER_TEST_CONTAINER_REUSE";
    private const string DefaultContainerImage = "mcr.microsoft.com/mssql/server:2022-latest";
    private const string DefaultContainerName = "workable-sqlserver-tests";
    private const string DefaultSqlHost = "127.0.0.1";
    private const string DefaultSqlUser = "sa";
    private const string DefaultSqlPassword = "WorkableTests_StrongPassword_123!";
    private static readonly TimeSpan SqlServerStartupTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SqlServerPollInterval = TimeSpan.FromSeconds(1);
    private static readonly Lazy<Task<BenchmarkSqlServerEnvironment>> Shared = new(CreateCore);

    private readonly ContainerRuntime? runtime;
    private readonly bool stopManagedContainerOnDispose;

    private BenchmarkSqlServerEnvironment(
        string connectionString,
        string description,
        ContainerRuntime? runtime,
        bool stopManagedContainerOnDispose)
    {
        this.ConnectionString = connectionString;
        this.Description = description;
        this.runtime = runtime;
        this.stopManagedContainerOnDispose = stopManagedContainerOnDispose;
    }

    public string ConnectionString { get; }

    public string Description { get; }

    public static Task<BenchmarkSqlServerEnvironment> GetShared()
        => Shared.Value;

    public static async Task PrepareSchema(
        string connectionString,
        string schemaName,
        bool resetStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);

        await EnsureDatabase(connectionString, cancellationToken);
        await WorkableSqlServerSchema.Apply(
            connectionString,
            schemaName,
            cancellationToken: cancellationToken);

        if (!resetStore)
        {
            return;
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
DELETE FROM {QuoteIdentifier(schemaName)}.[WorkflowRuns];
DELETE FROM {QuoteIdentifier(schemaName)}.[WorkQueueEntries];
DELETE FROM {QuoteIdentifier(schemaName)}.[WorkEntries];
""";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task PrepareSchema(string schemaName, bool resetStore, CancellationToken cancellationToken = default)
    {
        await PrepareSchema(
            this.ConnectionString,
            schemaName,
            resetStore,
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (!this.stopManagedContainerOnDispose || this.runtime is null)
        {
            return;
        }

        await this.runtime.RunChecked("stop", DefaultContainerName);
    }

    private static async Task<BenchmarkSqlServerEnvironment> CreateCore()
    {
        var explicitConnectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(explicitConnectionString))
        {
            await WaitForSqlServerAvailability(explicitConnectionString);
            return new BenchmarkSqlServerEnvironment(
                explicitConnectionString,
                $"SQL Server from ${ConnectionStringEnvironmentVariable}",
                runtime: null,
                stopManagedContainerOnDispose: false);
        }

        var runtime = await ContainerRuntime.Resolve(Environment.GetEnvironmentVariable(ContainerRuntimeEnvironmentVariable));
        var reuseContainer = ReadBooleanEnvironmentVariable(ContainerReuseEnvironmentVariable, defaultValue: true);
        var imageName = Environment.GetEnvironmentVariable(ContainerImageEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(imageName))
        {
            imageName = DefaultContainerImage;
        }

        var status = await runtime.EnsureSqlServerContainer(DefaultContainerName, imageName, DefaultSqlPassword);
        var connectionString = CreateManagedContainerConnectionString(status.Host, status.Port);
        await WaitForSqlServerAvailability(connectionString);
        return new BenchmarkSqlServerEnvironment(
            connectionString,
            $"{runtime.CommandName} container '{DefaultContainerName}' on {status.Host}:{status.Port}",
            runtime,
            stopManagedContainerOnDispose: !reuseContainer && status.WasCreated);
    }

    private static async Task EnsureDatabase(string connectionString, CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
        {
            return;
        }

        var databaseName = builder.InitialCatalog;
        builder.InitialCatalog = "master";
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
IF DB_ID(N'{EscapeLiteral(databaseName)}') IS NULL
BEGIN
    CREATE DATABASE {QuoteIdentifier(databaseName)};
END
""";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task WaitForSqlServerAvailability(string connectionString)
    {
        var availabilityConnectionString = CreateAvailabilityConnectionString(connectionString);
        var startedAt = Stopwatch.GetTimestamp();
        Exception? lastException = null;

        while (Stopwatch.GetElapsedTime(startedAt) < SqlServerStartupTimeout)
        {
            try
            {
                await using var connection = new SqlConnection(availabilityConnectionString);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1;";
                await command.ExecuteScalarAsync();
                return;
            }
            catch (Exception exception) when (exception is InvalidOperationException or SqlException)
            {
                lastException = exception;
                await Task.Delay(SqlServerPollInterval);
            }
        }

        throw new TimeoutException(
            "Timed out waiting for the benchmark SQL Server host to accept connections.",
            lastException);
    }

    private static string CreateAvailabilityConnectionString(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
        {
            return connectionString;
        }

        builder.InitialCatalog = "master";
        return builder.ConnectionString;
    }

    private static string CreateManagedContainerConnectionString(string host, int port)
        => new SqlConnectionStringBuilder
        {
            DataSource = $"{host},{port}",
            UserID = DefaultSqlUser,
            Password = DefaultSqlPassword,
            InitialCatalog = "WorkablePerformanceHarness",
            Encrypt = false,
            TrustServerCertificate = true,
            ConnectTimeout = 5,
            Pooling = false,
        }.ConnectionString;

    private static bool ReadBooleanEnvironmentVariable(string variableName, bool defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return bool.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"Environment variable '{variableName}' must be 'true' or 'false' when it is set.");
    }

    private static string QuoteIdentifier(string identifier)
        => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static string EscapeLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private sealed record ContainerStatus(string Host, int Port, bool WasCreated);

    private sealed class ContainerRuntime(string commandName)
    {
        public string CommandName { get; } = commandName;

        public static async Task<ContainerRuntime> Resolve(string? configuredRuntime)
        {
            if (!string.IsNullOrWhiteSpace(configuredRuntime))
            {
                var runtime = new ContainerRuntime(configuredRuntime);
                if (await runtime.IsAvailable())
                {
                    return runtime;
                }

                throw new InvalidOperationException(
                    $"The configured benchmark SQL Server container runtime '{configuredRuntime}' is not available.");
            }

            var candidates = new[] { "docker", "podman" }
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
                .Select(candidate => new ContainerRuntime(candidate));
            var availableRuntimes = new List<ContainerRuntime>();
            foreach (var runtime in candidates)
            {
                if (await runtime.IsAvailable())
                {
                    availableRuntimes.Add(runtime);
                }
            }

            if (availableRuntimes.Count > 0)
            {
                return availableRuntimes[0];
            }

            throw new InvalidOperationException(
                $"Durable benchmarks require either the '{ConnectionStringEnvironmentVariable}' environment variable or a Docker-compatible runtime such as docker or podman.");
        }

        public async Task<ContainerStatus> EnsureSqlServerContainer(string containerName, string imageName, string password)
        {
            var inspect = await this.TryRun("inspect", "--format", "{{.State.Status}}", containerName);
            if (inspect.ExitCode != 0)
            {
                await this.RunChecked(
                    "run",
                    "--detach",
                    "--name",
                    containerName,
                    "--platform",
                    "linux/amd64",
                    "--publish",
                    "127.0.0.1::1433",
                    "--env",
                    "ACCEPT_EULA=Y",
                    "--env",
                    "MSSQL_PID=Developer",
                    "--env",
                    $"MSSQL_SA_PASSWORD={password}",
                    imageName);

                var createdPort = await this.ReadPublishedPort(containerName);
                return new ContainerStatus(createdPort.Host, createdPort.Port, WasCreated: true);
            }

            var status = inspect.StandardOutput.Trim();
            if (!status.Equals("running", StringComparison.OrdinalIgnoreCase))
            {
                await this.RunChecked("start", containerName);
            }

            var publishedPort = await this.ReadPublishedPort(containerName);
            return new ContainerStatus(publishedPort.Host, publishedPort.Port, WasCreated: false);
        }

        public Task<ProcessResult> RunChecked(params string[] arguments)
            => this.Run(requireSuccess: true, arguments);

        private async Task<bool> IsAvailable()
        {
            var result = await this.TryRun("--version");
            return result.ExitCode == 0;
        }

        private async Task<(string Host, int Port)> ReadPublishedPort(string containerName)
        {
            var result = await this.RunChecked("port", containerName, "1433/tcp");
            var lines = result.StandardOutput
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var mapping = lines.FirstOrDefault(line => line.Contains(':', StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    $"Could not determine the published SQL Server port for container '{containerName}'.");

            var separatorIndex = mapping.LastIndexOf(':');
            if (separatorIndex < 0 || separatorIndex == mapping.Length - 1)
            {
                throw new InvalidOperationException(
                    $"Unexpected port mapping '{mapping}' returned for benchmark SQL Server container '{containerName}'.");
            }

            var host = mapping[..separatorIndex].Trim().Trim('[', ']');
            if (host is "0.0.0.0" or "::" or "")
            {
                host = DefaultSqlHost;
            }

            if (!int.TryParse(mapping[(separatorIndex + 1)..], out var port))
            {
                throw new InvalidOperationException(
                    $"Unexpected port mapping '{mapping}' returned for benchmark SQL Server container '{containerName}'.");
            }

            return (host, port);
        }

        private Task<ProcessResult> TryRun(params string[] arguments)
            => this.Run(requireSuccess: false, arguments);

        private async Task<ProcessResult> Run(bool requireSuccess, params string[] arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = this.CommandName,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            try
            {
                using var process = new Process
                {
                    StartInfo = startInfo,
                };

                process.Start();
                var standardOutput = process.StandardOutput.ReadToEndAsync();
                var standardError = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                var result = new ProcessResult(
                    process.ExitCode,
                    (await standardOutput).Trim(),
                    (await standardError).Trim());
                if (requireSuccess && result.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"Command '{this.CommandName} {string.Join(' ', arguments)}' failed with exit code {result.ExitCode}: {result.StandardError}");
                }

                return result;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                if (requireSuccess)
                {
                    throw;
                }

                return new ProcessResult(-1, string.Empty, exception.Message);
            }
        }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
