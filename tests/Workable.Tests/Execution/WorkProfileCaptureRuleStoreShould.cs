using System.Reflection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Profiling")]
public sealed class WorkProfileCaptureRuleStoreShould
{
    [Fact]
    public void PruneExpiredRuleStatesAndRejectFurtherReservationsAtCapacity()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new WorkProfileCaptureRuleStore();
        var expired = new WorkProfileCaptureRuleStore.RuleState(
            Guid.NewGuid(),
            "orders.expired",
            actorId: null,
            maximumMatches: 1,
            createdAt: now.AddMinutes(-2),
            expiresAt: now.AddMinutes(-1),
            createdBy: WorkActor.Unknown);
        var rules = (Dictionary<Guid, WorkProfileCaptureRuleStore.RuleState>)typeof(WorkProfileCaptureRuleStore)
            .GetField("rules", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(store)!;
        rules.Add(expired.Id, expired);

        Assert.Empty(store.GetRules());
        Assert.False(expired.IsActive);

        var exhausted = new WorkProfileCaptureRuleStore.RuleState(
            Guid.NewGuid(),
            "orders.exhausted",
            actorId: null,
            maximumMatches: 1,
            createdAt: now,
            expiresAt: now.AddMinutes(5),
            createdBy: WorkActor.Unknown);
        Assert.True(exhausted.TryReserve(now));
        Assert.Equal(0, exhausted.AvailableMatches);
        Assert.False(exhausted.TryReserve(now));
        Assert.False(exhausted.Complete(committed: false, now));
        Assert.True(exhausted.TryReserve(now));
    }

    [Fact]
    public void SkipPendingAndInactiveIndexedRulesAndRejectReservationsThatExpireAtAdmission()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new WorkProfileCaptureRuleStore();
        var snapshot = store.Create(
            "orders.indexed",
            actorId: null,
            maximumMatches: 1,
            expiresAfter: TimeSpan.FromMinutes(5),
            createdBy: WorkActor.Unknown);
        var rules = (Dictionary<Guid, WorkProfileCaptureRuleStore.RuleState>)typeof(WorkProfileCaptureRuleStore)
            .GetField("rules", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(store)!;
        var indexed = rules[snapshot.Id];
        Assert.True(indexed.TryReserve(now));

        Assert.Null(store.TryAcquire(
            "orders.indexed",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess)));

        Assert.False(indexed.Complete(committed: false, now));
        indexed.Deactivate();
        Assert.Null(store.TryAcquire(
            "orders.indexed",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess)));

        var expiresDuringAdmission = new WorkProfileCaptureRuleStore.RuleState(
            Guid.NewGuid(),
            "orders.expiring",
            actorId: null,
            maximumMatches: 1,
            createdAt: now.AddMinutes(-2),
            expiresAt: now.AddMilliseconds(-1),
            createdBy: WorkActor.Unknown);

        Assert.False(expiresDuringAdmission.TryReserve(now.AddMinutes(-1)));
        Assert.False(expiresDuringAdmission.IsActive);
        Assert.Equal(0, expiresDuringAdmission.PendingMatches);
    }

    [Fact]
    public void MatchMostSpecificRuleAndCommitItsMatch()
    {
        var store = new WorkProfileCaptureRuleStore();
        var actor = new WorkActor("user-1", "User One");
        var context = WorkRequestContext.Create(WorkInvocationChannel.InProcess, actor);
        var broad = store.Create("orders.run", null, 2, TimeSpan.FromMinutes(5), actor);
        var specific = store.Create("orders.run", "user-1", 1, TimeSpan.FromMinutes(5), actor);

        using (var lease = store.TryAcquire("orders.run", context))
        {
            Assert.NotNull(lease);
            lease.Commit();
        }

        var remaining = store.GetRules();

        Assert.DoesNotContain(remaining, rule => rule.Id == specific.Id);
        Assert.Equal(2, Assert.Single(remaining, rule => rule.Id == broad.Id).RemainingMatches);
    }

    [Fact]
    public void MatchTheOldestAcrossEquallySpecificDefinitionAndActorRules()
    {
        var actor = new WorkActor("user-1");
        var context = WorkRequestContext.Create(WorkInvocationChannel.InProcess, actor);
        var actorFirstStore = new WorkProfileCaptureRuleStore();
        var actorFirst = actorFirstStore.Create(null, actor.Id, 1, TimeSpan.FromMinutes(5), actor);
        Assert.True(SpinWait.SpinUntil(() => DateTimeOffset.UtcNow > actorFirst.CreatedAt, TimeSpan.FromSeconds(1)));
        var definitionSecond = actorFirstStore.Create("orders.run", null, 1, TimeSpan.FromMinutes(5), actor);

        using (var lease = actorFirstStore.TryAcquire("orders.run", context))
        {
            Assert.NotNull(lease);
            lease.Commit();
        }

        Assert.DoesNotContain(actorFirstStore.GetRules(), rule => rule.Id == actorFirst.Id);
        Assert.Contains(actorFirstStore.GetRules(), rule => rule.Id == definitionSecond.Id);

        var definitionFirstStore = new WorkProfileCaptureRuleStore();
        var definitionFirst = definitionFirstStore.Create("orders.run", null, 1, TimeSpan.FromMinutes(5), actor);
        Assert.True(SpinWait.SpinUntil(() => DateTimeOffset.UtcNow > definitionFirst.CreatedAt, TimeSpan.FromSeconds(1)));
        var actorSecond = definitionFirstStore.Create(null, actor.Id, 1, TimeSpan.FromMinutes(5), actor);

        using (var lease = definitionFirstStore.TryAcquire("orders.run", context))
        {
            Assert.NotNull(lease);
            lease.Commit();
        }

        Assert.DoesNotContain(definitionFirstStore.GetRules(), rule => rule.Id == definitionFirst.Id);
        Assert.Contains(definitionFirstStore.GetRules(), rule => rule.Id == actorSecond.Id);
    }

    [Fact]
    public void MatchGlobalRulesAfterMoreSpecificRulesAreUnavailable()
    {
        var store = new WorkProfileCaptureRuleStore();
        var actor = new WorkActor("user-1");
        var context = WorkRequestContext.Create(WorkInvocationChannel.InProcess, actor);
        var global = store.Create(null, null, 1, TimeSpan.FromMinutes(5), actor);
        var definition = store.Create("orders.run", null, 1, TimeSpan.FromMinutes(5), actor);

        using (var lease = store.TryAcquire("orders.run", context))
        {
            Assert.NotNull(lease);
            lease.Commit();
        }

        Assert.DoesNotContain(store.GetRules(), rule => rule.Id == definition.Id);
        Assert.Contains(store.GetRules(), rule => rule.Id == global.Id);

        using (var lease = store.TryAcquire("other.run", context))
        {
            Assert.NotNull(lease);
            lease.Commit();
        }

        Assert.Empty(store.GetRules());
    }

    [Fact]
    public void RestoreAReservedMatchWhenQueueAcceptanceDoesNotCommit()
    {
        var store = new WorkProfileCaptureRuleStore();
        var actor = new WorkActor("user-1");
        var context = WorkRequestContext.Create(WorkInvocationChannel.InProcess, actor);
        var created = store.Create(null, "user-1", 1, TimeSpan.FromMinutes(5), actor);

        store.TryAcquire("orders.run", context)!.Dispose();

        Assert.Equal(1, Assert.Single(store.GetRules(), rule => rule.Id == created.Id).RemainingMatches);
    }

    [Fact]
    public void RestoreAGlobalRuleToItsFallbackBucketAfterRollback()
    {
        var store = new WorkProfileCaptureRuleStore();
        var actor = new WorkActor("user-1");
        var context = WorkRequestContext.Create(WorkInvocationChannel.InProcess, actor);
        store.Create(null, null, 1, TimeSpan.FromMinutes(5), actor);

        store.TryAcquire("orders.run", context)!.Dispose();
        using var restored = store.TryAcquire("other.run", context);

        Assert.NotNull(restored);
        restored.Commit();
        Assert.Empty(store.GetRules());
    }

    [Fact]
    public void AnonymousActorsSkipActorBucketsAndUseDefinitionThenGlobalRules()
    {
        var store = new WorkProfileCaptureRuleStore();
        store.Create(null, "known-user", 1, TimeSpan.FromMinutes(5), WorkActor.Unknown);
        var definition = store.Create("orders.run", null, 1, TimeSpan.FromMinutes(5), WorkActor.Unknown);
        var global = store.Create(null, null, 1, TimeSpan.FromMinutes(5), WorkActor.Unknown);
        var anonymous = WorkRequestContext.Create(WorkInvocationChannel.InProcess, WorkActor.Unknown);

        using (var lease = store.TryAcquire("orders.run", anonymous))
        {
            Assert.NotNull(lease);
            lease.Commit();
        }
        Assert.DoesNotContain(store.GetRules(), rule => rule.Id == definition.Id);

        using (var lease = store.TryAcquire("other.run", anonymous))
        {
            Assert.NotNull(lease);
            lease.Commit();
        }
        Assert.DoesNotContain(store.GetRules(), rule => rule.Id == global.Id);
        Assert.Single(store.GetRules());
    }

    [Fact]
    public void RestoreASpecificRuleToItsIndexedBucketAfterRollback()
    {
        var store = new WorkProfileCaptureRuleStore();
        var actor = new WorkActor("user-1");
        var context = WorkRequestContext.Create(WorkInvocationChannel.InProcess, actor);
        store.Create("orders.run", actor.Id, 1, TimeSpan.FromMinutes(5), actor);

        store.TryAcquire("orders.run", context)!.Dispose();
        using var restored = store.TryAcquire("orders.run", context);

        Assert.NotNull(restored);
        restored.Commit();
        Assert.Empty(store.GetRules());
    }

    [Fact]
    public void RejectExpiredRuleStateReservationsAndHarmlessLateCompletion()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new WorkProfileCaptureRuleStore.RuleState(
            Guid.NewGuid(),
            "orders.run",
            actorId: null,
            maximumMatches: 1,
            createdAt: now - TimeSpan.FromMinutes(2),
            expiresAt: now - TimeSpan.FromMinutes(1),
            createdBy: WorkActor.Unknown);

        Assert.Equal(0, state.PendingMatches);
        Assert.False(state.TryReserve(now));
        Assert.False(state.IsActive);
        Assert.False(state.Complete(committed: false, now: now));
    }

    [Fact]
    public void RuleStateCoversInactiveCommittedRestoredAndOrderingBoundaries()
    {
        var now = DateTimeOffset.UtcNow;
        var earlier = new WorkProfileCaptureRuleStore.RuleState(
            Guid.Empty,
            null,
            null,
            2,
            now.AddSeconds(-1),
            now.AddMinutes(5),
            WorkActor.Unknown);
        var later = new WorkProfileCaptureRuleStore.RuleState(
            Guid.NewGuid(),
            null,
            null,
            2,
            now,
            now.AddMinutes(5),
            WorkActor.Unknown);
        Assert.True(WorkProfileCaptureRuleStore.RuleState.CompareOrder(earlier, later) < 0);

        var sameTimeLowId = new WorkProfileCaptureRuleStore.RuleState(
            Guid.Empty, null, null, 1, now, now.AddMinutes(5), WorkActor.Unknown);
        var sameTimeHighId = new WorkProfileCaptureRuleStore.RuleState(
            Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
            null, null, 1, now, now.AddMinutes(5), WorkActor.Unknown);
        Assert.True(WorkProfileCaptureRuleStore.RuleState.CompareOrder(sameTimeLowId, sameTimeHighId) < 0);

        later.Deactivate();
        Assert.False(later.TryReserve(now));

        Assert.True(earlier.TryReserve(now));
        Assert.False(earlier.Complete(committed: true, now));
        Assert.True(earlier.IsActive);
        Assert.True(earlier.TryReserve(now));
        earlier.Deactivate();
        Assert.True(earlier.Complete(committed: false, now));
        Assert.False(earlier.IsActive);

        var expiring = new WorkProfileCaptureRuleStore.RuleState(
            Guid.NewGuid(), null, null, 1, now, now, WorkActor.Unknown);
        Assert.False(expiring.TryReserve(now));
        Assert.False(expiring.IsActive);
    }

    [Fact]
    public void RuleLeaseCompletionIsIdempotentAcrossCommitAndDisposeOrders()
    {
        var store = new WorkProfileCaptureRuleStore();
        var actor = new WorkActor("user-1");
        var context = WorkRequestContext.Create(WorkInvocationChannel.InProcess, actor);
        store.Create("orders.run", null, 2, TimeSpan.FromMinutes(5), actor);

        var committed = store.TryAcquire("orders.run", context)!;
        committed.Commit();
        committed.Commit();
        committed.Dispose();

        var rolledBack = store.TryAcquire("orders.run", context)!;
        rolledBack.Dispose();
        rolledBack.Dispose();
        rolledBack.Commit();

        Assert.Equal(1, Assert.Single(store.GetRules()).RemainingMatches);
    }

    [Fact]
    public void ValidateRuleSelectorsMatchesAndLifetime()
    {
        var store = new WorkProfileCaptureRuleStore();

        store.Create(null, null, 1, TimeSpan.FromMinutes(5), WorkActor.Unknown);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.Create("orders.run", null, 0, TimeSpan.FromMinutes(5), WorkActor.Unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.Create("orders.run", null, 1_001, TimeSpan.FromMinutes(5), WorkActor.Unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.Create("orders.run", null, 1, TimeSpan.FromSeconds(30), WorkActor.Unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.Create("orders.run", null, 1, TimeSpan.FromHours(25), WorkActor.Unknown));
        store.Create(
            new string('d', WorkProfileCaptureRuleStore.MaximumSelectorLength),
            new string('a', WorkProfileCaptureRuleStore.MaximumSelectorLength),
            1,
            TimeSpan.FromMinutes(5),
            WorkActor.Unknown);
        Assert.Throws<ArgumentException>(() =>
            store.Create(
                new string('d', WorkProfileCaptureRuleStore.MaximumSelectorLength + 1),
                null,
                1,
                TimeSpan.FromMinutes(5),
                WorkActor.Unknown));
        Assert.Throws<ArgumentException>(() =>
            store.Create(
                null,
                new string('a', WorkProfileCaptureRuleStore.MaximumSelectorLength + 1),
                1,
                TimeSpan.FromMinutes(5),
                WorkActor.Unknown));
    }

    [Fact]
    public void DeleteRulesAndIgnoreNonMatchingWorkers()
    {
        var store = new WorkProfileCaptureRuleStore();
        var created = store.Create("orders.run", "user-1", 1, TimeSpan.FromMinutes(5), WorkActor.Unknown);
        var otherActor = WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            new WorkActor("user-2"));

        Assert.Null(store.TryAcquire("orders.run", otherActor));
        Assert.True(store.Delete(created.Id));
        Assert.False(store.Delete(created.Id));
        Assert.Empty(store.GetRules());
    }

    [Fact]
    public void ReserveMatchesAtomicallyUnderConcurrentQueueAdmission()
    {
        const int matches = 1_000;
        var store = new WorkProfileCaptureRuleStore();
        var actor = new WorkActor("user-1");
        var context = WorkRequestContext.Create(WorkInvocationChannel.InProcess, actor);
        store.Create("orders.run", null, matches, TimeSpan.FromMinutes(5), actor);
        var committed = 0;

        Parallel.For(0, matches * 2, _ =>
        {
            using var lease = store.TryAcquire("orders.run", context);
            if (lease is null)
            {
                return;
            }

            lease.Commit();
            Interlocked.Increment(ref committed);
        });

        Assert.Equal(matches, committed);
        Assert.Empty(store.GetRules());
    }

    [Fact]
    public void PreserveRolledBackMatchesDuringConcurrentLeaseCompletion()
    {
        const int matches = 1_000;
        var store = new WorkProfileCaptureRuleStore();
        var actor = new WorkActor("user-1");
        var context = WorkRequestContext.Create(WorkInvocationChannel.InProcess, actor);
        store.Create("orders.run", null, matches, TimeSpan.FromMinutes(5), actor);
        var leases = Enumerable.Range(0, matches)
            .Select(_ => store.TryAcquire("orders.run", context)!)
            .ToArray();

        Parallel.For(0, leases.Length, index =>
        {
            if (index % 2 == 0)
            {
                leases[index].Commit();
            }

            leases[index].Dispose();
        });

        Assert.Equal(matches / 2, Assert.Single(store.GetRules()).RemainingMatches);

        var committed = 0;
        Parallel.For(0, matches, _ =>
        {
            using var lease = store.TryAcquire("orders.run", context);
            if (lease is null)
            {
                return;
            }

            lease.Commit();
            Interlocked.Increment(ref committed);
        });

        Assert.Equal(matches / 2, committed);
        Assert.Empty(store.GetRules());
    }

    [Fact]
    public void CapTheNumberOfActiveRules()
    {
        var store = new WorkProfileCaptureRuleStore();
        for (var index = 0; index < WorkProfileCaptureRuleStore.MaximumActiveRules; index++)
        {
            store.Create($"orders.run.{index}", null, 1, TimeSpan.FromMinutes(5), WorkActor.Unknown);
        }

        Assert.Null(store.TryAcquire(
            "orders.target",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess, new WorkActor("user-1"))));

        var exception = Assert.Throws<ArgumentException>(() =>
            store.Create("orders.overflow", null, 1, TimeSpan.FromMinutes(5), WorkActor.Unknown));

        Assert.Contains(
            WorkProfileCaptureRuleStore.MaximumActiveRules.ToString(),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReclaimExhaustedRulesOnTheNextAdministrativeOperation()
    {
        var store = new WorkProfileCaptureRuleStore();
        var actor = new WorkActor("user-1");
        var context = WorkRequestContext.Create(WorkInvocationChannel.InProcess, actor);
        for (var index = 0; index < WorkProfileCaptureRuleStore.MaximumActiveRules; index++)
        {
            store.Create("orders.run", null, 1, TimeSpan.FromMinutes(5), actor);
        }

        for (var index = 0; index < WorkProfileCaptureRuleStore.MaximumActiveRules; index++)
        {
            using var lease = store.TryAcquire("orders.run", context);
            Assert.NotNull(lease);
            lease.Commit();
        }

        Assert.Null(store.TryAcquire("orders.run", context));
        var replacement = store.Create("orders.run", null, 1, TimeSpan.FromMinutes(5), actor);

        Assert.Equal(replacement.Id, Assert.Single(store.GetRules()).Id);
    }

    [Fact]
    public void SkipExhaustedPendingRulesAndRestoreRolledBackRules()
    {
        var store = new WorkProfileCaptureRuleStore();
        var actor = new WorkActor("user-1");
        var context = WorkRequestContext.Create(WorkInvocationChannel.InProcess, actor);
        var leases = new WorkProfileCaptureRuleStore.WorkProfileCaptureRuleLease[
            WorkProfileCaptureRuleStore.MaximumActiveRules];
        for (var index = 0; index < leases.Length; index++)
        {
            store.Create("orders.run", null, 1, TimeSpan.FromMinutes(5), actor);
            leases[index] = store.TryAcquire("orders.run", context)!;
        }

        Assert.Null(store.TryAcquire("orders.run", context));

        leases[0].Dispose();
        using var restored = store.TryAcquire("orders.run", context);
        Assert.NotNull(restored);

        restored.Commit();
        foreach (var lease in leases.Skip(1))
        {
            lease.Commit();
        }

        Assert.Empty(store.GetRules());
    }
}
