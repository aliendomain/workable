namespace Workable;

internal sealed record WorkRegistrationConfiguration(
    WorkDefinition Definition,
    WorkOperateAuthorizationConfiguration OperateAuthorization,
    IReadOnlyList<WorkExceptionClassifier> ExceptionClassifiers,
    IReadOnlyList<WorkAutomaticStartRegistration> AutomaticStarts,
    IReadOnlyList<WorkInitializationRegistration> Initializers)
{
    public IReadOnlyList<Type> InitializerTypes { get; } =
        [.. Initializers.Select(initializer => initializer.InitializerType).Distinct()];
}
