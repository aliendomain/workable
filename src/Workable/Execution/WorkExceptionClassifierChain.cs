using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Workable;

internal sealed class WorkExceptionClassifierChain(
    IReadOnlyList<WorkExceptionClassifier> systemClassifiers,
    IReadOnlyList<WorkExceptionClassifier> globalClassifiers,
    ILogger? logger)
{
    public WorkExceptionClassification Classify(RegisteredWork work, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(exception);

        var workClassification = Classify(work.ExceptionClassifiers, exception);
        if (workClassification != WorkExceptionClassification.Unknown)
        {
            return workClassification;
        }

        var systemClassification = Classify(systemClassifiers, exception);
        if (systemClassification != WorkExceptionClassification.Unknown)
        {
            return systemClassification;
        }

        return Classify(globalClassifiers, exception);
    }

    private WorkExceptionClassification Classify(IReadOnlyList<WorkExceptionClassifier> classifiers, Exception exception)
        => classifiers
            .Select(classifier => this.ClassifySafely(classifier, exception))
            .FirstOrDefault(
                classification => classification != WorkExceptionClassification.Unknown,
                WorkExceptionClassification.Unknown);

    private WorkExceptionClassification ClassifySafely(WorkExceptionClassifier classifier, Exception exception)
    {
        try
        {
            return (WorkExceptionClassification)classifier.DynamicInvoke(exception)!;
        }
        catch (TargetInvocationException classifierException)
        {
            logger?.LogWarning(
                classifierException.InnerException ?? classifierException,
                "A Workable exception classifier failed while classifying an execution exception.");
            return WorkExceptionClassification.Unknown;
        }
    }
}
