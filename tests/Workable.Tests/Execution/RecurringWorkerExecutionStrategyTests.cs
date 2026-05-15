using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Recurrence")]
public sealed class RecurringWorkerExecutionStrategyTests
{
    [Fact]
    public async Task RecurringIterationsUseFreshScopedServices()
    {
        var attempts = 0;
        var scopeIds = new List<Guid>();
        var system = CreateSystem(
            services => services.AddScoped<ScopedMarker>(),
            async (context, input, cancellationToken) =>
            {
                attempts++;
                scopeIds.Add(context.Services.GetRequiredService<ScopedMarker>().Id);
                await Task.Yield();
                return WorkExecutionResult.Success();
            },
            recurrence => recurrence with
            {
                Interval = TimeSpan.FromMilliseconds(1),
            });

        await system.Start();

        var handle = await system.Queue.Enqueue("recurring-work");
        var workerId = RequiredWorkerId(handle);
        try
        {
            await Eventually(() => Task.FromResult(attempts >= 3));
        }
        finally
        {
            await CancelIfActive(system, workerId);
        }

        var completion = await handle.WaitForCompletion();
        Assert.Equal(WorkCompletionStatus.Canceled, completion.Status);
        Assert.True(attempts >= 3);
        Assert.Equal(scopeIds.Count, scopeIds.Distinct().Count());
    }

    [Fact]
    public async Task PushSkipsCurrentRecurrenceWait()
    {
        var attempts = 0;
        var system = CreateSystem(
            _ => { },
            (context, input, cancellationToken) =>
            {
                attempts++;
                return Task.FromResult(WorkExecutionResult.Success());
            },
            recurrence => recurrence with
            {
                Interval = TimeSpan.FromMinutes(5),
            });

        await system.Start();

        var handle = await system.Queue.Enqueue("recurring-work");
        var workerId = RequiredWorkerId(handle);
        try
        {
            await Eventually(async () => attempts == 1 && await WorkerIsWaiting(system, workerId));
            var waitingWorker = RequiredWorker(await system.Query.GetWorker(workerId));

            var push = await system.Workers.Execute(waitingWorker.Version, WorkAction.Push);

            Assert.True(push.IsAccepted);
            await Eventually(() => Task.FromResult(attempts >= 2));
        }
        finally
        {
            await CancelIfActive(system, workerId);
        }

        var completion = await handle.WaitForCompletion();
        Assert.Equal(WorkCompletionStatus.Canceled, completion.Status);
    }

    [Fact]
    public async Task DeclarativeFailureCanContinueUntilCircuitOpens()
    {
        var attempts = 0;
        var system = CreateSystem(
            _ => { },
            (context, input, cancellationToken) =>
            {
                attempts++;
                return Task.FromResult(WorkExecutionResult.Failure([WorkMessage.Error("sample.failure", "Failed.")]));
            },
            recurrence => recurrence with
            {
                Interval = TimeSpan.FromMilliseconds(1),
                CircuitBreakerFailureThreshold = 3,
                ContinueAfterFailure = true,
            });

        await system.Start();

        var completion = await (await system.Queue.Enqueue("recurring-work")).WaitForCompletion();

        Assert.Equal(3, attempts);
        Assert.Equal(WorkCompletionStatus.Failed, completion.Status);
        Assert.Equal("sample.failure", Assert.Single(completion.Messages).Code);
    }

    [Fact]
    public async Task ContinueAfterFailureFalseStopsAfterFirstFailedIteration()
    {
        var attempts = 0;
        var system = CreateSystem(
            _ => { },
            (context, input, cancellationToken) =>
            {
                attempts++;
                return Task.FromResult(WorkExecutionResult.Failure([WorkMessage.Error("sample.failure", "Failed.")]));
            },
            recurrence => recurrence with
            {
                ContinueAfterFailure = false,
            });

        await system.Start();

        var completion = await (await system.Queue.Enqueue("recurring-work")).WaitForCompletion();

        Assert.Equal(1, attempts);
        Assert.Equal(WorkCompletionStatus.Failed, completion.Status);
    }

    [Fact]
    public async Task CircuitBreakerPublishesEventWhenOpened()
    {
        var system = CreateSystem(
            _ => { },
            (context, input, cancellationToken) =>
                Task.FromResult(WorkExecutionResult.Failure([WorkMessage.Error("sample.failure", "Failed.")])),
            recurrence => recurrence with
            {
                Interval = TimeSpan.FromMilliseconds(1),
                CircuitBreakerFailureThreshold = 2,
                RaiseCircuitBreakerOpenedEvent = true,
            });

        await system.Start();
        await using var subscription = system.Events.Subscribe(new WorkEventFilter(EventType: "worker.recurrence.circuit_opened"));
        await using var reader = subscription.Read().GetAsyncEnumerator();

        var completion = await (await system.Queue.Enqueue("recurring-work")).WaitForCompletion();
        var workEvent = await ReadNext(reader);

        Assert.Equal(WorkCompletionStatus.Failed, completion.Status);
        Assert.Equal("worker.recurrence.circuit_opened", workEvent.EventType);
    }

    [Fact]
    public async Task RuntimeReconfigurationCanDisableRecurrenceWhileWaiting()
    {
        var attempts = 0;
        var system = CreateSystem(
            _ => { },
            (context, input, cancellationToken) =>
            {
                attempts++;
                return Task.FromResult(WorkExecutionResult.Success());
            },
            recurrence => recurrence with
            {
                Interval = TimeSpan.FromMinutes(5),
            });

        await system.Start();

        var handle = await system.Queue.Enqueue("recurring-work");
        var workerId = RequiredWorkerId(handle);
        await Eventually(async () => attempts == 1 && await WorkerIsWaiting(system, workerId));
        var worker = RequiredWorker(await system.Query.GetWorker(workerId));

        var reconfigure = await system.Workers.Reconfigure(
            worker.Version,
            new WorkerReconfiguration(Recurrence: WorkRecurrenceConfiguration.Disabled));
        var completion = await handle.WaitForCompletion();

        Assert.True(reconfigure.IsAccepted);
        Assert.Equal(1, attempts);
        Assert.Equal(WorkCompletionStatus.Completed, completion.Status);
    }

    [Fact]
    public async Task RuntimeReconfigurationCanDisableRecurrenceWhileExecuting()
    {
        var release = CreateSignal();
        var started = CreateSignal();
        var attempts = 0;
        var system = CreateSystem(
            _ => { },
            async (context, input, cancellationToken) =>
            {
                attempts++;
                started.SetResult();
                await release.Task.WaitAsync(cancellationToken);
                return WorkExecutionResult.Success();
            },
            recurrence => recurrence with
            {
                Interval = TimeSpan.FromMinutes(5),
            });

        await system.Start();

        var handle = await system.Queue.Enqueue("recurring-work");
        var workerId = RequiredWorkerId(handle);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var worker = RequiredWorker(await system.Query.GetWorker(workerId));

        var reconfigure = await system.Workers.Reconfigure(
            worker.Version,
            new WorkerReconfiguration(Recurrence: WorkRecurrenceConfiguration.Disabled));
        release.SetResult();
        var completion = await handle.WaitForCompletion();

        Assert.True(reconfigure.IsAccepted);
        Assert.Equal(1, attempts);
        Assert.Equal(WorkCompletionStatus.Completed, completion.Status);
    }

    [Fact]
    public async Task PauseWhileWaitingCompletesAsPausedAndStartResumes()
    {
        var attempts = 0;
        var system = CreateSystem(
            _ => { },
            (context, input, cancellationToken) =>
            {
                attempts++;
                return Task.FromResult(WorkExecutionResult.Success());
            },
            recurrence => recurrence with
            {
                Interval = TimeSpan.FromMinutes(5),
            });

        await system.Start();

        var handle = await system.Queue.Enqueue("recurring-work");
        var workerId = RequiredWorkerId(handle);
        await Eventually(async () => attempts == 1 && await WorkerIsWaiting(system, workerId));
        var waitingWorker = RequiredWorker(await system.Query.GetWorker(workerId));

        var pause = await system.Workers.Execute(waitingWorker.Version, WorkAction.Pause);
        var paused = await handle.WaitForCompletion();
        var start = await system.Workers.Execute(RequiredCompletionWorker(paused).Version, WorkAction.Start);
        await Eventually(() => Task.FromResult(attempts >= 2));
        await CancelIfActive(system, workerId);
        var canceled = await handle.WaitForCompletion();

        Assert.True(pause.IsAccepted);
        Assert.Equal(WorkCompletionStatus.Paused, paused.Status);
        Assert.True(start.IsAccepted);
        Assert.Equal(WorkCompletionStatus.Canceled, canceled.Status);
    }

    [Fact]
    public async Task PauseWhileExecutingCancelsIterationAndCompletesAsPaused()
    {
        var tokenCanceled = CreateSignal();
        var started = CreateSignal();
        var system = CreateSystem(
            _ => { },
            async (context, input, cancellationToken) =>
            {
                started.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    tokenCanceled.SetResult();
                    throw;
                }

                return WorkExecutionResult.Success();
            },
            recurrence => recurrence);

        await system.Start();

        var handle = await system.Queue.Enqueue("recurring-work");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var worker = RequiredWorker(await system.Query.GetWorker(RequiredWorkerId(handle)));

        var pause = await system.Workers.Execute(worker.Version, WorkAction.Pause);
        var completion = await handle.WaitForCompletion();

        await tokenCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(pause.IsAccepted);
        Assert.Equal(WorkCompletionStatus.Paused, completion.Status);
    }

    [Fact]
    public async Task CancelWhileWaitingCompletesAsCanceled()
    {
        var attempts = 0;
        var system = CreateSystem(
            _ => { },
            (context, input, cancellationToken) =>
            {
                attempts++;
                return Task.FromResult(WorkExecutionResult.Success());
            },
            recurrence => recurrence with
            {
                Interval = TimeSpan.FromMinutes(5),
            });

        await system.Start();

        var handle = await system.Queue.Enqueue("recurring-work");
        var workerId = RequiredWorkerId(handle);
        await Eventually(async () => attempts == 1 && await WorkerIsWaiting(system, workerId));
        var worker = RequiredWorker(await system.Query.GetWorker(workerId));

        var cancel = await system.Workers.Execute(worker.Version, WorkAction.Cancel);
        var completion = await handle.WaitForCompletion();

        Assert.True(cancel.IsAccepted);
        Assert.Equal(WorkCompletionStatus.Canceled, completion.Status);
    }

    [Fact]
    public async Task TransientRetryRunsInsideRecurringIteration()
    {
        var attempts = 0;
        var system = CreateSystem(
            _ => { },
            (context, input, cancellationToken) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new TimeoutException("Try again.");
                }

                return Task.FromResult(WorkExecutionResult.Success());
            },
            recurrence => recurrence with
            {
                Interval = TimeSpan.FromMinutes(5),
            },
            configuration => configuration
                .RetryTransientFailures(2, TimeSpan.FromMilliseconds(1), jitter: TimeSpan.Zero)
                .ClassifyExceptions(exception => exception is TimeoutException
                    ? WorkExceptionClassification.Transient
                    : WorkExceptionClassification.Unknown));

        await system.Start();

        var handle = await system.Queue.Enqueue("recurring-work");
        var workerId = RequiredWorkerId(handle);
        await Eventually(async () => attempts == 2 && await WorkerIsWaiting(system, workerId));
        await CancelIfActive(system, workerId);
        var completion = await handle.WaitForCompletion();

        Assert.Equal(2, attempts);
        Assert.Equal(WorkCompletionStatus.Canceled, completion.Status);
        Assert.Contains(completion.Worker?.Iterations ?? [], iteration => iteration.Status == WorkCompletionStatus.Completed);
    }

    [Fact]
    public async Task ExceptionFailuresCanContinueUntilCircuitOpens()
    {
        var attempts = 0;
        var system = CreateSystem(
            _ => { },
            (context, input, cancellationToken) =>
            {
                attempts++;
                throw new InvalidOperationException("Not transient.");
            },
            recurrence => recurrence with
            {
                Interval = TimeSpan.FromMilliseconds(1),
                CircuitBreakerFailureThreshold = 2,
            });

        await system.Start();

        var completion = await (await system.Queue.Enqueue("recurring-work")).WaitForCompletion();

        Assert.Equal(2, attempts);
        Assert.Equal(WorkCompletionStatus.Failed, completion.Status);
        Assert.Equal("workable.execution.exception", Assert.Single(completion.Messages).Code);
    }

    [Fact]
    public async Task IterationHistoryRetainsConfiguredSuccessfulAndFailedCounts()
    {
        var attempts = 0;
        var system = CreateSystem(
            _ => { },
            async (context, input, cancellationToken) =>
            {
                var attempt = Interlocked.Increment(ref attempts);
                if (attempt > 5)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                var output = WorkOutput.FromValue(attempt);
                return attempt is 3 or 5
                    ? WorkExecutionResult.Failure([WorkMessage.Error("sample.failure", "Failed.")], output)
                    : WorkExecutionResult.Success(output);
            },
            recurrence => recurrence with
            {
                Interval = TimeSpan.FromMilliseconds(1),
                CircuitBreakerFailureThreshold = 10,
                RetainedSuccessfulIterations = 2,
                RetainedFailedIterations = 1,
            });

        await system.Start();

        var handle = await system.Queue.Enqueue("recurring-work");
        var workerId = RequiredWorkerId(handle);
        await Eventually(() => Task.FromResult(Volatile.Read(ref attempts) >= 6));
        await CancelIfActive(system, workerId);
        var completion = await handle.WaitForCompletion();
        var iterations = completion.Worker?.Iterations ?? [];

        Assert.Equal(WorkCompletionStatus.Canceled, completion.Status);
        Assert.Equal(4, iterations.Count);
        Assert.Equal(
            [WorkCompletionStatus.Completed, WorkCompletionStatus.Completed, WorkCompletionStatus.Failed, WorkCompletionStatus.Canceled],
            iterations.Select(iteration => iteration.Status));
        Assert.Equal([2, 4, 5, null], iterations.Select(iteration => iteration.Output?.ToValue<int>()));
    }

    [Fact]
    public async Task WaitingRecurringWorkerHoldsConcurrencyCapacity()
    {
        var firstAttempts = 0;
        var secondStarted = CreateSignal();
        var definition = WorkDefinition.Create("recurring-work", "Runs repeatedly.",
            configuration: WorkConfiguration.Default with
            {
                Recurrence = WorkRecurrenceConfiguration.Every(TimeSpan.FromMinutes(5)),
                Concurrency = new WorkConcurrencyConfiguration
                {
                    IsEnabled = true,
                    MaximumCapacity = 1,
                    LimitReachedBehavior = WorkConcurrencyLimitReachedBehavior.DeferStart,
                    BlockingMode = WorkConcurrencyBlockingMode.WhileExecuting,
                },
            });
        var system = CreateSystem(definition, (context, input, cancellationToken) =>
        {
            if (Interlocked.Increment(ref firstAttempts) == 1)
            {
                return Task.FromResult(WorkExecutionResult.Success());
            }

            secondStarted.SetResult();
            return Task.FromResult(WorkExecutionResult.Success());
        });

        await system.Start();

        var first = await system.Queue.Enqueue("recurring-work");
        var firstWorkerId = RequiredWorkerId(first);
        await Eventually(async () => firstAttempts == 1 && await WorkerIsWaiting(system, firstWorkerId));
        var second = await system.Queue.Enqueue("recurring-work");

        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.False(secondStarted.Task.IsCompleted);

        await CancelIfActive(system, firstWorkerId);
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await CancelIfActive(system, RequiredWorkerId(second));
    }

    private static IWorkSystem CreateSystem(
        Action<IServiceCollection> configureServices,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute,
        Func<WorkRecurrenceConfiguration, WorkRecurrenceConfiguration> configureRecurrence)
        => CreateSystem(configureServices, execute, configureRecurrence, _ => { });

    private static IWorkSystem CreateSystem(
        Action<IServiceCollection> configureServices,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute,
        Func<WorkRecurrenceConfiguration, WorkRecurrenceConfiguration> configureRecurrence,
        Action<IWorkConfigurationBuilder> configureWork)
    {
        var services = new ServiceCollection();
        configureServices(services);
        return services
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create("recurring-work", "Runs repeatedly.",
                    configuration: WorkConfiguration.Default with
                    {
                        Recurrence = configureRecurrence(WorkRecurrenceConfiguration.Every(TimeSpan.FromMilliseconds(1))),
                    }),
                execute,
                configureWork))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;
    }

    private static IWorkSystem CreateSystem(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute)
        => new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(definition, execute))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

    private static WorkerId RequiredWorkerId(IWorkerHandle handle)
        => handle.WorkerId ?? throw new InvalidOperationException("Expected worker id.");

    private static WorkerSnapshot RequiredWorker(WorkerSnapshot? worker)
        => worker ?? throw new InvalidOperationException("Expected worker.");

    private static WorkerSnapshot RequiredCompletionWorker(WorkCompletion completion)
        => completion.Worker ?? throw new InvalidOperationException("Expected completion worker.");

    private static async Task<bool> WorkerIsWaiting(IWorkSystem system, WorkerId workerId)
        => (await system.Query.GetWorker(workerId))?.State == WorkerState.Waiting;

    private static async Task CancelIfActive(IWorkSystem system, WorkerId workerId)
    {
        var worker = await system.Query.GetWorker(workerId);
        if (worker is null || worker.State is WorkerState.Canceled or WorkerState.Completed)
        {
            return;
        }

        await system.Workers.Execute(worker.Version, WorkAction.Cancel);
    }

    private static TaskCompletionSource CreateSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<WorkEvent> ReadNext(IAsyncEnumerator<WorkEvent> reader)
    {
        var hasEvent = await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(hasEvent);
        return reader.Current;
    }

    private static async Task Eventually(Func<Task<bool>> condition)
    {
        var timeout = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < timeout)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(await condition());
    }

    private sealed class ScopedMarker
    {
        public Guid Id { get; } = Guid.NewGuid();
    }
}
