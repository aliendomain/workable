namespace Workable;

/// <summary>
/// Provides the reserved instrumentation keys emitted by Workable profile nodes.
/// </summary>
public static class WorkProfileInstrumentation
{
    /// <summary>
    /// Identifies explicit application profiling nodes.
    /// </summary>
    public const string Application = "application";

    /// <summary>
    /// Identifies outbound <see cref="System.Net.Http.HttpClient"/> profiling nodes.
    /// </summary>
    public const string HttpClient = "http.client";

    /// <summary>
    /// Identifies Microsoft.Data.SqlClient profiling nodes.
    /// </summary>
    public const string SqlClient = "sql.client";

    /// <summary>
    /// Identifies profile diagnostics emitted by Workable itself.
    /// </summary>
    public const string WorkableProfiling = "workable.profiling";
}
