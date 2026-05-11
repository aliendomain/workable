using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Workable;
internal sealed class WorkEventStream : IWorkEventStream, IAsyncDisposable
{
    private static readonly WorkEventSubscriptionOptions DefaultOptions = new();
    private readonly Lock sync = new();
    private WorkEventSubscription[] subscriptions = [];
    private bool isDisposed;

    internal int ActiveSubscriptionCount
        => Volatile.Read(ref this.subscriptions).Length;

    public IWorkEventSubscription Subscribe(
        WorkEventFilter? filter = null,
        WorkEventSubscriptionOptions? options = null)
    {
        options ??= DefaultOptions;
        if (options.Capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Capacity, "Event subscription capacity must be greater than zero.");
        }

        var subscription = new WorkEventSubscription(this, filter, options);
        lock (this.sync)
        {
            ObjectDisposedException.ThrowIf(this.isDisposed, this);
            this.subscriptions = [.. this.subscriptions, subscription];
        }

        return subscription;
    }

    public void Publish(WorkEvent workEvent)
    {
        ArgumentNullException.ThrowIfNull(workEvent);

        if (Volatile.Read(ref this.isDisposed))
        {
            return;
        }

        var subscribers = Volatile.Read(ref this.subscriptions);
        foreach (var subscription in subscribers)
        {
            subscription.Publish(workEvent);
        }
    }

    public ValueTask DisposeAsync()
    {
        WorkEventSubscription[] subscribers;
        lock (this.sync)
        {
            if (this.isDisposed)
            {
                return ValueTask.CompletedTask;
            }

            this.isDisposed = true;
            subscribers = this.subscriptions;
            this.subscriptions = [];
        }

        foreach (var subscription in subscribers)
        {
            subscription.DisposeFromOwner();
        }

        return ValueTask.CompletedTask;
    }

    private void Remove(WorkEventSubscription subscription)
    {
        lock (this.sync)
        {
            var index = Array.IndexOf(this.subscriptions, subscription);
            if (index < 0)
            {
                return;
            }

            if (this.subscriptions.Length == 1)
            {
                this.subscriptions = [];
                return;
            }

            var remaining = new WorkEventSubscription[this.subscriptions.Length - 1];
            if (index > 0)
            {
                Array.Copy(this.subscriptions, 0, remaining, 0, index);
            }

            if (index < this.subscriptions.Length - 1)
            {
                Array.Copy(this.subscriptions, index + 1, remaining, index, this.subscriptions.Length - index - 1);
            }

            this.subscriptions = remaining;
        }
    }

    private sealed class WorkEventSubscription(
        WorkEventStream owner,
        WorkEventFilter? filter,
        WorkEventSubscriptionOptions options) : IWorkEventSubscription
    {
        private readonly Channel<WorkEvent> events = Channel.CreateBounded<WorkEvent>(
            new BoundedChannelOptions(options.Capacity)
            {
                FullMode = ToBoundedChannelFullMode(options.OverflowBehavior),
                SingleReader = true,
                SingleWriter = false,
            });
        private int isDisposed;

        public async IAsyncEnumerable<WorkEvent> Read([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                await foreach (var workEvent in this.events.Reader.ReadAllAsync(cancellationToken))
                {
                    yield return workEvent;
                }
            }
            finally
            {
                await this.DisposeAsync();
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref this.isDisposed, 1) == 1)
            {
                return ValueTask.CompletedTask;
            }

            this.events.Writer.TryComplete();
            owner.Remove(this);
            return ValueTask.CompletedTask;
        }

        internal void DisposeFromOwner()
        {
            if (Interlocked.Exchange(ref this.isDisposed, 1) == 0)
            {
                this.events.Writer.TryComplete();
            }
        }

        internal void Publish(WorkEvent workEvent)
        {
            if (Volatile.Read(ref this.isDisposed) == 1 || filter?.Matches(workEvent) == false)
            {
                return;
            }

            this.events.Writer.TryWrite(workEvent);
        }

        private static BoundedChannelFullMode ToBoundedChannelFullMode(WorkEventOverflowBehavior behavior)
            => behavior switch
            {
                WorkEventOverflowBehavior.DropNewest => BoundedChannelFullMode.DropNewest,
                WorkEventOverflowBehavior.DropWrite => BoundedChannelFullMode.DropWrite,
                _ => BoundedChannelFullMode.DropOldest,
            };
    }
}
