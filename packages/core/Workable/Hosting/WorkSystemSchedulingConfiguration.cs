namespace Workable;

/// <summary>
/// Configures runtime scheduling for one work system.
/// </summary>
public sealed record WorkSystemSchedulingConfiguration
{
    internal static readonly TimeSpan DefaultMinimumInterval = TimeSpan.FromMinutes(1);
    internal const long MinimumOccurrencePayloadBytes = 2;
    public static readonly TimeSpan MinimumHistoryRetention = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaximumHistoryRetention = TimeSpan.FromDays(7);
    public const int DefaultMaximumActiveSchedules = 1_000;
    public const int DefaultMaximumActiveSchedulesPerDefinition = 100;
    public const int DefaultMaximumActiveSchedulesPerActor = 250;
    public const int DefaultMaximumRetainedSchedules = 10_000;
    public const int DefaultMaximumRetainedSchedulesPerActor = 2_000;
    public const long DefaultMaximumSchedulePayloadBytes = 1_048_576;
    public const long DefaultMaximumRetainedPayloadBytes = 268_435_456;
    public const long DefaultMaximumRetainedPayloadBytesPerActor = 67_108_864;
    public const int DefaultMaximumRetainedOccurrences = 100_000;
    public const long DefaultMaximumOccurrencePayloadBytes = 65_536;
    public const long DefaultMaximumRetainedOccurrencePayloadBytes = 67_108_864;
    public const int DefaultMaximumDispatchesPerBatch = 25;
    public const long DefaultMaximumClaimedPayloadBytesPerBatch = 8_388_608;
    public const long DefaultMaximumOccurrenceQueryPayloadBytes = 4_194_304;

    public static WorkSystemSchedulingConfiguration Default { get; } = new();

    /// <summary>
    /// Gets whether runtime-created schedules are enabled for this system.
    /// </summary>
    public bool IsEnabled { get; init; }

    /// <summary>
    /// Gets how long dispatch occurrences and terminal schedule records are retained.
    /// </summary>
    public TimeSpan HistoryRetention { get; init; } = TimeSpan.FromDays(1);

    /// <summary>
    /// Gets the maximum number of active runtime schedules allowed in this work system.
    /// </summary>
    public int MaximumActiveSchedules { get; init; } = DefaultMaximumActiveSchedules;

    /// <summary>
    /// Gets the maximum number of active runtime schedules allowed for one work definition.
    /// </summary>
    public int MaximumActiveSchedulesPerDefinition { get; init; } = DefaultMaximumActiveSchedulesPerDefinition;

    /// <summary>
    /// Gets the maximum number of active runtime schedules allowed for one creator id.
    /// </summary>
    public int MaximumActiveSchedulesPerActor { get; init; } = DefaultMaximumActiveSchedulesPerActor;

    /// <summary>
    /// Gets the maximum number of active and terminal schedule records retained for this work system.
    /// </summary>
    public int MaximumRetainedSchedules { get; init; } = DefaultMaximumRetainedSchedules;

    /// <summary>
    /// Gets the maximum number of active and terminal schedule records retained for one creator id.
    /// </summary>
    public int MaximumRetainedSchedulesPerActor { get; init; } = DefaultMaximumRetainedSchedulesPerActor;

    /// <summary>
    /// Gets the maximum serialized size, in bytes, accepted for one schedule.
    /// </summary>
    public long MaximumSchedulePayloadBytes { get; init; } = DefaultMaximumSchedulePayloadBytes;

    /// <summary>
    /// Gets the maximum aggregate serialized schedule size, in bytes, retained for this work system.
    /// </summary>
    public long MaximumRetainedPayloadBytes { get; init; } = DefaultMaximumRetainedPayloadBytes;

    /// <summary>
    /// Gets the maximum aggregate serialized schedule size, in bytes, retained for one creator id.
    /// </summary>
    public long MaximumRetainedPayloadBytesPerActor { get; init; } = DefaultMaximumRetainedPayloadBytesPerActor;

    /// <summary>
    /// Gets the maximum number of dispatch occurrences retained for this work system.
    /// </summary>
    public int MaximumRetainedOccurrences { get; init; } = DefaultMaximumRetainedOccurrences;

    /// <summary>
    /// Gets the maximum serialized message size retained for one dispatch occurrence. The minimum is two bytes,
    /// which accommodates an empty JSON array when larger messages must be omitted.
    /// </summary>
    public long MaximumOccurrencePayloadBytes { get; init; } = DefaultMaximumOccurrencePayloadBytes;

    /// <summary>
    /// Gets the maximum aggregate serialized occurrence-message size retained for this work system.
    /// </summary>
    public long MaximumRetainedOccurrencePayloadBytes { get; init; } = DefaultMaximumRetainedOccurrencePayloadBytes;

    /// <summary>
    /// Gets the maximum number of due schedules one scheduler host may claim and dispatch concurrently in one batch.
    /// </summary>
    public int MaximumDispatchesPerBatch { get; init; } = DefaultMaximumDispatchesPerBatch;

    /// <summary>
    /// Gets the maximum aggregate serialized schedule payload one scheduler host may claim in one batch.
    /// </summary>
    public long MaximumClaimedPayloadBytesPerBatch { get; init; } = DefaultMaximumClaimedPayloadBytesPerBatch;

    /// <summary>
    /// Gets the maximum aggregate occurrence-message payload returned by one history query.
    /// </summary>
    public long MaximumOccurrenceQueryPayloadBytes { get; init; } = DefaultMaximumOccurrenceQueryPayloadBytes;

    internal TimeSpan MinimumInterval { get; init; } = DefaultMinimumInterval;
}
