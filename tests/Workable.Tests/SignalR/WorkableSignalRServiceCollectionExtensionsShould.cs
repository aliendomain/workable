using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using System.Text.Json;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Workable;

namespace Workable.Tests;

[Trait("Category", "SignalR")]
public sealed class WorkableSignalRServiceCollectionExtensionsShould
{
    [Fact]
    public async Task RegisterInfrastructureOnlyOnceWhileComposingOptions()
    {
        var services = new ServiceCollection();

        services.AddWorkableSignalR(options =>
            options.PublishInterval = TimeSpan.FromSeconds(7));
        services.AddWorkableSignalR(options =>
            options.LiveTimeWindow = TimeSpan.FromSeconds(3));

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IWorkSystemLifecycleObserver));
        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<WorkableSignalROptions>>().Value;
        Assert.Equal(TimeSpan.FromSeconds(7), options.PublishInterval);
        Assert.Equal(TimeSpan.FromSeconds(3), options.LiveTimeWindow);
    }

    [Fact]
    public async Task LeaveTheHostsSharedSignalRJsonProtocolOptionsUnchanged()
    {
        var hostConverter = new JsonStringEnumConverter<DayOfWeek>();
        var services = new ServiceCollection();
        services
            .AddSignalR()
            .AddJsonProtocol(options =>
                options.PayloadSerializerOptions.Converters.Add(hostConverter));

        services.AddWorkableSignalR();

        await using var provider = services.BuildServiceProvider();
        var protocol = provider.GetRequiredService<IOptions<JsonHubProtocolOptions>>().Value;
        Assert.Equal([hostConverter], protocol.PayloadSerializerOptions.Converters);
    }

    [Fact]
    public async Task UseHostJsonPolicyForWorkablePayloadsWithoutChangingOtherHubOptions()
    {
        var services = new ServiceCollection();
        services
            .AddSignalR()
            .AddHubOptions<HostOwnedHub>(options =>
                options.SupportedProtocols = ["messagepack"])
            .AddJsonProtocol(options =>
                options.PayloadSerializerOptions.PropertyNamingPolicy = null);
        services.AddWorkableSignalR();

        await using var provider = services.BuildServiceProvider();
        var payload = Assert.IsType<JsonElement>(provider
            .GetRequiredService<IWorkableSignalRPayloadSerializer>()
            .Serialize(new HostPolicyPayload(DayOfWeek.Monday)));
        var hubOptions = provider
            .GetRequiredService<IOptions<HubOptions<WorkableRealtimeHub>>>()
            .Value;
        var hostHubOptions = provider
            .GetRequiredService<IOptions<HubOptions<HostOwnedHub>>>()
            .Value;

        Assert.Equal("Monday", payload.GetProperty("Day").GetString());
        Assert.False(payload.TryGetProperty("day", out _));
        Assert.Equal(["json"], hubOptions.SupportedProtocols);
        Assert.Equal(["messagepack"], hostHubOptions.SupportedProtocols);
    }

    [Fact]
    public async Task LetLateExplicitGlobalProtocolPolicyFlowToTheWorkableHub()
    {
        var services = new ServiceCollection();
        services.AddWorkableSignalR();
        services.AddSingleton<IHubProtocol, HostOwnedHubProtocol>();
        services.Configure<HubOptions>(options =>
            options.SupportedProtocols = [HostOwnedHubProtocol.ProtocolName]);

        await using var provider = services.BuildServiceProvider();
        var globalOptions = provider.GetRequiredService<IOptions<HubOptions>>().Value;
        var hostHubOptions = provider.GetRequiredService<IOptions<HubOptions<HostOwnedHub>>>().Value;
        var workableHubOptions = provider.GetRequiredService<IOptions<HubOptions<WorkableRealtimeHub>>>().Value;

        Assert.Equal([HostOwnedHubProtocol.ProtocolName], globalOptions.SupportedProtocols);
        Assert.Null(hostHubOptions.SupportedProtocols);
        Assert.Equal([HostOwnedHubProtocol.ProtocolName], workableHubOptions.SupportedProtocols);
    }

    [Fact]
    public async Task LeaveHostProtocolPolicyUntouchedWhenSignalRWasAlreadyRegistered()
    {
        var services = new ServiceCollection();
        services.AddSignalR();
        services.AddSingleton<IHubProtocol, HostOwnedHubProtocol>();
        services.Configure<HubOptions>(options =>
            options.SupportedProtocols = [HostOwnedHubProtocol.ProtocolName]);
        services.AddWorkableSignalR();

        await using var provider = services.BuildServiceProvider();

        Assert.Equal(
            [HostOwnedHubProtocol.ProtocolName],
            provider.GetRequiredService<IOptions<HubOptions>>().Value.SupportedProtocols);
        Assert.Equal(
            [HostOwnedHubProtocol.ProtocolName],
            provider.GetRequiredService<IOptions<HubOptions<WorkableRealtimeHub>>>().Value.SupportedProtocols);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeaveHostAuthorizationPoliciesUntouchedRegardlessOfRegistrationOrder(
        bool registerHostAfterWorkable)
    {
        var defaultPolicy = new AuthorizationPolicyBuilder()
            .RequireClaim("host-default")
            .Build();
        var fallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireClaim("host-fallback")
            .Build();
        static void RegisterHostPolicies(
            IServiceCollection services,
            AuthorizationPolicy defaultPolicy,
            AuthorizationPolicy fallbackPolicy)
            => services.AddAuthorization(options =>
            {
                options.DefaultPolicy = defaultPolicy;
                options.FallbackPolicy = fallbackPolicy;
                options.AddPolicy(
                    "HostNamed",
                    policy => policy.RequireClaim("host-named"));
            });
        var services = new ServiceCollection();
        if (!registerHostAfterWorkable)
        {
            RegisterHostPolicies(services, defaultPolicy, fallbackPolicy);
        }

        services.AddWorkableSignalR();
        if (registerHostAfterWorkable)
        {
            RegisterHostPolicies(services, defaultPolicy, fallbackPolicy);
        }

        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;

        Assert.Same(defaultPolicy, options.DefaultPolicy);
        Assert.Same(fallbackPolicy, options.FallbackPolicy);
        Assert.NotNull(options.GetPolicy("HostNamed"));
    }

    [Fact]
    public async Task LeaveSignalRsGlobalJsonDefaultIntactWhenTheHostCallsAddSignalRAfterWorkable()
    {
        var services = new ServiceCollection();
        services.AddWorkableSignalR();
        services.AddSignalR();

        await using var provider = services.BuildServiceProvider();
        var protocols = provider.GetServices<IHubProtocol>();
        var workableHubOptions = provider.GetRequiredService<IOptions<HubOptions<WorkableRealtimeHub>>>().Value;

        Assert.Contains(protocols, protocol => protocol.Name == "json");
        Assert.Equal(
            ["json"],
            provider.GetRequiredService<IOptions<HubOptions>>().Value.SupportedProtocols);
        Assert.Null(provider.GetRequiredService<IOptions<HubOptions<HostOwnedHub>>>().Value.SupportedProtocols);
        Assert.Equal(["json"], workableHubOptions.SupportedProtocols);
    }

    [Fact]
    public async Task PreserveLateGlobalJsonPolicyWithoutInspectingRegistrationShape()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkableSignalR();
        services.AddSingleton<IHubProtocol>(_ => new HostOwnedJsonHubProtocol());
        services.AddSingleton<IWorkableSignalRPayloadSerializer, HostOwnedPayloadSerializer>();
        services.Configure<HubOptions>(options =>
            options.SupportedProtocols = ["json"]);

        await using var provider = services.BuildServiceProvider();
        var resolvedProtocol = provider
            .GetRequiredService<IHubProtocolResolver>()
            .GetProtocol("json", ["json"]);

        Assert.NotNull(resolvedProtocol);
        Assert.IsType<HostOwnedJsonHubProtocol>(resolvedProtocol);
        Assert.IsType<HostOwnedPayloadSerializer>(
            provider.GetRequiredService<IWorkableSignalRPayloadSerializer>());
        Assert.Equal("json", resolvedProtocol.Name);
        Assert.Equal(
            ["json"],
            provider.GetRequiredService<IOptions<HubOptions>>().Value.SupportedProtocols);
        Assert.Equal(
            ["json"],
            provider.GetRequiredService<IOptions<HubOptions<WorkableRealtimeHub>>>().Value.SupportedProtocols);
    }

    [Fact]
    public async Task PreserveAHostJsonProtocolRegisteredAfterSignalRAndBeforeWorkable()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        services.AddSingleton<IHubProtocol>(_ => new HostOwnedJsonHubProtocol());
        services.AddSingleton<IWorkableSignalRPayloadSerializer, HostOwnedPayloadSerializer>();
        services.Configure<HubOptions>(options =>
            options.SupportedProtocols = ["json"]);
        services.AddWorkableSignalR();

        await using var provider = services.BuildServiceProvider();
        var resolvedProtocol = provider
            .GetRequiredService<IHubProtocolResolver>()
            .GetProtocol("json", ["json"]);

        Assert.NotNull(resolvedProtocol);
        Assert.IsType<HostOwnedJsonHubProtocol>(resolvedProtocol);
        Assert.IsType<HostOwnedPayloadSerializer>(
            provider.GetRequiredService<IWorkableSignalRPayloadSerializer>());
        Assert.Equal(["json"], provider.GetRequiredService<IOptions<HubOptions>>().Value.SupportedProtocols);
    }

    [Fact]
    public void AppendSignalRDefaultsWithoutReorderingExistingGlobalRegistrations()
    {
        var services = new ServiceCollection();
        var hostOptions = ServiceDescriptor.Singleton<IConfigureOptions<HubOptions>, HostHubOptions>();
        var hostProtocol = ServiceDescriptor.Singleton<IHubProtocol, HostOwnedHubProtocol>();
        ((IServiceCollection)services).Add(hostOptions);
        ((IServiceCollection)services).Add(hostProtocol);

        services.AddWorkableSignalR();

        var globalOptions = services
            .Where(descriptor => descriptor.ServiceType == typeof(IConfigureOptions<HubOptions>))
            .ToArray();
        var protocols = services
            .Where(descriptor => descriptor.ServiceType == typeof(IHubProtocol))
            .ToArray();
        Assert.Same(hostOptions, globalOptions[0]);
        Assert.Same(hostProtocol, protocols[0]);
    }

    [Theory]
    [InlineData(nameof(WorkableSignalROptions.PublishInterval))]
    [InlineData(nameof(WorkableSignalROptions.DiagnosticsPublishInterval))]
    [InlineData(nameof(WorkableSignalROptions.BatchTimeWindow))]
    [InlineData(nameof(WorkableSignalROptions.LiveTimeWindow))]
    [InlineData(nameof(WorkableSignalROptions.MinimumTimeWindow))]
    [InlineData(nameof(WorkableSignalROptions.EventSubscriptionCapacity))]
    [InlineData(nameof(WorkableSignalROptions.EventOverflowBehavior))]
    [InlineData(nameof(WorkableSignalROptions.EventMaxBatchSize))]
    [InlineData(nameof(WorkableSignalROptions.MaximumSubscriptionsPerConnectionPerKind))]
    [InlineData(nameof(WorkableSignalROptions.MaximumSubscriptionsPerKind))]
    [InlineData(nameof(WorkableSignalROptions.MaximumEventFilterValuesPerField))]
    [InlineData(nameof(WorkableSignalROptions.MaximumEventFilterValueLength))]
    public void RejectMalformedRealtimeOptions(string propertyName)
    {
        var options = new WorkableSignalROptions();
        switch (propertyName)
        {
            case nameof(WorkableSignalROptions.PublishInterval):
                options.PublishInterval = TimeSpan.Zero;
                break;
            case nameof(WorkableSignalROptions.DiagnosticsPublishInterval):
                options.DiagnosticsPublishInterval = TimeSpan.Zero;
                break;
            case nameof(WorkableSignalROptions.BatchTimeWindow):
                options.BatchTimeWindow = TimeSpan.Zero;
                break;
            case nameof(WorkableSignalROptions.LiveTimeWindow):
                options.LiveTimeWindow = TimeSpan.Zero;
                break;
            case nameof(WorkableSignalROptions.MinimumTimeWindow):
                options.MinimumTimeWindow = TimeSpan.Zero;
                break;
            case nameof(WorkableSignalROptions.EventSubscriptionCapacity):
                options.EventSubscriptionCapacity = 0;
                break;
            case nameof(WorkableSignalROptions.EventOverflowBehavior):
                options.EventOverflowBehavior = (WorkEventOverflowBehavior)int.MaxValue;
                break;
            case nameof(WorkableSignalROptions.EventMaxBatchSize):
                options.EventMaxBatchSize = 0;
                break;
            case nameof(WorkableSignalROptions.MaximumSubscriptionsPerConnectionPerKind):
                options.MaximumSubscriptionsPerConnectionPerKind = 0;
                break;
            case nameof(WorkableSignalROptions.MaximumSubscriptionsPerKind):
                options.MaximumSubscriptionsPerKind = 0;
                break;
            case nameof(WorkableSignalROptions.MaximumEventFilterValuesPerField):
                options.MaximumEventFilterValuesPerField = 0;
                break;
            case nameof(WorkableSignalROptions.MaximumEventFilterValueLength):
                options.MaximumEventFilterValueLength = 0;
                break;
        }

        var exception = Assert.Throws<InvalidOperationException>(() =>
            WorkableSignalROptionsValidation.ThrowIfInvalidRealtime(options));

        Assert.Contains(propertyName, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectPerConnectionSubscriptionLimitAboveTheGlobalPerKindLimit()
    {
        var options = new WorkableSignalROptions
        {
            MaximumSubscriptionsPerConnectionPerKind = 2,
            MaximumSubscriptionsPerKind = 1,
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            WorkableSignalROptionsValidation.ThrowIfInvalidRealtime(options));

        Assert.Contains(nameof(WorkableSignalROptions.MaximumSubscriptionsPerKind), exception.Message);
    }

    [Fact]
    public void RejectTimerIntervalsThatExceedTheRuntimeLimit()
    {
        var options = new WorkableSignalROptions
        {
            PublishInterval = TimeSpan.FromMilliseconds(uint.MaxValue),
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            WorkableSignalROptionsValidation.ThrowIfInvalidRealtime(options));

        Assert.Contains(nameof(options.PublishInterval), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptDefaultRealtimeOptionsAndGuardValidationInput()
    {
        WorkableSignalROptionsValidation.ThrowIfInvalidRealtime(new WorkableSignalROptions());

        Assert.Throws<ArgumentNullException>(() =>
            WorkableSignalROptionsValidation.ThrowIfInvalidRealtime(null!));
    }

    [Fact]
    public void KeepWorkableStreamingEnumContractsAsStringsWithoutSharedProtocolOptions()
    {
        var now = DateTimeOffset.UtcNow;
        var completed = new WorkableRealtimeIterationCompleted(
            WorkerId.New(),
            4,
            WorkerState.Completed,
            2,
            now,
            now,
            TimeSpan.Zero,
            WorkCompletionStatus.Completed,
            1,
            null,
            [WorkMessage.Warning("warning", "Warning")],
            WorkOrigin.Create(WorkInvocationChannel.SignalR, surface: WorkOriginSurface.WorkableAdapter));

        var json = JsonSerializer.SerializeToElement(completed, JsonSerializerOptions.Default);

        Assert.Equal(nameof(WorkerState.Completed), json.GetProperty("WorkerState").GetString());
        Assert.Equal(nameof(WorkCompletionStatus.Completed), json.GetProperty("Status").GetString());
        Assert.Equal(nameof(WorkMessageSeverity.Warning), json.GetProperty("Messages")[0].GetProperty("Severity").GetString());
        Assert.Equal(nameof(WorkInvocationChannel.SignalR), json.GetProperty("CancellationOrigin").GetProperty("Channel").GetString());
    }

    [Fact]
    public void ReadAndWriteStringEnumListsOnWorkableCriteriaLocally()
    {
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var criteria = new WorkWorkerOverviewRealtimeCriteria(
            LogSortDirection: WorkWorkerOverviewSortDirection.Asc,
            LogLevels: [Microsoft.Extensions.Logging.LogLevel.Warning],
            TimelineSortDirection: WorkWorkerOverviewSortDirection.Asc,
            TimelineCategories: [WorkWorkerOverviewTimelineCategory.Failure]);

        var json = JsonSerializer.SerializeToElement(criteria, jsonOptions);
        var roundTripped = JsonSerializer.Deserialize<WorkWorkerOverviewRealtimeCriteria>(
            """
            {
              "logSortDirection": "Asc",
              "logLevels": ["Warning", 4],
              "timelineSortDirection": "Desc",
              "timelineCategories": ["Failure"]
            }
            """,
            jsonOptions);
        var nullLists = JsonSerializer.Deserialize<WorkWorkerOverviewRealtimeCriteria>(
            "{\"logLevels\":null,\"timelineCategories\":null}",
            jsonOptions);
        var serializedNullLists = JsonSerializer.SerializeToElement(nullLists, jsonOptions);

        Assert.Equal("Warning", json.GetProperty("logLevels")[0].GetString());
        Assert.Equal("Failure", json.GetProperty("timelineCategories")[0].GetString());
        Assert.NotNull(roundTripped);
        Assert.Equal(
            [Microsoft.Extensions.Logging.LogLevel.Warning, Microsoft.Extensions.Logging.LogLevel.Error],
            roundTripped.LogLevels);
        Assert.Equal([WorkWorkerOverviewTimelineCategory.Failure], roundTripped.TimelineCategories);
        Assert.NotNull(nullLists);
        Assert.Null(nullLists.LogLevels);
        Assert.Null(nullLists.TimelineCategories);
        Assert.Equal(JsonValueKind.Null, serializedNullLists.GetProperty("logLevels").ValueKind);
        Assert.Equal(JsonValueKind.Null, serializedNullLists.GetProperty("timelineCategories").ValueKind);
    }

    [Theory]
    [InlineData("{\"logLevels\":{}}")]
    [InlineData("{\"logLevels\":[true]}")]
    public void RejectMalformedWorkableStringEnumLists(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<WorkWorkerOverviewRealtimeCriteria>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private sealed record HostPolicyPayload(DayOfWeek Day);

    private sealed class HostOwnedHub : Hub;

    private sealed class HostOwnedHubProtocol : IHubProtocol
    {
        public const string ProtocolName = "host-owned";

        public string Name => ProtocolName;

        public int Version => 1;

        public TransferFormat TransferFormat => TransferFormat.Binary;

        public bool IsVersionSupported(int version) => version == this.Version;

        public bool TryParseMessage(
            ref ReadOnlySequence<byte> input,
            IInvocationBinder binder,
            [NotNullWhen(true)]
            out HubMessage? message)
        {
            message = null;
            return false;
        }

        public void WriteMessage(HubMessage message, IBufferWriter<byte> output)
            => throw new NotSupportedException();

        public ReadOnlyMemory<byte> GetMessageBytes(HubMessage message)
            => throw new NotSupportedException();
    }

    private sealed class HostOwnedJsonHubProtocol : IHubProtocol
    {
        public string Name => "json";

        public int Version => 1;

        public TransferFormat TransferFormat => TransferFormat.Text;

        public bool IsVersionSupported(int version) => version == this.Version;

        public bool TryParseMessage(
            ref ReadOnlySequence<byte> input,
            IInvocationBinder binder,
            [NotNullWhen(true)]
            out HubMessage? message)
        {
            message = null;
            return false;
        }

        public void WriteMessage(HubMessage message, IBufferWriter<byte> output)
            => throw new NotSupportedException();

        public ReadOnlyMemory<byte> GetMessageBytes(HubMessage message)
            => throw new NotSupportedException();
    }

    private sealed class HostOwnedPayloadSerializer : IWorkableSignalRPayloadSerializer
    {
        public object? Serialize<T>(T value) => value;
    }

    private sealed class HostHubOptions : IConfigureOptions<HubOptions>
    {
        public void Configure(HubOptions options)
            => options.MaximumReceiveMessageSize ??= 1024;
    }
}
