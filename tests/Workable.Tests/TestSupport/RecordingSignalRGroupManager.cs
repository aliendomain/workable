using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;

namespace Workable.Tests;

internal sealed class RecordingSignalRGroupManager : IGroupManager
{
    private readonly Channel<SignalRGroupCall> addCalls = Channel.CreateUnbounded<SignalRGroupCall>();

    public bool FailAdds { get; init; }

    public List<SignalRGroupCall> Adds { get; } = [];

    public List<SignalRGroupCall> Removes { get; } = [];

    public Task AddToGroupAsync(
        string connectionId,
        string groupName,
        CancellationToken cancellationToken = default)
    {
        if (this.FailAdds)
        {
            throw new InvalidOperationException("Add failed.");
        }

        var call = new SignalRGroupCall(connectionId, groupName);
        this.Adds.Add(call);
        this.addCalls.Writer.TryWrite(call);
        return Task.CompletedTask;
    }

    public Task RemoveFromGroupAsync(
        string connectionId,
        string groupName,
        CancellationToken cancellationToken = default)
    {
        this.Removes.Add(new SignalRGroupCall(connectionId, groupName));
        return Task.CompletedTask;
    }

    public Task<SignalRGroupCall> WaitForAdd()
        => this.addCalls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
}

internal sealed record SignalRGroupCall(string ConnectionId, string GroupName);
