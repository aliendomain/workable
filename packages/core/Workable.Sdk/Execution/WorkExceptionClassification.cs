namespace Workable;

/// <summary>
/// Represents how Workable classified an exception for retry behavior.
/// </summary>
public enum WorkExceptionClassification
{
    /// <summary>
    /// Workable could not determine whether the exception is transient.
    /// </summary>
    Unknown,

    /// <summary>
    /// The exception is classified as transient and may be retried when transient retry is enabled.
    /// </summary>
    Transient,

    /// <summary>
    /// The exception is classified as non-transient and should not be retried.
    /// </summary>
    NonTransient,
}
