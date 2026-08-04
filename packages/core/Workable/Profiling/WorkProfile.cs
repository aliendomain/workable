using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Workable;

internal sealed class WorkProfile :
    IWorkProfiler,
    IWorkAutomaticProfiler,
    IWorkProfilePendingInstrumentationRegistry,
    IWorkAutomaticProfileSamplingGate
{
    private const int MaximumOmissionInstrumentationKeys = 32;
    private const int MaximumOmissionInstrumentationKeyLength = 128;
    private const string OtherOmissionInstrumentationKey = "other";

    private readonly ProfileScope root;
    private readonly AsyncLocal<ProfileScope?> current = new();
    private readonly ConcurrentDictionary<IWorkProfilePendingInstrumentation, byte> pendingInstrumentation = new();
    private readonly ConcurrentDictionary<string, OmissionCounter> omittedAutomaticNodes = new(StringComparer.Ordinal);
    private readonly Lock omittedAutomaticNodesSync = new();
    private readonly int maximumAutomaticInstrumentationNodes;
    private readonly bool fullAutomaticCapture;
    private int automaticInstrumentationNodeCount;
    private int pendingInstrumentationRegistrations;
    private int finalizing;
    private int omissionSummaryAdded;
    private int otherOmittedAutomaticNodeCount;
    private int omissionInstrumentationKeyCapacityReached;
    private ManualResetEventSlim? pendingInstrumentationRegistrationDrain;

    public WorkProfile(
        string rootName,
        int maximumAutomaticInstrumentationNodes = WorkSystemProfilingConfiguration.DefaultMaximumAutomaticInstrumentationNodes,
        WorkProfileCaptureMode captureMode = WorkProfileCaptureMode.Bounded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootName);
        if (maximumAutomaticInstrumentationNodes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumAutomaticInstrumentationNodes),
                maximumAutomaticInstrumentationNodes,
                "The automatic instrumentation node limit must be greater than zero.");
        }

        this.root = new ProfileScope(null, WorkProfileMetricType.Scope, rootName, context: null);
        this.maximumAutomaticInstrumentationNodes = maximumAutomaticInstrumentationNodes;
        this.fullAutomaticCapture = captureMode == WorkProfileCaptureMode.Full;
    }

    public DateTimeOffset StartedAt => this.root.StartedAt;

    public void AddInfo(string name, object? context = null)
        => this.CurrentScope.AddInfo(name, context);

    public IWorkProfileScope StartTiming(string name, object? context = null)
        => this.CurrentScope.StartTiming(name, context);

    public IWorkProfileScope CreateScope(string name, object? context = null)
        => this.PushScope(new ProfileScope(this.CurrentScope, WorkProfileMetricType.Scope, name, context).Initialize());

    public IWorkProfileScope CreateMethodScope(Type type, string methodName, object? context = null, string label = "Input")
    {
        var identity = $"{type.FullName ?? type.Name}.{methodName}";
        var scope = new MethodProfileScope(this.CurrentScope, $"Executing {identity}", context, label);
        return this.PushScope(scope);
    }

    public IWorkProfileScope CreateMethodScope<T>(
        object? context = null,
        string label = "Input",
        [CallerMemberName] string methodName = "")
        => this.CreateMethodScope(typeof(T), methodName, context, label);

    public bool TryAddAutomaticInfo(string instrumentation, string name, object? context = null)
    {
        if (!((IWorkProfilePendingInstrumentationRegistry)this).TryEnterPendingInstrumentationRegistration())
        {
            return false;
        }

        try
        {
            if (!this.TryReserveAutomaticNode(instrumentation))
            {
                return false;
            }

            this.CurrentScope.AddInfo(name, context);
            return true;
        }
        finally
        {
            ((IWorkProfilePendingInstrumentationRegistry)this).ExitPendingInstrumentationRegistration();
        }
    }

    public bool TryAddAutomaticInfo<TContext>(
        string instrumentation,
        string name,
        Func<TContext> contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        if (!((IWorkProfilePendingInstrumentationRegistry)this).TryEnterPendingInstrumentationRegistration())
        {
            return false;
        }

        var reserved = false;
        try
        {
            if (!this.TryReserveAutomaticNode(instrumentation))
            {
                return false;
            }

            reserved = true;
            this.CurrentScope.AddInfo(name, contextFactory());
            return true;
        }
        catch
        {
            if (reserved)
            {
                this.ReleaseAutomaticNode();
            }

            throw;
        }
        finally
        {
            ((IWorkProfilePendingInstrumentationRegistry)this).ExitPendingInstrumentationRegistration();
        }
    }

    public bool TryStartAutomaticTiming(
        string instrumentation,
        string name,
        object? context,
        out IWorkProfileScope? scope)
    {
        if (!((IWorkProfilePendingInstrumentationRegistry)this).TryEnterPendingInstrumentationRegistration())
        {
            scope = null;
            return false;
        }

        try
        {
            if (!this.TryReserveAutomaticNode(instrumentation))
            {
                scope = null;
                return false;
            }

            scope = this.CurrentScope.StartTiming(name, context);
            return true;
        }
        finally
        {
            ((IWorkProfilePendingInstrumentationRegistry)this).ExitPendingInstrumentationRegistration();
        }
    }

    public bool TryStartAutomaticTiming<TContext>(
        string instrumentation,
        string name,
        Func<TContext> contextFactory,
        out TContext? context,
        out IWorkProfileScope? scope)
        where TContext : class
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        context = null;
        scope = null;
        if (!((IWorkProfilePendingInstrumentationRegistry)this).TryEnterPendingInstrumentationRegistration())
        {
            return false;
        }

        var reserved = false;
        try
        {
            if (!this.TryReserveAutomaticNode(instrumentation))
            {
                return false;
            }

            reserved = true;
            context = contextFactory();
            scope = this.CurrentScope.StartTiming(name, context);
            return true;
        }
        catch
        {
            context = null;
            scope = null;
            if (reserved)
            {
                this.ReleaseAutomaticNode();
            }

            throw;
        }
        finally
        {
            ((IWorkProfilePendingInstrumentationRegistry)this).ExitPendingInstrumentationRegistration();
        }
    }

    public WorkProfileSnapshot ToSnapshot()
    {
        Volatile.Write(ref this.finalizing, 1);
        this.WaitForPendingInstrumentationRegistrations();

        foreach (var instrumentation in this.pendingInstrumentation.Keys)
        {
            if (this.pendingInstrumentation.TryRemove(instrumentation, out _))
            {
                instrumentation.FinalizeForProfileSnapshot();
            }
        }

        this.AddAutomaticInstrumentationOmissionSummary();

        return new WorkProfileSnapshot(
            Snapshot(this.root),
            this.root.StartedAt,
            DateTimeOffset.UtcNow);
    }

    bool IWorkProfilePendingInstrumentationRegistry.IsAcceptingPendingInstrumentation
        => Volatile.Read(ref this.finalizing) == 0;

    bool IWorkProfilePendingInstrumentationRegistry.TryEnterPendingInstrumentationRegistration()
    {
        if (Volatile.Read(ref this.finalizing) != 0)
        {
            return false;
        }

        Interlocked.Increment(ref this.pendingInstrumentationRegistrations);
        if (Volatile.Read(ref this.finalizing) == 0)
        {
            return true;
        }

        Interlocked.Decrement(ref this.pendingInstrumentationRegistrations);
        return false;
    }

    void IWorkProfilePendingInstrumentationRegistry.RegisterPendingInstrumentation(
        IWorkProfilePendingInstrumentation instrumentation)
        => this.pendingInstrumentation.TryAdd(instrumentation, 0);

    void IWorkProfilePendingInstrumentationRegistry.ExitPendingInstrumentationRegistration()
    {
        if (Interlocked.Decrement(ref this.pendingInstrumentationRegistrations) == 0 &&
            Volatile.Read(ref this.finalizing) != 0)
        {
            Volatile.Read(ref this.pendingInstrumentationRegistrationDrain)?.Set();
        }
    }

    void IWorkProfilePendingInstrumentationRegistry.UnregisterPendingInstrumentation(
        IWorkProfilePendingInstrumentation instrumentation)
        => this.pendingInstrumentation.TryRemove(instrumentation, out _);

    bool IWorkAutomaticProfileSamplingGate.TryReserveAutomaticNodeForSampling(string instrumentation)
        => this.TryReserveAutomaticNode(instrumentation);

    bool IWorkAutomaticProfileSamplingGate.TryStartReservedAutomaticTiming<TContext>(
        string name,
        Func<TContext> contextFactory,
        out TContext? context,
        out IWorkProfileScope? scope)
        where TContext : class
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        context = null;
        scope = null;
        try
        {
            context = contextFactory();
            scope = this.CurrentScope.StartTiming(name, context);
            return true;
        }
        catch
        {
            context = null;
            scope = null;
            this.ReleaseAutomaticNode();
            throw;
        }
    }

    void IWorkAutomaticProfileSamplingGate.ReleaseReservedAutomaticNode()
        => this.ReleaseAutomaticNode();

    private ProfileScope CurrentScope => this.current.Value ??= this.root;

    private void WaitForPendingInstrumentationRegistrations()
    {
        if (Volatile.Read(ref this.pendingInstrumentationRegistrations) == 0)
        {
            return;
        }

        var created = new ManualResetEventSlim(initialState: false);
        var drain = Interlocked.CompareExchange(
            ref this.pendingInstrumentationRegistrationDrain,
            created,
            comparand: null) ?? created;
        if (!ReferenceEquals(drain, created))
        {
            created.Dispose();
        }

        if (Volatile.Read(ref this.pendingInstrumentationRegistrations) == 0)
        {
            drain.Set();
        }

        drain.Wait();
    }

    private bool TryReserveAutomaticNode(string instrumentation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instrumentation);
        if (Volatile.Read(ref this.finalizing) != 0)
        {
            return false;
        }

        if (this.fullAutomaticCapture)
        {
            return true;
        }

        while (true)
        {
            var currentCount = Volatile.Read(ref this.automaticInstrumentationNodeCount);
            if (currentCount >= this.maximumAutomaticInstrumentationNodes)
            {
                this.RecordAutomaticNodeOmission(instrumentation);
                return false;
            }

            if (Interlocked.CompareExchange(
                ref this.automaticInstrumentationNodeCount,
                currentCount + 1,
                currentCount) == currentCount)
            {
                return true;
            }
        }
    }

    private void ReleaseAutomaticNode()
    {
        if (!this.fullAutomaticCapture)
        {
            Interlocked.Decrement(ref this.automaticInstrumentationNodeCount);
        }
    }

    private void RecordAutomaticNodeOmission(string instrumentation)
    {
        var normalizedInstrumentation = instrumentation.Length <= MaximumOmissionInstrumentationKeyLength
            ? instrumentation
            : instrumentation[..MaximumOmissionInstrumentationKeyLength];
        if (string.Equals(normalizedInstrumentation, OtherOmissionInstrumentationKey, StringComparison.Ordinal))
        {
            Interlocked.Increment(ref this.otherOmittedAutomaticNodeCount);
            return;
        }

        if (this.omittedAutomaticNodes.TryGetValue(normalizedInstrumentation, out var existing))
        {
            Interlocked.Increment(ref existing.Count);
            return;
        }

        if (Volatile.Read(ref this.omissionInstrumentationKeyCapacityReached) != 0)
        {
            Interlocked.Increment(ref this.otherOmittedAutomaticNodeCount);
            return;
        }

        lock (this.omittedAutomaticNodesSync)
        {
            if (this.omittedAutomaticNodes.TryGetValue(normalizedInstrumentation, out existing))
            {
                Interlocked.Increment(ref existing.Count);
                return;
            }

            if (this.omittedAutomaticNodes.Count >= MaximumOmissionInstrumentationKeys)
            {
                Volatile.Write(ref this.omissionInstrumentationKeyCapacityReached, 1);
                Interlocked.Increment(ref this.otherOmittedAutomaticNodeCount);
                return;
            }

            var counter = new OmissionCounter { Count = 1 };
            this.omittedAutomaticNodes.TryAdd(normalizedInstrumentation, counter);
            if (this.omittedAutomaticNodes.Count >= MaximumOmissionInstrumentationKeys)
            {
                Volatile.Write(ref this.omissionInstrumentationKeyCapacityReached, 1);
            }
        }
    }

    private void AddAutomaticInstrumentationOmissionSummary()
    {
        var otherOmissions = Volatile.Read(ref this.otherOmittedAutomaticNodeCount);
        if ((this.omittedAutomaticNodes.IsEmpty && otherOmissions == 0) ||
            Interlocked.Exchange(ref this.omissionSummaryAdded, 1) != 0)
        {
            return;
        }

        var omitted = this.omittedAutomaticNodes
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .ToDictionary(
                entry => entry.Key,
                entry => Volatile.Read(ref entry.Value.Count),
                StringComparer.Ordinal);
        if (otherOmissions > 0)
        {
            omitted[OtherOmissionInstrumentationKey] = otherOmissions;
        }

        this.root.AddInfo("Automatic instrumentation truncated", new
        {
            MaximumNodes = this.maximumAutomaticInstrumentationNodes,
            OmittedByInstrumentation = omitted,
        });
    }

    private ProfileScopeRestorer PushScope(ProfileScope scope)
    {
        if (!this.CurrentScope.IsActive)
        {
            throw new InvalidOperationException("Cannot create a child profile scope after the current scope has been disposed.");
        }

        var previous = this.CurrentScope;
        this.current.Value = scope;
        return new ProfileScopeRestorer(this, scope, previous);
    }

    private static WorkProfileSnapshotNode Snapshot(ProfileMetric metric)
    {
        if (metric is not ProfileScope rootScope)
        {
            return SnapshotLeaf(metric);
        }

        var frames = ArrayPool<SnapshotFrame>.Shared.Rent(64);
        var depth = 1;
        frames[0] = new SnapshotFrame(rootScope);
        try
        {
            while (true)
            {
                ref var frame = ref frames[depth - 1];
                if (frame.Entries.MoveNext())
                {
                    var entry = frame.Entries.Current;
                    if (entry is ProfileScope nestedScope)
                    {
                        if (depth == frames.Length)
                        {
                            GrowSnapshotFrames(ref frames, depth);
                        }

                        frames[depth++] = new SnapshotFrame(nestedScope);
                    }
                    else
                    {
                        frame.Children.Add(SnapshotLeaf(entry));
                    }

                    continue;
                }

                frame.Entries.Dispose();
                var inclusiveMilliseconds = frame.Scope.InclusiveMilliseconds;
                var completed = new WorkProfileSnapshotNode(
                    frame.Scope.MetricType,
                    inclusiveMilliseconds,
                    Math.Max(0, inclusiveMilliseconds - frame.NestedScopeMilliseconds),
                    frame.Scope.Label,
                    frame.Scope.Context,
                    frame.Children);
                frames[depth - 1] = default;
                depth--;
                if (depth == 0)
                {
                    return completed;
                }

                ref var parent = ref frames[depth - 1];
                parent.NestedScopeMilliseconds += inclusiveMilliseconds;
                parent.Children.Add(completed);
            }
        }
        finally
        {
            for (var index = 0; index < depth; index++)
            {
                frames[index].Entries?.Dispose();
                frames[index] = default;
            }

            ArrayPool<SnapshotFrame>.Shared.Return(frames, clearArray: true);
        }
    }

    private static void GrowSnapshotFrames(ref SnapshotFrame[] frames, int depth)
    {
        var expanded = ArrayPool<SnapshotFrame>.Shared.Rent(checked(frames.Length * 2));
        Array.Copy(frames, expanded, depth);
        ArrayPool<SnapshotFrame>.Shared.Return(frames, clearArray: true);
        frames = expanded;
    }

    private static WorkProfileSnapshotNode SnapshotLeaf(ProfileMetric metric)
    {
        if (metric is ProfileTiming timing)
        {
            var elapsedMilliseconds = timing.ElapsedMilliseconds;
            return new WorkProfileSnapshotNode(
                WorkProfileMetricType.Timing,
                elapsedMilliseconds,
                elapsedMilliseconds,
                timing.Label,
                timing.Context,
                []);
        }

        return new WorkProfileSnapshotNode(
            WorkProfileMetricType.Metric,
            0,
            0,
            metric.Label,
            metric.Context,
            []);
    }

    private struct SnapshotFrame
    {
        public SnapshotFrame(ProfileScope scope)
        {
            this.Scope = scope;
            this.Entries = scope.Entries.GetEnumerator();
            this.Children = new List<WorkProfileSnapshotNode>(scope.EntryCount);
        }

        public ProfileScope Scope { get; }

        public IEnumerator<ProfileMetric> Entries { get; }

        public List<WorkProfileSnapshotNode> Children { get; }

        public long NestedScopeMilliseconds { get; set; }
    }

    private sealed class ProfileScopeRestorer(WorkProfile owner, ProfileScope scope, ProfileScope previous) : IWorkProfileScope
    {
        private bool disposed;

        public void SetResult(object? context = null)
            => scope.AddInfo("Result", context);

        public void Dispose()
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            if (owner.current.Value != scope)
            {
                throw new InvalidOperationException("Profile scopes must be disposed in reverse order of creation.");
            }

            scope.Dispose();
            owner.current.Value = previous;
        }
    }

    private class ProfileMetric(string label, object? context)
    {
        public string Label { get; } = context is string text ? $"{label} ({text})" : label;

        public object? Context { get; } = context;
    }

    private sealed class ProfileInfo(string label, object? context) : ProfileMetric(label, context);

    private sealed class OmissionCounter
    {
        public int Count;
    }

    private class ProfileTiming(string label, object? context) : ProfileMetric(label, context), IWorkProfileScope
    {
        private readonly Stopwatch stopwatch = Stopwatch.StartNew();
        private bool disposed;

        public bool IsActive => !this.disposed;

        public long ElapsedMilliseconds => this.stopwatch.ElapsedMilliseconds;

        public void SetResult(object? context = null)
        {
        }

        public void Dispose()
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            this.stopwatch.Stop();
        }
    }

    private class ProfileScope(
        ProfileScope? parent,
        WorkProfileMetricType metricType,
        string label,
        object? context) : ProfileTiming(label, context)
    {
        private readonly ConcurrentQueue<ProfileMetric> entries = [];

        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

        public WorkProfileMetricType MetricType { get; } = metricType;

        public long InclusiveMilliseconds => this.ElapsedMilliseconds;

        public int EntryCount => this.entries.Count;

        public IEnumerable<ProfileMetric> Entries => this.entries;

        public void AddInfo(string name, object? context = null)
            => this.entries.Enqueue(new ProfileInfo(name, context));

        public ProfileTiming StartTiming(string name, object? context = null)
        {
            var timing = new ProfileTiming(name, context);
            this.entries.Enqueue(timing);
            return timing;
        }

        protected ProfileScope()
            : this(null, WorkProfileMetricType.Scope, string.Empty, null)
        {
        }

        public ProfileScope AddChild(ProfileScope scope)
        {
            this.entries.Enqueue(scope);
            return scope;
        }

        public ProfileScope Initialize()
        {
            parent?.AddChild(this);
            return this;
        }
    }

    private sealed class MethodProfileScope : ProfileScope
    {
        public MethodProfileScope(ProfileScope? parent, string label, object? context, string inputLabel)
            : base(parent, WorkProfileMetricType.MethodScope, label, context)
        {
            this.Initialize();
            if (context is not null)
            {
                this.AddInfo(inputLabel, context);
            }
        }
    }
}
