namespace Workable;

internal sealed record WorkAuthorizationRegistration(
    WorkDefinitionAuthorization DefinitionAuthorization,
    WorkOperateAuthorizationConfiguration OperateAuthorization);
