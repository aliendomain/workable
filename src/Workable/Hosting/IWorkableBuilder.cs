namespace Workable;

public interface IWorkableBuilder
{
    IWorkableBuilder ClassifyExceptions(WorkExceptionClassifier classifier);
}
