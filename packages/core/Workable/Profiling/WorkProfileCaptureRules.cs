namespace Workable;

internal sealed record WorkProfileCaptureRuleSnapshot(
    Guid Id,
    string? DefinitionName,
    string? ActorId,
    int MaximumMatches,
    int RemainingMatches,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    WorkActor CreatedBy);

internal interface IWorkProfileCaptureRuleSystem
{
    WorkSystemProfilingConfiguration ProfilingConfiguration { get; }

    IReadOnlyList<WorkProfileCaptureRuleSnapshot> GetProfileCaptureRules();

    WorkProfileCaptureRuleSnapshot CreateProfileCaptureRule(
        string? definitionName,
        string? actorId,
        int maximumMatches,
        TimeSpan expiresAfter,
        WorkActor createdBy);

    bool DeleteProfileCaptureRule(Guid id);
}

internal sealed class WorkProfileCaptureRuleStore
{
    internal const int MaximumActiveRules = 1_000;
    internal const int MaximumRuleMatches = 1_000;
    internal const int MaximumSelectorLength = 512;
    internal static readonly TimeSpan MinimumRuleLifetime = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan MaximumRuleLifetime = TimeSpan.FromHours(24);

    private readonly Lock sync = new();
    private readonly Dictionary<Guid, RuleState> rules = [];
    private RuleIndex activeRules = RuleIndex.Empty;

    public IReadOnlyList<WorkProfileCaptureRuleSnapshot> GetRules()
    {
        lock (this.sync)
        {
            this.PruneInactiveLocked(DateTimeOffset.UtcNow);
            return [.. this.rules.Values
                .OrderBy(rule => rule.ExpiresAt)
                .ThenBy(rule => rule.CreatedAt)
                .Select(rule => rule.ToSnapshot())];
        }
    }

    public WorkProfileCaptureRuleSnapshot Create(
        string? definitionName,
        string? actorId,
        int maximumMatches,
        TimeSpan expiresAfter,
        WorkActor createdBy)
    {
        definitionName = Normalize(definitionName);
        actorId = Normalize(actorId);
        if (definitionName is null && actorId is null)
        {
            throw new ArgumentException("A profile capture rule must match a work definition, an actor id, or both.");
        }

        ValidateSelectorLength(definitionName, "work definition");
        ValidateSelectorLength(actorId, "actor id");

        if (maximumMatches is <= 0 or > MaximumRuleMatches)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumMatches),
                maximumMatches,
                $"Profile capture rule matches must be between 1 and {MaximumRuleMatches}.");
        }

        if (expiresAfter < MinimumRuleLifetime || expiresAfter > MaximumRuleLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresAfter),
                expiresAfter,
                $"Profile capture rule lifetime must be between {MinimumRuleLifetime} and {MaximumRuleLifetime}.");
        }

        ArgumentNullException.ThrowIfNull(createdBy);
        var now = DateTimeOffset.UtcNow;
        var rule = new RuleState(
            Guid.NewGuid(),
            definitionName,
            actorId,
            maximumMatches,
            now,
            now + expiresAfter,
            createdBy);
        lock (this.sync)
        {
            this.PruneInactiveLocked(now);
            if (this.rules.Count >= MaximumActiveRules)
            {
                throw new ArgumentException(
                    $"A Workable system cannot have more than {MaximumActiveRules} active profile capture rules.");
            }

            this.rules.Add(rule.Id, rule);
            this.PublishActiveRulesLocked();
            return rule.ToSnapshot();
        }
    }

    public bool Delete(Guid id)
    {
        lock (this.sync)
        {
            if (!this.rules.Remove(id, out var removed))
            {
                return false;
            }

            removed.Deactivate();
            this.PublishActiveRulesLocked();
            return true;
        }
    }

    public WorkProfileCaptureRuleLease? TryAcquire(
        string definitionName,
        WorkRequestContext requestContext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionName);
        ArgumentNullException.ThrowIfNull(requestContext);
        var index = Volatile.Read(ref this.activeRules);
        if (index.IsEmpty)
        {
            return null;
        }

        var actorId = Normalize(requestContext.Actor.Id);
        var now = DateTimeOffset.UtcNow;
        RuleState? reserved = null;
        if (actorId is not null)
        {
            var specific = index.GetSpecific(definitionName, actorId);
            if (!specific.IsEmpty)
            {
                reserved = TryReserve(specific, now);
            }
        }

        if (reserved is null)
        {
            var definitionRules = index.GetDefinitionOnly(definitionName);
            var actorRules = actorId is null ? RuleBucket.Empty : index.GetActorOnly(actorId);
            if (!definitionRules.IsEmpty || !actorRules.IsEmpty)
            {
                reserved = TryReserveMerged(definitionRules, actorRules, now);
            }
        }

        return reserved is null ? null : new WorkProfileCaptureRuleLease(this, reserved);
    }

    private void Complete(RuleState rule, bool committed)
    {
        rule.Complete(committed, DateTimeOffset.UtcNow);
        if (!committed && rule.IsActive && rule.AvailableMatches > 0)
        {
            Volatile.Read(ref this.activeRules).Restore(rule);
        }
    }

    private void PruneInactiveLocked(DateTimeOffset now)
    {
        List<Guid>? inactive = null;
        var changed = false;
        foreach (var entry in this.rules)
        {
            var rule = entry.Value;
            if (rule.ExpiresAt <= now && rule.IsActive)
            {
                rule.Deactivate();
                changed = true;
            }

            if (!rule.IsActive && rule.PendingMatches == 0)
            {
                (inactive ??= []).Add(entry.Key);
            }
        }

        foreach (var id in inactive ?? [])
        {
            this.rules.Remove(id);
            changed = true;
        }

        if (changed)
        {
            this.PublishActiveRulesLocked();
        }
    }

    private void PublishActiveRulesLocked()
        => Volatile.Write(ref this.activeRules, RuleIndex.Create(this.rules.Values));

    private static RuleState? TryReserve(
        RuleBucket candidates,
        DateTimeOffset now)
    {
        while (candidates.TryPeek(now, out var rule, out var index))
        {
            var candidate = rule!;
            if (candidate.TryReserve(now))
            {
                candidates.AdvanceIfUnavailable(index, candidate);
                return candidate;
            }

            candidates.AdvanceIfUnavailable(index, candidate);
        }

        return null;
    }

    private static RuleState? TryReserveMerged(
        RuleBucket definitionRules,
        RuleBucket actorRules,
        DateTimeOffset now)
    {
        while (true)
        {
            var hasDefinition = definitionRules.TryPeek(
                now,
                out var definitionCandidate,
                out var definitionIndex);
            var hasActor = actorRules.TryPeek(
                now,
                out var actorCandidate,
                out var actorIndex);
            if (!hasDefinition && !hasActor)
            {
                return null;
            }

            RuleBucket selectedBucket;
            RuleState candidate;
            int selectedIndex;
            if (!hasDefinition)
            {
                selectedBucket = actorRules;
                candidate = actorCandidate!;
                selectedIndex = actorIndex;
            }
            else if (!hasActor || RuleState.CompareOrder(definitionCandidate!, actorCandidate!) <= 0)
            {
                selectedBucket = definitionRules;
                candidate = definitionCandidate!;
                selectedIndex = definitionIndex;
            }
            else
            {
                selectedBucket = actorRules;
                candidate = actorCandidate!;
                selectedIndex = actorIndex;
            }

            if (candidate.TryReserve(now))
            {
                selectedBucket.AdvanceIfUnavailable(selectedIndex, candidate);
                return candidate;
            }

            selectedBucket.AdvanceIfUnavailable(selectedIndex, candidate);
        }
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void ValidateSelectorLength(string? value, string selectorName)
    {
        if (value?.Length > MaximumSelectorLength)
        {
            throw new ArgumentException(
                $"A profile capture rule {selectorName} cannot exceed {MaximumSelectorLength} characters.");
        }
    }

    private sealed class RuleIndex
    {
        public static RuleIndex Empty { get; } = new([], [], []);

        private readonly Dictionary<RuleSelector, RuleBucket> specific;
        private readonly Dictionary<string, RuleBucket> definitionOnly;
        private readonly Dictionary<string, RuleBucket> actorOnly;

        private RuleIndex(
            Dictionary<RuleSelector, RuleBucket> specific,
            Dictionary<string, RuleBucket> definitionOnly,
            Dictionary<string, RuleBucket> actorOnly)
        {
            this.specific = specific;
            this.definitionOnly = definitionOnly;
            this.actorOnly = actorOnly;
        }

        public bool IsEmpty => this.specific.Count == 0 && this.definitionOnly.Count == 0 && this.actorOnly.Count == 0;

        public RuleBucket GetSpecific(string definitionName, string actorId)
            => this.specific.GetValueOrDefault(new RuleSelector(definitionName, actorId)) ?? RuleBucket.Empty;

        public RuleBucket GetDefinitionOnly(string definitionName)
            => this.definitionOnly.GetValueOrDefault(definitionName) ?? RuleBucket.Empty;

        public RuleBucket GetActorOnly(string actorId)
            => this.actorOnly.GetValueOrDefault(actorId) ?? RuleBucket.Empty;

        public void Restore(RuleState rule)
        {
            if (rule.DefinitionName is not null && rule.ActorId is not null)
            {
                this.GetSpecific(rule.DefinitionName, rule.ActorId).Reset();
            }
            else if (rule.DefinitionName is not null)
            {
                this.GetDefinitionOnly(rule.DefinitionName).Reset();
            }
            else if (rule.ActorId is not null)
            {
                this.GetActorOnly(rule.ActorId).Reset();
            }
        }

        public static RuleIndex Create(IEnumerable<RuleState> source)
        {
            var active = source.Where(rule => rule.IsActive).ToArray();
            if (active.Length == 0)
            {
                return Empty;
            }

            var specific = active
                .Where(rule => rule.DefinitionName is not null && rule.ActorId is not null)
                .GroupBy(
                    rule => new RuleSelector(rule.DefinitionName!, rule.ActorId!),
                    RuleSelectorComparer.Instance)
                .ToDictionary(
                    group => group.Key,
                    group => new RuleBucket(Order(group)),
                    RuleSelectorComparer.Instance);
            var definitionOnly = active
                .Where(rule => rule.DefinitionName is not null && rule.ActorId is null)
                .GroupBy(rule => rule.DefinitionName!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => new RuleBucket(Order(group)),
                    StringComparer.OrdinalIgnoreCase);
            var actorOnly = active
                .Where(rule => rule.DefinitionName is null && rule.ActorId is not null)
                .GroupBy(rule => rule.ActorId!, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => new RuleBucket(Order(group)),
                    StringComparer.Ordinal);
            return new RuleIndex(specific, definitionOnly, actorOnly);
        }

        private static RuleState[] Order(IEnumerable<RuleState> rules)
            => [.. rules.OrderBy(rule => rule.CreatedAt).ThenBy(rule => rule.Id)];
    }

    private sealed class RuleBucket(RuleState[] rules)
    {
        public static RuleBucket Empty { get; } = new([]);

        private int firstCandidate;

        public bool IsEmpty => rules.Length == 0;

        public bool TryPeek(
            DateTimeOffset now,
            out RuleState? candidate,
            out int index)
        {
            index = Volatile.Read(ref this.firstCandidate);
            while (index < rules.Length)
            {
                candidate = rules[index];
                if (candidate.IsActive && candidate.ExpiresAt > now && candidate.AvailableMatches > 0)
                {
                    return true;
                }

                if (candidate.ExpiresAt <= now)
                {
                    candidate.Deactivate();
                }

                this.AdvancePast(index);
                index = Volatile.Read(ref this.firstCandidate);
            }

            candidate = null;
            return false;
        }

        public void AdvanceIfUnavailable(int index, RuleState candidate)
        {
            if (!candidate.IsActive || candidate.AvailableMatches <= 0)
            {
                this.AdvancePast(index);
            }
        }

        public void Reset() => Volatile.Write(ref this.firstCandidate, 0);

        private void AdvancePast(int index)
        {
            while (true)
            {
                var current = Volatile.Read(ref this.firstCandidate);
                if (current > index ||
                    Interlocked.CompareExchange(ref this.firstCandidate, index + 1, current) == current)
                {
                    return;
                }
            }
        }
    }

    private readonly record struct RuleSelector(string DefinitionName, string ActorId);

    private sealed class RuleSelectorComparer : IEqualityComparer<RuleSelector>
    {
        public static RuleSelectorComparer Instance { get; } = new();

        public bool Equals(RuleSelector x, RuleSelector y)
            => StringComparer.OrdinalIgnoreCase.Equals(x.DefinitionName, y.DefinitionName) &&
                StringComparer.Ordinal.Equals(x.ActorId, y.ActorId);

        public int GetHashCode(RuleSelector obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.DefinitionName),
                StringComparer.Ordinal.GetHashCode(obj.ActorId));
    }

    internal sealed class WorkProfileCaptureRuleLease(
        WorkProfileCaptureRuleStore owner,
        RuleState rule) : IDisposable
    {
        private int completionState;

        public void Commit()
        {
            if (Interlocked.CompareExchange(ref this.completionState, 1, 0) == 0)
            {
                owner.Complete(rule, committed: true);
            }
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref this.completionState, 2, 0) == 0)
            {
                owner.Complete(rule, committed: false);
            }
        }
    }

    internal sealed class RuleState(
        Guid id,
        string? definitionName,
        string? actorId,
        int maximumMatches,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        WorkActor createdBy)
    {
        public Guid Id { get; } = id;

        public string? DefinitionName { get; } = definitionName;

        public string? ActorId { get; } = actorId;

        public int MaximumMatches { get; } = maximumMatches;

        private int active = 1;
        private long matchState = PackMatchState(maximumMatches, pendingMatches: 0);

        public int AvailableMatches => GetAvailableMatches(Volatile.Read(ref this.matchState));

        public int PendingMatches => GetPendingMatches(Volatile.Read(ref this.matchState));

        public bool IsActive => Volatile.Read(ref this.active) != 0;

        public DateTimeOffset CreatedAt { get; } = createdAt;

        public DateTimeOffset ExpiresAt { get; } = expiresAt;

        public WorkActor CreatedBy { get; } = createdBy;

        public static int CompareOrder(RuleState left, RuleState right)
        {
            var createdComparison = left.CreatedAt.CompareTo(right.CreatedAt);
            return createdComparison != 0 ? createdComparison : left.Id.CompareTo(right.Id);
        }

        public bool TryReserve(DateTimeOffset now)
        {
            if (!this.IsActive || this.ExpiresAt <= now)
            {
                if (this.ExpiresAt <= now)
                {
                    this.Deactivate();
                }

                return false;
            }

            while (true)
            {
                var state = Volatile.Read(ref this.matchState);
                var available = GetAvailableMatches(state);
                if (available <= 0)
                {
                    return false;
                }

                var pending = GetPendingMatches(state);
                var reservedState = PackMatchState(available - 1, pending + 1);
                if (Interlocked.CompareExchange(ref this.matchState, reservedState, state) != state)
                {
                    continue;
                }

                var reservationTime = DateTimeOffset.UtcNow;
                if (this.IsActive && this.ExpiresAt > reservationTime)
                {
                    return true;
                }

                if (this.ExpiresAt <= reservationTime)
                {
                    this.Deactivate();
                }

                this.Complete(committed: false, reservationTime);
                return false;
            }
        }

        public bool Complete(bool committed, DateTimeOffset now)
        {
            long completedState;
            while (true)
            {
                var state = Volatile.Read(ref this.matchState);
                var pending = GetPendingMatches(state);
                if (pending <= 0)
                {
                    return false;
                }

                var restoreMatch = !committed && this.IsActive && this.ExpiresAt > now;
                completedState = PackMatchState(
                    GetAvailableMatches(state) + (restoreMatch ? 1 : 0),
                    pending - 1);
                if (Interlocked.CompareExchange(ref this.matchState, completedState, state) == state)
                {
                    if (restoreMatch)
                    {
                        return false;
                    }

                    break;
                }
            }

            if (this.ExpiresAt <= now ||
                (!committed && !this.IsActive) ||
                (GetAvailableMatches(completedState) == 0 && GetPendingMatches(completedState) == 0))
            {
                this.Deactivate();
                return GetPendingMatches(completedState) == 0;
            }

            return false;
        }

        public void Deactivate() => Volatile.Write(ref this.active, 0);

        public WorkProfileCaptureRuleSnapshot ToSnapshot()
            => new(
                this.Id,
                this.DefinitionName,
                this.ActorId,
                this.MaximumMatches,
                this.AvailableMatches,
                this.CreatedAt,
                this.ExpiresAt,
                this.CreatedBy);

        private static long PackMatchState(int availableMatches, int pendingMatches)
            => ((long)(uint)pendingMatches << 32) | (uint)availableMatches;

        private static int GetAvailableMatches(long state) => (int)(uint)state;

        private static int GetPendingMatches(long state) => (int)(state >> 32);
    }
}
