using Microsoft.AspNetCore.SignalR;

namespace Workable;

internal sealed class WorkableSignalRAuthenticationFilter : IHubFilter
{
    private static readonly object ConnectionSnapshotKey = new();
    private static readonly object ExpirationRegistrationKey = new();

    public async Task OnConnectedAsync(
        HubLifetimeContext context,
        Func<HubLifetimeContext, Task> next)
    {
        var httpContext = context.Context.GetHttpContext();
        await EnsureAuthenticatedAsync(httpContext);
        await WorkableAspNetCoreAuthentication.PrepareAuthorizationSnapshotAsync(httpContext!);
        var snapshot = WorkableAspNetCoreAuthentication.GetCurrentSnapshot(httpContext)
            ?? throw new HubException("Authentication is required.");
        context.Context.Items[ConnectionSnapshotKey] = snapshot;
        if (snapshot.AuthenticationExpiresUtc is { } expiresUtc)
        {
            context.Context.Items[ExpirationRegistrationKey] = new ExpirationRegistration(
                expiresUtc,
                context.Context.Abort);
        }
        using var scope = WorkableAspNetCoreAuthentication.UseSnapshot(snapshot);
        await next(context);
    }

    public async Task OnDisconnectedAsync(
        HubLifetimeContext context,
        Exception? exception,
        Func<HubLifetimeContext, Exception?, Task> next)
    {
        try
        {
            await next(context, exception);
        }
        finally
        {
            if (context.Context.Items.Remove(ExpirationRegistrationKey, out var value) &&
                value is IDisposable registration)
            {
                registration.Dispose();
            }
        }
    }

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        using var scope = UseConnectionSnapshot(invocationContext.Context);
        return await next(invocationContext);
    }

    internal static IDisposable UseConnectionSnapshot(HubCallerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Items.TryGetValue(ConnectionSnapshotKey, out var value) ||
            value is not WorkableAspNetCoreAuthentication.WorkableAuthenticationSnapshot snapshot)
        {
            throw new HubException("Authentication is required.");
        }

        return WorkableAspNetCoreAuthentication.UseSnapshot(snapshot);
    }

    private static async Task EnsureAuthenticatedAsync(Microsoft.AspNetCore.Http.HttpContext? httpContext)
    {
        if (!await WorkableAspNetCoreAuthentication.EnsureAuthenticatedAsync(httpContext))
        {
            throw new HubException("Authentication is required.");
        }
    }

    internal sealed class ExpirationRegistration : IDisposable
    {
        private static readonly TimeSpan MaximumTimerDelay = TimeSpan.FromDays(1);
        private readonly object gate = new();
        private readonly DateTimeOffset expiresUtc;
        private readonly Action abort;
        private readonly Timer timer;
        private bool disposed;

        public ExpirationRegistration(DateTimeOffset expiresUtc, Action abort)
        {
            this.expiresUtc = expiresUtc;
            this.abort = abort;
            this.timer = new Timer(
                static state => ((ExpirationRegistration)state!).ScheduleNext(),
                this,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            this.ScheduleNext();
        }

        public void Dispose()
        {
            lock (this.gate)
            {
                if (this.disposed)
                {
                    return;
                }

                this.disposed = true;
                this.timer.Dispose();
            }
        }

        internal void ScheduleNext()
        {
            var shouldAbort = false;
            lock (this.gate)
            {
                if (this.disposed)
                {
                    return;
                }

                var remaining = this.expiresUtc - DateTimeOffset.UtcNow;
                shouldAbort = remaining <= TimeSpan.Zero;
                this.timer.Change(
                    shouldAbort
                        ? Timeout.InfiniteTimeSpan
                        : remaining < MaximumTimerDelay ? remaining : MaximumTimerDelay,
                    Timeout.InfiniteTimeSpan);
            }

            if (shouldAbort)
            {
                this.abort();
            }
        }
    }
}
