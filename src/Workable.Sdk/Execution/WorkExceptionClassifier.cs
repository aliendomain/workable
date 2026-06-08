namespace Workable;

/// <summary>
/// Classifies an exception thrown during work execution so Workable can apply the appropriate failure handling.
/// </summary>
/// <param name="exception">The exception raised by the work executor or initializer.</param>
/// <returns>
/// The classification that tells Workable whether the failure should be treated as transient, terminal,
/// or otherwise specially handled.
/// </returns>
public delegate WorkExceptionClassification WorkExceptionClassifier(Exception exception);
