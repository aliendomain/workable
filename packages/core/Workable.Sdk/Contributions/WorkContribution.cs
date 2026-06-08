namespace Workable;
internal sealed record WorkContribution(
    WorkDefinition Definition,
    string? SystemName,
    Func<IServiceProvider, IWorkExecutor> ExecutorFactory,
    IReadOnlyList<WorkExceptionClassifier> ExceptionClassifiers,
    IReadOnlyList<WorkAutomaticStartRegistration> AutomaticStarts,
    IReadOnlyList<WorkInitializationRegistration> Initializers);
