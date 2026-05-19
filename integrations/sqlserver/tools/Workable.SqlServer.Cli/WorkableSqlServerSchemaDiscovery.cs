using System.Text.RegularExpressions;
using System.Xml.Linq;

internal static partial class WorkableSqlServerSchemaDiscovery
{
    private const string SqlServerRegistrationMethod = "AddWorkableSqlServerDurableQueue";

    public static async Task<WorkableSqlServerSchemaDiscoveryResult> Discover(
        WorkableSqlServerSchemaDiscoveryRequest request)
    {
        var projects = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var solutionPath in request.SolutionPaths)
        {
            foreach (var projectPath in LoadSolutionProjects(solutionPath, request.IncludeTests))
            {
                projects.Add(projectPath);
            }
        }

        foreach (var projectPath in request.ProjectPaths)
        {
            var resolvedProjectPath = ResolvePath(projectPath, Directory.GetCurrentDirectory());
            projects.Add(resolvedProjectPath);
        }

        var features = new List<WorkableSqlServerSchemaFeatureDiscovery>();
        var targets = new List<WorkableSqlServerSchemaTargetDiscovery>();
        var filesScanned = 0;

        foreach (var projectPath in projects)
        {
            var projectDirectory = Path.GetDirectoryName(projectPath)
                ?? throw new InvalidOperationException($"Could not determine project directory for '{projectPath}'.");

            foreach (var sourcePath in EnumerateSourceFiles(projectDirectory))
            {
                filesScanned++;
                var source = await File.ReadAllTextAsync(sourcePath);
                DiscoverFeatures(projectPath, sourcePath, source, features);
                DiscoverTargets(projectPath, sourcePath, source, targets);
            }
        }

        return new WorkableSqlServerSchemaDiscoveryResult(
            ProjectsScanned: projects.Count,
            FilesScanned: filesScanned,
            Features: features,
            Targets: targets);
    }

    private static IEnumerable<string> LoadSolutionProjects(string solutionPath, bool includeTests)
    {
        var resolvedSolutionPath = ResolvePath(solutionPath, Directory.GetCurrentDirectory());
        var solutionDirectory = Path.GetDirectoryName(resolvedSolutionPath)
            ?? Directory.GetCurrentDirectory();
        var document = XDocument.Load(resolvedSolutionPath);

        return document
            .Descendants("Project")
            .Select(project => project.Attribute("Path")?.Value)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => ResolvePath(path!, solutionDirectory))
            .Where(path => includeTests || !IsTestProject(path));
    }

    private static IEnumerable<string> EnumerateSourceFiles(string projectDirectory)
        => Directory
            .EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsGeneratedPath(path));

    private static bool IsGeneratedPath(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTestProject(string projectPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(projectPath);
        if (fileName.Contains(".Test", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains(".Tests", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var segments = projectPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment => segment.Equals("test", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("tests", StringComparison.OrdinalIgnoreCase));
    }

    private static void DiscoverFeatures(
        string projectPath,
        string sourcePath,
        string source,
        List<WorkableSqlServerSchemaFeatureDiscovery> features)
    {
        var code = MaskCSharpTrivia(source);
        if (ContainsDurableQueueConfiguration(code))
        {
            features.Add(new WorkableSqlServerSchemaFeatureDiscovery(
                WorkableSqlServerSchemaFeature.DurableQueue,
                projectPath,
                sourcePath));
        }

        if (ContainsPersistenceBackedIdempotencyConfiguration(code))
        {
            features.Add(new WorkableSqlServerSchemaFeatureDiscovery(
                WorkableSqlServerSchemaFeature.PersistenceBackedIdempotency,
                projectPath,
                sourcePath));
        }

        if (ContainsPersistenceBackedConcurrencyConfiguration(code))
        {
            features.Add(new WorkableSqlServerSchemaFeatureDiscovery(
                WorkableSqlServerSchemaFeature.PersistenceBackedConcurrency,
                projectPath,
                sourcePath));
        }
    }

    private static bool ContainsDurableQueueConfiguration(string source)
        => source.Contains(".QueueDurably(", StringComparison.Ordinal)
            || source.Contains("[WorkQueueDurability", StringComparison.Ordinal)
            || ContainsInWindow(
                source,
                "UseQueueDurability(new WorkQueueDurabilityConfiguration",
                "IsEnabled = true",
                maximumWindowLength: 600);

    private static bool ContainsPersistenceBackedIdempotencyConfiguration(string source)
        => source.Contains(".CoordinatePersistently(", StringComparison.Ordinal)
            || ContainsInWindow(
                source,
                "UseCoordination(new WorkCoordinationConfiguration",
                "Storage = WorkCoordinationStorage.Persistent",
                maximumWindowLength: 600)
            || ContainsInWindow(
                source,
                "new WorkCoordinationConfiguration",
                "Storage = WorkCoordinationStorage.Persistent",
                maximumWindowLength: 600);

    private static bool ContainsPersistenceBackedConcurrencyConfiguration(string source)
        => source.Contains(".CoordinatePersistently(", StringComparison.Ordinal)
            || ContainsInWindow(
                source,
                "UseCoordination(new WorkCoordinationConfiguration",
                "Storage = WorkCoordinationStorage.Persistent",
                maximumWindowLength: 800)
            || ContainsInWindow(
                source,
                "new WorkCoordinationConfiguration",
                "Storage = WorkCoordinationStorage.Persistent",
                maximumWindowLength: 800);

    private static bool ContainsInWindow(
        string source,
        string marker,
        string required,
        int maximumWindowLength)
    {
        var searchIndex = 0;
        while (searchIndex < source.Length)
        {
            var markerIndex = source.IndexOf(marker, searchIndex, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                return false;
            }

            var windowLength = Math.Min(maximumWindowLength, source.Length - markerIndex);
            if (source.AsSpan(markerIndex, windowLength).IndexOf(required, StringComparison.Ordinal) >= 0)
            {
                return true;
            }

            searchIndex = markerIndex + marker.Length;
        }

        return false;
    }

    private static string MaskCSharpTrivia(string source)
    {
        var chars = source.ToCharArray();
        for (var index = 0; index < chars.Length; index++)
        {
            var current = chars[index];
            var next = index + 1 < chars.Length ? chars[index + 1] : '\0';
            var previous = index > 0 ? chars[index - 1] : '\0';

            if (current == '/' && next == '/')
            {
                chars[index++] = ' ';
                chars[index] = ' ';
                while (index + 1 < chars.Length && chars[index + 1] is not '\r' and not '\n')
                {
                    chars[++index] = ' ';
                }

                continue;
            }

            if (current == '/' && next == '*')
            {
                chars[index++] = ' ';
                chars[index] = ' ';
                while (index + 1 < chars.Length)
                {
                    current = chars[++index];
                    next = index + 1 < chars.Length ? chars[index + 1] : '\0';
                    chars[index] = ' ';
                    if (current == '*' && next == '/')
                    {
                        chars[++index] = ' ';
                        break;
                    }
                }

                continue;
            }

            if (current != '"')
            {
                continue;
            }

            var isVerbatim = previous == '@';
            chars[index] = ' ';
            while (index + 1 < chars.Length)
            {
                current = chars[++index];
                next = index + 1 < chars.Length ? chars[index + 1] : '\0';
                chars[index] = ' ';

                if (isVerbatim && current == '"' && next == '"')
                {
                    chars[++index] = ' ';
                    continue;
                }

                if (current == '"' && (isVerbatim || previous != '\\'))
                {
                    break;
                }

                previous = current;
            }
        }

        return new string(chars);
    }

    private static void DiscoverTargets(
        string projectPath,
        string sourcePath,
        string source,
        List<WorkableSqlServerSchemaTargetDiscovery> targets)
    {
        var code = MaskCSharpTrivia(source);
        var searchIndex = 0;
        while (searchIndex < code.Length)
        {
            var methodIndex = code.IndexOf(SqlServerRegistrationMethod, searchIndex, StringComparison.Ordinal);
            if (methodIndex < 0)
            {
                return;
            }

            searchIndex = methodIndex + SqlServerRegistrationMethod.Length;
            if (!TryReadInvocationArguments(source, searchIndex, out var invocation, out var endIndex))
            {
                continue;
            }

            searchIndex = endIndex;
            var arguments = SplitTopLevelArguments(invocation);
            var connectionString = arguments.Count > 0
                ? TryReadStringLiteral(arguments[0])
                : null;
            var schemaName = TryReadNamedStringArgument(arguments, "schemaName")
                ?? (arguments.Count > 1 ? TryReadStringLiteral(arguments[1]) : null);

            if (connectionString is null && schemaName is null)
            {
                continue;
            }

            targets.Add(new WorkableSqlServerSchemaTargetDiscovery(
                connectionString,
                schemaName,
                projectPath,
                sourcePath));
        }
    }

    private static bool TryReadInvocationArguments(
        string source,
        int startIndex,
        out string invocation,
        out int endIndex)
    {
        invocation = string.Empty;
        endIndex = startIndex;
        var openParenIndex = source.IndexOf('(', startIndex);
        if (openParenIndex < 0)
        {
            return false;
        }

        var depth = 0;
        var inString = false;
        var inVerbatimString = false;
        for (var index = openParenIndex; index < source.Length; index++)
        {
            var current = source[index];
            var previous = index > 0 ? source[index - 1] : '\0';

            if (inString)
            {
                if (inVerbatimString)
                {
                    if (current == '"' && index + 1 < source.Length && source[index + 1] == '"')
                    {
                        index++;
                        continue;
                    }

                    if (current == '"')
                    {
                        inString = false;
                        inVerbatimString = false;
                    }
                }
                else if (current == '"' && previous != '\\')
                {
                    inString = false;
                }

                continue;
            }

            if (current == '"')
            {
                inString = true;
                inVerbatimString = previous == '@';
                continue;
            }

            if (current == '(')
            {
                depth++;
                continue;
            }

            if (current == ')')
            {
                depth--;
                if (depth == 0)
                {
                    invocation = source[(openParenIndex + 1)..index];
                    endIndex = index + 1;
                    return true;
                }
            }
        }

        return false;
    }

    private static IReadOnlyList<string> SplitTopLevelArguments(string invocation)
    {
        var arguments = new List<string>();
        var start = 0;
        var depth = 0;
        var inString = false;
        var inVerbatimString = false;

        for (var index = 0; index < invocation.Length; index++)
        {
            var current = invocation[index];
            var previous = index > 0 ? invocation[index - 1] : '\0';

            if (inString)
            {
                if (inVerbatimString)
                {
                    if (current == '"' && index + 1 < invocation.Length && invocation[index + 1] == '"')
                    {
                        index++;
                        continue;
                    }

                    if (current == '"')
                    {
                        inString = false;
                        inVerbatimString = false;
                    }
                }
                else if (current == '"' && previous != '\\')
                {
                    inString = false;
                }

                continue;
            }

            if (current == '"')
            {
                inString = true;
                inVerbatimString = previous == '@';
                continue;
            }

            if (current is '(' or '[' or '{')
            {
                depth++;
                continue;
            }

            if (current is ')' or ']' or '}')
            {
                depth--;
                continue;
            }

            if (current == ',' && depth == 0)
            {
                arguments.Add(invocation[start..index].Trim());
                start = index + 1;
            }
        }

        var finalArgument = invocation[start..].Trim();
        if (!string.IsNullOrWhiteSpace(finalArgument))
        {
            arguments.Add(finalArgument);
        }

        return arguments;
    }

    private static string? TryReadNamedStringArgument(IReadOnlyList<string> arguments, string name)
    {
        var prefix = name + ":";
        var argument = arguments.FirstOrDefault(argument => argument.TrimStart().StartsWith(prefix, StringComparison.Ordinal));
        return argument is null
            ? null
            : TryReadStringLiteral(argument[(argument.IndexOf(':') + 1)..].Trim());
    }

    private static string? TryReadStringLiteral(string value)
    {
        value = value.Trim();
        if (value.Length < 2 || value.StartsWith('$'))
        {
            return null;
        }

        if (value.StartsWith("@\"", StringComparison.Ordinal) && value.EndsWith('"'))
        {
            return value[2..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
        }

        if (!value.StartsWith('"') || !value.EndsWith('"'))
        {
            return null;
        }

        return StringEscapeRegex().Replace(value[1..^1], match => match.Value switch
        {
            "\\\\" => "\\",
            "\\\"" => "\"",
            "\\n" => "\n",
            "\\r" => "\r",
            "\\t" => "\t",
            _ => match.Value,
        });
    }

    private static string ResolvePath(string path, string basePath)
        => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(basePath, path));

    [GeneratedRegex("""\\[\\\"nrt]""")]
    private static partial Regex StringEscapeRegex();
}

internal sealed record WorkableSqlServerSchemaDiscoveryRequest(
    IReadOnlyList<string> SolutionPaths,
    IReadOnlyList<string> ProjectPaths,
    bool IncludeTests);

internal sealed record WorkableSqlServerSchemaDiscoveryResult(
    int ProjectsScanned,
    int FilesScanned,
    IReadOnlyList<WorkableSqlServerSchemaFeatureDiscovery> Features,
    IReadOnlyList<WorkableSqlServerSchemaTargetDiscovery> Targets)
{
    public bool RequiresSchema => this.Features.Count > 0;
}

internal sealed record WorkableSqlServerSchemaFeatureDiscovery(
    WorkableSqlServerSchemaFeature Feature,
    string ProjectPath,
    string SourcePath);

internal sealed record WorkableSqlServerSchemaTargetDiscovery(
    string? ConnectionString,
    string? SchemaName,
    string ProjectPath,
    string SourcePath);

internal enum WorkableSqlServerSchemaFeature
{
    DurableQueue,
    PersistenceBackedIdempotency,
    PersistenceBackedConcurrency,
}
