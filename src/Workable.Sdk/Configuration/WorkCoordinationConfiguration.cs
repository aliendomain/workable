using System.Text.Json.Serialization;

namespace Workable;

public sealed record WorkCoordinationConfiguration
{
    public static WorkCoordinationConfiguration Default { get; } = new();

    public bool IsEnabled { get; init; }

    public WorkCoordinationStorage Storage { get; init; } = WorkCoordinationStorage.Local;

    public WorkIdempotencyConfiguration Idempotency { get; init; } = WorkIdempotencyConfiguration.Default;

    public WorkConcurrencyConfiguration Concurrency { get; init; } = WorkConcurrencyConfiguration.Default;

    public WorkQueueDurabilityConfiguration Durability { get; init; } = WorkQueueDurabilityConfiguration.Default;

    [JsonIgnore]
    public bool UsesPersistentStorage => this.IsEnabled && this.Storage == WorkCoordinationStorage.Persistent;

    [JsonIgnore]
    public bool IsIdempotencyEnabled => this.IsEnabled && this.Idempotency.IsEnabled;

    [JsonIgnore]
    public bool IsPersistentIdempotencyEnabled => this.IsIdempotencyEnabled && this.UsesPersistentStorage;

    [JsonIgnore]
    public bool IsConcurrencyEnabled => this.IsEnabled && this.Concurrency.IsEnabled;

    [JsonIgnore]
    public bool IsPersistentConcurrencyEnabled => this.IsConcurrencyEnabled && this.UsesPersistentStorage;

    [JsonIgnore]
    public bool IsDurabilityEnabled => this.IsEnabled && this.Durability.IsEnabled;

    [JsonIgnore]
    public bool RequiresPersistenceStore => this.UsesPersistentStorage;
}
