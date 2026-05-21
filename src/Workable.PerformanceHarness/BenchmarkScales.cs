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
}
