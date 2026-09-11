namespace Workable;

/// <summary>
/// Identifies one runtime-created work schedule.
/// </summary>
/// <param name="Value">The underlying GUID value.</param>
public readonly record struct WorkScheduleId(Guid Value)
{
    /// <summary>
    /// Creates a new unique schedule identifier.
    /// </summary>
    public static WorkScheduleId New() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => this.Value.ToString("D");
}
