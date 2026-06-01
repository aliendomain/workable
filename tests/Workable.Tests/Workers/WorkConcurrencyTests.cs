using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Concurrency")]
public sealed class WorkConcurrencyTests
{
    [Fact]
    public async Task DisabledConcurrencyAllowsMultipleWorkersToRun()
    {
        var firstStarted = CreateSignal();
        var secondStarted = CreateSignal();
        var release = CreateSignal();
        var starts = 0;
        var definition = WorkDefinition.Create("no-limit", "Runs without concurrency limits.");
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            if (Interlocked.Increment(ref starts) == 1)
            {
                firstStarted.TrySetResult();
            }
            else
            {
                secondStarted.TrySetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
            return WorkExecutionResult.Success();
        });

        await system.Start();

        var first = await system.Queue.Enqueue("no-limit");
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await system.Queue.Enqueue("no-limit");
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();

        Assert.Equal(WorkQueueStatus.Accepted, first.QueueOutcome.Status);
        Assert.Equal(WorkQueueStatus.Accepted, second.QueueOutcome.Status);
        Assert.True((await first.WaitForCompletion()).IsCompletedSuccessfully);
        Assert.True((await second.WaitForCompletion()).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task IgnoreRejectsWorkersWhenConcurrencyCapacityIsReached()
    {
        var firstStarted = CreateSignal();
        var release = CreateSignal();
        var definition = WorkDefinition.Create("ignore-limit", "Rejects over-capacity workers.",
            configuration: ConcurrencyConfiguration(WorkConcurrencyLimitReachedBehavior.Ignore));
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            firstStarted.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return WorkExecutionResult.Success();
        });

        await system.Start();

        var first = await system.Queue.Enqueue("ignore-limit");
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await system.Queue.Enqueue("ignore-limit");
        release.SetResult();

        Assert.Equal(WorkQueueStatus.Accepted, first.QueueOutcome.Status);
        Assert.Equal(WorkQueueStatus.Invalid, second.QueueOutcome.Status);
        Assert.Contains(second.QueueOutcome.Messages, message => message.Code == "workable.concurrency.capacity_reached");
        Assert.Equal(WorkCompletionStatus.Invalid, (await second.WaitForCompletion()).Status);
        Assert.True((await first.WaitForCompletion()).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DeferStartQueuesWorkersUntilConcurrencyCapacityIsAvailable()
    {
        var firstStarted = CreateSignal();
        var secondStarted = CreateSignal();
        var release = CreateSignal();
        var starts = 0;
        var definition = WorkDefinition.Create("deferred-limit", "Defers over-capacity workers.",
            configuration: ConcurrencyConfiguration(WorkConcurrencyLimitReachedBehavior.DeferStart));
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            if (Interlocked.Increment(ref starts) == 1)
            {
                firstStarted.SetResult();
            }
            else
            {
                secondStarted.SetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
            return WorkExecutionResult.Success();
        });

        await system.Start();

        var first = await system.Queue.Enqueue("deferred-limit");
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await system.Queue.Enqueue("deferred-limit");
        await RequiredQueuedWorker(system, second);

        Assert.False(secondStarted.Task.IsCompleted);

        release.SetResult();
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WorkQueueStatus.Accepted, first.QueueOutcome.Status);
        Assert.Equal(WorkQueueStatus.Accepted, second.QueueOutcome.Status);
        Assert.Contains(second.QueueOutcome.Messages, message => message.Code == "workable.concurrency.start_deferred");
        Assert.True((await first.WaitForCompletion()).IsCompletedSuccessfully);
        Assert.True((await second.WaitForCompletion()).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DefaultBlockingModeCountsFailedWorkersAgainstConcurrencyCapacity()
    {
        var secondStarted = CreateSignal();
        var attempts = 0;
        var definition = WorkDefinition.Create("failed-limit", "Failed workers hold capacity by default.",
            configuration: ConcurrencyConfiguration(WorkConcurrencyLimitReachedBehavior.DeferStart));
        var system = CreateSystem(definition, (context, input, cancellationToken) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                return Task.FromResult(WorkExecutionResult.Failure([WorkMessage.Error("test.failure", "The work failed.")]));
            }

            secondStarted.SetResult();
            return Task.FromResult(WorkExecutionResult.Success());
        });

        await system.Start();

        var first = await system.Queue.Enqueue("failed-limit");
        var failed = await first.WaitForCompletion();
        var failedWorker = RequiredCompletionWorker(failed);
        var second = await system.Queue.Enqueue("failed-limit");

        Assert.Equal(WorkCompletionStatus.Failed, failed.Status);
        await RequiredQueuedWorker(system, second);
        Assert.False(secondStarted.Task.IsCompleted);

        var cancelFailed = await system.Workers.Execute(failedWorker.Version, WorkAction.Cancel);
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(cancelFailed.IsAccepted);
        Assert.True((await second.WaitForCompletion()).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task StrictConcurrencyRejectsManualStartWhenCapacityIsReached()
    {
        var firstStarted = CreateSignal();
        var secondStarted = CreateSignal();
        var release = CreateSignal();
        var starts = 0;
        var definition = WorkDefinition.Create("strict-limit", "Manual start respects strict concurrency.",
            configuration: ConcurrencyConfiguration(
                WorkConcurrencyLimitReachedBehavior.DeferStart,
                overrideBehavior: WorkConcurrencyOverrideBehavior.Strict));
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            if (Interlocked.Increment(ref starts) == 1)
            {
                firstStarted.SetResult();
            }
            else
            {
                secondStarted.SetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
            return WorkExecutionResult.Success();
        });

        await system.Start();

        var first = await system.Queue.Enqueue("strict-limit");
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await system.Queue.Enqueue("strict-limit");
        var secondWorker = RequiredWorker(await system.Query.Worker(RequiredWorkerId(second)));

        var start = await system.Workers.Execute(secondWorker.Version, WorkAction.Start);

        Assert.Equal(WorkActionStatus.Invalid, start.Status);
        Assert.Contains(start.Messages, message => message.Code == "workable.concurrency.capacity_reached");
        Assert.False(secondStarted.Task.IsCompleted);
        release.SetResult();
        Assert.True((await first.WaitForCompletion()).IsCompletedSuccessfully);
        Assert.True((await second.WaitForCompletion()).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task FlexibleConcurrencyAllowsManualStartWhenCapacityIsReached()
    {
        var firstStarted = CreateSignal();
        var secondStarted = CreateSignal();
        var release = CreateSignal();
        var starts = 0;
        var definition = WorkDefinition.Create("flexible-limit", "Manual start can override flexible concurrency.",
            configuration: ConcurrencyConfiguration(
                WorkConcurrencyLimitReachedBehavior.DeferStart,
                overrideBehavior: WorkConcurrencyOverrideBehavior.Flexible));
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            if (Interlocked.Increment(ref starts) == 1)
            {
                firstStarted.SetResult();
            }
            else
            {
                secondStarted.SetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
            return WorkExecutionResult.Success();
        });

        await system.Start();

        var first = await system.Queue.Enqueue("flexible-limit");
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await system.Queue.Enqueue("flexible-limit");
        var secondWorker = RequiredWorker(await system.Query.Worker(RequiredWorkerId(second)));

        var start = await system.Workers.Execute(secondWorker.Version, WorkAction.Start);
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();

        Assert.True(start.IsAccepted);
        Assert.True((await first.WaitForCompletion()).IsCompletedSuccessfully);
        Assert.True((await second.WaitForCompletion()).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task SimultaneousQueueRequestsCannotOverbookIgnoreCapacity()
    {
        var started = CreateSignal();
        var release = CreateSignal();
        var definition = WorkDefinition.Create("race-ignore", "Rejects concurrent over-capacity queue requests.",
            configuration: ConcurrencyConfiguration(WorkConcurrencyLimitReachedBehavior.Ignore));
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            started.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            return WorkExecutionResult.Success();
        });

        await system.Start();

        var queueTasks = Enumerable.Range(0, 20)
            .Select(_ => system.Queue.Enqueue("race-ignore"))
            .ToArray();
        var handles = await Task.WhenAll(queueTasks);
        release.SetResult();

        Assert.Equal(1, handles.Count(handle => handle.QueueOutcome.Status == WorkQueueStatus.Accepted));
        Assert.Equal(19, handles.Count(handle => handle.QueueOutcome.Status == WorkQueueStatus.Invalid));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True((await handles.Single(handle => handle.QueueOutcome.IsAccepted).WaitForCompletion()).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task CapacityGreaterThanOneAllowsThatManyWorkersBeforeDeferring()
    {
        var firstStarted = CreateSignal();
        var secondStarted = CreateSignal();
        var thirdStarted = CreateSignal();
        var release = CreateSignal();
        var starts = 0;
        var definition = WorkDefinition.Create("capacity-two", "Allows two workers before deferring.",
            configuration: ConcurrencyConfiguration(
                WorkConcurrencyLimitReachedBehavior.DeferStart,
                maximumCapacity: 2));
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            var start = Interlocked.Increment(ref starts);
            if (start == 1)
            {
                firstStarted.SetResult();
            }
            else if (start == 2)
            {
                secondStarted.SetResult();
            }
            else
            {
                thirdStarted.SetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
            return WorkExecutionResult.Success();
        });

        await system.Start();

        var first = await system.Queue.Enqueue("capacity-two");
        var second = await system.Queue.Enqueue("capacity-two");
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var third = await system.Queue.Enqueue("capacity-two");

        await RequiredQueuedWorker(system, third);
        Assert.False(thirdStarted.Task.IsCompleted);

        release.SetResult();
        await thirdStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True((await first.WaitForCompletion()).IsCompletedSuccessfully);
        Assert.True((await second.WaitForCompletion()).IsCompletedSuccessfully);
        Assert.True((await third.WaitForCompletion()).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task SeparateDefinitionsDoNotBlockEachOther()
    {
        var firstStarted = CreateSignal();
        var secondStarted = CreateSignal();
        var release = CreateSignal();
        var firstDefinition = WorkDefinition.Create("definition-one", "Uses its own capacity.",
            configuration: ConcurrencyConfiguration(WorkConcurrencyLimitReachedBehavior.DeferStart));
        var secondDefinition = WorkDefinition.Create("definition-two", "Uses separate capacity.",
            configuration: ConcurrencyConfiguration(WorkConcurrencyLimitReachedBehavior.DeferStart));
        var system = CreateSystem(builder =>
        {
            builder.AddWork(firstDefinition, async (context, input, cancellationToken) =>
            {
                firstStarted.SetResult();
                await release.Task.WaitAsync(cancellationToken);
                return WorkExecutionResult.Success();
            });
            builder.AddWork(secondDefinition, async (context, input, cancellationToken) =>
            {
                secondStarted.SetResult();
                await release.Task.WaitAsync(cancellationToken);
                return WorkExecutionResult.Success();
            });
        });

        await system.Start();

        var first = await system.Queue.Enqueue("definition-one");
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await system.Queue.Enqueue("definition-two");
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();

        Assert.True((await first.WaitForCompletion()).IsCompletedSuccessfully);
        Assert.True((await second.WaitForCompletion()).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task PerSubjectConcurrencyAllowsDifferentSubjectsToRunConcurrently()
    {
        var firstStarted = CreateSignal();
        var secondStarted = CreateSignal();
        var release = CreateSignal();
        var starts = 0;
        var definition = WorkDefinition.Create("subject-capacity", "Limits capacity per subject.",
            configuration: ConcurrencyConfiguration(
                WorkConcurrencyLimitReachedBehavior.DeferStart,
                scope: WorkConcurrencyScope.PerSubject));
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            if (Interlocked.Increment(ref starts) == 1)
            {
                firstStarted.SetResult();
            }
            else
            {
                secondStarted.SetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
            return WorkExecutionResult.Success();
        });

        await system.Start();

        var first = await system.Queue.Enqueue("subject-capacity", SubjectInput("one"));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await system.Queue.Enqueue("subject-capacity", SubjectInput("two"));
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();

        Assert.True((await first.WaitForCompletion()).IsCompletedSuccessfully);
        Assert.True((await second.WaitForCompletion()).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task PerSubjectConcurrencyDefersSameSubjectUntilCapacityIsAvailable()
    {
        var firstStarted = CreateSignal();
        var secondStarted = CreateSignal();
        var release = CreateSignal();
        var starts = 0;
        var definition = WorkDefinition.Create("same-subject-capacity", "Defers workers with the same subject.",
            configuration: ConcurrencyConfiguration(
                WorkConcurrencyLimitReachedBehavior.DeferStart,
                scope: WorkConcurrencyScope.PerSubject));
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            if (Interlocked.Increment(ref starts) == 1)
            {
                firstStarted.SetResult();
            }
            else
            {
                secondStarted.SetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
            return WorkExecutionResult.Success();
        });

        await system.Start();

        var first = await system.Queue.Enqueue("same-subject-capacity", SubjectInput("same"));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await system.Queue.Enqueue("same-subject-capacity", SubjectInput("same"));

        await RequiredQueuedWorker(system, second);
        Assert.False(secondStarted.Task.IsCompleted);

        release.SetResult();
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True((await first.WaitForCompletion()).IsCompletedSuccessfully);
        Assert.True((await second.WaitForCompletion()).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DeferredSubjectDoesNotBlockDifferentSubject()
    {
        var started = new[] { CreateSignal(), CreateSignal() };
        var release = CreateSignal();
        var starts = 0;
        var definition = WorkDefinition.Create("subject-drain", "Starts unrelated subjects even when a deferred subject is blocked.",
            configuration: ConcurrencyConfiguration(
                WorkConcurrencyLimitReachedBehavior.DeferStart,
                scope: WorkConcurrencyScope.PerSubject));
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            var start = Interlocked.Increment(ref starts);
            if (start <= started.Length)
            {
                started[start - 1].SetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
            return WorkExecutionResult.Success();
        });

        await system.Start();

        var first = await system.Queue.Enqueue("subject-drain", SubjectInput("blocked"));
        await started[0].Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await system.Queue.Enqueue("subject-drain", SubjectInput("blocked"));
        var third = await system.Queue.Enqueue("subject-drain", SubjectInput("free"));
        await started[1].Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WorkerState.Queued, RequiredWorker(await system.Query.Worker(RequiredWorkerId(second))).State);
        release.SetResult();

        Assert.True((await first.WaitForCompletion()).IsCompletedSuccessfully);
        Assert.True((await third.WaitForCompletion()).IsCompletedSuccessfully);
        Assert.True((await second.WaitForCompletion()).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task PerConcurrencyKeyAllowsDifferentKeysToRunConcurrently()
    {
        var firstStarted = CreateSignal();
        var secondStarted = CreateSignal();
        var release = CreateSignal();
        var starts = 0;
        var definition = WorkDefinition.Create("key-capacity", "Limits capacity per concurrency key.",
            configuration: ConcurrencyConfiguration(
                WorkConcurrencyLimitReachedBehavior.DeferStart,
                scope: WorkConcurrencyScope.PerConcurrencyKey));
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            if (Interlocked.Increment(ref starts) == 1)
            {
                firstStarted.SetResult();
            }
            else
            {
                secondStarted.SetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
            return WorkExecutionResult.Success();
        });

        await system.Start();

        var first = await system.Queue.Enqueue("key-capacity", ConcurrencyKeyInput("one"));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await system.Queue.Enqueue("key-capacity", ConcurrencyKeyInput("two"));
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();

        Assert.True((await first.WaitForCompletion()).IsCompletedSuccessfully);
        Assert.True((await second.WaitForCompletion()).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task PerConcurrencyKeyDefersSameKeyUntilCapacityIsAvailable()
    {
        var firstStarted = CreateSignal();
        var secondStarted = CreateSignal();
        var release = CreateSignal();
        var starts = 0;
        var definition = WorkDefinition.Create("same-key-capacity", "Defers workers with the same concurrency key.",
            configuration: ConcurrencyConfiguration(
                WorkConcurrencyLimitReachedBehavior.DeferStart,
                scope: WorkConcurrencyScope.PerConcurrencyKey));
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            if (Interlocked.Increment(ref starts) == 1)
            {
                firstStarted.SetResult();
            }
            else
            {
                secondStarted.SetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
            return WorkExecutionResult.Success();
        });

        await system.Start();

        var first = await system.Queue.Enqueue("same-key-capacity", ConcurrencyKeyInput("same"));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await system.Queue.Enqueue("same-key-capacity", ConcurrencyKeyInput("same"));

        await RequiredQueuedWorker(system, second);
        Assert.False(secondStarted.Task.IsCompleted);

        release.SetResult();
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True((await first.WaitForCompletion()).IsCompletedSuccessfully);
        Assert.True((await second.WaitForCompletion()).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task PerSubjectConcurrencyRejectsQueueRequestsWithoutSubject()
    {
        var definition = WorkDefinition.Create("missing-subject", "Requires a subject for subject-scoped concurrency.",
            configuration: ConcurrencyConfiguration(
                WorkConcurrencyLimitReachedBehavior.DeferStart,
                scope: WorkConcurrencyScope.PerSubject));
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue("missing-subject");

        Assert.Equal(WorkQueueStatus.Invalid, handle.QueueOutcome.Status);
        Assert.Contains(handle.QueueOutcome.Messages, message => message.Code == "workable.concurrency.subject_required");
    }

    [Fact]
    public async Task PerConcurrencyKeyRejectsQueueRequestsWithoutKey()
    {
        var definition = WorkDefinition.Create("missing-key", "Requires a key for key-scoped concurrency.",
            configuration: ConcurrencyConfiguration(
                WorkConcurrencyLimitReachedBehavior.DeferStart,
                scope: WorkConcurrencyScope.PerConcurrencyKey));
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue("missing-key");

        Assert.Equal(WorkQueueStatus.Invalid, handle.QueueOutcome.Status);
        Assert.Contains(handle.QueueOutcome.Messages, message => message.Code == "workable.concurrency.key_required");
    }

    [Theory]
    [InlineData(WorkConcurrencyScope.PerSubject)]
    [InlineData(WorkConcurrencyScope.PerConcurrencyKey)]
    public async Task IgnoreRejectsSameScopedGroupWhenCapacityIsReached(WorkConcurrencyScope scope)
    {
        var firstStarted = CreateSignal();
        var release = CreateSignal();
        var definition = WorkDefinition.Create($"ignore-{scope}".ToLowerInvariant(), "Rejects over-capacity workers in the same scoped group.",
            configuration: ConcurrencyConfiguration(
                WorkConcurrencyLimitReachedBehavior.Ignore,
                scope: scope));
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            firstStarted.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            return WorkExecutionResult.Success();
        });

        await system.Start();

        var first = await system.Queue.Enqueue($"ignore-{scope}".ToLowerInvariant(), ScopedInput(scope, "same"));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await system.Queue.Enqueue($"ignore-{scope}".ToLowerInvariant(), ScopedInput(scope, "same"));
        release.SetResult();

        Assert.Equal(WorkQueueStatus.Accepted, first.QueueOutcome.Status);
        Assert.Equal(WorkQueueStatus.Invalid, second.QueueOutcome.Status);
        Assert.Contains(second.QueueOutcome.Messages, message => message.Code == "workable.concurrency.capacity_reached");
        Assert.True((await first.WaitForCompletion()).IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(WorkConcurrencyScope.PerSubject)]
    [InlineData(WorkConcurrencyScope.PerConcurrencyKey)]
    public async Task ScopedCapacityGreaterThanOneAllowsThatManyPerGroupBeforeDeferring(WorkConcurrencyScope scope)
    {
        var firstStarted = CreateSignal();
        var secondStarted = CreateSignal();
        var thirdStarted = CreateSignal();
        var release = CreateSignal();
        var starts = 0;
        var definition = WorkDefinition.Create($"capacity-two-{scope}".ToLowerInvariant(), "Allows configured capacity within each scoped group.",
            configuration: ConcurrencyConfiguration(
                WorkConcurrencyLimitReachedBehavior.DeferStart,
                maximumCapacity: 2,
                scope: scope));
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            var start = Interlocked.Increment(ref starts);
            if (start == 1)
            {
                firstStarted.SetResult();
            }
            else if (start == 2)
            {
                secondStarted.SetResult();
            }
            else
            {
                thirdStarted.SetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
            return WorkExecutionResult.Success();
        });

        await system.Start();

        var first = await system.Queue.Enqueue($"capacity-two-{scope}".ToLowerInvariant(), ScopedInput(scope, "same"));
        var second = await system.Queue.Enqueue($"capacity-two-{scope}".ToLowerInvariant(), ScopedInput(scope, "same"));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var third = await system.Queue.Enqueue($"capacity-two-{scope}".ToLowerInvariant(), ScopedInput(scope, "same"));

        await RequiredQueuedWorker(system, third);
        Assert.False(thirdStarted.Task.IsCompleted);

        release.SetResult();
        await thirdStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True((await first.WaitForCompletion()).IsCompletedSuccessfully);
        Assert.True((await second.WaitForCompletion()).IsCompletedSuccessfully);
        Assert.True((await third.WaitForCompletion()).IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(WorkConcurrencyScope.PerSubject)]
    [InlineData(WorkConcurrencyScope.PerConcurrencyKey)]
    public async Task SimultaneousScopedQueueRequestsCannotOverbookIgnoreCapacity(WorkConcurrencyScope scope)
    {
        var started = CreateSignal();
        var release = CreateSignal();
        var definition = WorkDefinition.Create($"race-{scope}".ToLowerInvariant(), "Rejects concurrent over-capacity queue requests in the same scoped group.",
            configuration: ConcurrencyConfiguration(
                WorkConcurrencyLimitReachedBehavior.Ignore,
                scope: scope));
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            started.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            return WorkExecutionResult.Success();
        });

        await system.Start();

        var queueTasks = Enumerable.Range(0, 20)
            .Select(_ => system.Queue.Enqueue($"race-{scope}".ToLowerInvariant(), ScopedInput(scope, "same")))
            .ToArray();
        var handles = await Task.WhenAll(queueTasks);
        release.SetResult();

        Assert.Equal(1, handles.Count(handle => handle.QueueOutcome.Status == WorkQueueStatus.Accepted));
        Assert.Equal(19, handles.Count(handle => handle.QueueOutcome.Status == WorkQueueStatus.Invalid));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True((await handles.Single(handle => handle.QueueOutcome.IsAccepted).WaitForCompletion()).IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(WorkConcurrencyScope.PerSubject)]
    [InlineData(WorkConcurrencyScope.PerConcurrencyKey)]
    public async Task StrictManualStartRejectsScopedWorkerWhenGroupCapacityIsReached(WorkConcurrencyScope scope)
    {
        var firstStarted = CreateSignal();
        var secondStarted = CreateSignal();
        var release = CreateSignal();
        var definition = WorkDefinition.Create($"strict-manual-{scope}".ToLowerInvariant(), "Strict manual start respects scoped capacity.",
            configuration: ConcurrencyConfiguration(
                WorkConcurrencyLimitReachedBehavior.DeferStart,
                overrideBehavior: WorkConcurrencyOverrideBehavior.Strict,
                scope: scope) with
            {
                Start = WorkStartConfiguration.DoNotStart,
            });
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            if (!firstStarted.Task.IsCompleted)
            {
                firstStarted.SetResult();
            }
            else
            {
                secondStarted.SetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
            return WorkExecutionResult.Success();
        });

        await system.Start();

        var first = await system.Queue.Enqueue($"strict-manual-{scope}".ToLowerInvariant(), ScopedInput(scope, "same"));
        var second = await system.Queue.Enqueue($"strict-manual-{scope}".ToLowerInvariant(), ScopedInput(scope, "same"));
        var firstWorker = RequiredWorker(await system.Query.Worker(RequiredWorkerId(first)));
        var secondWorker = RequiredWorker(await system.Query.Worker(RequiredWorkerId(second)));

        var firstStart = await system.Workers.Execute(firstWorker.Version, WorkAction.Start);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondStart = await system.Workers.Execute(secondWorker.Version, WorkAction.Start);
        release.SetResult();

        Assert.True(firstStart.IsAccepted);
        Assert.Equal(WorkActionStatus.Invalid, secondStart.Status);
        Assert.Contains(secondStart.Messages, message => message.Code == "workable.concurrency.capacity_reached");
        Assert.False(secondStarted.Task.IsCompleted);
        Assert.True((await first.WaitForCompletion()).IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(WorkConcurrencyScope.PerSubject)]
    [InlineData(WorkConcurrencyScope.PerConcurrencyKey)]
    public async Task FlexibleManualStartCanOverrideScopedCapacity(WorkConcurrencyScope scope)
    {
        var firstStarted = CreateSignal();
        var secondStarted = CreateSignal();
        var release = CreateSignal();
        var starts = 0;
        var definition = WorkDefinition.Create($"flexible-manual-{scope}".ToLowerInvariant(), "Flexible manual start can override scoped capacity.",
            configuration: ConcurrencyConfiguration(
                WorkConcurrencyLimitReachedBehavior.DeferStart,
                overrideBehavior: WorkConcurrencyOverrideBehavior.Flexible,
                scope: scope) with
            {
                Start = WorkStartConfiguration.DoNotStart,
            });
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            if (Interlocked.Increment(ref starts) == 1)
            {
                firstStarted.SetResult();
            }
            else
            {
                secondStarted.SetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
            return WorkExecutionResult.Success();
        });

        await system.Start();

        var first = await system.Queue.Enqueue($"flexible-manual-{scope}".ToLowerInvariant(), ScopedInput(scope, "same"));
        var second = await system.Queue.Enqueue($"flexible-manual-{scope}".ToLowerInvariant(), ScopedInput(scope, "same"));
        var firstWorker = RequiredWorker(await system.Query.Worker(RequiredWorkerId(first)));
        var secondWorker = RequiredWorker(await system.Query.Worker(RequiredWorkerId(second)));

        var firstStart = await system.Workers.Execute(firstWorker.Version, WorkAction.Start);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondStart = await system.Workers.Execute(secondWorker.Version, WorkAction.Start);
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();

        Assert.True(firstStart.IsAccepted);
        Assert.True(secondStart.IsAccepted);
        Assert.True((await first.WaitForCompletion()).IsCompletedSuccessfully);
        Assert.True((await second.WaitForCompletion()).IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(WorkConcurrencyScope.PerSubject)]
    [InlineData(WorkConcurrencyScope.PerConcurrencyKey)]
    public async Task SameScopedGroupAcrossDefinitionsDoesNotShareCapacity(WorkConcurrencyScope scope)
    {
        var firstStarted = CreateSignal();
        var secondStarted = CreateSignal();
        var release = CreateSignal();
        var firstDefinition = WorkDefinition.Create($"first-{scope}".ToLowerInvariant(), "Uses scoped capacity in one definition.",
            configuration: ConcurrencyConfiguration(
                WorkConcurrencyLimitReachedBehavior.DeferStart,
                scope: scope));
        var secondDefinition = WorkDefinition.Create($"second-{scope}".ToLowerInvariant(), "Uses scoped capacity in another definition.",
            configuration: ConcurrencyConfiguration(
                WorkConcurrencyLimitReachedBehavior.DeferStart,
                scope: scope));
        var system = CreateSystem(builder =>
        {
            builder.AddWork(firstDefinition, async (context, input, cancellationToken) =>
            {
                firstStarted.SetResult();
                await release.Task.WaitAsync(cancellationToken);
                return WorkExecutionResult.Success();
            });
            builder.AddWork(secondDefinition, async (context, input, cancellationToken) =>
            {
                secondStarted.SetResult();
                await release.Task.WaitAsync(cancellationToken);
                return WorkExecutionResult.Success();
            });
        });

        await system.Start();

        var first = await system.Queue.Enqueue($"first-{scope}".ToLowerInvariant(), ScopedInput(scope, "same"));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await system.Queue.Enqueue($"second-{scope}".ToLowerInvariant(), ScopedInput(scope, "same"));
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();

        Assert.True((await first.WaitForCompletion()).IsCompletedSuccessfully);
        Assert.True((await second.WaitForCompletion()).IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(WorkConcurrencyScope.PerSubject)]
    [InlineData(WorkConcurrencyScope.PerConcurrencyKey)]
    public async Task QueueOverrideCanSetScopedConcurrency(WorkConcurrencyScope scope)
    {
        var firstStarted = CreateSignal();
        var secondStarted = CreateSignal();
        var release = CreateSignal();
        var starts = 0;
        var definition = WorkDefinition.Create($"override-{scope}".ToLowerInvariant(), "Queue override supplies scoped concurrency.");
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            if (Interlocked.Increment(ref starts) == 1)
            {
                firstStarted.SetResult();
            }
            else
            {
                secondStarted.SetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
            return WorkExecutionResult.Success();
        });

        await system.Start();

        var options = new WorkerOptions(Configuration: ConcurrencyConfiguration(
            WorkConcurrencyLimitReachedBehavior.DeferStart,
            scope: scope));
        var first = await system.Queue.Enqueue($"override-{scope}".ToLowerInvariant(), ScopedInput(scope, "same"), options);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await system.Queue.Enqueue($"override-{scope}".ToLowerInvariant(), ScopedInput(scope, "same"), options);

        await RequiredQueuedWorker(system, second);
        Assert.False(secondStarted.Task.IsCompleted);
        release.SetResult();
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True((await first.WaitForCompletion()).IsCompletedSuccessfully);
        Assert.True((await second.WaitForCompletion()).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DeferredConcurrencyKeyDoesNotBlockDifferentConcurrencyKey()
    {
        var started = new[] { CreateSignal(), CreateSignal() };
        var release = CreateSignal();
        var starts = 0;
        var definition = WorkDefinition.Create("key-drain", "Starts unrelated keys even when a deferred key is blocked.",
            configuration: ConcurrencyConfiguration(
                WorkConcurrencyLimitReachedBehavior.DeferStart,
                scope: WorkConcurrencyScope.PerConcurrencyKey));
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            var start = Interlocked.Increment(ref starts);
            if (start <= started.Length)
            {
                started[start - 1].SetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
            return WorkExecutionResult.Success();
        });

        await system.Start();

        var first = await system.Queue.Enqueue("key-drain", ConcurrencyKeyInput("blocked"));
        await started[0].Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await system.Queue.Enqueue("key-drain", ConcurrencyKeyInput("blocked"));
        var third = await system.Queue.Enqueue("key-drain", ConcurrencyKeyInput("free"));
        await started[1].Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WorkerState.Queued, RequiredWorker(await system.Query.Worker(RequiredWorkerId(second))).State);
        release.SetResult();

        Assert.True((await first.WaitForCompletion()).IsCompletedSuccessfully);
        Assert.True((await third.WaitForCompletion()).IsCompletedSuccessfully);
        Assert.True((await second.WaitForCompletion()).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WorkerSnapshotAndEventsExposeConcurrencyKey()
    {
        var key = new WorkConcurrencyKey("tenant", "tenant-123");
        var definition = WorkDefinition.Create("key-metadata", "Exposes concurrency key metadata.",
            configuration: ConcurrencyConfiguration(
                WorkConcurrencyLimitReachedBehavior.DeferStart,
                scope: WorkConcurrencyScope.PerConcurrencyKey));
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();
        await using var subscription = system.Events.Subscribe(new WorkEventFilter(ConcurrencyKey: key, EventType: "worker.queued"));
        await using var reader = subscription.Read().GetAsyncEnumerator();

        var handle = await system.Queue.Enqueue("key-metadata", WorkInput.Empty.WithConcurrencyKey(key));
        var worker = RequiredWorker(await system.Query.Worker(RequiredWorkerId(handle)));
        var workEvent = await ReadNext(reader);

        Assert.Equal(key, worker.ConcurrencyKey);
        Assert.Equal(key, workEvent.ConcurrencyKey);
        Assert.True((await handle.WaitForCompletion()).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DoNotStartDoesNotReserveCapacityUntilManualStart()
    {
        var firstStarted = CreateSignal();
        var release = CreateSignal();
        var definition = WorkDefinition.Create("manual-capacity", "Queued manual workers do not reserve capacity.",
            configuration: ConcurrencyConfiguration(
                WorkConcurrencyLimitReachedBehavior.DeferStart,
                overrideBehavior: WorkConcurrencyOverrideBehavior.Strict) with
            {
                Start = WorkStartConfiguration.DoNotStart,
            });
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            firstStarted.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return WorkExecutionResult.Success();
        });

        await system.Start();

        var first = await system.Queue.Enqueue("manual-capacity");
        var second = await system.Queue.Enqueue("manual-capacity");
        var firstWorker = RequiredWorker(await system.Query.Worker(RequiredWorkerId(first)));
        var secondWorker = RequiredWorker(await system.Query.Worker(RequiredWorkerId(second)));

        var firstStart = await system.Workers.Execute(firstWorker.Version, WorkAction.Start);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondStartWhileFull = await system.Workers.Execute(secondWorker.Version, WorkAction.Start);
        release.SetResult();
        Assert.True((await first.WaitForCompletion()).IsCompletedSuccessfully);
        var refreshedSecond = RequiredWorker(await system.Query.Worker(RequiredWorkerId(second)));
        var secondStartAfterCapacity = await system.Workers.Execute(refreshedSecond.Version, WorkAction.Start);

        Assert.True(firstStart.IsAccepted);
        Assert.Equal(WorkActionStatus.Invalid, secondStartWhileFull.Status);
        Assert.Contains(secondStartWhileFull.Messages, message => message.Code == "workable.concurrency.capacity_reached");
        Assert.True(secondStartAfterCapacity.IsAccepted);
        Assert.True((await second.WaitForCompletion()).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task CancelingRunningWorkerFreesCapacityForDeferredWorker()
    {
        var firstStarted = CreateSignal();
        var secondStarted = CreateSignal();
        var starts = 0;
        var definition = WorkDefinition.Create("cancel-frees-capacity", "Deferred workers start after cancel frees capacity.",
            configuration: ConcurrencyConfiguration(WorkConcurrencyLimitReachedBehavior.DeferStart));
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            if (Interlocked.Increment(ref starts) == 1)
            {
                firstStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            secondStarted.SetResult();
            return WorkExecutionResult.Success();
        });

        await system.Start();

        var first = await system.Queue.Enqueue("cancel-frees-capacity");
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await system.Queue.Enqueue("cancel-frees-capacity");
        var firstWorker = RequiredWorker(await system.Query.Worker(RequiredWorkerId(first)));

        var cancel = await system.Workers.Execute(firstWorker.Version, WorkAction.Cancel);
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(cancel.IsAccepted);
        Assert.Equal(WorkCompletionStatus.Canceled, (await first.WaitForCompletion()).Status);
        Assert.True((await second.WaitForCompletion()).IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(WorkConcurrencyBlockingMode.WhileExecuting, false)]
    [InlineData(WorkConcurrencyBlockingMode.WhileExecutingOrPaused, false)]
    [InlineData(WorkConcurrencyBlockingMode.WhileExecutingOrFailed, true)]
    [InlineData(WorkConcurrencyBlockingMode.WhileExecutingPausedOrFailed, true)]
    public async Task BlockingModeControlsWhetherFailedWorkersHoldCapacity(
        WorkConcurrencyBlockingMode blockingMode,
        bool failedWorkerHoldsCapacity)
    {
        var secondStarted = CreateSignal();
        var attempts = 0;
        var definition = WorkDefinition.Create($"failed-{blockingMode}".ToLowerInvariant(), "Checks failed worker capacity behavior.",
            configuration: ConcurrencyConfiguration(
                WorkConcurrencyLimitReachedBehavior.DeferStart,
                blockingMode: blockingMode));
        var system = CreateSystem(definition, (context, input, cancellationToken) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                return Task.FromResult(WorkExecutionResult.Failure([WorkMessage.Error("test.failure", "The work failed.")]));
            }

            secondStarted.SetResult();
            return Task.FromResult(WorkExecutionResult.Success());
        });

        await system.Start();

        var first = await system.Queue.Enqueue($"failed-{blockingMode}".ToLowerInvariant());
        var failed = await first.WaitForCompletion();
        var second = await system.Queue.Enqueue($"failed-{blockingMode}".ToLowerInvariant());

        if (failedWorkerHoldsCapacity)
        {
            await RequiredQueuedWorker(system, second);
            Assert.False(secondStarted.Task.IsCompleted);
            var cancel = await system.Workers.Execute(RequiredCompletionWorker(failed).Version, WorkAction.Cancel);
            Assert.True(cancel.IsAccepted);
        }

        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True((await second.WaitForCompletion()).IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(WorkConcurrencyBlockingMode.WhileExecuting, false)]
    [InlineData(WorkConcurrencyBlockingMode.WhileExecutingOrPaused, true)]
    [InlineData(WorkConcurrencyBlockingMode.WhileExecutingOrFailed, false)]
    [InlineData(WorkConcurrencyBlockingMode.WhileExecutingPausedOrFailed, true)]
    public async Task BlockingModeControlsWhetherPausedWorkersHoldCapacity(
        WorkConcurrencyBlockingMode blockingMode,
        bool pausedWorkerHoldsCapacity)
    {
        var firstStarted = CreateSignal();
        var secondStarted = CreateSignal();
        var attempts = 0;
        var definition = WorkDefinition.Create($"paused-{blockingMode}".ToLowerInvariant(), "Checks paused worker capacity behavior.",
            configuration: ConcurrencyConfiguration(
                WorkConcurrencyLimitReachedBehavior.DeferStart,
                blockingMode: blockingMode));
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                firstStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            secondStarted.SetResult();
            return WorkExecutionResult.Success();
        });

        await system.Start();

        var first = await system.Queue.Enqueue($"paused-{blockingMode}".ToLowerInvariant());
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var firstWorker = RequiredWorker(await system.Query.Worker(RequiredWorkerId(first)));
        var pause = await system.Workers.Execute(firstWorker.Version, WorkAction.Pause);
        var paused = await first.WaitForCompletion();
        var second = await system.Queue.Enqueue($"paused-{blockingMode}".ToLowerInvariant());

        if (pausedWorkerHoldsCapacity)
        {
            await RequiredQueuedWorker(system, second);
            Assert.False(secondStarted.Task.IsCompleted);
            var cancel = await system.Workers.Execute(RequiredCompletionWorker(paused).Version, WorkAction.Cancel);
            Assert.True(cancel.IsAccepted);
        }

        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(pause.IsAccepted);
        Assert.Equal(WorkCompletionStatus.Paused, paused.Status);
        Assert.True((await second.WaitForCompletion()).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DeferredWorkersStartInQueueOrder()
    {
        var started = new[]
        {
            CreateSignal(),
            CreateSignal(),
            CreateSignal(),
        };
        var release = new[]
        {
            CreateSignal(),
            CreateSignal(),
            CreateSignal(),
        };
        var startedInputs = new List<int>();
        var sync = new Lock();
        var definition = WorkDefinition.Create("deferred-order", "Starts deferred workers in queue order.",
            configuration: ConcurrencyConfiguration(WorkConcurrencyLimitReachedBehavior.DeferStart));
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            var value = input?.ToValue<int>() ?? throw new InvalidOperationException("Expected input.");
            lock (sync)
            {
                startedInputs.Add(value);
            }

            started[value - 1].SetResult();
            await release[value - 1].Task.WaitAsync(cancellationToken);
            return WorkExecutionResult.Success();
        });

        await system.Start();

        var first = await system.Queue.Enqueue("deferred-order", WorkInput.FromValue(1));
        await started[0].Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await system.Queue.Enqueue("deferred-order", WorkInput.FromValue(2));
        var third = await system.Queue.Enqueue("deferred-order", WorkInput.FromValue(3));

        await RequiredQueuedWorker(system, second);
        await RequiredQueuedWorker(system, third);
        Assert.False(started[1].Task.IsCompleted);
        Assert.False(started[2].Task.IsCompleted);

        release[0].SetResult();
        await started[1].Task.WaitAsync(TimeSpan.FromSeconds(5));
        await RequiredQueuedWorker(system, third);
        Assert.False(started[2].Task.IsCompleted);

        release[1].SetResult();
        await started[2].Task.WaitAsync(TimeSpan.FromSeconds(5));
        release[2].SetResult();

        Assert.Equal([1, 2, 3], startedInputs);
        Assert.True((await first.WaitForCompletion()).IsCompletedSuccessfully);
        Assert.True((await second.WaitForCompletion()).IsCompletedSuccessfully);
        Assert.True((await third.WaitForCompletion()).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ReconfiguringDeferredWorkerToDisableConcurrencyStartsIt()
    {
        var firstStarted = CreateSignal();
        var secondStarted = CreateSignal();
        var release = CreateSignal();
        var starts = 0;
        var definition = WorkDefinition.Create("disable-deferred", "Disabling concurrency starts deferred work.",
            configuration: ConcurrencyConfiguration(WorkConcurrencyLimitReachedBehavior.DeferStart));
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            if (Interlocked.Increment(ref starts) == 1)
            {
                firstStarted.SetResult();
            }
            else
            {
                secondStarted.SetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
            return WorkExecutionResult.Success();
        });

        await system.Start();

        var first = await system.Queue.Enqueue("disable-deferred");
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await system.Queue.Enqueue("disable-deferred");
        var secondWorker = RequiredWorker(await system.Query.Worker(RequiredWorkerId(second)));

        var reconfigure = await system.Workers.Reconfigure(
            secondWorker.Version,
            new WorkerReconfiguration(Coordination: WorkCoordinationConfiguration.Default));
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();

        Assert.True(reconfigure.IsAccepted);
        Assert.True((await first.WaitForCompletion()).IsCompletedSuccessfully);
        Assert.True((await second.WaitForCompletion()).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task QueueOverridesForSameDefinitionShareConcurrencyCapacity()
    {
        var firstStarted = CreateSignal();
        var secondStarted = CreateSignal();
        var release = CreateSignal();
        var starts = 0;
        var definition = WorkDefinition.Create("override-shared-capacity", "Queue overrides still share definition capacity.",
            configuration: ConcurrencyConfiguration(
                WorkConcurrencyLimitReachedBehavior.DeferStart,
                overrideBehavior: WorkConcurrencyOverrideBehavior.Flexible));
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            if (Interlocked.Increment(ref starts) == 1)
            {
                firstStarted.SetResult();
            }
            else
            {
                secondStarted.SetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
            return WorkExecutionResult.Success();
        });

        await system.Start();

        var first = await system.Queue.Enqueue("override-shared-capacity");
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await system.Queue.Enqueue(
            "override-shared-capacity",
            options: new WorkerOptions(
                Configuration: ConcurrencyConfiguration(
                    WorkConcurrencyLimitReachedBehavior.DeferStart,
                    overrideBehavior: WorkConcurrencyOverrideBehavior.Strict)));

        await RequiredQueuedWorker(system, second);
        Assert.False(secondStarted.Task.IsCompleted);
        release.SetResult();

        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True((await first.WaitForCompletion()).IsCompletedSuccessfully);
        Assert.True((await second.WaitForCompletion()).IsCompletedSuccessfully);
    }

    private static WorkConfiguration ConcurrencyConfiguration(
        WorkConcurrencyLimitReachedBehavior limitReachedBehavior,
        WorkConcurrencyOverrideBehavior overrideBehavior = WorkConcurrencyOverrideBehavior.Flexible,
        int maximumCapacity = 1,
        WorkConcurrencyScope scope = WorkConcurrencyScope.PerDefinition,
        WorkConcurrencyBlockingMode blockingMode = WorkConcurrencyBlockingMode.WhileExecutingPausedOrFailed)
        => WorkConfiguration.Default with
        {
            Coordination = WorkCoordinationConfiguration.Default with
            {
                IsEnabled = true,
                Concurrency = WorkConcurrencyConfiguration.Default with
                {
                    IsEnabled = true,
                    MaximumCapacity = maximumCapacity,
                    Scope = scope,
                    BlockingMode = blockingMode,
                    LimitReachedBehavior = limitReachedBehavior,
                    OverrideBehavior = overrideBehavior,
                },
            },
        };

    private static IWorkSystem CreateSystem(Action<IWorkSystemBuilder> configure)
        => new ServiceCollection()
            .AddWorkableSystem(configure)
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

    private static IWorkSystem CreateSystem(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute)
        => new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(definition, execute))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

    private static TaskCompletionSource CreateSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static WorkerId RequiredWorkerId(IWorkerHandle handle)
        => handle.WorkerId ?? throw new InvalidOperationException("Expected the queue to accept a worker.");

    private static async Task<WorkerSnapshot> RequiredQueuedWorker(IWorkSystem system, IWorkerHandle handle)
    {
        var worker = RequiredWorker(await system.Query.Worker(RequiredWorkerId(handle)));
        Assert.Equal(WorkerState.Queued, worker.State);
        return worker;
    }

    private static WorkerSnapshot RequiredWorker(WorkerSnapshot? worker)
        => worker ?? throw new InvalidOperationException("Expected worker to exist.");

    private static WorkerSnapshot RequiredCompletionWorker(WorkCompletion completion)
        => completion.Worker ?? throw new InvalidOperationException("Expected completion to include worker.");

    private static WorkInput SubjectInput(string value)
        => WorkInput.Empty.WithSubject(new WorkSubjectId("subject", value));

    private static WorkInput ConcurrencyKeyInput(string value)
        => WorkInput.Empty.WithConcurrencyKey(new WorkConcurrencyKey("key", value));

    private static WorkInput ScopedInput(WorkConcurrencyScope scope, string value)
        => scope switch
        {
            WorkConcurrencyScope.PerSubject => SubjectInput(value),
            WorkConcurrencyScope.PerConcurrencyKey => ConcurrencyKeyInput(value),
            _ => WorkInput.Empty,
        };

    private static Task<WorkExecutionResult> SuccessfulWork(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    private static async Task<WorkEvent> ReadNext(IAsyncEnumerator<WorkEvent> reader)
    {
        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        return reader.Current;
    }
}
