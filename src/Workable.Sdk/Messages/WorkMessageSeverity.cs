namespace Workable;

/// <summary>
/// Represents the severity of a structured <see cref="WorkMessage"/>.
/// </summary>
public enum WorkMessageSeverity
{
    /// <summary>
    /// Diagnostic trace detail.
    /// </summary>
    Trace,

    /// <summary>
    /// Diagnostic debug detail.
    /// </summary>
    Debug,

    /// <summary>
    /// Informational detail.
    /// </summary>
    Information,

    /// <summary>
    /// Alias for <see cref="Information"/>.
    /// </summary>
    Info = Information,

    /// <summary>
    /// A warning that may need attention.
    /// </summary>
    Warning,

    /// <summary>
    /// An error condition.
    /// </summary>
    Error,

    /// <summary>
    /// A critical error condition.
    /// </summary>
    Critical,
}
