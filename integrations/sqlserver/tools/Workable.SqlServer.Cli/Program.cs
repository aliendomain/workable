using Workable.SqlServer;

return await WorkableSqlServerCli.Run(args);

internal static class WorkableSqlServerCli
{
    public static async Task<int> Run(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            WriteHelp();
            return args.Length == 0 ? 1 : 0;
        }

        if (!string.Equals(args[0], "schema", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Unknown command '{args[0]}'.");
            WriteHelp();
            return 1;
        }

        if (args.Length < 2 || IsHelp(args[1]))
        {
            WriteSchemaHelp();
            return args.Length < 2 ? 1 : 0;
        }

        return args[1].ToLowerInvariant() switch
        {
            "generate" => await Generate(args[2..]),
            "apply" => await Apply(args[2..]),
            _ => UnknownSchemaCommand(args[1]),
        };
    }

    private static async Task<int> Generate(string[] args)
    {
        var options = ParseOptions(args);
        if (options.Help)
        {
            WriteGenerateHelp();
            return 0;
        }

        if (options.Error is not null)
        {
            Console.Error.WriteLine(options.Error);
            WriteGenerateHelp();
            return 1;
        }

        var discovery = await Discover(options);
        if (discovery is { RequiresSchema: false })
        {
            Console.Error.WriteLine("No Workable SQL Server persistence features were detected; no schema script is required.");
            return 0;
        }

        var schemaName = options.Value("schema") ?? "workable";
        var script = discovery is null
            ? WorkableSqlServerSchema.GenerateScript(schemaName)
            : GenerateDiscoveredScript(discovery, schemaName);
        var output = options.Value("output");
        if (string.IsNullOrWhiteSpace(output))
        {
            Console.WriteLine(script);
            return 0;
        }

        await File.WriteAllTextAsync(output, script + Environment.NewLine);
        Console.WriteLine($"Wrote Workable SQL Server schema script to {output}.");
        return 0;
    }

    private static async Task<int> Apply(string[] args)
    {
        var options = ParseOptions(args);
        if (options.Help)
        {
            WriteApplyHelp();
            return 0;
        }

        if (options.Error is not null)
        {
            Console.Error.WriteLine(options.Error);
            WriteApplyHelp();
            return 1;
        }

        var discovery = await Discover(options);
        if (discovery is { RequiresSchema: false })
        {
            Console.WriteLine("No Workable SQL Server persistence features were detected; no schema deployment is required.");
            return 0;
        }

        var schemaName = options.Value("schema") ?? "workable";
        var targets = BuildDeploymentTargets(options, discovery, schemaName);
        if (targets.Count == 0)
        {
            Console.Error.WriteLine("A connection string is required. Pass --connection-string, set WORKABLE_SQLSERVER_CONNECTION_STRING, or use a project with a literal AddWorkableSqlServerDurableQueue connection string.");
            WriteApplyHelp();
            return 1;
        }

        foreach (var target in targets)
        {
            await WorkableSqlServerSchema.Apply(target.ConnectionString, target.SchemaName);
            Console.WriteLine($"Applied Workable SQL Server schema to schema '{target.SchemaName}'.");
        }

        return 0;
    }

    private static async Task<WorkableSqlServerSchemaDiscoveryResult?> Discover(ParsedOptions options)
    {
        var solutionPaths = options.Values("solution");
        var projectPaths = options.Values("project");
        if (solutionPaths.Count == 0 && projectPaths.Count == 0)
        {
            return null;
        }

        var result = await WorkableSqlServerSchemaDiscovery.Discover(
            new WorkableSqlServerSchemaDiscoveryRequest(
                solutionPaths,
                projectPaths,
                IncludeTests: options.Has("include-tests")));

        WriteDiscoverySummary(result, Console.Error);
        return result;
    }

    private static string GenerateDiscoveredScript(
        WorkableSqlServerSchemaDiscoveryResult discovery,
        string defaultSchemaName)
    {
        var discoveredSchemas = discovery.Targets
            .Select(target => target.SchemaName)
            .Where(schema => !string.IsNullOrWhiteSpace(schema))
            .Select(schema => schema!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var schemaNames = discoveredSchemas.Length > 0
            ? discoveredSchemas
            : [defaultSchemaName];

        if (schemaNames.Length == 1)
        {
            return WorkableSqlServerSchema.GenerateScript(schemaNames[0]);
        }

        return string.Join(
            Environment.NewLine + Environment.NewLine,
            schemaNames.Select(schema => $"-- Workable SQL Server schema: {schema}{Environment.NewLine}{WorkableSqlServerSchema.GenerateScript(schema)}"));
    }

    private static IReadOnlyList<WorkableSqlServerSchemaDeploymentTarget> BuildDeploymentTargets(
        ParsedOptions options,
        WorkableSqlServerSchemaDiscoveryResult? discovery,
        string defaultSchemaName)
    {
        var targets = new List<WorkableSqlServerSchemaDeploymentTarget>();
        targets.AddRange(options
            .Values("connection-string")
            .Where(connectionString => !string.IsNullOrWhiteSpace(connectionString))
            .Select(connectionString => new WorkableSqlServerSchemaDeploymentTarget(connectionString, defaultSchemaName)));

        if (targets.Count == 0)
        {
            var environmentConnectionString = Environment.GetEnvironmentVariable("WORKABLE_SQLSERVER_CONNECTION_STRING");
            if (!string.IsNullOrWhiteSpace(environmentConnectionString))
            {
                targets.Add(new WorkableSqlServerSchemaDeploymentTarget(environmentConnectionString, defaultSchemaName));
            }
        }

        if (discovery is not null)
        {
            targets.AddRange(discovery.Targets
                .Where(target => !string.IsNullOrWhiteSpace(target.ConnectionString))
                .Select(target => new WorkableSqlServerSchemaDeploymentTarget(
                    target.ConnectionString!,
                    string.IsNullOrWhiteSpace(target.SchemaName) ? defaultSchemaName : target.SchemaName!)));
        }

        return targets
            .DistinctBy(target => string.Concat(target.ConnectionString, "\u001f", target.SchemaName), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void WriteDiscoverySummary(WorkableSqlServerSchemaDiscoveryResult discovery, TextWriter writer)
    {
        writer.WriteLine($"Scanned {discovery.ProjectsScanned} project(s) and {discovery.FilesScanned} C# file(s).");
        if (discovery.Features.Count > 0)
        {
            writer.WriteLine("Detected SQL Server persistence features: " + string.Join(", ", discovery.Features.Select(feature => feature.Feature).Distinct()));
        }

        if (discovery.Targets.Count > 0)
        {
            writer.WriteLine($"Detected {discovery.Targets.Count} SQL Server registration target(s).");
        }
    }

    private static int UnknownSchemaCommand(string command)
    {
        Console.Error.WriteLine($"Unknown schema command '{command}'.");
        WriteSchemaHelp();
        return 1;
    }

    private static ParsedOptions ParseOptions(string[] args)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            var current = args[index];
            if (IsHelp(current))
            {
                return new ParsedOptions(values, flags, Help: true, Error: null);
            }

            var option = NormalizeOption(current);
            if (option is null)
            {
                return new ParsedOptions(values, flags, Help: false, Error: $"Unexpected argument '{current}'.");
            }

            if (!option.RequiresValue)
            {
                flags.Add(option.Name);
                continue;
            }

            if (index + 1 >= args.Length || NormalizeOption(args[index + 1]) is not null)
            {
                return new ParsedOptions(values, flags, Help: false, Error: $"Option '{current}' requires a value.");
            }

            if (!values.TryGetValue(option.Name, out var optionValues))
            {
                optionValues = [];
                values[option.Name] = optionValues;
            }

            optionValues.Add(args[++index]);
        }

        return new ParsedOptions(values, flags, Help: false, Error: null);
    }

    private static OptionDefinition? NormalizeOption(string value)
        => value switch
        {
            "--schema" or "-s" => new OptionDefinition("schema", RequiresValue: true),
            "--output" or "-o" => new OptionDefinition("output", RequiresValue: true),
            "--connection-string" or "-c" => new OptionDefinition("connection-string", RequiresValue: true),
            "--solution" => new OptionDefinition("solution", RequiresValue: true),
            "--project" => new OptionDefinition("project", RequiresValue: true),
            "--include-tests" => new OptionDefinition("include-tests", RequiresValue: false),
            _ => null,
        };

    private static bool IsHelp(string value)
        => value is "-h" or "--help" or "help";

    private static void WriteHelp()
    {
        Console.WriteLine("""
Workable SQL Server CLI

Usage:
  workable-sqlserver schema generate [--schema <name>] [--output <path>]
  workable-sqlserver schema generate --solution <path> [--project <path>] [--schema <name>] [--output <path>]
  workable-sqlserver schema apply --connection-string <connection-string> [--schema <name>]
  workable-sqlserver schema apply --solution <path> --connection-string <connection-string> [--schema <name>]

Commands:
  schema generate   Generate the SQL script required by Workable.SqlServer.
  schema apply      Apply the SQL schema directly to a SQL Server database.
""");
    }

    private static void WriteSchemaHelp()
    {
        Console.WriteLine("""
Usage:
  workable-sqlserver schema generate [--schema <name>] [--output <path>]
  workable-sqlserver schema generate --solution <path> [--project <path>] [--schema <name>] [--output <path>]
  workable-sqlserver schema apply --connection-string <connection-string> [--schema <name>]
  workable-sqlserver schema apply --solution <path> --connection-string <connection-string> [--schema <name>]
""");
    }

    private static void WriteGenerateHelp()
    {
        Console.WriteLine("""
Usage:
  workable-sqlserver schema generate [--schema <name>] [--output <path>]
  workable-sqlserver schema generate --solution <path> [--project <path>] [--schema <name>] [--output <path>]

Options:
  -s, --schema <name>   SQL schema name. Defaults to workable.
  -o, --output <path>   Write the script to a file. Defaults to stdout.
      --solution <path> Scan a solution for Workable SQL Server persistence features.
      --project <path>  Scan a project for Workable SQL Server persistence features. Can be repeated.
      --include-tests   Include test projects when scanning a solution.
""");
    }

    private static void WriteApplyHelp()
    {
        Console.WriteLine("""
Usage:
  workable-sqlserver schema apply --connection-string <connection-string> [--schema <name>]
  workable-sqlserver schema apply --solution <path> --connection-string <connection-string> [--schema <name>]

Options:
  -c, --connection-string <connection-string>   Target SQL Server connection string. Can be repeated.
  -s, --schema <name>                           SQL schema name. Defaults to workable.
      --solution <path>                         Scan a solution for Workable SQL Server persistence features.
      --project <path>                          Scan a project for Workable SQL Server persistence features. Can be repeated.
      --include-tests                           Include test projects when scanning a solution.

Environment:
  WORKABLE_SQLSERVER_CONNECTION_STRING can be used instead of --connection-string.
""");
    }

    private sealed record ParsedOptions(
        IReadOnlyDictionary<string, List<string>> OptionValues,
        IReadOnlySet<string> Flags,
        bool Help,
        string? Error)
    {
        public string? Value(string name)
            => this.OptionValues.TryGetValue(name, out var values) ? values[^1] : null;

        public IReadOnlyList<string> Values(string name)
            => this.OptionValues.TryGetValue(name, out var values) ? values : [];

        public bool Has(string name)
            => this.Flags.Contains(name);
    }

    private sealed record OptionDefinition(string Name, bool RequiresValue);

    private sealed record WorkableSqlServerSchemaDeploymentTarget(string ConnectionString, string SchemaName);
}
