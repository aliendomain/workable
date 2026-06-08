namespace Workable;

/// <summary>
/// Describes the immediate result of a definition reconfiguration request.
/// </summary>
public enum WorkDefinitionReconfigurationStatus
{
    /// <summary>
    /// The reconfiguration request was accepted and applied.
    /// </summary>
    Accepted,
    /// <summary>
    /// The caller was not authorized to reconfigure the definition.
    /// </summary>
    Unauthorized,
    /// <summary>
    /// No matching definition was found.
    /// </summary>
    NotFound,
    /// <summary>
    /// The reconfiguration request was invalid.
    /// </summary>
    Invalid,
    /// <summary>
    /// The reconfiguration request conflicted with the supplied definition revision.
    /// </summary>
    Conflict,
}
