namespace Workable;

public sealed class WorkSystemAuthorizationRequiredException : InvalidOperationException
{
    public WorkSystemAuthorizationRequiredException(WorkSystemId systemId, string? systemName)
        : base(CreateMessage(systemId, systemName))
    {
        this.SystemId = systemId;
        this.SystemName = systemName;
    }

    public WorkSystemId SystemId { get; }

    public string? SystemName { get; }

    private static string CreateMessage(WorkSystemId systemId, string? systemName)
        => string.IsNullOrWhiteSpace(systemName)
            ? $"Workable system '{systemId}' requires an authorized session."
            : $"Workable system '{systemName}' ({systemId}) requires an authorized session.";
}
