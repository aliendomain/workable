using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace Workable.SampleHost;

internal sealed class SampleSqlServerPersistenceTarget : IAsyncDisposable
{
    private const string ConnectionStringEnvironmentVariable = "WORKABLE_SAMPLE_SQLSERVER_CONNECTION_STRING";
    private const string ContainerRuntimeEnvironmentVariable = "WORKABLE_SAMPLE_SQLSERVER_CONTAINER_RUNTIME";
    private const string ContainerImageEnvironmentVariable = "WORKABLE_SAMPLE_SQLSERVER_CONTAINER_IMAGE";
    private const string ContainerReuseEnvironmentVariable = "WORKABLE_SAMPLE_SQLSERVER_CONTAINER_REUSE";
    private const string DefaultContainerImage = "mcr.microsoft.com/mssql/server:2022-latest";
    private const string DefaultContainerName = "workable-samplehost-sqlserver";
    private const string DefaultDatabaseName = "WorkableSampleHost";
    private const string DefaultLocalDbDataSource = @"(localdb)\MSSQLLocalDB";
    private const string DefaultSqlHost = "127.0.0.1";
    private const string DefaultSqlUser = "sa";
    private const string DefaultSqlPassword = "WorkableSampleHost_StrongPassword_123!";
    private static readonly TimeSpan LocalDbStartupTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SqlServerStartupTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SqlServerPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DatabaseReadyTimeout = TimeSpan.FromSeconds(30);

    private readonly ContainerRuntime? runtime;
    private readonly bool stopManagedContainerOnDispose;

    private SampleSqlServerPersistenceTarget(
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

    public static async Task<SampleSqlServerPersistenceTarget> Resolve(CancellationToken cancellationToken = default)
    {
        var explicitConnectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(explicitConnectionString))
        {
            var connectionString = NormalizeConnectionString(explicitConnectionString);
            await PrepareDatabase(connectionString, SqlServerStartupTimeout, cancellationToken);
            return new SampleSqlServerPersistenceTarget(
                connectionString,
                Describe($"SQL Server from ${ConnectionStringEnvironmentVariable}", connectionString),
                runtime: null,
                stopManagedContainerOnDispose: false);
        }

        var localDbTarget = await TryResolveLocalDb(cancellationToken);
        if (localDbTarget is not null)
        {
            return localDbTarget;
        }

        return await ResolveManagedContainer(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (!this.stopManagedContainerOnDispose || this.runtime is null)
        {
            return;
        }

        await this.runtime.RunChecked("stop", DefaultContainerName);
    }

    private static async Task<SampleSqlServerPersistenceTarget?> TryResolveLocalDb(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var connectionString = new SqlConnectionStringBuilder
        {
            DataSource = DefaultLocalDbDataSource,
            InitialCatalog = DefaultDatabaseName,
            IntegratedSecurity = true,
            TrustServerCertificate = true,
        }.ConnectionString;

        try
        {
            await PrepareDatabase(connectionString, LocalDbStartupTimeout, cancellationToken);
            return new SampleSqlServerPersistenceTarget(
                connectionString,
                Describe("SQL Server LocalDB", connectionString),
                runtime: null,
                stopManagedContainerOnDispose: false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or PlatformNotSupportedException or SqlException or TimeoutException)
        {
            return null;
        }
    }

    private static async Task<SampleSqlServerPersistenceTarget> ResolveManagedContainer(CancellationToken cancellationToken)
    {
        var runtime = await ContainerRuntime.Resolve();

        var reuseContainer = ReadBooleanEnvironmentVariable(ContainerReuseEnvironmentVariable, defaultValue: true);
        var imageName = Environment.GetEnvironmentVariable(ContainerImageEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(imageName))
        {
            imageName = DefaultContainerImage;
        }

        var status = await runtime.EnsureSqlServerContainer(DefaultContainerName, imageName, DefaultSqlPassword);
        var connectionString = CreateManagedContainerConnectionString(status.Host, status.Port, DefaultDatabaseName);
        await PrepareDatabase(connectionString, SqlServerStartupTimeout, cancellationToken);

        return new SampleSqlServerPersistenceTarget(
            connectionString,
            Describe($"{runtime.CommandName} container '{DefaultContainerName}' on {status.Host}:{status.Port}", connectionString),
            runtime,
            stopManagedContainerOnDispose: !reuseContainer && status.WasCreated);
    }

    private static string NormalizeConnectionString(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.InitialCatalog) && string.IsNullOrWhiteSpace(builder.AttachDBFilename))
        {
            builder.InitialCatalog = DefaultDatabaseName;
        }

        return builder.ConnectionString;
    }

    private static async Task PrepareDatabase(
        string connectionString,
        TimeSpan startupTimeout,
        CancellationToken cancellationToken)
    {
        if (await CanOpenConnection(connectionString, cancellationToken))
        {
            return;
        }

        if (HasAttachedDatabaseFile(connectionString))
        {
            await WaitForSqlServerAvailability(connectionString, startupTimeout, cancellationToken);
            return;
        }

        var masterConnectionString = BuildMasterConnectionString(connectionString);
        await WaitForSqlServerAvailability(masterConnectionString, startupTimeout, cancellationToken);

        var databaseName = GetDatabaseName(connectionString);
        if (!string.IsNullOrWhiteSpace(databaseName))
        {
            await EnsureDatabaseExists(masterConnectionString, databaseName, cancellationToken);
        }

        await WaitForSqlServerAvailability(connectionString, DatabaseReadyTimeout, cancellationToken);
    }

    private static async Task<bool> CanOpenConnection(string connectionString, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(CreateProbeConnectionString(connectionString));
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            await command.ExecuteScalarAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or SqlException)
        {
            return false;
        }
    }

    private static async Task WaitForSqlServerAvailability(
        string connectionString,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        Exception? lastException = null;
        var probeConnectionString = CreateProbeConnectionString(connectionString);

        while (Stopwatch.GetElapsedTime(startedAt) < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await using var connection = new SqlConnection(probeConnectionString);
                await connection.OpenAsync(cancellationToken);

                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1;";
                await command.ExecuteScalarAsync(cancellationToken);
                return;
            }
            catch (Exception exception) when (exception is InvalidOperationException or SqlException)
            {
                lastException = exception;
                await Task.Delay(SqlServerPollInterval, cancellationToken);
            }
        }

        throw new TimeoutException(
            "Timed out waiting for the sample host SQL Server persistence target to accept connections.",
            lastException);
    }

    private static async Task EnsureDatabaseExists(
        string masterConnectionString,
        string databaseName,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(CreateProbeConnectionString(masterConnectionString));
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
IF DB_ID(@DatabaseName) IS NULL
BEGIN
    DECLARE @CreateDatabaseSql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@DatabaseName) + N';';
    EXEC(@CreateDatabaseSql);
END
""";
        command.Parameters.AddWithValue("@DatabaseName", databaseName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string BuildMasterConnectionString(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = "master",
        };
        builder.AttachDBFilename = string.Empty;
        return builder.ConnectionString;
    }

    private static string CreateProbeConnectionString(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
        };

        if (builder.ConnectTimeout == 0 || builder.ConnectTimeout > 5)
        {
            builder.ConnectTimeout = 5;
        }

        return builder.ConnectionString;
    }

    private static string CreateManagedContainerConnectionString(string host, int port, string databaseName)
        => new SqlConnectionStringBuilder
        {
            DataSource = $"{host},{port}",
            UserID = DefaultSqlUser,
            Password = DefaultSqlPassword,
            InitialCatalog = databaseName,
            Encrypt = false,
            TrustServerCertificate = true,
            ConnectTimeout = 5,
            Pooling = false,
        }.ConnectionString;

    private static string Describe(string source, string connectionString)
    {
        var databaseName = GetDatabaseName(connectionString);
        return string.IsNullOrWhiteSpace(databaseName)
            ? source
            : $"{source} ({databaseName})";
    }

    private static string? GetDatabaseName(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (!string.IsNullOrWhiteSpace(builder.AttachDBFilename))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(builder.InitialCatalog)
            ? DefaultDatabaseName
            : builder.InitialCatalog;
    }

    private static bool HasAttachedDatabaseFile(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        return !string.IsNullOrWhiteSpace(builder.AttachDBFilename);
    }

    private static bool ReadBooleanEnvironmentVariable(string variableName, bool defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException(
            $"Environment variable '{variableName}' must be 'true' or 'false' when it is set.");
    }

    private sealed record ContainerStatus(string Host, int Port, bool WasCreated);

    private sealed class ContainerRuntime(string commandName)
    {
        public string CommandName { get; } = commandName;

        public static async Task<ContainerRuntime> Resolve()
        {
            var explicitRuntime = Environment.GetEnvironmentVariable(ContainerRuntimeEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(explicitRuntime))
            {
                var runtime = new ContainerRuntime(explicitRuntime);
                if (await runtime.IsAvailable())
                {
                    return runtime;
                }

                throw new InvalidOperationException(
                    $"The configured sample-host SQL Server container runtime '{explicitRuntime}' is not available.");
            }

            foreach (var candidate in new[] { "docker", "podman" })
            {
                var runtime = new ContainerRuntime(candidate);
                if (await runtime.IsAvailable())
                {
                    return runtime;
                }
            }

            throw new InvalidOperationException(
                $"Workable Sample Host durable SQL persistence requires SQL Server LocalDB, the '{ConnectionStringEnvironmentVariable}' environment variable, or a Docker-compatible runtime such as docker or podman.");
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
                    $"Unexpected port mapping '{mapping}' returned for sample-host SQL Server container '{containerName}'.");
            }

            var host = mapping[..separatorIndex].Trim().Trim('[', ']');
            if (host is "0.0.0.0" or "::" or "")
            {
                host = DefaultSqlHost;
            }

            if (!int.TryParse(mapping[(separatorIndex + 1)..], out var port))
            {
                throw new InvalidOperationException(
                    $"Unexpected port mapping '{mapping}' returned for sample-host SQL Server container '{containerName}'.");
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
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
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
