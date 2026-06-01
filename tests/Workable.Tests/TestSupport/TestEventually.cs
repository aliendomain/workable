namespace Workable.Tests;

internal static class TestEventually
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(10);

    public static Task Until(
        Func<bool> condition,
        string? message = null,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
        => Until(
            () => Task.FromResult(condition()),
            message,
            timeout,
            pollInterval,
            cancellationToken);

    public static async Task Until(
        Func<Task<bool>> condition,
        string? message = null,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var timeoutValue = timeout ?? DefaultTimeout;
        var pollIntervalValue = pollInterval ?? DefaultPollInterval;
        if (timeoutValue <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive.");
        }

        if (pollIntervalValue <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), "Poll interval must be positive.");
        }

        var deadline = DateTimeOffset.UtcNow + timeoutValue;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await condition())
            {
                return;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var delay = remaining < pollIntervalValue ? remaining : pollIntervalValue;
            await Task.Delay(delay, cancellationToken);
        }

        Assert.True(await condition(), message ?? "Expected condition to become true.");
    }

    public static async Task<T> UntilNotNull<T>(
        Func<Task<T?>> getValue,
        string? message = null,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        T? result = null;
        await Until(
            async () =>
            {
                result = await getValue();
                return result is not null;
            },
            message ?? "Expected value to become available.",
            timeout,
            pollInterval,
            cancellationToken);

        return result!;
    }

    public static Task ReadModelDrained(
        IWorkSystem system,
        string? message = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        return Until(
            () => system.Diagnostics.ReadModel.PendingUpdateCount == 0,
            message ?? "Expected the read model to drain pending updates.",
            cancellationToken: cancellationToken);
    }

    public static Task ClockAfter(DateTimeOffset timestamp)
        => Until(
            () => DateTimeOffset.UtcNow > timestamp,
            $"Expected the clock to advance past {timestamp:O}.",
            timeout: TimeSpan.FromSeconds(1),
            pollInterval: TimeSpan.FromMilliseconds(1));

    public static Task ThroughputBucketClosed()
    {
        var currentSecond = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return Until(
            () => DateTimeOffset.UtcNow.ToUnixTimeSeconds() > currentSecond,
            "Expected the current throughput bucket second to close.",
            timeout: TimeSpan.FromSeconds(2));
    }
}
