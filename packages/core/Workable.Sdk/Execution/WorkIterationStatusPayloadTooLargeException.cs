namespace Workable;

/// <summary>
/// Indicates that an iteration status payload exceeds the configured per-item size limit.
/// </summary>
public sealed class WorkIterationStatusPayloadTooLargeException : InvalidOperationException
{
    /// <summary>
    /// Creates an iteration status payload-size exception.
    /// </summary>
    public WorkIterationStatusPayloadTooLargeException(int payloadBytes, int maximumPayloadBytes)
        : base(
            $"The iteration status payload is {payloadBytes} UTF-8 JSON bytes, which exceeds the configured " +
            $"maximum of {maximumPayloadBytes} bytes.")
    {
        this.PayloadBytes = payloadBytes;
        this.MaximumPayloadBytes = maximumPayloadBytes;
    }

    /// <summary>Gets the serialized UTF-8 JSON payload size.</summary>
    public int PayloadBytes { get; }

    /// <summary>Gets the configured maximum serialized payload size.</summary>
    public int MaximumPayloadBytes { get; }
}
