namespace Workable;

/// <summary>
/// Identifies where a work definition's authorization requirement came from.
/// </summary>
public enum WorkAuthorizationRegistrationSource
{
    /// <summary>
    /// No authorization requirement was configured.
    /// </summary>
    None,

    /// <summary>
    /// The requirement came from <see cref="WorkAuthorizationAttribute"/>.
    /// </summary>
    Attribute,

    /// <summary>
    /// The requirement came from fluent registration.
    /// </summary>
    Fluent,
}
