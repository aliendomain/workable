using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;

namespace Workable;

internal sealed class WorkableHttpClientProfilingObserver : IDisposable
{
    private const string ActivitySourceName = "System.Net.Http";
    private const string RequestActivityName = "System.Net.Http.HttpRequestOut";
    private const string InstrumentationName = "http.client";
    private const string SamplingAdmissionTag = "workable.profiling.http-client.admission";
    private const int MaximumCapturedTextLength = 2048;
    private const int MaximumUriInspectionLength = MaximumCapturedTextLength * 2;

    private readonly IWorkProfilingContextAccessor profilingContextAccessor;
    private readonly ConcurrentDictionary<WorkSystemId, byte> activeSystems = new();
    private readonly ConcurrentDictionary<Activity, ActiveHttpRequest> activeRequests = new();
    private readonly ConcurrentDictionary<WorkSystemId, ConcurrentDictionary<Activity, ActiveHttpRequest>> activeRequestsBySystem = new();
    private readonly ActivityListener listener;
    private int disposed;

    public WorkableHttpClientProfilingObserver(
        IWorkProfilingContextAccessor profilingContextAccessor)
    {
        this.profilingContextAccessor = profilingContextAccessor;
        this.listener = new ActivityListener
        {
            ShouldListenTo = static source => string.Equals(source.Name, ActivitySourceName, StringComparison.Ordinal),
            Sample = SampleRequest,
            SampleUsingParentId = SampleRequest,
            ActivityStarted = this.HandleStarted,
            ActivityStopped = this.HandleStopped,
        };
        ActivitySource.AddActivityListener(this.listener);
    }

    internal WorkableHttpClientProfilingObserver(
        WorkSystemId systemId,
        IWorkProfilingContextAccessor profilingContextAccessor)
        : this(profilingContextAccessor)
        => this.RegisterSystem(systemId);

    internal void RegisterSystem(WorkSystemId systemId)
    {
        this.activeRequestsBySystem.GetOrAdd(systemId, static _ => new());
        this.activeSystems.TryAdd(systemId, 0);
    }

    internal void UnregisterSystem(WorkSystemId systemId)
    {
        this.activeSystems.TryRemove(systemId, out _);
        if (!this.activeRequestsBySystem.TryRemove(systemId, out var systemRequests))
        {
            return;
        }

        foreach (var entry in systemRequests)
        {
            if (systemRequests.TryRemove(entry.Key, out _) &&
                this.activeRequests.TryRemove(entry.Key, out var active))
            {
                active.FinalizeIncomplete();
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
        {
            return;
        }

        this.listener.Dispose();
        this.activeSystems.Clear();
        this.activeRequestsBySystem.Clear();
        foreach (var entry in this.activeRequests)
        {
            if (this.activeRequests.TryRemove(entry.Key, out var active))
            {
                active.FinalizeIncomplete();
            }
        }
    }

    private ActivitySamplingResult SampleRequest(
        ref ActivityCreationOptions<ActivityContext> options)
    {
        if (!this.TrySampleRequest(options.Name, out var sampledRequest))
        {
            return ActivitySamplingResult.None;
        }

        options.SamplingTags[SamplingAdmissionTag] = sampledRequest;
        return ActivitySamplingResult.AllData;
    }

    private ActivitySamplingResult SampleRequest(
        ref ActivityCreationOptions<string> options)
    {
        if (!this.TrySampleRequest(options.Name, out var sampledRequest))
        {
            return ActivitySamplingResult.None;
        }

        options.SamplingTags[SamplingAdmissionTag] = sampledRequest;
        return ActivitySamplingResult.AllData;
    }

    private bool TrySampleRequest(string operationName, out SampledRequest? sampledRequest)
    {
        sampledRequest = null;
        if (!string.Equals(operationName, RequestActivityName, StringComparison.Ordinal) ||
            !this.TryGetCurrentProfiler(out var context))
        {
            return false;
        }

        var samplingGate = context.Profiler as IWorkAutomaticProfileSamplingGate;
        if (samplingGate is not null &&
            !samplingGate.TryReserveAutomaticNodeForSampling(InstrumentationName))
        {
            return false;
        }

        if (context.Profiler is IWorkProfilePendingInstrumentationRegistry registry &&
            !registry.IsAcceptingPendingInstrumentation)
        {
            samplingGate?.ReleaseReservedAutomaticNode();
            return false;
        }

        sampledRequest = new SampledRequest(context, samplingGate);
        return true;
    }

    private void HandleStarted(Activity activity)
    {
        if (!string.Equals(activity.OperationName, RequestActivityName, StringComparison.Ordinal) ||
            activity.GetTagItem(SamplingAdmissionTag) is not SampledRequest sampledRequest)
        {
            return;
        }

        activity.SetTag(SamplingAdmissionTag, null);
        var profilingContext = sampledRequest.ProfilingContext;

        if (this.IsDisposed || !this.activeSystems.ContainsKey(profilingContext.SystemId))
        {
            sampledRequest.Dispose();
            return;
        }

        var pendingRegistry = profilingContext.Profiler as IWorkProfilePendingInstrumentationRegistry;
        if (pendingRegistry is not null &&
            !pendingRegistry.TryEnterPendingInstrumentationRegistration())
        {
            sampledRequest.Dispose();
            return;
        }

        try
        {
            var started = !sampledRequest.HasReservation
                ? profilingContext.TryStartAutomaticTiming(
                    InstrumentationName,
                    "HTTP Request",
                    () => HttpClientProfileContext.Start(activity),
                    out HttpClientProfileContext? context,
                    out var scope)
                : sampledRequest.TryStartReservedTiming(
                    "HTTP Request",
                    () => HttpClientProfileContext.Start(activity),
                    out context,
                    out scope);
            if (!started)
            {
                return;
            }

            var active = new ActiveHttpRequest(
                this,
                activity,
                profilingContext.SystemId,
                context!,
                scope!,
                pendingRegistry);
            if (!this.activeRequests.TryAdd(activity, active))
            {
                active.FinalizeIncomplete();
                return;
            }

            var systemRequests = this.activeRequestsBySystem.GetOrAdd(
                profilingContext.SystemId,
                static _ => new());
            systemRequests.TryAdd(activity, active);
            pendingRegistry?.RegisterPendingInstrumentation(active);
            active.RemovePendingRegistrationIfCompleted();
            if ((this.IsDisposed || !this.activeSystems.ContainsKey(profilingContext.SystemId)) &&
                this.activeRequests.TryRemove(activity, out var added))
            {
                systemRequests.TryRemove(activity, out _);
                added.FinalizeIncomplete();
            }
        }
        finally
        {
            sampledRequest.Dispose();
            pendingRegistry?.ExitPendingInstrumentationRegistration();
        }
    }

    private void HandleStopped(Activity activity)
    {
        if (!this.activeRequests.TryRemove(activity, out var active))
        {
            return;
        }

        this.RemoveFromSystemIndex(activity, active);
        active.Complete(activity);
    }

    private bool TryGetCurrentProfiler(out WorkProfilingContext context)
    {
        if (this.IsDisposed ||
            !this.profilingContextAccessor.TryGetCurrent(out context) ||
            !this.activeSystems.ContainsKey(context.SystemId))
        {
            context = default;
            return false;
        }

        return true;
    }

    private void FinalizeForProfileSnapshot(Activity activity, ActiveHttpRequest expected)
    {
        if (this.activeRequests.TryRemove(activity, out var active))
        {
            this.RemoveFromSystemIndex(activity, active);
            active.FinalizeIncomplete();
            return;
        }

        expected.WaitForCompletion();
    }

    private void RemoveFromSystemIndex(Activity activity, ActiveHttpRequest active)
    {
        if (this.activeRequestsBySystem.TryGetValue(active.SystemId, out var systemRequests))
        {
            systemRequests.TryRemove(activity, out _);
        }
    }

    private bool IsDisposed => Volatile.Read(ref this.disposed) != 0;

    private sealed class SampledRequest(
        WorkProfilingContext profilingContext,
        IWorkAutomaticProfileSamplingGate? samplingGate) : IDisposable
    {
        private int reservationConsumed;

        public WorkProfilingContext ProfilingContext { get; } = profilingContext;

        public bool HasReservation => samplingGate is not null;

        public bool TryStartReservedTiming<TContext>(
            string name,
            Func<TContext> contextFactory,
            out TContext? context,
            out IWorkProfileScope? scope)
            where TContext : class
        {
            if (samplingGate is null || Interlocked.Exchange(ref this.reservationConsumed, 1) != 0)
            {
                context = null;
                scope = null;
                return false;
            }

            return samplingGate.TryStartReservedAutomaticTiming(name, contextFactory, out context, out scope);
        }

        public void Dispose()
        {
            if (samplingGate is not null && Interlocked.Exchange(ref this.reservationConsumed, 1) == 0)
            {
                samplingGate.ReleaseReservedAutomaticNode();
            }
        }
    }

    private static object? GetTag(Activity activity, string currentName, string legacyName)
        => activity.GetTagItem(currentName) ?? activity.GetTagItem(legacyName);

    private static string? GetStringTag(Activity activity, string currentName, string legacyName)
    {
        var value = GetTag(activity, currentName, legacyName);
        return value is null
            ? null
            : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static string? GetStringTag(Activity activity, string name)
        => GetStringTag(activity, name, name);

    private static int? GetInt32Tag(Activity activity, string currentName, string legacyName)
    {
        var value = GetTag(activity, currentName, legacyName);
        return value switch
        {
            int number => number,
            HttpStatusCode statusCode => (int)statusCode,
            IConvertible convertible => TryConvertInt32(convertible),
            _ => null,
        };
    }

    private static int? TryConvertInt32(IConvertible value)
    {
        try
        {
            return value.ToInt32(CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    private static CapturedUri CaptureUri(string? uriValue)
    {
        if (string.IsNullOrEmpty(uriValue))
        {
            return new(null, false, false);
        }

        var inspectionLength = Math.Min(uriValue.Length, MaximumUriInspectionLength);
        var inspected = uriValue.AsSpan(0, inspectionLength);
        var inspectionTruncated = uriValue.Length > inspectionLength;
        if (inspected.Trim().IsEmpty)
        {
            return new(null, false, inspectionTruncated);
        }

        var separator = inspected.IndexOfAny('?', '#');
        bool? hasQueryString = separator >= 0
            ? inspected[separator] == '?'
            : inspectionTruncated
                ? null
                : false;
        var sanitized = separator < 0 ? inspected : inspected[..separator];
        if (sanitized.IsEmpty ||
            !HasCompleteAuthority(uriValue.Length, inspectionLength, inspected, separator))
        {
            return new(null, hasQueryString, inspectionTruncated);
        }

        var candidate = uriValue.Length <= inspectionLength
            ? uriValue
            : sanitized.ToString();
        if (!Uri.TryCreate(candidate, UriKind.RelativeOrAbsolute, out var uri))
        {
            return new(null, hasQueryString, inspectionTruncated);
        }

        if (!uri.IsAbsoluteUri)
        {
            if (LooksLikeAuthorityUri(sanitized))
            {
                return new(null, hasQueryString, inspectionTruncated);
            }

            return new(
                Truncate(separator < 0 ? uri.OriginalString : sanitized.ToString()),
                hasQueryString,
                inspectionTruncated);
        }

        var host = uri.HostNameType == UriHostNameType.IPv6
            ? $"[{uri.IdnHost}]"
            : uri.IdnHost;
        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        return new(
            Truncate($"{uri.Scheme}://{host}{port}{uri.AbsolutePath}"),
            hasQueryString,
            inspectionTruncated);
    }

    private static bool LooksLikeAuthorityUri(ReadOnlySpan<char> value)
        => value.StartsWith("//", StringComparison.Ordinal) ||
            value.IndexOf("://", StringComparison.Ordinal) >= 0;

    private static bool HasCompleteAuthority(
        int originalLength,
        int inspectionLength,
        ReadOnlySpan<char> inspected,
        int separator)
    {
        if (originalLength <= inspectionLength)
        {
            return true;
        }

        var schemeSeparator = inspected.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator < 0)
        {
            return true;
        }

        var authority = inspected[(schemeSeparator + 3)..];
        return separator >= schemeSeparator + 3 || authority.IndexOf('/') >= 0;
    }

    private static string Truncate(string value)
        => value.Length <= MaximumCapturedTextLength
            ? value
            : value[..MaximumCapturedTextLength];

    internal static string? CaptureUriForBenchmark(string? uriValue)
        => CaptureUri(uriValue).Value;

    private readonly record struct CapturedUri(
        string? Value,
        bool? HasQueryString,
        bool InspectionTruncated);

    private sealed class ActiveHttpRequest(
        WorkableHttpClientProfilingObserver owner,
        Activity activity,
        WorkSystemId systemId,
        HttpClientProfileContext context,
        IWorkProfileScope scope,
        IWorkProfilePendingInstrumentationRegistry? pendingRegistry) : IWorkProfilePendingInstrumentation
    {
        private int completionState;

        public WorkSystemId SystemId { get; } = systemId;

        public HttpClientProfileContext Context { get; } = context;

        public void Complete(Activity completedActivity)
            => this.Finish(() => this.Context.Complete(completedActivity));

        public void FinalizeIncomplete()
            => this.Finish(this.Context.FinalizeIncomplete);

        public void FinalizeForProfileSnapshot()
            => owner.FinalizeForProfileSnapshot(activity, this);

        public void RemovePendingRegistrationIfCompleted()
        {
            if (Volatile.Read(ref this.completionState) == 2)
            {
                pendingRegistry?.UnregisterPendingInstrumentation(this);
            }
        }

        public void WaitForCompletion()
        {
            var spinner = new SpinWait();
            while (Volatile.Read(ref this.completionState) == 1)
            {
                spinner.SpinOnce();
            }
        }

        private void Finish(Action completeContext)
        {
            if (Interlocked.CompareExchange(ref this.completionState, 1, 0) != 0)
            {
                this.WaitForCompletion();
                return;
            }

            try
            {
                completeContext();
            }
            finally
            {
                try
                {
                    scope.Dispose();
                }
                finally
                {
                    Volatile.Write(ref this.completionState, 2);
                    pendingRegistry?.UnregisterPendingInstrumentation(this);
                }
            }
        }
    }

    private sealed class HttpClientProfileContext
    {
        private bool methodObserved;
        private bool uriObserved;

        private HttpClientProfileContext()
        {
        }

        public string Provider { get; } = ActivitySourceName;

        public string? Method { get; private set; }

        public string? Uri { get; private set; }

        public bool? HasQueryString { get; private set; }

        public bool UriInspectionTruncated { get; private set; }

        public string? ProtocolVersion { get; private set; }

        public int? StatusCode { get; private set; }

        public bool? IsSuccessStatusCode { get; private set; }

        public string Outcome { get; private set; } = "Pending";

        public string? ExceptionType { get; private set; }

        public static HttpClientProfileContext Start(Activity activity)
        {
            var context = new HttpClientProfileContext();
            context.ReadRequest(activity);
            return context;
        }

        public void Complete(Activity activity)
        {
            this.ReadRequest(activity);
            this.ProtocolVersion = TruncateNullable(GetStringTag(activity, "network.protocol.version", "http.flavor"));
            this.StatusCode = GetInt32Tag(activity, "http.response.status_code", "http.status_code");
            this.IsSuccessStatusCode = this.StatusCode is >= 200 and <= 299;
            this.ExceptionType = TruncateNullable(GetStringTag(activity, "error.type"));
            this.Outcome = IsCancellation(this.ExceptionType)
                ? "Canceled"
                : activity.Status == ActivityStatusCode.Error || this.ExceptionType is not null
                    ? "Faulted"
                    : "Completed";
        }

        public void FinalizeIncomplete()
            => this.Outcome = "Incomplete";

        private void ReadRequest(Activity activity)
        {
            if (!this.methodObserved)
            {
                var method = GetStringTag(activity, "http.request.method", "http.method");
                if (method is not null)
                {
                    this.Method = Truncate(method);
                    this.methodObserved = true;
                }
            }

            if (!this.uriObserved)
            {
                var uriValue = GetStringTag(activity, "url.full", "http.url");
                if (uriValue is not null)
                {
                    var captured = CaptureUri(uriValue);
                    this.Uri = captured.Value;
                    this.HasQueryString = captured.HasQueryString;
                    this.UriInspectionTruncated = captured.InspectionTruncated;
                    this.uriObserved = true;
                }
            }
        }

        private static bool IsCancellation(string? errorType)
            => errorType?.Contains("cancel", StringComparison.OrdinalIgnoreCase) == true;

        private static string? TruncateNullable(string? value)
            => value is null ? null : Truncate(value);
    }
}
