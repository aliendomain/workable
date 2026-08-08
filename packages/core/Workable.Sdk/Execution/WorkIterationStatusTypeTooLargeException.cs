namespace Workable;

/// <summary>
/// Indicates that an iteration status type exceeds the configured size limit.
/// </summary>
public sealed class WorkIterationStatusTypeTooLargeException : InvalidOperationException
{
    /// <summary>
    /// Creates an iteration status type-size exception.
    /// </summary>
    public WorkIterationStatusTypeTooLargeException(int typeBytes, int maximumTypeBytes)
        : base(
            $"The iteration status type is {typeBytes} UTF-8 bytes, which exceeds the configured " +
            $"maximum of {maximumTypeBytes} bytes.")
    {
        this.TypeBytes = typeBytes;
        this.MaximumTypeBytes = maximumTypeBytes;
    }

    /// <summary>Gets the UTF-8 status type size.</summary>
    public int TypeBytes { get; }

    /// <summary>Gets the configured maximum status type size.</summary>
    public int MaximumTypeBytes { get; }
}
