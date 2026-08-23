using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.SignalR;

namespace Workable;

/// <summary>
/// SignalR hub that exposes Workable realtime event, worker-list, named-view, and worker-overview subscriptions.
/// </summary>
/// <remarks>
/// This hub is observability-focused. Clients subscribe to updates here, then use direct .NET or HTTP APIs for
/// queueing, querying, and worker actions.
/// </remarks>
public sealed class WorkableRealtimeHub(
    IWorkSystemRegistry registry,
    IWorkAuthorizationGroupResolver groupResolver,
    IWorkRequestContextFactory requestContexts,
    WorkableViewQueryAdapter views,
    IWorkableSignalRPayloadSerializer payloads,
    WorkableRealtimeEventSubscriptions eventSubscriptions,
    WorkableRealtimeViewSubscriptions viewSubscriptions,
    WorkableRealtimeWorkerOverviewSubscriptions workerOverviewSubscriptions) : Hub
{
    /// <summary>
    /// Starts or replaces a named view subscription for the current connection.
    /// </summary>
    /// <param name="subscriptionId">The caller-defined logical handle for this live view stream.</param>
    /// <param name="viewName">The built-in or custom view name to subscribe to.</param>
    /// <param name="criteria">Optional criteria used to scope the view components.</param>
    /// <param name="systemName">Optional named system. When omitted, the default Workable system is used.</param>
    public Task WatchView(
        string subscriptionId,
        string viewName,
        WorkViewCriteria? criteria = null,
        string? systemName = null)
        => WatchViewCore(subscriptionId, viewName, criteria, systemName, currentActorOnly: false);

    /// <summary>
    /// Starts or replaces a live worker-list subscription for the current connection.
    /// </summary>
    /// <param name="subscriptionId">The caller-defined logical handle for this live worker stream.</param>
    /// <param name="criteria">
    /// Optional scope and worker-grid component options. Use the worker-grid <c>actorId</c> option to watch work
    /// originated by one actor, and its <c>take</c> option to limit the snapshot size.
    /// </param>
    /// <param name="systemName">Optional named system. When omitted, the default Workable system is used.</param>
    /// <remarks>
    /// This is the discoverable worker-list form of <see cref="WatchView"/>. It uses the same <c>workers</c> named
    /// view, initial seed, shared change stream, coalescing, authorization, and reconciliation path. A caller-supplied
    /// worker-grid actor id is only a filter within that authorization and must not be used as an end-user isolation
    /// boundary.
    /// </remarks>
    public Task WatchWorkers(
        string subscriptionId,
        WorkViewCriteria? criteria = null,
        string? systemName = null)
        => WatchViewCore(subscriptionId, "workers", criteria, systemName, currentActorOnly: false);

    /// <summary>
    /// Starts or replaces a worker-list subscription scoped to the authenticated originating actor.
    /// </summary>
    /// <param name="subscriptionId">The caller-defined logical handle for this live worker stream.</param>
    /// <param name="criteria">Optional worker-grid scope, state, paging, and key filters.</param>
    /// <param name="systemName">Optional named system. When omitted, the default Workable system is used.</param>
    /// <remarks>
    /// Any caller-supplied worker-grid actor id is replaced with the authenticated actor id. The call fails closed
    /// when the authenticated principal does not resolve to a stable actor id. Criteria may contain only worker-grid
    /// components; omit criteria to use the default detailed worker grid.
    /// </remarks>
    public Task WatchMyWorkers(
        string subscriptionId,
        WorkViewCriteria? criteria = null,
        string? systemName = null)
        => WatchViewCore(subscriptionId, "workers", criteria, systemName, currentActorOnly: true);

    private async Task WatchViewCore(
        string subscriptionId,
        string viewName,
        WorkViewCriteria? criteria,
        string? systemName,
        bool currentActorOnly)
    {
        var system = ResolveSystem(systemName);
        WorkableRealtimeViewSubscription? subscription = null;
        try
        {
            var (authorization, session) = await CreateAuthorization(system, systemName);
            WorkViewCriteria normalizedCriteria;
            try
            {
                normalizedCriteria = currentActorOnly
                    ? views.NormalizeActorWorkerViewCriteria(criteria, RequireActorId(authorization.Actor))
                    : views.NormalizeViewCriteria(viewName, criteria);
            }
            catch (ArgumentException exception)
            {
                throw new HubException(exception.Message);
            }

            var validationResult = await QueryView(
                system,
                session,
                authorization,
                viewName,
                normalizedCriteria);
            var invalidComponent = validationResult.Components.Values.FirstOrDefault(
                component => string.Equals(component.Status, "error", StringComparison.OrdinalIgnoreCase));
            if (invalidComponent is not null)
            {
                throw new HubException(invalidComponent.Error ?? "The requested view configuration is invalid.");
            }

            if (!await CanProduceAuthorizedView(session, viewName, normalizedCriteria))
            {
                throw new HubException("The requested view does not have an authorized realtime data source.");
            }

            subscription = await viewSubscriptions.WatchView(
                this.Context.ConnectionId,
                this.Groups,
                system,
                subscriptionId,
                viewName,
                normalizedCriteria,
                authorization,
                this.Context.ConnectionAborted);

            await viewSubscriptions.WaitForStreaming(system, this.Context.ConnectionAborted);
            await SendView(subscription, session, this.Clients.Caller);
        }
        catch (OperationCanceledException) when (this.Context.ConnectionAborted.IsCancellationRequested)
        {
            // The client disconnected while the initial view payload was being prepared.
        }
        catch
        {
            if (subscription is not null)
            {
                await viewSubscriptions.UnwatchView(
                    this.Context.ConnectionId,
                    this.Groups,
                    system,
                    subscription.SubscriptionId,
                    CancellationToken.None);
            }

            throw;
        }
        finally
        {
            if (subscription is not null)
            {
                viewSubscriptions.CompleteSeed(subscription.GroupName);
            }
        }
    }

    /// <summary>
    /// Stops a named view subscription for the current connection.
    /// </summary>
    /// <param name="subscriptionId">The subscription id originally passed to <see cref="WatchView"/>.</param>
    /// <param name="systemName">Optional named system. When omitted, the default Workable system is used.</param>
    public async Task UnwatchView(string subscriptionId, string? systemName = null)
    {
        var system = ResolveSystem(systemName);
        try
        {
            await EnsureCanAccessNamedSystem(
                system,
                systemName,
                CreateRequestContext());
            await viewSubscriptions.UnwatchView(
                this.Context.ConnectionId,
                this.Groups,
                system,
                subscriptionId,
                this.Context.ConnectionAborted);
        }
        catch (OperationCanceledException) when (this.Context.ConnectionAborted.IsCancellationRequested)
        {
            // The client disconnected while its view subscription was being removed.
        }
    }

    /// <summary>
    /// Stops a live worker-list subscription for the current connection.
    /// </summary>
    /// <param name="subscriptionId">The subscription id originally passed to <see cref="WatchWorkers"/>.</param>
    /// <param name="systemName">Optional named system. When omitted, the default Workable system is used.</param>
    public Task UnwatchWorkers(string subscriptionId, string? systemName = null)
        => UnwatchView(subscriptionId, systemName);

    /// <summary>
    /// Stops an authenticated-actor worker-list subscription for the current connection.
    /// </summary>
    public Task UnwatchMyWorkers(string subscriptionId, string? systemName = null)
        => UnwatchWorkers(subscriptionId, systemName);

    /// <summary>
    /// Starts or replaces a worker-overview subscription for the current connection.
    /// </summary>
    /// <param name="subscriptionId">The caller-defined logical handle for this live worker-overview stream.</param>
    /// <param name="workerId">The worker identifier as a string-form GUID.</param>
    /// <param name="criteria">Optional realtime criteria that describe the visible worker detail screen state.</param>
    /// <param name="systemName">Optional named system. When omitted, the default Workable system is used.</param>
    public async Task WatchWorkerOverview(
        string subscriptionId,
        string workerId,
        WorkWorkerOverviewRealtimeCriteria? criteria = null,
        string? systemName = null)
    {
        var system = ResolveSystem(systemName);
        WorkableRealtimeWorkerOverviewSubscription? subscription = null;
        try
        {
            var (authorization, session) = await CreateAuthorization(system, systemName);
            if (!await HasAuthorizedChangeStream(session))
            {
                throw new HubException("The requested worker overview does not have an authorized realtime data source.");
            }

            subscription = await workerOverviewSubscriptions.Watch(
                this.Context.ConnectionId,
                this.Groups,
                system,
                subscriptionId,
                ParseWorkerId(workerId),
                views.NormalizeWorkerOverviewRealtimeCriteria(criteria),
                authorization,
                this.Context.ConnectionAborted);
            var hasPublishedState = await SendWorkerOverview(subscription, session, this.Clients.Caller);
            if (!hasPublishedState)
            {
                await workerOverviewSubscriptions.Unwatch(
                    this.Context.ConnectionId,
                    this.Groups,
                    system,
                    subscription.SubscriptionId,
                    CancellationToken.None);
                subscription = null;
                return;
            }

            workerOverviewSubscriptions.SetSeeded(subscription.GroupName, hasPublishedState);
        }
        catch (OperationCanceledException) when (this.Context.ConnectionAborted.IsCancellationRequested)
        {
            // The client disconnected while the worker overview stream was starting.
        }
        catch
        {
            if (subscription is not null)
            {
                await workerOverviewSubscriptions.Unwatch(
                    this.Context.ConnectionId,
                    this.Groups,
                    system,
                    subscription.SubscriptionId,
                    CancellationToken.None);
            }

            throw;
        }
    }

    /// <summary>
    /// Stops a worker-overview subscription for the current connection.
    /// </summary>
    /// <param name="subscriptionId">The subscription id originally passed to <see cref="WatchWorkerOverview"/>.</param>
    /// <param name="systemName">Optional named system. When omitted, the default Workable system is used.</param>
    public async Task UnwatchWorkerOverview(string subscriptionId, string? systemName = null)
    {
        var system = ResolveSystem(systemName);
        try
        {
            await EnsureCanAccessNamedSystem(
                system,
                systemName,
                CreateRequestContext());
            await workerOverviewSubscriptions.Unwatch(
                this.Context.ConnectionId,
                this.Groups,
                system,
                subscriptionId,
                this.Context.ConnectionAborted);
        }
        catch (OperationCanceledException) when (this.Context.ConnectionAborted.IsCancellationRequested)
        {
            // The client disconnected while its worker-overview subscription was being removed.
        }
    }

    /// <summary>
    /// Starts or replaces a raw event subscription for the current connection.
    /// </summary>
    /// <param name="criteria">Optional event-type, definition, and key filters.</param>
    /// <param name="systemName">Optional named system. When omitted, the default Workable system is used.</param>
    public async Task WatchEvents(
        WorkableRealtimeEventCriteria? criteria = null,
        string? systemName = null)
    {
        var system = ResolveSystem(systemName);
        try
        {
            var (authorization, session) = await CreateAuthorization(system, systemName);
            WorkEventFilter? filter;
            try
            {
                filter = eventSubscriptions.CreateFilter(session.Catalog, criteria);
            }
            catch (ArgumentException exception)
            {
                throw new HubException(exception.Message);
            }

            if (!await HasAuthorizedEventStream(session, filter))
            {
                throw new HubException("The requested event subscription does not have an authorized realtime data source.");
            }

            await eventSubscriptions.WatchEvents(
                this.Context.ConnectionId,
                this.Groups,
                system,
                filter,
                authorization,
                this.Context.ConnectionAborted);
        }
        catch (OperationCanceledException) when (this.Context.ConnectionAborted.IsCancellationRequested)
        {
            // The client disconnected while the event pump was starting.
        }
    }

    /// <summary>
    /// Stops a raw event subscription for the current connection.
    /// </summary>
    /// <param name="criteria">The same normalized filter shape originally used to watch the event stream.</param>
    /// <param name="systemName">Optional named system. When omitted, the default Workable system is used.</param>
    public async Task UnwatchEvents(
        WorkableRealtimeEventCriteria? criteria = null,
        string? systemName = null)
    {
        var system = ResolveSystem(systemName);
        var (_, session) = await this.CreateAuthorization(system, systemName);
        await eventSubscriptions.UnwatchEvents(
            this.Context.ConnectionId,
            this.Groups,
            system,
            session.Catalog,
            criteria,
            this.Context.ConnectionAborted);
    }

    /// <summary>
    /// Streams retained and live application-defined status messages for one work iteration in sequence order.
    /// </summary>
    /// <param name="workerId">The worker identifier as a string-form GUID.</param>
    /// <param name="iterationSequence">The iteration sequence within the worker.</param>
    /// <param name="afterSequence">The last status sequence already received, or zero to replay from the beginning.</param>
    /// <param name="systemName">Optional named system. When omitted, the default Workable system is used.</param>
    /// <param name="cancellationToken">Cancels the streaming invocation.</param>
    /// <returns>Status items followed by a terminal retained iteration result, or one terminal typed gap message.</returns>
    public async IAsyncEnumerable<WorkableRealtimeIterationStatusMessage> StreamIterationStatus(
        string workerId,
        long iterationSequence,
        long afterSequence = 0,
        string? systemName = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var authentication = WorkableSignalRAuthenticationFilter.UseConnectionSnapshot(this.Context);
        ValidateIterationStatusArguments(iterationSequence, afterSequence);
        var system = ResolveSystem(systemName);
        var (_, session) = await CreateAuthorization(system, systemName);
        var iteration = new WorkerIterationReference(ParseWorkerId(workerId), iterationSequence);
        await foreach (var message in StreamIterationStatusCore(
            session,
            iteration,
            afterSequence,
            requiredActorId: null,
            this.Context.ConnectionAborted,
            cancellationToken))
        {
            yield return message;
        }
    }

    /// <summary>
    /// Streams retained and live status messages for one iteration owned by the authenticated originating actor.
    /// </summary>
    /// <param name="workerId">The worker identifier as a string-form GUID.</param>
    /// <param name="iterationSequence">The iteration sequence within the worker.</param>
    /// <param name="afterSequence">The last status sequence already received, or zero to replay from the beginning.</param>
    /// <param name="systemName">Optional named system. When omitted, the default Workable system is used.</param>
    /// <param name="cancellationToken">Cancels the streaming invocation.</param>
    /// <returns>Status items followed by a terminal retained iteration snapshot, or one terminal replay gap.</returns>
    public async IAsyncEnumerable<WorkableRealtimeIterationStatusMessage> StreamMyIterationStatus(
        string workerId,
        long iterationSequence,
        long afterSequence = 0,
        string? systemName = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var authentication = WorkableSignalRAuthenticationFilter.UseConnectionSnapshot(this.Context);
        ValidateIterationStatusArguments(iterationSequence, afterSequence);
        var system = ResolveSystem(systemName);
        var (authorization, session) = await CreateAuthorization(system, systemName);
        var iteration = new WorkerIterationReference(ParseWorkerId(workerId), iterationSequence);
        await foreach (var message in StreamIterationStatusCore(
            session,
            iteration,
            afterSequence,
            RequireActorId(authorization.Actor),
            this.Context.ConnectionAborted,
            cancellationToken))
        {
            yield return message;
        }
    }

    internal static IWorkIterationStatusSubscription SubscribeIterationStatus(
        IWorkIterationStatusStream stream,
        WorkerIterationReference iteration,
        long afterSequence)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (afterSequence < 0)
        {
            throw new HubException("The status sequence cursor cannot be negative.");
        }

        try
        {
            return stream.Subscribe(iteration, afterSequence);
        }
        catch (WorkIterationStatusCursorOutOfRangeException exception)
        {
            throw new HubException(exception.Message);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new HubException("The iteration status stream could not be opened.");
        }
        catch (KeyNotFoundException)
        {
            throw new HubException(
                $"Worker '{iteration.WorkerId}' iteration {iteration.Sequence} does not have an available status stream.");
        }
        catch (WorkIterationStatusSubscriptionLimitException exception)
        {
            throw new HubException(exception.Message);
        }
    }

    /// <summary>
    /// Removes all active subscriptions for the connection that is disconnecting.
    /// </summary>
    /// <param name="exception">The disconnect exception, if one caused the disconnect.</param>
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        eventSubscriptions.RemoveConnection(this.Context.ConnectionId);
        viewSubscriptions.RemoveConnection(this.Context.ConnectionId);
        workerOverviewSubscriptions.RemoveConnection(this.Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    private async Task SendView(
        WorkableRealtimeViewSubscription subscription,
        IWorkSystemSession session,
        IClientProxy client)
    {
        var result = await QueryView(
            ResolveSystem(session.SystemName),
            session,
            subscription.Authorization,
            subscription.ViewName,
            subscription.Criteria);
        await client.SendAsync(
            WorkableRealtimeClientMethods.ViewUpdated,
            payloads.Serialize(new WorkableRealtimeViewEnvelope<WorkComponentQueryResult>(
                subscription.SubscriptionId,
                subscription.ViewName,
                result)),
            this.Context.ConnectionAborted);
    }

    private Task<WorkComponentQueryResult> QueryView(
        IWorkSystem system,
        IWorkSystemSession session,
        WorkAuthorizationSnapshot authorization,
        string viewName,
        WorkViewCriteria criteria)
        => WorkableRealtimeWorkflowViews.IsWorkflowView(viewName)
            ? WorkableRealtimeWorkflowViews.Query(
                system,
                authorization,
                viewName,
                criteria,
                this.Context.ConnectionAborted)
            : views.View(
                session,
                viewName,
                criteria,
                cancellationToken: this.Context.ConnectionAborted);

    internal static async IAsyncEnumerable<WorkableRealtimeIterationStatusMessage> ReadIterationStatus(
        IWorkIterationStatusStream stream,
        WorkerIterationReference iteration,
        long afterSequence,
        CancellationToken connectionAborted,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IWorkIterationStatusSubscription? subscription = null;
        WorkIterationStatusGapException? gap = null;
        try
        {
            subscription = SubscribeIterationStatus(stream, iteration, afterSequence);
        }
        catch (WorkIterationStatusGapException exception)
        {
            gap = exception;
        }

        if (gap is not null)
        {
            yield return WorkableRealtimeIterationStatusMessage.From(gap);
            yield break;
        }

        await foreach (var message in ReadIterationStatus(
            subscription!,
            connectionAborted,
            cancellationToken))
        {
            yield return message;
        }
    }

    internal static async IAsyncEnumerable<WorkableRealtimeIterationStatusMessage> StreamIterationStatusCore(
        IWorkSystemSession session,
        WorkerIterationReference iteration,
        long afterSequence,
        string? requiredActorId,
        CancellationToken connectionAborted,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            connectionAborted,
            cancellationToken);
        var worker = await session.Query.Worker(iteration.WorkerId, linkedCancellation.Token);
        if (worker is null ||
            (requiredActorId is not null && !ActorIdsMatch(worker.RequestContext.Actor.Id, requiredActorId)))
        {
            yield break;
        }

        IWorkIterationStatusSubscription? subscription = null;
        WorkIterationStatusGapException? replayGap = null;
        try
        {
            subscription = SubscribeIterationStatus(session.IterationStatuses, iteration, afterSequence);
        }
        catch (WorkIterationStatusGapException gap)
        {
            replayGap = gap;
        }

        if (replayGap is not null)
        {
            yield return WorkableRealtimeIterationStatusMessage.From(replayGap);
            yield break;
        }

        var activeSubscription = subscription!;
        WorkIterationStatusCompletion? completion = null;
        await using (activeSubscription)
        await using (var reader = activeSubscription
            .Read(linkedCancellation.Token)
            .GetAsyncEnumerator(linkedCancellation.Token))
        {
            while (true)
            {
                bool hasNext;
                WorkIterationStatusGapException? liveGap = null;
                try
                {
                    hasNext = await reader.MoveNextAsync();
                }
                catch (WorkIterationStatusGapException gap)
                {
                    hasNext = false;
                    liveGap = gap;
                }

                if (liveGap is not null)
                {
                    yield return WorkableRealtimeIterationStatusMessage.From(liveGap);
                    yield break;
                }

                if (!hasNext)
                {
                    completion = activeSubscription.Completion;
                    break;
                }

                yield return WorkableRealtimeIterationStatusMessage.From(reader.Current);
            }
        }

        if (completion is null)
        {
            yield break;
        }

        yield return WorkableRealtimeIterationStatusMessage.From(completion);
    }

    internal static async IAsyncEnumerable<WorkableRealtimeIterationStatusMessage> ReadIterationStatus(
        IWorkIterationStatusSubscription subscription,
        CancellationToken connectionAborted,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using (subscription)
        using (var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            connectionAborted,
            cancellationToken))
        await using (var reader = subscription
            .Read(linkedCancellation.Token)
            .GetAsyncEnumerator(linkedCancellation.Token))
        {
            while (true)
            {
                bool hasNext;
                WorkIterationStatusGapException? gap = null;
                try
                {
                    hasNext = await reader.MoveNextAsync();
                }
                catch (WorkIterationStatusGapException exception)
                {
                    hasNext = false;
                    gap = exception;
                }

                if (gap is not null)
                {
                    yield return WorkableRealtimeIterationStatusMessage.From(gap);
                    yield break;
                }

                if (!hasNext)
                {
                    yield break;
                }

                yield return WorkableRealtimeIterationStatusMessage.From(reader.Current);
            }
        }
    }

    private async Task<bool> SendWorkerOverview(
        WorkableRealtimeWorkerOverviewSubscription subscription,
        IWorkSystemSession session,
        IClientProxy client)
    {
        var result = await views.WorkerOverviewRealtimeState(
            session,
            subscription.WorkerId,
            subscription.Criteria,
            cancellationToken: this.Context.ConnectionAborted);
        if (result is null)
        {
            return false;
        }

        await client.SendAsync(
            WorkableRealtimeClientMethods.WorkerOverviewUpdated,
            payloads.Serialize(new WorkableRealtimeViewEnvelope<WorkWorkerOverviewRealtimeUpdate>(
                subscription.SubscriptionId,
                "worker-overview",
                WorkableRealtimeWorkerOverviewUpdateFactory.CreateSnapshot(result))),
            this.Context.ConnectionAborted);
        return true;
    }

    private static ValueTask<bool> CanProduceAuthorizedView(
        IWorkSystemSession session,
        string viewName,
        WorkViewCriteria criteria)
        => WorkableRealtimeWorkflowViews.IsWorkflowView(viewName)
            ? HasAuthorizedEventStream(
                session,
                WorkableRealtimeWorkflowViews.CreateEventFilter(criteria))
            : HasAuthorizedChangeStreamForView(session, criteria);

    private static async ValueTask<bool> HasAuthorizedChangeStreamForView(
        IWorkSystemSession session,
        WorkViewCriteria criteria)
    {
        if (AllComponentsRequireReadableWorkScope(criteria.Components) &&
            !HasReadableWorkScope(session.Catalog, criteria.Scope))
        {
            return false;
        }

        return await HasAuthorizedChangeStream(session);
    }

    private static bool AllComponentsRequireReadableWorkScope(IReadOnlyList<WorkComponentRequest>? components)
        => components is { Count: > 0 } && components.All(request =>
            request.Type.Trim().ToLowerInvariant() is not (
                "system" or
                "systemdiagnostics" or
                "queuediagnostics" or
                "readmodeldiagnostics" or
                "retentiondiagnostics" or
                "concurrencydiagnostics" or
                "durabilitydiagnostics" or
                "idempotencydiagnostics"));

    internal static bool HasReadableWorkScope(IWorkCatalog catalog, WorkSystemCriteria? scope)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (scope is null)
        {
            return catalog.Definitions.Count > 0;
        }

        HashSet<string>? readableNames = null;
        if (scope.DefinitionNames is not null)
        {
            readableNames = scope.DefinitionNames
                .Where(name => !string.IsNullOrWhiteSpace(name) && catalog.TryGet(name, out _))
                .Select(name => name.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(scope.DefinitionName))
        {
            var exactName = catalog.TryGet(scope.DefinitionName, out var definition)
                ? new HashSet<string>([definition.Name], StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            readableNames = IntersectReadableNames(readableNames, exactName);
        }

        if (!string.IsNullOrWhiteSpace(scope.Category))
        {
            var categoryNames = catalog
                .ListByCategory(scope.Category, scope.IncludeSubcategories)
                .Select(definition => definition.Name);
            readableNames = IntersectReadableNames(readableNames, categoryNames);
        }

        return readableNames?.Count > 0 ||
            readableNames is null && catalog.Definitions.Count > 0;
    }

    private static HashSet<string> IntersectReadableNames(
        HashSet<string>? current,
        IEnumerable<string> requested)
    {
        var requestedSet = requested.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (current is null)
        {
            return requestedSet;
        }

        current.IntersectWith(requestedSet);
        return current;
    }

    private static async ValueTask<bool> HasAuthorizedChangeStream(IWorkSystemSession session)
    {
        await using var subscription = session.Changes.Subscribe();
        return !ReferenceEquals(subscription, EmptyWorkChangeSubscription.Instance);
    }

    private static async ValueTask<bool> HasAuthorizedEventStream(
        IWorkSystemSession session,
        WorkEventFilter? filter)
    {
        await using var subscription = session.Events.Subscribe(filter);
        return !ReferenceEquals(subscription, EmptyWorkEventSubscription.Instance);
    }

    private async ValueTask<(WorkAuthorizationSnapshot Authorization, IWorkSystemSession Session)> CreateAuthorization(
        IWorkSystem system,
        string? systemName)
    {
        var requestContext = CreateRequestContext();
        var groups = await groupResolver.GetGroups(
            requestContext,
            system.Name,
            this.Context.ConnectionAborted);
        var authorizationContext = requestContext with
        {
            Authorization = WorkAuthorizationSnapshot.CreateForSystem(
                system.Name,
                requestContext.Actor,
                groups,
                readableDefinitionIds: null,
                isAuthenticated: requestContext.IsAuthenticated),
        };
        await EnsureCanAccessNamedSystem(system, systemName, authorizationContext);
        var session = await system.CreateSession(authorizationContext, this.Context.ConnectionAborted);

        return (
            (session as WorkSystemSession)?.RequestContext.Authorization
                ?? throw new InvalidOperationException("Authorized Workable sessions require a canonical authorization snapshot."),
            session);
    }

    private static string RequireActorId(WorkActor actor)
    {
        if (string.IsNullOrWhiteSpace(actor.Id))
        {
            throw new HubException(
                "The authenticated caller does not have a stable actor id required by this operation.");
        }

        return actor.Id.Trim();
    }

    private static bool ActorIdsMatch(string? actual, string expected)
        => !string.IsNullOrWhiteSpace(actual) &&
            string.Equals(actual.Trim(), expected, StringComparison.Ordinal);

    private static void ValidateIterationStatusArguments(long iterationSequence, long afterSequence)
    {
        if (iterationSequence <= 0)
        {
            throw new HubException("The iteration sequence must be greater than zero.");
        }

        if (afterSequence < 0)
        {
            throw new HubException("The iteration status sequence cursor cannot be negative.");
        }
    }

    private WorkRequestContext CreateRequestContext()
        => requestContexts.Create(
            this.Context.GetHttpContext(),
            WorkInvocationChannel.SignalR)
            .WithSurface(WorkOriginSurface.WorkableAdapter);

    private async ValueTask EnsureCanAccessNamedSystem(
        IWorkSystem system,
        string? systemName,
        WorkRequestContext requestContext)
    {
        if (string.IsNullOrWhiteSpace(systemName) ||
            (await system.DescribeAccess(requestContext, this.Context.ConnectionAborted)).HasAnyAccess())
        {
            return;
        }

        throw SystemNotFound(systemName);
    }

    private IWorkSystem ResolveSystem(string? systemName)
    {
        var system = string.IsNullOrWhiteSpace(systemName)
            ? registry.Default
            : registry.TryGet(systemName, out var namedSystem)
                ? namedSystem
                : throw SystemNotFound(systemName);
        if (!system.RequiresAuthorization)
        {
            if (!string.IsNullOrWhiteSpace(systemName))
            {
                throw SystemNotFound(systemName);
            }

            throw new HubException(
                $"Workable system '{system.Name ?? "<default>"}' is not available through SignalR because it does not require authorization.");
        }

        return system;
    }

    private static HubException SystemNotFound(string? systemName)
        => new($"Workable system '{systemName}' was not found.");

    private static WorkerId ParseWorkerId(string workerId)
        => Guid.TryParse(workerId, out var parsed)
            ? new WorkerId(parsed)
            : throw new HubException($"Worker id '{workerId}' is not valid.");
}
