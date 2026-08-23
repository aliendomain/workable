namespace Workable;

internal sealed class WorkableSignalRRegistration
{
    private readonly object sync = new();
    private readonly TaskCompletionSource mapping = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? advertisedHubPath;

    public string? AdvertisedHubPath
    {
        get
        {
            lock (sync)
            {
                return advertisedHubPath;
            }
        }
    }

    public void Advertise(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (sync)
        {
            if (advertisedHubPath is not null &&
                !string.Equals(advertisedHubPath, path, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Only one Workable SignalR mapping can be advertised. Map aliases with advertise: false.");
            }

            advertisedHubPath = path;
            mapping.TrySetResult();
        }
    }

    public void MarkMapped()
        => mapping.TrySetResult();

    public Task WaitUntilMapped(CancellationToken cancellationToken)
        => mapping.Task.WaitAsync(cancellationToken);
}
