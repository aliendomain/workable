namespace Workable;

/// <summary>
/// Configures host-wide Workable behavior that applies across registered systems.
/// </summary>
public interface IWorkableBuilder
{
    /// <summary>
    /// Adds an exception classifier that Workable evaluates when a work execution throws.
    /// </summary>
    /// <param name="classifier">
    /// A delegate that inspects a thrown exception and returns the classification Workable should use
    /// when recording the failure and deciding whether it is transient.
    /// </param>
    /// <returns>The same builder so additional host-wide options can be chained.</returns>
    IWorkableBuilder ClassifyExceptions(WorkExceptionClassifier classifier);
}
