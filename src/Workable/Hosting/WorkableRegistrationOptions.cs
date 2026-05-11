namespace Workable;

internal sealed record WorkableRegistrationOptions(
    IReadOnlyList<WorkExceptionClassifier> ExceptionClassifiers);
