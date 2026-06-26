namespace Workable.PerformanceHarness;

internal static class BenchmarkScales
{
    public static readonly int[] QueryWorkerCounts =
    [
        100,
        10_000,
        100_000,
    ];

    public static readonly int[] MutationWorkerCounts =
    [
        100,
        5_000,
        25_000,
    ];

    public static readonly int[] BulkActionWorkerCounts =
    [
        100,
        1_000,
        5_000,
    ];

    public static readonly int[] MillionWorkerCounts =
    [
        1_000_000,
    ];

    public static readonly int[] DurableWorkerCounts =
    [
        1,
        100,
    ];

    public static readonly int[] DurableSoakWorkerCounts =
    [
        100,
        1_000,
    ];

    public static readonly int[] WorkflowBranchCounts =
    [
        1,
        4,
        16,
    ];

    public static readonly int[] RecoveryBranchCounts =
    [
        2,
        8,
    ];

    public static readonly int[] AuthorizationDefinitionCounts =
    [
        8,
        64,
    ];

    public static readonly int[] SignalRSubscriptionCounts =
    [
        1,
        32,
    ];

    public static readonly int[] IdempotencyParallelism =
    [
        4,
        16,
    ];
}
