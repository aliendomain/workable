namespace Workable;

/// <summary>
/// Thrown when a system requires an authorized session but none was supplied.
/// </summary>
public sealed class WorkSystemAuthorizationRequiredException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WorkSystemAuthorizationRequiredException"/> class.
    /// </summary>
    /// <param name="systemId">The identifier of the affected system.</param>
    /// <param name="systemName">The configured system name, when one exists.</param>
    public WorkSystemAuthorizationRequiredException(WorkSystemId systemId, string? systemName)
        : base(CreateMessage(systemId, systemName))
    {
        this.SystemId = systemId;
        this.SystemName = systemName;
    }

    /// <summary>
    /// Gets the identifier of the affected system.
    /// </summary>
    public WorkSystemId SystemId { get; }

    /// <summary>
    /// Gets the configured system name, when one exists.
    /// </summary>
    public string? SystemName { get; }

    private static string CreateMessage(WorkSystemId systemId, string? systemName)
        => string.IsNullOrWhiteSpace(systemName)
            ? $"Workable system '{systemId}' requires an authorized session."
            : $"Workable system '{systemName}' ({systemId}) requires an authorized session.";
}
