using System.Text.Json.Serialization;

namespace Workable;

/// <summary>
/// Configures the coordination features that control duplicate prevention, shared capacity, and durable queue behavior.
/// </summary>
/// <remarks>
/// Coordination acts as the parent switch for idempotency, concurrency, and durability. When
/// <see cref="IsEnabled"/> is <see langword="false"/>, those nested features are treated as disabled even if their
/// individual configuration objects are populated.
/// </remarks>
public sealed record WorkCoordinationConfiguration
{
    /// <summary>
    /// Gets the default coordination configuration with all coordination features disabled.
    /// </summary>
    public static WorkCoordinationConfiguration Default { get; } = new();

    /// <summary>
    /// Gets a value indicating whether coordination features are enabled for the definition or worker.
    /// </summary>
    public bool IsEnabled { get; init; }

    /// <summary>
    /// Gets the storage model used for enabled coordination features.
    /// </summary>
    public WorkCoordinationStorage Storage { get; init; } = WorkCoordinationStorage.Local;

    /// <summary>
    /// Gets the duplicate-prevention settings keyed by work definition and subject.
    /// </summary>
    public WorkIdempotencyConfiguration Idempotency { get; init; } = WorkIdempotencyConfiguration.Default;

    /// <summary>
    /// Gets the shared-capacity settings for this definition or worker.
    /// </summary>
    public WorkConcurrencyConfiguration Concurrency { get; init; } = WorkConcurrencyConfiguration.Default;

    /// <summary>
    /// Gets the durable queue and durable completion settings.
    /// </summary>
    public WorkQueueDurabilityConfiguration Durability { get; init; } = WorkQueueDurabilityConfiguration.Default;

    /// <summary>
    /// Gets a value indicating whether enabled coordination uses a persistent store instead of in-memory state.
    /// </summary>
    [JsonIgnore]
    public bool UsesPersistentStorage => this.IsEnabled && this.Storage == WorkCoordinationStorage.Persistent;

    /// <summary>
    /// Gets a value indicating whether idempotency is effectively enabled.
    /// </summary>
    [JsonIgnore]
    public bool IsIdempotencyEnabled => this.IsEnabled && this.Idempotency.IsEnabled;

    /// <summary>
    /// Gets a value indicating whether idempotency is enabled and backed by persistent storage.
    /// </summary>
    [JsonIgnore]
    public bool IsPersistentIdempotencyEnabled => this.IsIdempotencyEnabled && this.UsesPersistentStorage;

    /// <summary>
    /// Gets a value indicating whether concurrency is effectively enabled.
    /// </summary>
    [JsonIgnore]
    public bool IsConcurrencyEnabled => this.IsEnabled && this.Concurrency.IsEnabled;

    /// <summary>
    /// Gets a value indicating whether concurrency is enabled and backed by persistent storage.
    /// </summary>
    [JsonIgnore]
    public bool IsPersistentConcurrencyEnabled => this.IsConcurrencyEnabled && this.UsesPersistentStorage;

    /// <summary>
    /// Gets a value indicating whether durable queue behavior is effectively enabled.
    /// </summary>
    [JsonIgnore]
    public bool IsDurabilityEnabled => this.IsEnabled && this.Durability.IsEnabled;

    /// <summary>
    /// Gets a value indicating whether the configuration requires a registered persistence store.
    /// </summary>
    [JsonIgnore]
    public bool RequiresPersistenceStore => this.UsesPersistentStorage;
}
