namespace Workable;

internal sealed class WorkableBuilder : IWorkableBuilder
{
    private readonly List<WorkExceptionClassifier> exceptionClassifiers = [];

    public IWorkableBuilder ClassifyExceptions(WorkExceptionClassifier classifier)
    {
        ArgumentNullException.ThrowIfNull(classifier);

        this.exceptionClassifiers.Add(classifier);
        return this;
    }

    public WorkableRegistrationOptions Build()
        => new([.. this.exceptionClassifiers]);
}
