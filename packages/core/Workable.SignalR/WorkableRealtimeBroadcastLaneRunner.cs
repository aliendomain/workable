using Microsoft.Extensions.Logging;

namespace Workable;

internal sealed class WorkableRealtimeBroadcastLaneRunner
{
    private static readonly TimeSpan DefaultRestartDelay = TimeSpan.FromSeconds(1);
    private readonly ILogger<WorkableRealtimeBroadcastLaneRunner> logger;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    public WorkableRealtimeBroadcastLaneRunner(ILogger<WorkableRealtimeBroadcastLaneRunner> logger)
        : this(logger, Task.Delay)
    {
    }

    internal WorkableRealtimeBroadcastLaneRunner(
        ILogger<WorkableRealtimeBroadcastLaneRunner> logger,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.delay = delay ?? throw new ArgumentNullException(nameof(delay));
    }

    public async Task Run(
        IWorkSystem system,
        string laneName,
        Func<IWorkSystem, CancellationToken, Task> broadcast,
        CancellationToken cancellationToken,
        TimeSpan? restartDelay = null)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(laneName);
        ArgumentNullException.ThrowIfNull(broadcast);

        var resolvedRestartDelay = restartDelay ?? DefaultRestartDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await broadcast(system, cancellationToken);
                if (!cancellationToken.IsCancellationRequested)
                {
                    this.logger.LogWarning(
                        "The SignalR {LaneName} lane for system '{SystemName}' stopped unexpectedly and will restart.",
                        laneName,
                        system.Name);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                this.logger.LogError(
                    exception,
                    "The SignalR {LaneName} lane for system '{SystemName}' faulted and will restart.",
                    laneName,
                    system.Name);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await this.delay(resolvedRestartDelay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
