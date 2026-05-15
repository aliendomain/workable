namespace Workable;

public sealed record WorkableHttpDefinitionReconfigurationRequest(
    long Revision,
    WorkDefinitionReconfiguration Changes);
