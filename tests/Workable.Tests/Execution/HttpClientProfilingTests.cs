using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Profiling")]
public sealed class HttpClientProfilingTests
{
    private const string ActivitySourceName = "System.Net.Http";
    private const string RequestActivityName = "System.Net.Http.HttpRequestOut";

    [Fact]
    public void RegistrationIsIdempotentAndAdvertisesHttpClientProfiling()
    {
        var services = new ServiceCollection()
            .AddWorkableHttpClientProfiling()
            .AddWorkableHttpClientProfiling()
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create("http-profile-capability", "Exposes HTTP profiling capability."),
                SuccessfulWork));

        using var provider = services.BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;

        Assert.True(Assert.IsAssignableFrom<IWorkSystemCapabilitySource>(system).Capabilities.HttpClientProfilingAvailable);
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(WorkableHttpClientProfilingRegistrationMarker));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IWorkProfilingInstrumentationFactory));
    }

    [Fact]
    public void FactorySharesOneListenerAcrossSystemsAndReleasesItAfterTheLastSystemStops()
    {
        var firstSystemId = WorkSystemId.New();
        var secondSystemId = WorkSystemId.New();
        var firstProfile = new WorkProfile("first");
        var secondProfile = new WorkProfile("second");
        var accessor = new WorkProfilingContextAccessor();
        using var activities = new ActivitySource(ActivitySourceName);
        using var factory = new WorkableHttpClientProfilingInstrumentationFactory();
        var firstRegistration = factory.Create(firstSystemId, accessor);
        var observer = factory.Observer;
        var secondRegistration = factory.Create(secondSystemId, accessor);

        Assert.NotNull(observer);
        Assert.Same(observer, factory.Observer);
        Activity firstActivity;
        using (WorkProfilerContext.Begin(firstSystemId, firstProfile))
        {
            firstActivity = StartRequiredRequestActivity(
                activities,
                "GET",
                "https://example.test/first");
            Assert.True(firstActivity.IsAllDataRequested);
            Assert.False(firstActivity.Recorded);
        }

        firstRegistration.Dispose();
        firstRegistration.Dispose();
        firstActivity.SetTag("http.response.status_code", 200);
        firstActivity.Dispose();
        Assert.Same(observer, factory.Observer);
        using (WorkProfilerContext.Begin(firstSystemId, firstProfile))
        {
            Assert.Null(StartRequestActivity(activities, "https://example.test/first-stopped"));
        }

        using (WorkProfilerContext.Begin(secondSystemId, secondProfile))
        {
            WriteCompletedActivity(activities, "https://example.test/second");
        }

        secondRegistration.Dispose();
        Assert.Null(factory.Observer);
        var firstNode = Assert.Single(Flatten(firstProfile.ToSnapshot().Root), node => node.Label == "HTTP Request");
        Assert.Contains(
            "\"Outcome\":\"Incomplete\"",
            JsonSerializer.Serialize(firstNode.Context),
            StringComparison.Ordinal);
        Assert.Single(Flatten(secondProfile.ToSnapshot().Root), node => node.Label == "HTTP Request");

        factory.Dispose();
        Assert.Throws<ObjectDisposedException>(() => factory.Create(firstSystemId, accessor));
    }

    [Fact]
    public void FactoryReferenceCountsDuplicateSystemLeasesAndRejectsAnotherAccessor()
    {
        var systemId = WorkSystemId.New();
        var accessor = new WorkProfilingContextAccessor();
        using var factory = new WorkableHttpClientProfilingInstrumentationFactory();
        var first = factory.Create(systemId, accessor);
        var observer = factory.Observer;
        var second = factory.Create(systemId, accessor);

        Assert.Same(observer, factory.Observer);
        Assert.Throws<InvalidOperationException>(() =>
            factory.Create(systemId, new WorkProfilingContextAccessor()));

        first.Dispose();
        Assert.Same(observer, factory.Observer);
        second.Dispose();
        Assert.Null(factory.Observer);
    }

    [Fact]
    public void FactoryDisposalFinalizesActiveRequestsAndMakesOutstandingLeasesHarmless()
    {
        var systemId = WorkSystemId.New();
        var profile = new WorkProfile("root");
        var accessor = new WorkProfilingContextAccessor();
        using var activities = new ActivitySource(ActivitySourceName);
        var factory = new WorkableHttpClientProfilingInstrumentationFactory();
        var registration = factory.Create(systemId, accessor);

        using (WorkProfilerContext.Begin(systemId, profile))
        {
            using var activity = StartRequiredRequestActivity(
                activities,
                "GET",
                "https://example.test/factory-dispose");
            factory.Dispose();
            factory.Dispose();
            registration.Dispose();
            activity.SetTag("http.response.status_code", 200);
        }

        var node = Assert.Single(Flatten(profile.ToSnapshot().Root), candidate => candidate.Label == "HTTP Request");
        Assert.Contains(
            "\"Outcome\":\"Incomplete\"",
            JsonSerializer.Serialize(node.Context),
            StringComparison.Ordinal);
        Assert.Null(factory.Observer);
    }

    [Fact]
    public void HttpCaptureUsesSharedAutomaticInstrumentationLimitAndReportsOmissions()
    {
        var systemId = WorkSystemId.New();
        var profile = new WorkProfile("root", maximumAutomaticInstrumentationNodes: 1);
        using var activities = new ActivitySource(ActivitySourceName);
        using var observer = new WorkableHttpClientProfilingObserver(
            systemId,
            new WorkProfilingContextAccessor());

        using (WorkProfilerContext.Begin(systemId, profile))
        {
            WriteCompletedActivity(activities, $"https://example.test/{new string('x', 3000)}?token=secret");
            Assert.Null(StartRequestActivity(activities, "https://example.test/second"));
        }

        var snapshot = profile.ToSnapshot();

        var captured = Assert.Single(Flatten(snapshot.Root), node => node.Label == "HTTP Request");
        var capturedJson = JsonSerializer.Serialize(captured.Context);
        var summary = Assert.Single(
            Flatten(snapshot.Root),
            node => node.Label == "Automatic instrumentation truncated");
        Assert.DoesNotContain("secret", capturedJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("http.client", captured.Instrumentation);
        Assert.True(capturedJson.Length < 2500);
        Assert.Contains(
            "http.client",
            JsonSerializer.Serialize(summary.Context),
            StringComparison.Ordinal);
    }

    [Fact]
    public void BoundsVeryLargeUriSanitizationBeforeRetainingProfileContext()
    {
        var systemId = WorkSystemId.New();
        var profile = new WorkProfile("root", captureMode: WorkProfileCaptureMode.Full);
        using var activities = new ActivitySource(ActivitySourceName);
        using var observer = new WorkableHttpClientProfilingObserver(systemId, new WorkProfilingContextAccessor());
        var hugePath = new string('x', 1_000_000);

        using (WorkProfilerContext.Begin(systemId, profile))
        {
            WriteCompletedActivity(
                activities,
                $"https://user:password@example.test/{hugePath}?token=query-secret#fragment");
        }

        var node = Assert.Single(
            Flatten(profile.ToSnapshot().Root),
            candidate => candidate.Label == "HTTP Request");
        var contextJson = JsonSerializer.Serialize(node.Context);

        Assert.Contains("\"HasQueryString\":null", contextJson, StringComparison.Ordinal);
        Assert.Contains("\"UriInspectionTruncated\":true", contextJson, StringComparison.Ordinal);
        Assert.DoesNotContain("password", contextJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("query-secret", contextJson, StringComparison.OrdinalIgnoreCase);
        Assert.True(contextJson.Length < 2_500);
    }

    [Fact]
    public void OmitsAnOversizedAbsoluteAuthorityThatCannotBeSafelyInspected()
    {
        var systemId = WorkSystemId.New();
        var profile = new WorkProfile("root");
        using var activities = new ActivitySource(ActivitySourceName);
        using var observer = new WorkableHttpClientProfilingObserver(systemId, new WorkProfilingContextAccessor());
        var oversizedUserInfo = new string('s', 10_000);

        using (WorkProfilerContext.Begin(systemId, profile))
        {
            WriteCompletedActivity(
                activities,
                $"https://{oversizedUserInfo}@example.test/orders?token=query-secret");
        }

        var node = Assert.Single(
            Flatten(profile.ToSnapshot().Root),
            candidate => candidate.Label == "HTTP Request");
        var contextJson = JsonSerializer.Serialize(node.Context);

        Assert.Contains("\"Uri\":null", contextJson, StringComparison.Ordinal);
        Assert.Contains("\"HasQueryString\":null", contextJson, StringComparison.Ordinal);
        Assert.Contains("\"UriInspectionTruncated\":true", contextJson, StringComparison.Ordinal);
        Assert.DoesNotContain(oversizedUserInfo[..128], contextJson, StringComparison.Ordinal);
        Assert.DoesNotContain("query-secret", contextJson, StringComparison.Ordinal);
    }

    [Fact]
    public void BoundedUriCaptureRejectsWhitespaceAndTruncatesOversizedRelativePaths()
    {
        Assert.Null(WorkableHttpClientProfilingObserver.CaptureUriForBenchmark("   \t"));

        var captured = WorkableHttpClientProfilingObserver.CaptureUriForBenchmark(
            $"/{new string('x', 10_000)}");

        Assert.NotNull(captured);
        Assert.StartsWith("/", captured, StringComparison.Ordinal);
        Assert.Equal(2_048, captured.Length);
    }

    [Fact]
    public void IndependentTracingDoesNotDoubleCountPostCapHttpOmissions()
    {
        const int omittedRequestCount = 10;
        var systemId = WorkSystemId.New();
        var profile = new WorkProfile("root", maximumAutomaticInstrumentationNodes: 1);
        using var activities = new ActivitySource(ActivitySourceName);
        using var forcingListener = CreateForcingListener();
        using var observer = new WorkableHttpClientProfilingObserver(systemId, new WorkProfilingContextAccessor());
        Assert.True(profile.TryAddAutomaticInfo("setup", "Captured"));

        using (WorkProfilerContext.Begin(systemId, profile))
        {
            for (var index = 0; index < omittedRequestCount; index++)
            {
                using var activity = activities.StartActivity(RequestActivityName, ActivityKind.Client);
                Assert.NotNull(activity);
            }
        }

        var omissions = ReadOmissions(profile.ToSnapshot());

        Assert.Equal(omittedRequestCount, omissions["http.client"]);
    }

    [Fact]
    public async Task ConcurrentHttpSamplingAtomicallyAdmitsOnlyTheProfileCapacity()
    {
        const int requestCount = 32;
        var systemId = WorkSystemId.New();
        var profile = new WorkProfile("root", maximumAutomaticInstrumentationNodes: 1);
        using var activities = new ActivitySource(ActivitySourceName);
        using var arrived = new CountdownEvent(requestCount);
        using var released = new ManualResetEventSlim();
        using var forcingListener = CreateForcingListener(() =>
        {
            arrived.Signal();
            released.Wait(TimeSpan.FromSeconds(10));
        });
        using var observer = new WorkableHttpClientProfilingObserver(systemId, new WorkProfilingContextAccessor());
        var starts = Enumerable.Range(0, requestCount)
            .Select(_ => Task.Factory.StartNew(
                () =>
                {
                    using var ambient = WorkProfilerContext.Begin(systemId, profile);
                    using var activity = activities.StartActivity(RequestActivityName, ActivityKind.Client);
                    Assert.NotNull(activity);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();

        Assert.True(arrived.Wait(TimeSpan.FromSeconds(10)));
        released.Set();
        await Task.WhenAll(starts);
        var snapshot = profile.ToSnapshot();

        Assert.Single(Flatten(snapshot.Root), node => node.Label == "HTTP Request");
        Assert.Equal(requestCount - 1, ReadOmissions(snapshot)["http.client"]);
    }

    [Fact]
    public async Task DevelopmentDefaultCapturesSanitizedHttpRequestAndOmitsSensitiveTelemetry()
    {
        const string workName = "http-profile-development-default";
        using var activities = new ActivitySource(ActivitySourceName);
        using var provider = new ServiceCollection()
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment(Environments.Development))
            .AddWorkableHttpClientProfiling()
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create(workName, "Profiles an outbound HTTP request by default in development."),
                (_, _, _) =>
                {
                    using var activity = StartRequiredRequestActivity(
                        activities,
                        "POST",
                        "https://user:password@example.test:8443/orders/42?access_token=query-secret#fragment");
                    activity.SetTag("network.protocol.version", "2");
                    activity.SetTag("http.response.status_code", 201);
                    activity.SetTag("http.request.header.authorization", "Bearer header-secret");
                    activity.SetTag("http.request.header.x_api_key", "api-key-secret");
                    activity.SetTag("http.response.header.set_cookie", "session=cookie-secret");
                    activity.SetTag("http.request.body", "request-body-secret");
                    return Task.FromResult(WorkExecutionResult.Success());
                }))
            .BuildServiceProvider();
        await using var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var completion = await (await system.Queue.Enqueue(workName)).WaitForCompletion();

        Assert.True(completion.IsCompletedSuccessfully);
        var worker = completion.Worker ?? throw new InvalidOperationException("Expected worker snapshot.");
        Assert.True(worker.Options.ProfilingEnabled);
        var profile = worker.Profile ?? throw new InvalidOperationException("Expected worker profile.");
        var httpNode = Assert.Single(
            Flatten(profile.Root),
            node => node.MetricType == WorkProfileMetricType.Timing && node.Label == "HTTP Request");
        var contextJson = JsonSerializer.Serialize(httpNode.Context);

        Assert.Contains("\"Provider\":\"System.Net.Http\"", contextJson, StringComparison.Ordinal);
        Assert.Contains("\"Method\":\"POST\"", contextJson, StringComparison.Ordinal);
        Assert.Contains("\"Uri\":\"https://example.test:8443/orders/42\"", contextJson, StringComparison.Ordinal);
        Assert.Contains("\"HasQueryString\":true", contextJson, StringComparison.Ordinal);
        Assert.Contains("\"ProtocolVersion\":\"2\"", contextJson, StringComparison.Ordinal);
        Assert.Contains("\"StatusCode\":201", contextJson, StringComparison.Ordinal);
        Assert.Contains("\"IsSuccessStatusCode\":true", contextJson, StringComparison.Ordinal);
        Assert.Contains("\"Outcome\":\"Completed\"", contextJson, StringComparison.Ordinal);
        Assert.DoesNotContain("access_token", contextJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", contextJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", contextJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Api-Key", contextJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cookie", contextJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("body-secret", contextJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnconfiguredHttpClientProfilingDoesNotCaptureActivities()
    {
        const string workName = "http-profile-unconfigured";
        using var activities = new ActivitySource(ActivitySourceName);
        using var provider = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create(
                    workName,
                    "Does not capture HTTP activities without host registration.",
                    defaultOptions: new WorkerOptions(ProfilingEnabled: true)),
                (_, _, _) =>
                {
                    WriteCompletedActivity(activities, "https://example.test/unconfigured");
                    return Task.FromResult(WorkExecutionResult.Success());
                }))
            .BuildServiceProvider();
        await using var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var completion = await (await system.Queue.Enqueue(workName)).WaitForCompletion();

        var profile = completion.Worker?.Profile ?? throw new InvalidOperationException("Expected worker profile.");
        Assert.DoesNotContain(Flatten(profile.Root), node => node.Label == "HTTP Request");
        Assert.False(Assert.IsAssignableFrom<IWorkSystemCapabilitySource>(system).Capabilities.HttpClientProfilingAvailable);
    }

    [Fact]
    public async Task BuiltInHttpClientActivitiesAreCapturedWithoutWrappingTheClient()
    {
        const string workName = "http-profile-real-client";
        var endpoint = ReserveUnusedLoopbackEndpoint();
        using var provider = new ServiceCollection()
            .AddWorkableHttpClientProfiling()
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create(
                    workName,
                    "Profiles the activities emitted by a real HttpClient request.",
                    defaultOptions: new WorkerOptions(ProfilingEnabled: true)),
                async (_, _, cancellationToken) =>
                {
                    using var handler = new SocketsHttpHandler { UseProxy = false };
                    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
                    using var request = new HttpRequestMessage(
                        HttpMethod.Get,
                        $"http://127.0.0.1:{endpoint.Port}/health?token=real-query-secret");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "real-header-secret");

                    try
                    {
                        using var response = await client.SendAsync(request, cancellationToken);
                    }
                    catch (HttpRequestException)
                    {
                        return WorkExecutionResult.Success();
                    }

                    return WorkExecutionResult.Failure([
                        WorkMessage.Error("http.unexpected_success", "The reserved loopback endpoint unexpectedly accepted the request."),
                    ]);
                }))
            .BuildServiceProvider();
        await using var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var completion = await (await system.Queue.Enqueue(workName)).WaitForCompletion();

        Assert.True(completion.IsCompletedSuccessfully);
        var profile = completion.Worker?.Profile ?? throw new InvalidOperationException("Expected worker profile.");
        var httpNode = Assert.Single(Flatten(profile.Root), node => node.Label == "HTTP Request");
        var contextJson = JsonSerializer.Serialize(httpNode.Context);
        using var contextDocument = JsonDocument.Parse(contextJson);
        Assert.Contains("\"Method\":\"GET\"", contextJson, StringComparison.Ordinal);
        Assert.Contains($"\"Uri\":\"http://127.0.0.1:{endpoint.Port}/health\"", contextJson, StringComparison.Ordinal);
        Assert.Contains("\"Outcome\":\"Faulted\"", contextJson, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(contextDocument.RootElement.GetProperty("ExceptionType").GetString()));
        Assert.DoesNotContain("real-query-secret", contextJson, StringComparison.Ordinal);
        Assert.DoesNotContain("real-header-secret", contextJson, StringComparison.Ordinal);
    }

    [Fact]
    public void FaultedRequestCapturesErrorTypeWithoutUnselectedActivityTags()
    {
        var systemId = WorkSystemId.New();
        var profile = new WorkProfile("root");
        using var activities = new ActivitySource(ActivitySourceName);
        using var observer = new WorkableHttpClientProfilingObserver(systemId, new WorkProfilingContextAccessor());

        using (WorkProfilerContext.Begin(systemId, profile))
        {
            using var activity = StartRequiredRequestActivity(
                activities,
                "GET",
                "https://example.test/failure?token=query-secret");
            activity.SetTag("error.type", typeof(HttpRequestException).FullName);
            activity.SetTag("exception.message", "transport-secret-message");
            activity.SetStatus(ActivityStatusCode.Error);
        }

        var httpNode = Assert.Single(Flatten(profile.ToSnapshot().Root), node => node.Label == "HTTP Request");
        var contextJson = JsonSerializer.Serialize(httpNode.Context);
        Assert.Contains(typeof(HttpRequestException).FullName!, contextJson, StringComparison.Ordinal);
        Assert.Contains("\"Outcome\":\"Faulted\"", contextJson, StringComparison.Ordinal);
        Assert.DoesNotContain("transport-secret-message", contextJson, StringComparison.Ordinal);
        Assert.DoesNotContain("query-secret", contextJson, StringComparison.Ordinal);
    }

    [Fact]
    public void FaultedRequestDoesNotCaptureOpenTelemetryStatusDescription()
    {
        var systemId = WorkSystemId.New();
        var profile = new WorkProfile("root");
        using var activities = new ActivitySource(ActivitySourceName);
        using var observer = new WorkableHttpClientProfilingObserver(systemId, new WorkProfilingContextAccessor());

        using (WorkProfilerContext.Begin(systemId, profile))
        {
            using var activity = StartRequiredRequestActivity(
                activities,
                "GET",
                "https://example.test/failure");
            activity.SetTag("otel.status_description", "transport-secret-message");
            activity.SetStatus(ActivityStatusCode.Error);
        }

        var httpNode = Assert.Single(Flatten(profile.ToSnapshot().Root), node => node.Label == "HTTP Request");
        var contextJson = JsonSerializer.Serialize(httpNode.Context);
        Assert.Contains("\"ExceptionType\":null", contextJson, StringComparison.Ordinal);
        Assert.Contains("\"Outcome\":\"Faulted\"", contextJson, StringComparison.Ordinal);
        Assert.DoesNotContain("transport-secret-message", contextJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://user:password@[invalid?secret=value")]
    [InlineData("//user:password@example.test/orders?secret=value")]
    public void MalformedOrRelativeAuthorityUrisAreOmitted(string uri)
    {
        var systemId = WorkSystemId.New();
        var profile = new WorkProfile("root");
        using var activities = new ActivitySource(ActivitySourceName);
        using var observer = new WorkableHttpClientProfilingObserver(systemId, new WorkProfilingContextAccessor());

        using (WorkProfilerContext.Begin(systemId, profile))
        {
            using var activity = StartRequiredRequestActivity(activities, "GET", uri);
        }

        var httpNode = Assert.Single(Flatten(profile.ToSnapshot().Root), node => node.Label == "HTTP Request");
        var contextJson = JsonSerializer.Serialize(httpNode.Context);
        Assert.Contains("\"Uri\":null", contextJson, StringComparison.Ordinal);
        Assert.DoesNotContain("password", contextJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret=value", contextJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("System.Threading.Tasks.TaskCanceledException", ActivityStatusCode.Error, "Canceled")]
    [InlineData("System.Net.Http.HttpRequestException", ActivityStatusCode.Error, "Faulted")]
    [InlineData(null, ActivityStatusCode.Unset, "Completed")]
    public void ActivityErrorStateDeterminesOutcomeWhenThereIsNoResponse(
        string? errorType,
        ActivityStatusCode status,
        string expectedOutcome)
    {
        var systemId = WorkSystemId.New();
        var profile = new WorkProfile("root");
        using var activities = new ActivitySource(ActivitySourceName);
        using var observer = new WorkableHttpClientProfilingObserver(systemId, new WorkProfilingContextAccessor());

        using (WorkProfilerContext.Begin(systemId, profile))
        {
            using var activity = StartRequiredRequestActivity(
                activities,
                "HEAD",
                "/relative/path?secret=value#fragment");
            activity.SetTag("error.type", errorType);
            activity.SetStatus(status);
        }

        var httpNode = Assert.Single(Flatten(profile.ToSnapshot().Root), node => node.Label == "HTTP Request");
        var contextJson = JsonSerializer.Serialize(httpNode.Context);
        Assert.Contains("\"Uri\":\"/relative/path\"", contextJson, StringComparison.Ordinal);
        Assert.Contains($"\"Outcome\":\"{expectedOutcome}\"", contextJson, StringComparison.Ordinal);
        Assert.Contains("\"StatusCode\":null", contextJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ObserverTracksOnlyItsOwningProfileContext()
    {
        var ownerSystemId = WorkSystemId.New();
        var otherSystemId = WorkSystemId.New();
        var ownerProfile = new WorkProfile("owner");
        var otherProfile = new WorkProfile("other");
        using var activities = new ActivitySource(ActivitySourceName);
        using var observer = new WorkableHttpClientProfilingObserver(ownerSystemId, new WorkProfilingContextAccessor());

        Assert.Null(StartRequestActivity(activities, "https://example.test/no-profile"));
        using (WorkProfilerContext.Begin(otherSystemId, otherProfile))
        {
            Assert.Null(StartRequestActivity(activities, "https://example.test/other-profile"));
        }

        using (WorkProfilerContext.Begin(ownerSystemId, ownerProfile))
        {
            using var activity = StartRequestActivity(activities, "https://example.test/owner-profile");
            Assert.NotNull(activity);
        }

        Assert.DoesNotContain(Flatten(otherProfile.ToSnapshot().Root), node => node.Label == "HTTP Request");
        Assert.Single(Flatten(ownerProfile.ToSnapshot().Root), node => node.Label == "HTTP Request");
    }

    [Fact]
    public void UnregisteringASystemIsIdempotentAndStopsFutureSampling()
    {
        var systemId = WorkSystemId.New();
        var profile = new WorkProfile("root");
        using var activities = new ActivitySource(ActivitySourceName);
        using var observer = new WorkableHttpClientProfilingObserver(new WorkProfilingContextAccessor());
        observer.RegisterSystem(systemId);

        observer.UnregisterSystem(systemId);
        observer.UnregisterSystem(systemId);

        using var context = WorkProfilerContext.Begin(systemId, profile);
        Assert.Null(StartRequestActivity(activities, "https://example.test/not-captured"));
        Assert.DoesNotContain(Flatten(profile.ToSnapshot().Root), node => node.Label == "HTTP Request");
    }

    [Fact]
    public void DisposingObserverCompletesActiveRequestAndStopsFutureCapture()
    {
        var systemId = WorkSystemId.New();
        var profile = new WorkProfile("root");
        using var activities = new ActivitySource(ActivitySourceName);
        var observer = new WorkableHttpClientProfilingObserver(systemId, new WorkProfilingContextAccessor());

        using (WorkProfilerContext.Begin(systemId, profile))
        {
            using var activity = StartRequiredRequestActivity(activities, "GET", "https://example.test/active");
            observer.Dispose();
            observer.Dispose();
            WriteCompletedActivity(activities, "https://example.test/after-dispose");
        }

        Assert.Single(Flatten(profile.ToSnapshot().Root), node => node.Label == "HTTP Request");
    }

    [Fact]
    public void SnapshotFinalizesOutstandingRequestAndRejectsLateCaptureFromStaleContext()
    {
        var systemId = WorkSystemId.New();
        var profile = new WorkProfile("root");
        using var activities = new ActivitySource(ActivitySourceName);
        using var observer = new WorkableHttpClientProfilingObserver(systemId, new WorkProfilingContextAccessor());

        using (WorkProfilerContext.Begin(systemId, profile))
        {
            using var activity = StartRequiredRequestActivity(
                activities,
                "GET",
                "https://example.test/outstanding?token=secret");
            var snapshot = profile.ToSnapshot();
            var httpNode = Assert.Single(Flatten(snapshot.Root), node => node.Label == "HTTP Request");
            var beforeStop = JsonSerializer.Serialize(httpNode.Context);

            activity.SetTag("http.response.status_code", 200);
            activity.Dispose();
            var afterStop = JsonSerializer.Serialize(httpNode.Context);

            Assert.Contains("\"Outcome\":\"Incomplete\"", beforeStop, StringComparison.Ordinal);
            Assert.Equal(beforeStop, afterStop);
            Assert.Null(StartRequestActivity(activities, "https://example.test/late"));
        }
    }

    [Fact]
    public async Task SnapshotFinalizesConcurrentOutstandingRequestsWithoutSerializingTheirStarts()
    {
        const int requestCount = 32;
        var systemId = WorkSystemId.New();
        var profile = new WorkProfile("root");
        using var activities = new ActivitySource(ActivitySourceName);
        using var observer = new WorkableHttpClientProfilingObserver(systemId, new WorkProfilingContextAccessor());
        using var ready = new CountdownEvent(requestCount);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Enumerable.Range(0, requestCount)
            .Select(index => Task.Run(async () =>
            {
                using var profilingContext = WorkProfilerContext.Begin(systemId, profile);
                using var activity = StartRequiredRequestActivity(
                    activities,
                    "GET",
                    $"https://example.test/concurrent/{index}");
                ready.Signal();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
                activity.SetTag("http.response.status_code", 200);
            }))
            .ToArray();

        Assert.True(ready.Wait(TimeSpan.FromSeconds(10)));
        WorkProfileSnapshot snapshot;
        try
        {
            snapshot = profile.ToSnapshot();
        }
        finally
        {
            release.TrySetResult();
        }

        await Task.WhenAll(requests);
        var nodes = Flatten(snapshot.Root)
            .Where(node => node.Label == "HTTP Request")
            .ToList();
        Assert.Equal(requestCount, nodes.Count);
        Assert.All(nodes, node => Assert.Contains(
            "\"Outcome\":\"Incomplete\"",
            JsonSerializer.Serialize(node.Context),
            StringComparison.Ordinal));
    }

    [Fact]
    public void ExistingActivitySourceIsCapturedWhenObserverStartsLater()
    {
        var systemId = WorkSystemId.New();
        var profile = new WorkProfile("root");
        using var activities = new ActivitySource(ActivitySourceName);
        using var observer = new WorkableHttpClientProfilingObserver(systemId, new WorkProfilingContextAccessor());

        using (WorkProfilerContext.Begin(systemId, profile))
        {
            WriteCompletedActivity(activities, "https://example.test/existing-source");
        }

        Assert.Single(Flatten(profile.ToSnapshot().Root), node => node.Label == "HTTP Request");
    }

    [Fact]
    public void UnrelatedActivitiesAndMalformedTagsAreHandledConservatively()
    {
        var systemId = WorkSystemId.New();
        var profile = new WorkProfile("root");
        using var unrelated = new ActivitySource("Unrelated.Source");
        using var activities = new ActivitySource(ActivitySourceName);
        using var observer = new WorkableHttpClientProfilingObserver(systemId, new WorkProfilingContextAccessor());

        using (WorkProfilerContext.Begin(systemId, profile))
        {
            Assert.Null(unrelated.StartActivity(RequestActivityName));
            Assert.Null(activities.StartActivity("System.Net.Http.ConnectionSetup"));
            using var activity = StartRequiredRequestActivity(activities, "GET", "not a uri?secret=value");
            activity.SetTag("http.response.status_code", "not-a-number");
        }

        var node = Assert.Single(Flatten(profile.ToSnapshot().Root), candidate => candidate.Label == "HTTP Request");
        var contextJson = JsonSerializer.Serialize(node.Context);
        Assert.Contains("\"Uri\":\"not a uri\"", contextJson, StringComparison.Ordinal);
        Assert.Contains("\"StatusCode\":null", contextJson, StringComparison.Ordinal);
        Assert.DoesNotContain("secret=value", contextJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowingStringTagValuesCannotEscapeTheHttpDiagnosticsCallbacks()
    {
        var systemId = WorkSystemId.New();
        var profile = new WorkProfile("root");
        using var activities = new ActivitySource(ActivitySourceName);
        using var observer = new WorkableHttpClientProfilingObserver(systemId, new WorkProfilingContextAccessor());

        using (WorkProfilerContext.Begin(systemId, profile))
        {
            var activity = activities.StartActivity(
                RequestActivityName,
                ActivityKind.Client,
                default(ActivityContext),
                [
                    new KeyValuePair<string, object?>("http.request.method", new ThrowingStringTag()),
                    new KeyValuePair<string, object?>("url.full", "https://example.test/safe"),
                ]);
            Assert.NotNull(activity);
            activity.SetTag("error.type", new ThrowingStringTag());
            activity.Dispose();
        }

        var node = Assert.Single(Flatten(profile.ToSnapshot().Root), candidate => candidate.Label == "HTTP Request");
        var contextJson = JsonSerializer.Serialize(node.Context);
        Assert.Contains("\"Method\":null", contextJson, StringComparison.Ordinal);
        Assert.Contains("\"ExceptionType\":null", contextJson, StringComparison.Ordinal);
        Assert.Contains("\"Outcome\":\"Completed\"", contextJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowingConvertibleStatusTagCannotEscapeTheHttpDiagnosticsCallbacks()
    {
        var systemId = WorkSystemId.New();
        var profile = new WorkProfile("root");
        using var activities = new ActivitySource(ActivitySourceName);
        using var observer = new WorkableHttpClientProfilingObserver(systemId, new WorkProfilingContextAccessor());

        using (WorkProfilerContext.Begin(systemId, profile))
        {
            using var activity = StartRequiredRequestActivity(
                activities,
                "GET",
                "https://example.test/safe");
            activity.SetTag("http.response.status_code", new ThrowingConvertibleTag());
        }

        var node = Assert.Single(Flatten(profile.ToSnapshot().Root), candidate => candidate.Label == "HTTP Request");
        var contextJson = JsonSerializer.Serialize(node.Context);
        Assert.Contains("\"StatusCode\":null", contextJson, StringComparison.Ordinal);
        Assert.Contains("\"Outcome\":\"Completed\"", contextJson, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyParentIdsAndSparseTelemetryAreCapturedSafely()
    {
        var systemId = WorkSystemId.New();
        var profile = new WorkProfile("root");
        using var activities = new ActivitySource(ActivitySourceName);
        using var observer = new WorkableHttpClientProfilingObserver(systemId, new WorkProfilingContextAccessor());

        using (WorkProfilerContext.Begin(systemId, profile))
        {
            using (var legacyParentActivity = activities.StartActivity(
                RequestActivityName,
                ActivityKind.Client,
                "|legacy-parent.",
                [
                    new KeyValuePair<string, object?>("http.method", "PUT"),
                    new KeyValuePair<string, object?>("http.url", "https://example.test/legacy"),
                    new KeyValuePair<string, object?>("http.status_code", HttpStatusCode.Accepted),
                ]))
            {
                Assert.NotNull(legacyParentActivity);
            }

            using (var sparseActivity = activities.StartActivity(
                RequestActivityName,
                ActivityKind.Client,
                default(ActivityContext),
                [
                    new KeyValuePair<string, object?>("http.request.method", "OPTIONS"),
                    new KeyValuePair<string, object?>("url.full", string.Empty),
                ]))
            {
                Assert.NotNull(sparseActivity);
            }

            using (var invalidUriActivity = StartRequiredRequestActivity(
                activities,
                "PATCH",
                "http://[invalid?secret=value"))
            {
            }
        }

        var nodes = Flatten(profile.ToSnapshot().Root)
            .Where(node => node.Label == "HTTP Request")
            .ToList();
        Assert.Equal(3, nodes.Count);

        var legacyJson = JsonSerializer.Serialize(nodes[0].Context);
        Assert.Contains("\"Method\":\"PUT\"", legacyJson, StringComparison.Ordinal);
        Assert.Contains("\"StatusCode\":202", legacyJson, StringComparison.Ordinal);

        var sparseJson = JsonSerializer.Serialize(nodes[1].Context);
        Assert.Contains("\"Uri\":null", sparseJson, StringComparison.Ordinal);
        Assert.Contains("\"HasQueryString\":false", sparseJson, StringComparison.Ordinal);

        var invalidUriJson = JsonSerializer.Serialize(nodes[2].Context);
        Assert.Contains("\"Uri\":null", invalidUriJson, StringComparison.Ordinal);
        Assert.DoesNotContain("secret=value", invalidUriJson, StringComparison.Ordinal);
    }

    private static Activity StartRequiredRequestActivity(
        ActivitySource source,
        string method,
        string uri)
    {
        var activity = source.StartActivity(
            RequestActivityName,
            ActivityKind.Client,
            default(ActivityContext),
            [
                new KeyValuePair<string, object?>("http.request.method", method),
                new KeyValuePair<string, object?>("url.full", uri),
            ]);
        return activity ?? throw new InvalidOperationException("Expected HTTP request activity to be sampled.");
    }

    private static void WriteCompletedActivity(ActivitySource source, string uri)
    {
        using var activity = StartRequestActivity(source, uri);
    }

    private static Activity? StartRequestActivity(ActivitySource source, string uri)
        => source.StartActivity(
            RequestActivityName,
            ActivityKind.Client,
            default(ActivityContext),
            [
                new KeyValuePair<string, object?>("http.request.method", "GET"),
                new KeyValuePair<string, object?>("url.full", uri),
                new KeyValuePair<string, object?>("http.response.status_code", 200),
            ]);

    private static ActivityListener CreateForcingListener(Action? sampled = null)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = static source => string.Equals(source.Name, ActivitySourceName, StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => Sample(sampled),
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => Sample(sampled),
        };
        ActivitySource.AddActivityListener(listener);
        return listener;

        static ActivitySamplingResult Sample(Action? callback)
        {
            callback?.Invoke();
            return ActivitySamplingResult.AllData;
        }
    }

    private static IReadOnlyDictionary<string, int> ReadOmissions(WorkProfileSnapshot snapshot)
    {
        var summary = Assert.Single(
            Flatten(snapshot.Root),
            node => node.Label == "Automatic instrumentation truncated");
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(summary.Context));
        return document.RootElement
            .GetProperty("OmittedByInstrumentation")
            .EnumerateObject()
            .ToDictionary(entry => entry.Name, entry => entry.Value.GetInt32(), StringComparer.Ordinal);
    }

    private static Task<WorkExecutionResult> SuccessfulWork(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    private static IPEndPoint ReserveUnusedLoopbackEndpoint()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        listener.Stop();
        return endpoint;
    }

    private static IEnumerable<WorkProfileSnapshotNode> Flatten(WorkProfileSnapshotNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in Flatten(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = nameof(HttpClientProfilingTests);

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class ThrowingStringTag
    {
        public override string ToString()
            => throw new InvalidOperationException("Instrumentation must not invoke arbitrary tag formatting.");
    }

    private sealed class ThrowingConvertibleTag : IConvertible
    {
        public TypeCode GetTypeCode() => TypeCode.Object;

        public bool ToBoolean(IFormatProvider? provider) => throw UnexpectedConversion();

        public byte ToByte(IFormatProvider? provider) => throw UnexpectedConversion();

        public char ToChar(IFormatProvider? provider) => throw UnexpectedConversion();

        public DateTime ToDateTime(IFormatProvider? provider) => throw UnexpectedConversion();

        public decimal ToDecimal(IFormatProvider? provider) => throw UnexpectedConversion();

        public double ToDouble(IFormatProvider? provider) => throw UnexpectedConversion();

        public short ToInt16(IFormatProvider? provider) => throw UnexpectedConversion();

        public int ToInt32(IFormatProvider? provider) => throw UnexpectedConversion();

        public long ToInt64(IFormatProvider? provider) => throw UnexpectedConversion();

        public sbyte ToSByte(IFormatProvider? provider) => throw UnexpectedConversion();

        public float ToSingle(IFormatProvider? provider) => throw UnexpectedConversion();

        public string ToString(IFormatProvider? provider) => throw UnexpectedConversion();

        public object ToType(Type conversionType, IFormatProvider? provider) => throw UnexpectedConversion();

        public ushort ToUInt16(IFormatProvider? provider) => throw UnexpectedConversion();

        public uint ToUInt32(IFormatProvider? provider) => throw UnexpectedConversion();

        public ulong ToUInt64(IFormatProvider? provider) => throw UnexpectedConversion();

        private static InvalidOperationException UnexpectedConversion()
            => new("Instrumentation must not invoke arbitrary tag conversion.");
    }
}
