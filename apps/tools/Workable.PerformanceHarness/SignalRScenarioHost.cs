using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Workable.PerformanceHarness;

internal sealed class SignalRScenarioHost : IAsyncDisposable
{
    private readonly IHost host;

    private SignalRScenarioHost(IHost host, Uri baseAddress)
    {
        this.host = host;
        this.BaseAddress = baseAddress;
        this.System = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
    }

    public Uri BaseAddress { get; }

    public IWorkSystem System { get; }

    public static async Task<SignalRScenarioHost> Create(
        int eventMaxBatchSize,
        TimeSpan batchTimeWindow,
        CancellationToken cancellationToken = default)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseKestrel();
                web.UseUrls("http://127.0.0.1:0");
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthorization();
                    services.AddSingleton<IWorkAuthorizationGroupProvider>(_ =>
                        new FixedWorkAuthorizationGroupProvider([WorkableBenchmarkSystem.OperatorGroup]));
                    services.AddWorkableSystem(builder =>
                    {
                        builder.StartWithHost();
                        builder.RequireAuthorization();
                        builder.ConfigureAuthorization(authorization => authorization
                            .SystemAdministrators(WorkableBenchmarkSystem.OperatorGroup)
                            .WorkAdministrators(WorkableBenchmarkSystem.OperatorGroup)
                            .AllowDiagnosticsToGroups(WorkableBenchmarkSystem.OperatorGroup)
                            .AllowControlSystemToGroups(WorkableBenchmarkSystem.OperatorGroup)
                            .AllowReadAllWorkToGroups(WorkableBenchmarkSystem.OperatorGroup)
                            .AllowOperateAllWorkToGroups(WorkableBenchmarkSystem.OperatorGroup));
                        builder.AddWork(
                            WorkDefinition.Create(
                                "perf.transport.queued",
                                category: "Perf:Transport",
                                configuration: WorkConfiguration.Default with
                                {
                                    Start = WorkStartConfiguration.DoNotStart,
                                }),
                            SuccessfulWork,
                            configure: null,
                            authorize: AllowOperatorGroups);
                    });
                    services.AddWorkableSignalR(options =>
                    {
                        options.PublishInterval = TimeSpan.FromMilliseconds(25);
                        options.DiagnosticsPublishInterval = TimeSpan.FromMilliseconds(25);
                        options.MinimumTimeWindow = TimeSpan.FromMilliseconds(1);
                        options.LiveTimeWindow = TimeSpan.FromMilliseconds(1);
                        options.BatchTimeWindow = batchTimeWindow;
                        options.EventMaxBatchSize = eventMaxBatchSize;
                    });
                });
                web.Configure(app =>
                {
                    app.Use(async (context, next) =>
                    {
                        context.User = BenchmarkAuthenticationHandler.CreatePrincipal();
                        await next();
                    });
                    app.UseRouting();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapWorkableSignalR("/workable/realtime");
                    });
                });
            })
            .Build();

        await host.StartAsync(cancellationToken);
        var addresses = host.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            ?.Addresses;
        var baseAddress = addresses?.Select(static address => new Uri(address)).SingleOrDefault()
            ?? throw new InvalidOperationException("Expected SignalR scenario host address.");
        return new SignalRScenarioHost(host, baseAddress);
    }

    public WorkRequestContext CreateRequestContext(string description)
        => BenchmarkRequestContexts.CreateOperator(description);

    public HubConnection CreateSignalRConnection()
        => new HubConnectionBuilder()
            .WithUrl(
                new Uri(this.BaseAddress, "/workable/realtime"),
                options =>
                {
                    options.SkipNegotiation = true;
                    options.Transports = HttpTransportType.WebSockets;
                })
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            })
            .Build();

    public async ValueTask DisposeAsync()
    {
        await this.host.StopAsync();
        this.host.Dispose();
    }

    private static Task<WorkExecutionResult> SuccessfulWork(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    private static void AllowOperatorGroups(IWorkAuthorizationBuilder authorization)
        => authorization.RequireGroups(
            [WorkableBenchmarkSystem.OperatorGroup],
            [WorkableBenchmarkSystem.OperatorGroup]);

    private sealed class FixedWorkAuthorizationGroupProvider(IEnumerable<string> groups) : IWorkAuthorizationGroupProvider
    {
        private readonly IReadOnlySet<string> groups = new HashSet<string>(
            groups,
            StringComparer.OrdinalIgnoreCase);

        public IReadOnlySet<string> GetGroups(WorkActor actor, string? systemName)
            => actor == WorkActor.Unknown
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : this.groups;
    }
}
