using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Workable;

internal sealed class WorkProfile(string rootName) : IWorkProfiler
{
    private readonly ProfileScope root = new(null, WorkProfileMetricType.Scope, rootName, context: null);
    private readonly AsyncLocal<ProfileScope?> current = new();

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

    public WorkProfileSnapshot ToSnapshot()
        => new(Snapshot(this.root), this.root.StartedAt, DateTimeOffset.UtcNow);

    private ProfileScope CurrentScope => this.current.Value ??= this.root;

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
        => metric switch
        {
            ProfileScope scope => new WorkProfileSnapshotNode(
                scope.MetricType,
                scope.InclusiveMilliseconds,
                scope.SelfMilliseconds,
                scope.Label,
                scope.Context,
                [.. scope.Entries.Select(Snapshot)]),
            ProfileTiming timing => new WorkProfileSnapshotNode(
                WorkProfileMetricType.Timing,
                timing.ElapsedMilliseconds,
                timing.ElapsedMilliseconds,
                timing.Label,
                timing.Context,
                []),
            _ => new WorkProfileSnapshotNode(
                WorkProfileMetricType.Metric,
                0,
                0,
                metric.Label,
                metric.Context,
                []),
        };

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

        public IReadOnlyList<ProfileMetric> Entries => [.. this.entries];

        public long InclusiveMilliseconds => this.ElapsedMilliseconds;

        public long SelfMilliseconds
            => Math.Max(0, this.ElapsedMilliseconds - this.entries.OfType<ProfileScope>().Sum(entry => entry.InclusiveMilliseconds));

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
