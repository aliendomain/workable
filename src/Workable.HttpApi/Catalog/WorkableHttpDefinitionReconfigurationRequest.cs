namespace Workable;

/// <summary>
/// Represents the HTTP request body used to reconfigure a definition's default settings.
/// </summary>
/// <param name="Revision">The expected current definition revision used for optimistic concurrency.</param>
/// <param name="Changes">The default-option and configuration changes to apply for future queued workers.</param>
public sealed record WorkableHttpDefinitionReconfigurationRequest(
    long Revision,
    WorkDefinitionReconfiguration Changes);
