using System.Text.Json;
using SampleHost.Demo;
using Workable;
using Workable.SampleHost;
using Workable.SampleHost.Demo;
using Workable.SampleHost.Fulfillment;
using Workable.SampleHost.Operations;
using Workable.SqlServer;

var builder = WebApplication.CreateBuilder(args);
const string sampleCorsPolicy = "WorkableSampleUi";
const int sampleHttpPort = 61932;
const string sampleOperatorWorkflowName = "sample.demo.workflow.operator-lab";
const string sampleMultiBranchWorkflowName = "sample.demo.workflow.multi-branch-app";
const string sampleDataflowWorkflowName = "sample.demo.workflow.dataflow-lab";
const string sampleLargeDataflowWorkflowName = "sample.demo.workflow.large-dataflow-lab";

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.FormatterName = WorkableSampleConsoleFormatter.FormatterName);
builder.Logging.AddConsoleFormatter<WorkableSampleConsoleFormatter, Microsoft.Extensions.Logging.Console.ConsoleFormatterOptions>();
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("System", LogLevel.Warning);
builder.Logging.AddFilter("Workable", LogLevel.Information);

await using var samplePersistence = await SampleSqlServerPersistenceTarget.Resolve();

builder.Services.AddCors(options =>
{
    options.AddPolicy(sampleCorsPolicy, policy =>
    {
        policy
            .SetIsOriginAllowed(origin =>
            {
                if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
                {
                    return false;
                }

                return uri.Host is "localhost" or "127.0.0.1";
            })
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

builder.Services.AddWorkableSqlServerDurableQueue(samplePersistence.ConnectionString);
builder.Services.AddWorkableSqlServerProfiling();
builder.Services.AddWorkableHttpClientProfiling();
builder.Services.AddHttpClient<DemoProfilingHttpProbe>(client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["WorkableSample:ProfilingHttpBaseAddress"] ??
        $"http://127.0.0.1:{sampleHttpPort}");
});
builder.Services.AddSingleton<DemoRecurringIterationPlanStore>();
builder.Services.AddSingleton(new DemoProfilingSqlConnection(samplePersistence.ConnectionString));
builder.Services.AddScoped<DemoProfilingActivationMarker>();
builder.Services.AddScoped<DemoProfilingPlanner>();
builder.Services.AddScoped<DemoProfilingPipeline>();
builder.Services.AddScoped<DemoProfilingSqlProbe>();
builder.Services.AddScoped<DemoProfilingSectionWorker>();
builder.Services.AddScoped<DemoProfilingOutputComposer>();

builder.Services.AddWorkableSystem(workable =>
{
    workable.StartWithHost();
    ConfigureSampleSystemAuthorization(workable, isFulfillment: false);
    var sampleQuickDefinition = DemoDefinition("sample.demo.quick", "Samples:Demo", "Short sample work for UI state testing.");
    var sampleLongDefinition = DemoDefinition("sample.demo.long", "Samples:Demo", "Longer sample work for UI state testing.");
    var sampleMessagePanelDefinition = DemoDefinition(
        "sample.demo.message-panel",
        "Samples:Demo",
        "One-shot sample that returns a large retained message set across non-error severities.");
    var sampleProfilingLabDefinition = DemoProfileDefinition(
        "sample.demo.profiling-lab",
        "Samples:Demo",
        "One-shot profiling showcase with nested scopes, timings, injected-service contributions, SQL command capture, and outbound HTTP capture.");
    var sampleWorkflowSeedDefinition = DemoDefinition(
        "sample.demo.workflow.seed",
        "Samples:Workflow",
        "Produces a dynamic list of child worker inputs so the sample workflow can demonstrate DispatchEach fan-out.");
    var sampleThrottledDefinition = DemoDefinition(
        "sample.demo.throttled",
        "Samples:Demo",
        "Longer sample work without an artificial concurrency bottleneck.");
    workable.AddWork<HealthSnapshotWork>();
    workable.AddWork<SampleEchoWork>(
        configure: null,
        authorize: CreateSampleWorkAuthorization(
            SampleFakeAuth.OperationsCustomReadGroup,
            SampleFakeAuth.OperationsCustomOperateGroup));
    workable.AddWork<SampleDelayWork>();
    workable.AddWork<SampleSleepWork>();
    workable.AddWork<WelcomeEmailWork>();
    workable.AddWork<InvoiceGenerateWork>();
    workable.AddWork<InventoryAdjustWork>();
    workable.AddWork<CustomerSegmentWork>();
    workable.AddWork<ReportExportWork>();
    workable.AddWork<DataImportWork>();
    workable.AddWork<FlakyValidationWork>();
    workable.AddWork<DemoTimedWork>(sampleQuickDefinition);
    workable.AddWork<DemoTimedWork>(sampleLongDefinition);
    workable.AddWork<DemoMessagePanelWork>(sampleMessagePanelDefinition);
    workable.AddWork<DemoProfilingLabWork>(sampleProfilingLabDefinition);
    workable.AddWork<DemoForceCancelWork>(DemoDefinition("sample.demo.force-cancel", "Samples:Demo", "Ignores cancellation so shutdown must force-cancel it."));
    workable.AddWork<DemoWorkflowFanOutSeedWork>(sampleWorkflowSeedDefinition);
    workable.AddWork<DemoTimedWork>(sampleThrottledDefinition);
    workable.AddWork<DemoTimedWork>(
        DemoDefinition("sample.demo.durable", "Samples:Demo", "Durable sample work persisted through the sample host SQL Server durability store."),
        configuration => configuration.QueueDurably());
    workable.AddWork<DemoTimedWork>(
        DemoDefinition("sample.demo.idempotent", "Samples:Demo", "Idempotent sample work that rejects duplicate subjects to demonstrate diagnostics."),
        configuration => configuration.RejectDuplicateSubjects());
    workable.AddWork<DemoTimedWork>(
        DemoDefinition("sample.demo.queue-pressure", "Samples:Demo", "Queues faster than concurrency capacity to demonstrate queue pressure."),
        configuration => configuration.LimitConcurrency(
            maximumCapacity: 1,
            scope: WorkConcurrencyScope.PerConcurrencyKey,
            blockingMode: WorkConcurrencyBlockingMode.WhileExecuting,
            limitReachedBehavior: WorkConcurrencyLimitReachedBehavior.DeferStart));
    workable.AddWork<DemoTimedWork>(DemoRecurringDefinition("sample.demo.recurring", "Samples:Demo", "Small recurring pulse for UI waiting/running state testing."));
    workable.AddWork<DemoRecurringIterationWork>(
        DemoRecurringDefinition(
            "sample.demo.iteration-lab",
            "Samples:Demo",
            "Recurring sample that mixes normal success, non-transient failures, and transient recovery for iteration/logging demos."),
        configuration => configuration
            .UseRecurrence(WorkRecurrenceConfiguration.Every(TimeSpan.FromSeconds(2)) with
            {
                ContinueAfterFailure = false,
                CircuitBreakerFailureThreshold = 100,
            })
            .RetryTransientFailures(
                count: 4,
                initialDelay: TimeSpan.FromSeconds(5),
                jitter: TimeSpan.Zero,
                maximumDelay: TimeSpan.FromSeconds(5),
                backoff: WorkRetryBackoff.None)
            .ClassifyExceptions(exception => exception switch
            {
                DemoRecurringTransientException => WorkExceptionClassification.Transient,
                DemoRecurringNonTransientException => WorkExceptionClassification.NonTransient,
                _ => WorkExceptionClassification.Unknown,
            }));
    workable.AddWork<DemoRecurringMessageFloodWork>(
        DemoRecurringDefinition(
            "sample.demo.message-flood",
            "Samples:Demo",
            "Recurring sample that emits a high-volume retained log flood per successful iteration."),
        configuration => configuration.UseRecurrence(
            WorkRecurrenceConfiguration.Every(TimeSpan.FromSeconds(1)) with
            {
                RetainedIterations = 25,
            })
            .ConfigureLogging(
                level: LogLevel.Trace,
                maximumBufferedEntries: 400));
    workable.AddWorkflow(
        DemoWorkflowDefinition(
            sampleOperatorWorkflowName,
            "Samples:Workflow",
            "Long-running sample workflow with sequential dispatch, parallel fan-out, joins, profiling, and retained-message child work for operator UI testing."),
        workflow => workflow
            .DispatchWork(
                "prepare",
                sampleQuickDefinition,
                DemoWorkflowTimedInput(
                    "Prepare operator workflow inputs",
                    4_500,
                    "prepare"))
            .RunParallel("fan-out", parallel => parallel
                .DispatchWork(
                    "intake-audit",
                    sampleLongDefinition,
                    DemoWorkflowTimedInput(
                        "Audit inbound workload",
                        9_000,
                        "intake-audit"))
                .DispatchWork(
                    "fulfillment-sync",
                    sampleThrottledDefinition,
                    DemoWorkflowTimedInput(
                        "Sync fulfillment checkpoints",
                        8_000,
                        "fulfillment-sync"))
                .DispatchWork(
                    "operator-messages",
                    sampleMessagePanelDefinition,
                    DemoWorkflowMessagePanelInput("operator-messages")))
            .Join("fan-out-complete")
            .DispatchWork(
                "profile-summary",
                sampleProfilingLabDefinition,
                DemoWorkflowProfilingInput())
            .RunParallel("finalize", parallel => parallel
                .DispatchWork(
                    "publish-summary",
                    sampleQuickDefinition,
                    DemoWorkflowTimedInput(
                        "Publish operator summary",
                        3_500,
                        "publish-summary"))
                .DispatchWork(
                    "archive-results",
                    sampleLongDefinition,
                    DemoWorkflowTimedInput(
                        "Archive workflow artifacts",
                        6_000,
                        "archive-results")))
            .Join("workflow-complete")
            .DispatchWork(
                "closeout",
                sampleQuickDefinition,
                DemoWorkflowTimedInput(
                    "Close operator workflow",
                    2_000,
                    "closeout")),
        authorize: CreateSampleWorkAuthorization(
            SampleFakeAuth.OperationsCustomReadGroup,
            SampleFakeAuth.OperationsCustomOperateGroup));
    workable.AddWorkflow(
        DemoWorkflowDefinition(
            sampleMultiBranchWorkflowName,
            "Samples:Workflow",
            "Multi-branch app release workflow that combines worker steps and nested structure nodes for branch viewer testing."),
        workflow => workflow
            .DispatchWork(
                "prepare-release",
                sampleQuickDefinition,
                DemoWorkflowTimedInput(
                    "Prepare app release inputs",
                    3_500,
                    "prepare-release",
                    "multi-branch-app"))
            .RunParallel("release-streams", parallel => parallel
                .Branch("mobile-app", branch => branch
                    .DispatchWork(
                        "mobile-api-contract",
                        sampleQuickDefinition,
                        DemoWorkflowTimedInput(
                            "Validate mobile API contract",
                            4_500,
                            "mobile-api-contract",
                            "multi-branch-app"))
                    .RunParallel("mobile-validation", validation => validation
                        .DispatchWork(
                            "ios-smoke",
                            sampleLongDefinition,
                            DemoWorkflowTimedInput(
                                "Run iOS release smoke tests",
                                12_000,
                                "ios-smoke",
                                "multi-branch-app"))
                        .DispatchWork(
                            "android-smoke",
                            sampleLongDefinition,
                            DemoWorkflowTimedInput(
                                "Run Android release smoke tests",
                                13_500,
                                "android-smoke",
                                "multi-branch-app")))
                    .Join("mobile-validation-complete")
                    .DispatchWork(
                        "mobile-signoff",
                        sampleQuickDefinition,
                        DemoWorkflowTimedInput(
                            "Sign off mobile release",
                            3_000,
                            "mobile-signoff",
                            "multi-branch-app")))
                .Branch("web-portal", branch => branch
                    .DispatchWork(
                        "web-build",
                        sampleThrottledDefinition,
                        DemoWorkflowTimedInput(
                            "Build web portal release",
                            7_000,
                            "web-build",
                            "multi-branch-app"))
                    .RunParallel("web-verification", verification => verification
                        .DispatchWork(
                            "accessibility-audit",
                            sampleLongDefinition,
                            DemoWorkflowTimedInput(
                                "Run accessibility audit",
                                10_500,
                                "accessibility-audit",
                                "multi-branch-app"))
                        .DispatchWork(
                            "visual-regression",
                            sampleLongDefinition,
                            DemoWorkflowTimedInput(
                                "Run visual regression pack",
                                14_000,
                                "visual-regression",
                                "multi-branch-app")))
                    .Join("web-verification-complete")
                    .DispatchWork(
                        "web-signoff",
                        sampleQuickDefinition,
                        DemoWorkflowTimedInput(
                            "Sign off web release",
                            3_000,
                            "web-signoff",
                            "multi-branch-app")))
                .Branch("operations-readiness", branch => branch
                    .DispatchWork(
                        "support-briefing",
                        sampleMessagePanelDefinition,
                        DemoWorkflowMessagePanelInput(
                            "support-briefing",
                            "multi-branch-app"))
                    .DispatchWork(
                        "runbook-check",
                        sampleQuickDefinition,
                        DemoWorkflowTimedInput(
                            "Verify release runbook",
                            4_000,
                            "runbook-check",
                            "multi-branch-app"))
                    .RunParallel("readiness-checks", readiness => readiness
                        .DispatchWork(
                            "incident-channel",
                            sampleQuickDefinition,
                            DemoWorkflowTimedInput(
                                "Prepare incident response channel",
                                3_500,
                                "incident-channel",
                                "multi-branch-app"))
                        .DispatchWork(
                            "rollout-window",
                            sampleLongDefinition,
                            DemoWorkflowTimedInput(
                                "Confirm rollout window",
                                8_500,
                                "rollout-window",
                                "multi-branch-app")))))
            .Join("release-streams-complete")
            .DispatchWork(
                "combine-release-plan",
                sampleProfilingLabDefinition,
                DemoWorkflowProfilingInput(
                    "multi-branch-app",
                    "combine-release-plan"))
            .DispatchWork(
                "publish-release",
                sampleQuickDefinition,
                DemoWorkflowTimedInput(
                    "Publish combined app release",
                    3_500,
                    "publish-release",
                    "multi-branch-app")),
        authorize: CreateSampleWorkAuthorization(
            SampleFakeAuth.OperationsCustomReadGroup,
            SampleFakeAuth.OperationsCustomOperateGroup));
    workable.AddWorkflow(
        DemoWorkflowDefinition(
            sampleDataflowWorkflowName,
            "Samples:Workflow",
            "Dataflow sample workflow that builds a dynamic batch, fans it out with DispatchEach, waits at a join, and then finishes with normal workflow steps."),
        workflow =>
        {
            workflow.DispatchWork(
                "prepare-batch",
                sampleQuickDefinition,
                DemoWorkflowTimedInput(
                    "Prepare dataflow workflow inputs",
                    3_500,
                    "prepare-batch",
                    "dataflow-lab"));
            var buildBatch = workflow.DispatchWork<DemoWorkflowFanOutSeedOutput>(
                "build-batch",
                sampleWorkflowSeedDefinition,
                WorkInput.FromValue(
                    new DemoWorkflowFanOutSeedInput(
                        "dynamic-import-batch",
                        2_500,
                        [
                            new DemoWorkflowFanOutSeedItem("Normalize customer profile", 7_500, "normalize-customer-profile"),
                            new DemoWorkflowFanOutSeedItem("Sync entitlement ledger", 9_000, "sync-entitlement-ledger"),
                            new DemoWorkflowFanOutSeedItem("Render audit artifact", 6_000, "render-audit-artifact"),
                            new DemoWorkflowFanOutSeedItem("Publish notification payload", 8_500, "publish-notification-payload"),
                        ]),
                    identifiers: DemoWorkflowIdentifiers("build-batch", "dataflow-lab")));
            workflow
                .DispatchEach("fan-out-batch", buildBatch, sampleLongDefinition, output => output.Items)
                .Join("fan-out-complete")
                .DispatchWork(
                    "profile-results",
                    sampleProfilingLabDefinition,
                    DemoWorkflowProfilingInput("dataflow-lab", "profile-results"))
                .DispatchWork(
                    "closeout",
                    sampleQuickDefinition,
                    DemoWorkflowTimedInput(
                        "Close dataflow workflow",
                        2_500,
                        "closeout",
                        "dataflow-lab"));
        },
        authorize: CreateSampleWorkAuthorization(
            SampleFakeAuth.OperationsCustomReadGroup,
            SampleFakeAuth.OperationsCustomOperateGroup));
    workable.AddWorkflow(
        DemoWorkflowDefinition(
            sampleLargeDataflowWorkflowName,
            "Samples:Workflow",
            "Large dataflow sample workflow that expands a much bigger dynamic batch so the operator UI can exercise workflow child paging."),
        workflow =>
        {
            workflow.DispatchWork(
                "prepare-batch",
                sampleQuickDefinition,
                DemoWorkflowTimedInput(
                    "Prepare large dataflow workflow inputs",
                    3_500,
                    "prepare-batch",
                    "large-dataflow-lab"));
            var buildBatch = workflow.DispatchWork<DemoWorkflowFanOutSeedOutput>(
                "build-batch",
                sampleWorkflowSeedDefinition,
                WorkInput.FromValue(
                    new DemoWorkflowFanOutSeedInput(
                        "large-dynamic-import-batch",
                        2_500,
                        CreateLargeDataflowSeedItems(48)),
                    identifiers: DemoWorkflowIdentifiers("build-batch", "large-dataflow-lab")));
            workflow
                .DispatchEach("fan-out-batch", buildBatch, sampleLongDefinition, output => output.Items)
                .Join("fan-out-complete")
                .DispatchWork(
                    "closeout",
                    sampleQuickDefinition,
                    DemoWorkflowTimedInput(
                        "Close large dataflow workflow",
                        2_500,
                        "closeout",
                        "large-dataflow-lab"));
        },
        authorize: CreateSampleWorkAuthorization(
            SampleFakeAuth.OperationsCustomReadGroup,
            SampleFakeAuth.OperationsCustomOperateGroup));
});

builder.Services.AddWorkableSystem("fulfillment", workable =>
{
    workable.StartWithHost();
    ConfigureSampleSystemAuthorization(workable, isFulfillment: true);
    workable.AddWork<OrderPickListWork>(
        configure: null,
        authorize: CreateSampleWorkAuthorization(
            SampleFakeAuth.FulfillmentCustomReadGroup,
            SampleFakeAuth.FulfillmentCustomOperateGroup));
    workable.AddWork<ShipmentLabelWork>();
    workable.AddWork<CarrierRateShopWork>();
    workable.AddWork<WarehouseSlottingWork>();
    workable.AddWork<ReturnAuthorizationWork>();
    workable.AddWork<VendorReorderWork>();
    workable.AddWork<PackageManifestWork>();
    workable.AddWork<FulfillmentExceptionWork>();
    workable.AddWork<DemoTimedWork>(DemoDefinition("fulfillment.demo.quick", "Fulfillment:Demo", "Short fulfillment sample work for UI state testing."));
    workable.AddWork<DemoTimedWork>(DemoDefinition("fulfillment.demo.long", "Fulfillment:Demo", "Longer fulfillment sample work for UI state testing."));
    workable.AddWork<DemoTimedWork>(DemoDefinition("fulfillment.demo.throttled", "Fulfillment:Demo", "Longer fulfillment sample work without an artificial concurrency bottleneck."));
    workable.AddWork<DemoTimedWork>(DemoRecurringDefinition("fulfillment.demo.recurring", "Fulfillment:Demo", "Small recurring fulfillment pulse for UI waiting/running state testing."));
});

builder.Services.AddSingleton<DemoWorkloadController>();
builder.Services.AddHostedService(static services => services.GetRequiredService<DemoWorkloadController>());
builder.Services.AddSingleton<DemoSampleSystemSelection>();
builder.Services.AddSingleton<DemoQueuePressureController>();
builder.Services.AddSingleton<DemoProfilingPressureController>();
builder.Services.AddSingleton<DemoTightLoopController>();
builder.Services.AddSingleton<DemoDurabilityWarningController>(services => new DemoDurabilityWarningController(
    services.GetRequiredService<IWorkSystemRegistry>(),
    services.GetRequiredService<DemoSampleSystemSelection>(),
    samplePersistence.ConnectionString,
    services.GetRequiredService<ILogger<DemoDurabilityWarningController>>()));
builder.Services.AddWorkableHttpApi();
builder.Services.AddWorkableMcpServer();
builder.Services.AddWorkableSignalR(options =>
{
    options.PublishInterval = TimeSpan.FromMilliseconds(250);
    options.DiagnosticsPublishInterval = TimeSpan.FromMilliseconds(250);
    options.BatchTimeWindow = TimeSpan.FromSeconds(1);
    options.LiveTimeWindow = TimeSpan.FromMilliseconds(100);
    options.MinimumTimeWindow = TimeSpan.FromMilliseconds(100);
});

var app = builder.Build();

app.Logger.LogInformation("Sample durable SQL persistence target: {PersistenceTarget}", samplePersistence.Description);

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
    {
    }
    catch (ObjectDisposedException) when (lifetime.ApplicationStopping.IsCancellationRequested)
    {
    }
});

app.UseCors(sampleCorsPolicy);
app.Use((context, next) =>
{
    if (!SampleFakeAuth.TryApplyPathProfile(context))
    {
        context.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());
    }

    return next();
});

app.UseRouting();

app.MapGet("/", (HttpContext context) =>
{
    var requestHost = string.IsNullOrWhiteSpace(context.Request.Host.Value)
        ? $"localhost:{sampleHttpPort}"
        : context.Request.Host.Value;
    var workableUrlBase = $"{context.Request.Scheme}://{requestHost}";
    var authProfilesJson = JsonSerializer.Serialize(
        SampleFakeAuth.Profiles,
        new JsonSerializerOptions(JsonSerializerDefaults.Web));
    var selectedProfile = SampleFakeAuth.Resolve(context.Request.Query[SampleFakeAuth.QueryParameterName]);
    var selectedWorkableUrl = SampleFakeAuth.BuildWorkableApiUrl(workableUrlBase, selectedProfile.Id);
    return Results.Content(
        $$"""
        <!doctype html>
        <html lang="en">
        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Workable Sample Host</title>
            <style>
                body { font-family: system-ui, sans-serif; margin: 3rem; max-width: 1120px; line-height: 1.5; }
                button { font: inherit; padding: .7rem 1rem; border: 1px solid #222; background: #111; color: white; cursor: pointer; }
                button.running { background: #8b1d1d; }
                input { font: inherit; padding: .6rem .7rem; width: 9rem; }
                table { width: 100%; border-collapse: collapse; margin-top: 1.5rem; border: 1px solid #ddd; table-layout: fixed; }
                th, td { padding: .85rem 1rem; text-align: left; vertical-align: middle; border-bottom: 1px solid #ddd; }
                th { background: #f7f7f7; font-size: .85rem; text-transform: uppercase; letter-spacing: .04em; }
                tr:last-child td { border-bottom: 0; }
                .action-name { font-weight: 700; }
                .action-description { color: #555; font-size: .9rem; margin-top: .15rem; }
                .action-controls { display: flex; flex-wrap: wrap; gap: .75rem; align-items: center; }
                .sample-workload-controls { display: flex; flex-wrap: wrap; }
                .system-selection { display: flex; flex-wrap: wrap; gap: .75rem; align-items: center; margin: 1.25rem 0; padding: .9rem 1rem; border: 1px solid #ddd; background: #fafafa; }
                .system-selection-title { font-weight: 700; margin-right: .25rem; }
                .pressure-controls { display: grid; grid-template-columns: max-content max-content; }
                .burst-controls { display: grid; grid-template-columns: max-content max-content; }
                .durable-burst-controls { display: grid; grid-template-columns: max-content max-content; }
                .tight-loop-controls { display: flex; flex-wrap: wrap; }
                .interval-control { display: grid; grid-template-columns: max-content 8.5rem max-content; gap: .5rem; align-items: center; }
                .number-control { display: grid; grid-template-columns: max-content 8.5rem; gap: .5rem; align-items: center; }
                .percentage-control { display: grid; grid-template-columns: max-content 5rem max-content; gap: .5rem; align-items: center; }
                .system-controls { display: flex; flex-wrap: wrap; gap: .75rem; align-items: center; }
                .system-toggle { display: inline-flex; gap: .35rem; align-items: center; white-space: nowrap; }
                .auth-frame { margin: 1.25rem 0 1.5rem; padding: 1rem 1.1rem; border: 1px solid #ddd; background: #fafafa; border-radius: .75rem; }
                .auth-frame h2 { margin: 0 0 .35rem; font-size: 1rem; }
                .auth-frame p { margin: .2rem 0; }
                .auth-grid { display: grid; grid-template-columns: minmax(14rem, 18rem) 1fr; gap: 1rem; align-items: start; margin-top: .75rem; }
                .auth-meta { display: grid; gap: .65rem; }
                .auth-meta label { display: grid; gap: .35rem; font-weight: 600; }
                .auth-meta select,
                .auth-meta input { font: inherit; padding: .6rem .7rem; width: 100%; box-sizing: border-box; }
                .auth-copy-row { display: flex; gap: .5rem; }
                .auth-copy-row button { white-space: nowrap; }
                .auth-summary { display: grid; gap: .6rem; padding: .1rem 0; }
                .auth-summary strong { display: inline-block; margin-right: .35rem; }
                .auth-groups { word-break: break-word; }
                .system-toggle input { width: auto; padding: 0; }
                .interval-control input { width: 100%; box-sizing: border-box; }
                .number-control input { width: 100%; box-sizing: border-box; }
                .percentage-control input { width: 100%; box-sizing: border-box; }
                code { background: #f3f3f3; padding: .1rem .25rem; }
                .status { color: #333; margin: 0; }
                @media (max-width: 900px) {
                    table, thead, tbody, tr, th, td { display: block; }
                    thead { display: none; }
                    tr { border-bottom: 1px solid #ddd; }
                    tr:last-child { border-bottom: 0; }
                    td { border-bottom: 0; }
                    .auth-grid { grid-template-columns: 1fr; }
                    .auth-copy-row { flex-direction: column; }
                    .sample-workload-controls { grid-template-columns: 1fr; align-items: stretch; }
                    .pressure-controls { grid-template-columns: 1fr; align-items: stretch; }
                    .burst-controls { grid-template-columns: 1fr; align-items: stretch; }
                    .durable-burst-controls { grid-template-columns: 1fr; align-items: stretch; }
                    .tight-loop-controls { grid-template-columns: 1fr; align-items: stretch; }
                    .interval-control { grid-template-columns: max-content 1fr max-content; }
                    .number-control { grid-template-columns: max-content 1fr; }
                    .percentage-control { grid-template-columns: max-content 1fr max-content; }
                }
            </style>
        </head>
        <body>
            <h1>Workable Sample Host</h1>
            <p>Use the fake auth selector below, then add the generated Workable URL in the admin UI.</p>
            <p>Durable SQL persistence target: <code>{{samplePersistence.Description}}</code>.</p>
            <section class="auth-frame" aria-label="Workable authentication">
                <h2>Fake Authentication</h2>
                <p>Switch between sample users to exercise Workable authorization from the admin UI without standing up a real identity provider.</p>
                <div class="auth-grid">
                    <div class="auth-meta">
                        <label for="fake-auth-profile">
                            Sample user
                            <select id="fake-auth-profile"></select>
                        </label>
                        <label for="workable-api-url">
                            Workable API URL
                            <div class="auth-copy-row">
                                <input id="workable-api-url" readonly type="text" value="{{selectedWorkableUrl}}">
                                <button id="copy-workable-api-url" type="button">Copy</button>
                            </div>
                        </label>
                    </div>
                    <div class="auth-summary">
                        <p id="fake-auth-description"></p>
                        <p><strong>Expected result:</strong><span id="fake-auth-expected"></span></p>
                        <p><strong>Groups:</strong><span class="auth-groups" id="fake-auth-groups"></span></p>
                    </div>
                </div>
            </section>
            <div class="system-selection" aria-label="Sample systems">
                <span class="system-selection-title">Enabled systems</span>
                <label class="system-toggle">
                    <input id="system-operations" type="checkbox">
                    Operations
                </label>
                <label class="system-toggle">
                    <input id="system-fulfillment" type="checkbox">
                    Fulfillment
                </label>
            </div>
            <table>
                <colgroup>
                    <col style="width: 24%">
                    <col style="width: 56%">
                    <col style="width: 20%">
                </colgroup>
                <thead>
                    <tr>
                        <th>Action</th>
                        <th>Controls</th>
                        <th>Status</th>
                    </tr>
                </thead>
                <tbody>
                    <tr>
                        <td>
                            <div class="action-name">Sample workload</div>
                            <div class="action-description">Queue recurring demo workers.</div>
                        </td>
                        <td>
                            <div class="action-controls sample-workload-controls">
                                <button id="toggle" type="button">Start sample workers</button>
                                <label class="interval-control">
                                    Interval
                                    <input id="interval" type="number" min="1" max="10000" step="1">
                                    ms
                                </label>
                                <label class="percentage-control">
                                    Failures
                                    <input id="failure-percentage" type="number" min="0" max="100" step="1">
                                    %
                                </label>
                                <button id="update-settings" type="button">Update settings</button>
                            </div>
                        </td>
                        <td><p class="status" id="status">Loading sample workload status...</p></td>
                    </tr>
                    <tr>
                        <td>
                            <div class="action-name">Burst queue</div>
                            <div class="action-description">Submit many demo workers in parallel.</div>
                        </td>
                        <td>
                            <div class="action-controls burst-controls">
                                <label class="number-control">
                                    Workers
                                    <input id="burst-count" type="number" min="1" max="1000000" step="10" value="250">
                                </label>
                                <button id="burst-queue" type="button">Queue burst</button>
                            </div>
                        </td>
                        <td><p class="status" id="burst-status">Ready.</p></td>
                    </tr>
                    <tr>
                        <td>
                            <div class="action-name">Durable burst</div>
                            <div class="action-description">Persist demo workers through the configured SQL Server durability store before they start.</div>
                        </td>
                        <td>
                            <div class="action-controls durable-burst-controls">
                                <label class="number-control">
                                    Workers
                                    <input id="durable-burst-count" type="number" min="1" max="1000000" step="10" value="25">
                                </label>
                                <button id="durable-burst-queue" type="button">Queue durable burst</button>
                            </div>
                        </td>
                        <td><p class="status" id="durable-burst-status">Ready.</p></td>
                    </tr>
                    <tr>
                        <td>
                            <div class="action-name">Workflow operator lab</div>
                            <div class="action-description">Start a longer workflow that uses sequential dispatch, parallel branches, joins, profiling, and retained messages so the workflow graph has something meaningful to watch.</div>
                        </td>
                        <td>
                            <div class="action-controls">
                                <button id="workflow-start" type="button">Start operator workflow</button>
                            </div>
                        </td>
                        <td><p class="status" id="workflow-status">Ready.</p></td>
                    </tr>
                    <tr>
                        <td>
                            <div class="action-name">Workflow dataflow lab</div>
                            <div class="action-description">Start a workflow that builds a dynamic list, expands it with DispatchEach, waits for the generated child workers, and then continues with normal workflow steps.</div>
                        </td>
                        <td>
                            <div class="action-controls">
                                <button id="workflow-dataflow-start" type="button">Start dataflow workflow</button>
                            </div>
                        </td>
                        <td><p class="status" id="workflow-dataflow-status">Ready.</p></td>
                    </tr>
                    <tr>
                        <td>
                            <div class="action-name">Workflow multi-branch app</div>
                            <div class="action-description">Start an app release workflow with named branch structure nodes, nested parallel validation, joins, profiling, and worker rows for branch viewer testing.</div>
                        </td>
                        <td>
                            <div class="action-controls">
                                <button id="workflow-multi-branch-start" type="button">Start multi-branch workflow</button>
                            </div>
                        </td>
                        <td><p class="status" id="workflow-multi-branch-status">Ready.</p></td>
                    </tr>
                    <tr>
                        <td>
                            <div class="action-name">Workflow large dataflow lab</div>
                            <div class="action-description">Start a larger DispatchEach workflow that generates enough child workers to exercise paging in the workflow node inspector.</div>
                        </td>
                        <td>
                            <div class="action-controls">
                                <button id="workflow-large-dataflow-start" type="button">Start large dataflow workflow</button>
                            </div>
                        </td>
                        <td><p class="status" id="workflow-large-dataflow-status">Ready.</p></td>
                    </tr>
                    <tr>
                        <td>
                            <div class="action-name">Durability warning</div>
                            <div class="action-description">Hold durable enqueues inside an uncommitted SQL transaction so accepted waiters age into the durability warning state.</div>
                        </td>
                        <td>
                            <div class="action-controls">
                                <button id="durability-warning-start" type="button">Start durability waiters</button>
                                <button id="durability-warning-stop" class="running" type="button">Stop durability waiters</button>
                            </div>
                        </td>
                        <td><p class="status" id="durability-warning-status">Loading durability warning status...</p></td>
                    </tr>
                    <tr>
                        <td>
                            <div class="action-name">Idempotency duplicates</div>
                            <div class="action-description">Queue the same idempotent subject twice so the idempotency diagnostics panel has duplicate rejections to show.</div>
                        </td>
                        <td>
                            <div class="action-controls">
                                <button id="idempotency-warning" type="button">Trigger duplicate rejection</button>
                            </div>
                        </td>
                        <td><p class="status" id="idempotency-warning-status">Ready.</p></td>
                    </tr>
                    <tr>
                        <td>
                            <div class="action-name">Tight queue loops</div>
                            <div class="action-description">Continuously submit demo workers as fast as the selected systems accept them.</div>
                        </td>
                        <td>
                            <div class="action-controls tight-loop-controls">
                                <button id="tight-loop-start" type="button">Start tight loops</button>
                                <button id="tight-loop-stop" class="running" type="button">Stop tight loops</button>
                                <label class="system-toggle">
                                    <input id="tight-loop-yield" type="checkbox">
                                    Use Task.Yield
                                </label>
                            </div>
                        </td>
                        <td><p class="status" id="tight-loop-status">Loading tight-loop status...</p></td>
                    </tr>
                    <tr>
                        <td>
                            <div class="action-name">Queue pressure</div>
                            <div class="action-description">Queue 1-second concurrency-limited workers at 4 per second.</div>
                        </td>
                        <td>
                            <div class="action-controls pressure-controls">
                                <button id="pressure-start" type="button">Start pressure</button>
                                <button id="pressure-stop" class="running" type="button">Stop pressure</button>
                            </div>
                        </td>
                        <td><p class="status" id="pressure-status">Loading queue pressure status...</p></td>
                    </tr>
                    <tr>
                        <td>
                            <div class="action-name">Profiling pressure</div>
                            <div class="action-description">Continuously queue the profiling lab in bursts so you can watch profiler memory, SQL capture, and UI pressure over time.</div>
                        </td>
                        <td>
                            <div class="action-controls profiling-pressure-controls">
                                <label class="number-control">
                                    Burst
                                    <input id="profiling-pressure-burst" type="number" min="1" max="128" step="1" value="4">
                                </label>
                                <label class="interval-control">
                                    Every
                                    <input id="profiling-pressure-interval" type="number" min="25" max="30000" step="25" value="250">
                                    ms
                                </label>
                                <label class="number-control">
                                    Sections
                                    <input id="profiling-pressure-sections" type="number" min="1" max="6" step="1" value="4">
                                </label>
                                <label class="number-control">
                                    Steps
                                    <input id="profiling-pressure-steps" type="number" min="1" max="5" step="1" value="3">
                                </label>
                                <label class="interval-control">
                                    Delay
                                    <input id="profiling-pressure-delay" type="number" min="5" max="150" step="5" value="35">
                                    ms
                                </label>
                                <button id="profiling-pressure-start" type="button">Start profiling pressure</button>
                                <button id="profiling-pressure-stop" class="running" type="button">Stop profiling pressure</button>
                            </div>
                        </td>
                        <td><p class="status" id="profiling-pressure-status">Loading profiling pressure status...</p></td>
                    </tr>
                    <tr>
                        <td>
                            <div class="action-name">Force-cancel worker</div>
                            <div class="action-description">Queue work that ignores cooperative shutdown.</div>
                        </td>
                        <td>
                            <div class="action-controls">
                                <button id="force-cancel" type="button">Queue force-cancel worker</button>
                            </div>
                        </td>
                        <td><p class="status" id="force-cancel-status">Ready.</p></td>
                    </tr>
                </tbody>
            </table>
            <script>
                const authProfiles = {{authProfilesJson}};
                const fakeAuthQueryParameter = {{JsonSerializer.Serialize(SampleFakeAuth.QueryParameterName)}};
                const workableApiBaseUrl = {{JsonSerializer.Serialize(workableUrlBase)}};
                const defaultAuthProfileId = {{JsonSerializer.Serialize(selectedProfile.Id)}};
                const authProfileSelect = document.getElementById('fake-auth-profile');
                const authDescription = document.getElementById('fake-auth-description');
                const authExpected = document.getElementById('fake-auth-expected');
                const authGroups = document.getElementById('fake-auth-groups');
                const workableApiUrl = document.getElementById('workable-api-url');
                const copyWorkableApiUrl = document.getElementById('copy-workable-api-url');
                const sampleWorkflowName = {{JsonSerializer.Serialize(sampleOperatorWorkflowName)}};
                const sampleMultiBranchWorkflowName = {{JsonSerializer.Serialize(sampleMultiBranchWorkflowName)}};
                const sampleDataflowWorkflowName = {{JsonSerializer.Serialize(sampleDataflowWorkflowName)}};
                const sampleLargeDataflowWorkflowName = {{JsonSerializer.Serialize(sampleLargeDataflowWorkflowName)}};

                for (const profile of authProfiles) {
                    const option = document.createElement('option');
                    option.value = profile.id;
                    option.textContent = profile.label;
                    authProfileSelect.appendChild(option);
                }

                function buildWorkableApiUrl(profileId) {
                    const encodedProfileId = encodeURIComponent(profileId);
                    return `${workableApiBaseUrl}/fake-auth/${encodedProfileId}/workable`;
                }

                function updateAuthProfile(profileId, replaceHistory = true) {
                    const profile = authProfiles.find((candidate) => candidate.id === profileId) ?? authProfiles[0];
                    authProfileSelect.value = profile.id;
                    authDescription.textContent = profile.description;
                    authExpected.textContent = profile.expectedDiscovery;
                    authGroups.textContent = profile.groups.length > 0 ? profile.groups.join(', ') : 'None';
                    workableApiUrl.value = buildWorkableApiUrl(profile.id);

                    if (replaceHistory) {
                        const pageUrl = new URL(window.location.href);
                        pageUrl.searchParams.set(fakeAuthQueryParameter, profile.id);
                        window.history.replaceState({}, '', pageUrl);
                    }
                }

                authProfileSelect.addEventListener('change', () => {
                    updateAuthProfile(authProfileSelect.value);
                    refreshWorkflows();
                });

                copyWorkableApiUrl.addEventListener('click', async () => {
                    workableApiUrl.select();
                    workableApiUrl.setSelectionRange(0, workableApiUrl.value.length);
                    try {
                        await navigator.clipboard.writeText(workableApiUrl.value);
                        copyWorkableApiUrl.textContent = 'Copied';
                        window.setTimeout(() => {
                            copyWorkableApiUrl.textContent = 'Copy';
                        }, 1200);
                    } catch {
                        document.execCommand('copy');
                    }
                });

                updateAuthProfile(defaultAuthProfileId, false);

                const button = document.getElementById('toggle');
                const workflowStart = document.getElementById('workflow-start');
                const workflowMultiBranchStart = document.getElementById('workflow-multi-branch-start');
                const workflowDataflowStart = document.getElementById('workflow-dataflow-start');
                const workflowLargeDataflowStart = document.getElementById('workflow-large-dataflow-start');
                const forceCancel = document.getElementById('force-cancel');
                const burstCount = document.getElementById('burst-count');
                const burstQueue = document.getElementById('burst-queue');
                const durableBurstCount = document.getElementById('durable-burst-count');
                const durableBurstQueue = document.getElementById('durable-burst-queue');
                const durabilityWarningStart = document.getElementById('durability-warning-start');
                const durabilityWarningStop = document.getElementById('durability-warning-stop');
                const idempotencyWarning = document.getElementById('idempotency-warning');
                const pressureStart = document.getElementById('pressure-start');
                const pressureStop = document.getElementById('pressure-stop');
                const profilingPressureBurst = document.getElementById('profiling-pressure-burst');
                const profilingPressureInterval = document.getElementById('profiling-pressure-interval');
                const profilingPressureSections = document.getElementById('profiling-pressure-sections');
                const profilingPressureSteps = document.getElementById('profiling-pressure-steps');
                const profilingPressureDelay = document.getElementById('profiling-pressure-delay');
                const profilingPressureStart = document.getElementById('profiling-pressure-start');
                const profilingPressureStop = document.getElementById('profiling-pressure-stop');
                const tightLoopStart = document.getElementById('tight-loop-start');
                const tightLoopStop = document.getElementById('tight-loop-stop');
                const tightLoopYield = document.getElementById('tight-loop-yield');
                const interval = document.getElementById('interval');
                const failurePercentage = document.getElementById('failure-percentage');
                const updateSettings = document.getElementById('update-settings');
                const systemOperations = document.getElementById('system-operations');
                const systemFulfillment = document.getElementById('system-fulfillment');
                const status = document.getElementById('status');
                const workflowStatus = document.getElementById('workflow-status');
                const workflowMultiBranchStatus = document.getElementById('workflow-multi-branch-status');
                const workflowDataflowStatus = document.getElementById('workflow-dataflow-status');
                const workflowLargeDataflowStatus = document.getElementById('workflow-large-dataflow-status');
                const burstStatus = document.getElementById('burst-status');
                const durableBurstStatus = document.getElementById('durable-burst-status');
                const durabilityWarningStatus = document.getElementById('durability-warning-status');
                const idempotencyWarningStatus = document.getElementById('idempotency-warning-status');
                const pressureStatus = document.getElementById('pressure-status');
                const profilingPressureStatus = document.getElementById('profiling-pressure-status');
                const tightLoopStatus = document.getElementById('tight-loop-status');
                const forceCancelStatus = document.getElementById('force-cancel-status');
                let intervalDirty = false;
                let failurePercentageDirty = false;
                let selectedOperations = true;
                let selectedFulfillment = true;
                let sampleWorkloadRunning = false;
                let pressureRunning = false;
                let profilingPressureRunning = false;
                let durabilityWarningRunning = false;
                let tightLoopRunning = false;

                function workableApiPath(path) {
                    const base = workableApiUrl.value.replace(/\/$/, '');
                    return `${base}${path}`;
                }

                function firstMessageText(payload) {
                    if (!payload || !Array.isArray(payload.messages)) {
                        return null;
                    }

                    const messages = payload.messages
                        .map(message => message?.text)
                        .filter(Boolean);
                    return messages.length > 0 ? messages.join(' ') : null;
                }

                function describeWorkflowRun(run) {
                    const currentStep = run.currentStepName
                        ? ` - step ${run.currentStepName}`
                        : '';
                    const waitingChildren = run.outstandingChildren?.total > 0
                        ? ` - waiting on ${run.outstandingChildren.total} child ${run.outstandingChildren.total === 1 ? 'worker' : 'workers'}`
                        : '';
                    const completed = run.completedAt
                        ? ` - completed ${new Date(run.completedAt).toLocaleTimeString()}`
                        : '';
                    return `Run ${run.runId} - ${run.status}${currentStep}${waitingChildren}${completed}`;
                }

                function selectedSystemLabel() {
                    return [
                        selectedOperations ? 'operations' : null,
                        selectedFulfillment ? 'fulfillment' : null
                    ].filter(Boolean).join(', ') || 'none';
                }

                function updateFeatureAvailability() {
                    const anySelected = selectedOperations || selectedFulfillment;
                    button.disabled = !sampleWorkloadRunning && !anySelected;
                    burstQueue.disabled = !anySelected;
                    durableBurstQueue.disabled = !selectedOperations;
                    durabilityWarningStart.disabled = durabilityWarningRunning || !selectedOperations;
                    durabilityWarningStop.disabled = !durabilityWarningRunning;
                    idempotencyWarning.disabled = !selectedOperations;
                    pressureStart.disabled = pressureRunning || !selectedOperations;
                    profilingPressureStart.disabled = profilingPressureRunning || !selectedOperations;
                    profilingPressureStop.disabled = !profilingPressureRunning;
                    forceCancel.disabled = !selectedOperations;
                    tightLoopStart.disabled = tightLoopRunning || !anySelected;
                }

                async function refreshWorkflow(definitionName, statusElement) {
                    try {
                        const response = await fetch(workableApiPath(`/workflow-runs?definitionName=${encodeURIComponent(definitionName)}&includeFinal=true`));
                        const data = await response.json();
                        if (!response.ok) {
                            statusElement.textContent = firstMessageText(data) ?? `Workflow query failed with ${response.status}.`;
                            return;
                        }

                        const runs = Array.isArray(data.runs) ? data.runs : [];
                        if (runs.length === 0) {
                            statusElement.textContent = 'Ready. No visible sample workflow runs yet for this user.';
                            return;
                        }

                        const activeRun = runs.find(run => run.status === 'Running');
                        statusElement.textContent = describeWorkflowRun(activeRun ?? runs[0]);
                    } catch (error) {
                        statusElement.textContent = 'Unable to query sample workflow runs.';
                    }
                }

                async function refreshWorkflows() {
                    await Promise.all([
                        refreshWorkflow(sampleWorkflowName, workflowStatus),
                        refreshWorkflow(sampleMultiBranchWorkflowName, workflowMultiBranchStatus),
                        refreshWorkflow(sampleDataflowWorkflowName, workflowDataflowStatus),
                        refreshWorkflow(sampleLargeDataflowWorkflowName, workflowLargeDataflowStatus)
                    ]);
                }

                async function refresh() {
                    const response = await fetch('/sample-workload');
                    const data = await response.json();
                    sampleWorkloadRunning = data.isRunning;
                    button.textContent = data.isRunning ? 'Stop sample workers' : 'Start sample workers';
                    button.classList.toggle('running', data.isRunning);
                    if (!intervalDirty && document.activeElement !== interval) {
                        interval.value = data.queueIntervalMilliseconds;
                    }
                    if (!failurePercentageDirty && document.activeElement !== failurePercentage) {
                        failurePercentage.value = data.failurePercentage;
                    }
                    systemOperations.checked = data.operationsEnabled;
                    systemFulfillment.checked = data.fulfillmentEnabled;
                    selectedOperations = data.operationsEnabled;
                    selectedFulfillment = data.fulfillmentEnabled;
                    status.textContent = `${data.isRunning ? 'Running' : 'Stopped'} - queued ${data.queuedCount} - tracking ${data.trackedWorkerCount} - interval ${data.queueIntervalMilliseconds}ms - failures ${data.failurePercentage}% - systems ${selectedSystemLabel()}`;
                    updateFeatureAvailability();
                }

                async function refreshPressure() {
                    const response = await fetch('/sample-workload/queue-pressure');
                    const data = await response.json();
                    pressureRunning = data.isRunning;
                    pressureStart.disabled = pressureRunning || !selectedOperations;
                    pressureStop.disabled = !data.isRunning;
                    pressureStatus.textContent = `${data.isRunning ? 'Running' : 'Stopped'} - queued ${data.queuedCount} - tracking ${data.trackedWorkerCount} - ${data.workerDelayMilliseconds}ms work every ${data.queueIntervalMilliseconds}ms`;
                }

                async function refreshProfilingPressure() {
                    const response = await fetch('/sample-workload/profiling-pressure');
                    const data = await response.json();
                    profilingPressureRunning = data.isRunning;
                    profilingPressureStart.disabled = data.isRunning || !selectedOperations;
                    profilingPressureStop.disabled = !data.isRunning;
                    profilingPressureBurst.disabled = data.isRunning;
                    profilingPressureInterval.disabled = data.isRunning;
                    profilingPressureSections.disabled = data.isRunning;
                    profilingPressureSteps.disabled = data.isRunning;
                    profilingPressureDelay.disabled = data.isRunning;

                    const startedAt = data.startedAt
                        ? ` - started ${new Date(data.startedAt).toLocaleTimeString()}`
                        : '';
                    profilingPressureStatus.textContent =
                        `${data.isRunning ? 'Running' : 'Stopped'} - burst ${data.workersPerBurst} every ${data.queueIntervalMilliseconds}ms - ${data.sectionCount} sections x ${data.stepsPerSection} steps at ${data.delayMilliseconds}ms - submitted ${data.submittedCount}, accepted ${data.acceptedCount}, rejected ${data.rejectedCount}, failed ${data.failedCount}, tracking ${data.trackedWorkerCount}${startedAt}`;
                }

                async function refreshDurabilityWarning() {
                    const response = await fetch('/sample-workload/durability-warning');
                    const data = await response.json();
                    durabilityWarningRunning = data.isRunning;
                    durabilityWarningStart.disabled = data.isRunning || !selectedOperations;
                    durabilityWarningStop.disabled = !data.isRunning;
                    const startedAt = data.startedAt ? ` - started ${new Date(data.startedAt).toLocaleTimeString()}` : '';
                    durabilityWarningStatus.textContent = `${data.isRunning ? 'Running' : 'Stopped'} - waiters ${data.workerCount}${startedAt} - ${data.message}`;
                }

                async function refreshTightLoops() {
                    const response = await fetch('/sample-workload/tight-loops');
                    const data = await response.json();
                    tightLoopRunning = data.isRunning;
                    tightLoopStart.disabled = tightLoopRunning || (!selectedOperations && !selectedFulfillment);
                    tightLoopStop.disabled = !data.isRunning;
                    tightLoopYield.disabled = data.isRunning;
                    if (data.isRunning) {
                        tightLoopYield.checked = data.useTaskYield;
                    }
                    const selectedSystems = [
                        data.operationsRunning ? 'operations' : null,
                        data.fulfillmentRunning ? 'fulfillment' : null
                    ].filter(Boolean).join(', ') || 'none';
                    const mode = data.useTaskYield ? 'Task.Yield' : '500ms delay';
                    tightLoopStatus.textContent = `${data.isRunning ? 'Running' : 'Stopped'} - ${mode} - systems ${selectedSystems} - queued operations ${data.operationsQueued} - fulfillment ${data.fulfillmentQueued} - rejected ${data.rejectedCount} - failed ${data.failedCount}`;
                }

                button.addEventListener('click', async () => {
                    button.disabled = true;
                    try {
                        await fetch('/sample-workload/toggle', { method: 'POST' });
                        await refresh();
                    } finally {
                        button.disabled = false;
                        updateFeatureAvailability();
                    }
                });

                workflowStart.addEventListener('click', async () => {
                    await startWorkflow(
                        workflowStart,
                        workflowStatus,
                        sampleWorkflowName,
                        'Start the sample operator workflow from the sample host.');
                });

                workflowMultiBranchStart.addEventListener('click', async () => {
                    await startWorkflow(
                        workflowMultiBranchStart,
                        workflowMultiBranchStatus,
                        sampleMultiBranchWorkflowName,
                        'Start the sample multi-branch app workflow from the sample host.');
                });

                workflowDataflowStart.addEventListener('click', async () => {
                    await startWorkflow(
                        workflowDataflowStart,
                        workflowDataflowStatus,
                        sampleDataflowWorkflowName,
                        'Start the sample dataflow workflow from the sample host.');
                });

                workflowLargeDataflowStart.addEventListener('click', async () => {
                    await startWorkflow(
                        workflowLargeDataflowStart,
                        workflowLargeDataflowStatus,
                        sampleLargeDataflowWorkflowName,
                        'Start the large sample dataflow workflow from the sample host.');
                });

                forceCancel.addEventListener('click', async () => {
                    forceCancel.disabled = true;
                    try {
                        const response = await fetch('/sample-workload/force-cancel', { method: 'POST' });
                        const data = await response.json();
                        forceCancelStatus.textContent = data.workerId
                            ? `Queued ${data.definitionName} worker ${data.workerId}`
                            : data.message;
                    } catch (error) {
                        forceCancelStatus.textContent = 'Unable to queue force-cancel worker.';
                    } finally {
                        forceCancel.disabled = false;
                        updateFeatureAvailability();
                    }
                });

                burstQueue.addEventListener('click', async () => {
                    const count = Number(burstCount.value);
                    burstQueue.disabled = true;
                    burstStatus.textContent = `Submitting ${count} workers...`;
                    try {
                        const response = await fetch('/sample-workload/burst', {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: JSON.stringify({ count })
                        });
                        const data = await response.json();
                        const requested = data.requestedCount === data.submittedCount
                            ? ''
                            : ` requested ${data.requestedCount},`;
                        burstStatus.textContent = `Burst:${requested} submitted ${data.submittedCount}, accepted ${data.acceptedCount}, rejected ${data.rejectedCount} in ${data.elapsedMilliseconds}ms`;
                        await refresh();
                    } catch (error) {
                        burstStatus.textContent = 'Unable to queue burst workers.';
                    } finally {
                        burstQueue.disabled = false;
                        updateFeatureAvailability();
                    }
                });

                durableBurstQueue.addEventListener('click', async () => {
                    const count = Number(durableBurstCount.value);
                    durableBurstQueue.disabled = true;
                    durableBurstStatus.textContent = `Persisting ${count} durable workers...`;
                    try {
                        const response = await fetch('/sample-workload/durable-burst', {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: JSON.stringify({ count })
                        });
                        const data = await response.json();
                        const requested = data.requestedCount === data.submittedCount
                            ? ''
                            : ` requested ${data.requestedCount},`;
                        durableBurstStatus.textContent = `Durable:${requested} submitted ${data.submittedCount}, accepted ${data.acceptedCount}, rejected ${data.rejectedCount} in ${data.elapsedMilliseconds}ms`;
                        await refresh();
                    } catch (error) {
                        durableBurstStatus.textContent = 'Unable to queue durable burst workers.';
                    } finally {
                        durableBurstQueue.disabled = false;
                        updateFeatureAvailability();
                    }
                });

                durabilityWarningStart.addEventListener('click', async () => {
                    durabilityWarningStart.disabled = true;
                    durabilityWarningStatus.textContent = 'Starting durability waiters...';
                    try {
                        await fetch('/sample-workload/durability-warning/start', { method: 'POST' });
                    } finally {
                        await refreshDurabilityWarning();
                        await refresh();
                    }
                });

                durabilityWarningStop.addEventListener('click', async () => {
                    durabilityWarningStop.disabled = true;
                    durabilityWarningStatus.textContent = 'Stopping durability waiters...';
                    try {
                        await fetch('/sample-workload/durability-warning/stop', { method: 'POST' });
                    } finally {
                        await refreshDurabilityWarning();
                        await refresh();
                    }
                });

                idempotencyWarning.addEventListener('click', async () => {
                    idempotencyWarning.disabled = true;
                    idempotencyWarningStatus.textContent = 'Queueing duplicate idempotent work...';
                    try {
                        const response = await fetch('/sample-workload/idempotency-warning', { method: 'POST' });
                        const data = await response.json();
                        if (data.status === 'Skipped' || data.status === 'Failed') {
                            idempotencyWarningStatus.textContent = data.message;
                        } else {
                            idempotencyWarningStatus.textContent =
                                `Subject ${data.subjectValue}: accepted ${data.acceptedCount}, rejected ${data.rejectedCount}${data.rejectionCode ? ` (${data.rejectionCode})` : ''}. Open the idempotency diagnostics section in the system popover to see it.`;
                        }
                        await refresh();
                    } catch (error) {
                        idempotencyWarningStatus.textContent = 'Unable to trigger idempotency warning.';
                    } finally {
                        idempotencyWarning.disabled = false;
                        updateFeatureAvailability();
                    }
                });

                pressureStart.addEventListener('click', async () => {
                    pressureStart.disabled = true;
                    try {
                        await fetch('/sample-workload/queue-pressure/start', { method: 'POST' });
                    } finally {
                        await refreshPressure();
                        await refresh();
                    }
                });

                pressureStop.addEventListener('click', async () => {
                    pressureStop.disabled = true;
                    pressureStatus.textContent = 'Stopping pressure and canceling tracked workers...';
                    try {
                        await fetch('/sample-workload/queue-pressure/stop', { method: 'POST' });
                    } finally {
                        await refreshPressure();
                        await refresh();
                    }
                });

                profilingPressureStart.addEventListener('click', async () => {
                    profilingPressureStart.disabled = true;
                    profilingPressureStatus.textContent = 'Starting profiling pressure...';
                    try {
                        await fetch('/sample-workload/profiling-pressure/start', {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: JSON.stringify({
                                queueIntervalMilliseconds: Number(profilingPressureInterval.value),
                                workersPerBurst: Number(profilingPressureBurst.value),
                                sectionCount: Number(profilingPressureSections.value),
                                stepsPerSection: Number(profilingPressureSteps.value),
                                delayMilliseconds: Number(profilingPressureDelay.value)
                            })
                        });
                    } finally {
                        await refreshProfilingPressure();
                        await refresh();
                    }
                });

                profilingPressureStop.addEventListener('click', async () => {
                    profilingPressureStop.disabled = true;
                    profilingPressureStatus.textContent = 'Stopping profiling pressure and canceling tracked workers...';
                    try {
                        await fetch('/sample-workload/profiling-pressure/stop', { method: 'POST' });
                    } finally {
                        await refreshProfilingPressure();
                        await refresh();
                    }
                });

                interval.addEventListener('input', () => {
                    intervalDirty = true;
                });

                failurePercentage.addEventListener('input', () => {
                    failurePercentageDirty = true;
                });

                async function updateSystems() {
                    await fetch('/sample-workload/systems', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({
                            operations: systemOperations.checked,
                            fulfillment: systemFulfillment.checked
                        })
                    });
                    await refresh();
                    await refreshPressure();
                    await refreshProfilingPressure();
                    await refreshDurabilityWarning();
                    await refreshTightLoops();
                }

                systemOperations.addEventListener('change', updateSystems);
                systemFulfillment.addEventListener('change', updateSystems);

                interval.addEventListener('blur', async () => {
                    if (!intervalDirty) {
                        await refresh();
                    }
                });

                tightLoopStart.addEventListener('click', async () => {
                    tightLoopStart.disabled = true;
                    try {
                        await fetch('/sample-workload/tight-loops/start', {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: JSON.stringify({
                                useTaskYield: tightLoopYield.checked
                            })
                        });
                    } finally {
                        await refreshTightLoops();
                        await refresh();
                    }
                });

                tightLoopStop.addEventListener('click', async () => {
                    tightLoopStop.disabled = true;
                    tightLoopStatus.textContent = 'Stopping tight queue loops...';
                    try {
                        await fetch('/sample-workload/tight-loops/stop', { method: 'POST' });
                    } finally {
                        await refreshTightLoops();
                        await refresh();
                    }
                });

                failurePercentage.addEventListener('blur', async () => {
                    if (!failurePercentageDirty) {
                        await refresh();
                    }
                });

                updateSettings.addEventListener('click', async () => {
                    updateSettings.disabled = true;
                    try {
                        await fetch('/sample-workload/interval', {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: JSON.stringify({ milliseconds: Number(interval.value) })
                        });
                        await fetch('/sample-workload/failures', {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: JSON.stringify({ percentage: Number(failurePercentage.value) })
                        });
                        intervalDirty = false;
                        failurePercentageDirty = false;
                        await refresh();
                    } finally {
                        updateSettings.disabled = false;
                    }
                });

                async function startWorkflow(buttonElement, statusElement, definitionName, description) {
                    buttonElement.disabled = true;
                    statusElement.textContent = 'Starting sample workflow...';
                    try {
                        const response = await fetch(workableApiPath(`/workflows/${encodeURIComponent(definitionName)}`), {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: JSON.stringify({ description })
                        });
                        const data = await response.json();
                        if (!response.ok) {
                            statusElement.textContent = firstMessageText(data) ?? `Workflow start failed with ${response.status}.`;
                            return;
                        }

                        statusElement.textContent = data.runId
                            ? `Accepted run ${data.runId}.`
                            : 'Accepted sample workflow.';
                        await refreshWorkflows();
                    } catch (error) {
                        statusElement.textContent = 'Unable to start sample workflow.';
                    } finally {
                        buttonElement.disabled = false;
                    }
                }

                refresh();
                refreshWorkflows();
                refreshPressure();
                refreshProfilingPressure();
                refreshDurabilityWarning();
                refreshTightLoops();
                setInterval(() => {
                    refresh();
                    refreshWorkflows();
                    refreshPressure();
                    refreshProfilingPressure();
                    refreshDurabilityWarning();
                    refreshTightLoops();
                }, 2000);
            </script>
        </body>
        </html>
        """,
        "text/html");
});

var sampleWorkload = app.MapGroup("/sample-workload");

sampleWorkload.MapGet("", (DemoWorkloadController controller)
    => Results.Ok(controller.Status()));
sampleWorkload.MapGet("/profiling-http-probe/{sectionOrdinal:int}", async (
    int sectionOrdinal,
    string phase,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    await Task.Delay(15 + Math.Clamp(sectionOrdinal, 1, 6) * 5, cancellationToken);
    context.Response.Headers["X-Workable-Sample-Probe"] = "completed";
    return Results.Ok(new DemoProfilingHttpSnapshot(
        sectionOrdinal,
        phase,
        context.Request.Method,
        DateTimeOffset.UtcNow));
});
sampleWorkload.MapPost("/toggle", async (DemoWorkloadController controller, CancellationToken cancellationToken)
    => Results.Ok(await controller.Toggle(cancellationToken)));
sampleWorkload.MapPost("/interval", (DemoWorkloadController controller, DemoWorkloadIntervalRequest request)
    => Results.Ok(controller.SetQueueInterval(request.Milliseconds)));
sampleWorkload.MapPost("/failures", (DemoWorkloadController controller, DemoWorkloadFailureRequest request)
    => Results.Ok(controller.SetFailurePercentage(request.Percentage)));
sampleWorkload.MapPost("/systems", (DemoWorkloadController controller, DemoWorkloadSystemsRequest request)
    => Results.Ok(controller.SetEnabledSystems(request)));
sampleWorkload.MapPost("/burst", async (DemoWorkloadController controller, DemoBurstRequest request, CancellationToken cancellationToken)
    => Results.Ok(await controller.QueueBurst(request.Count, cancellationToken)));
sampleWorkload.MapPost("/durable-burst", async (DemoWorkloadController controller, DemoBurstRequest request, CancellationToken cancellationToken)
    => Results.Ok(await controller.QueueDurableBurst(request.Count, cancellationToken)));
sampleWorkload.MapGet("/durability-warning", (DemoDurabilityWarningController controller)
    => Results.Ok(controller.Status()));
sampleWorkload.MapPost("/durability-warning/start", async (DemoDurabilityWarningController controller, CancellationToken cancellationToken)
    => Results.Ok(await controller.Start(cancellationToken)));
sampleWorkload.MapPost("/durability-warning/stop", async (DemoDurabilityWarningController controller, CancellationToken cancellationToken)
    => Results.Ok(await controller.Stop(cancellationToken)));
sampleWorkload.MapPost("/idempotency-warning", async (DemoWorkloadController controller, CancellationToken cancellationToken)
    => Results.Ok(await controller.QueueIdempotencyWarning(cancellationToken)));
sampleWorkload.MapGet("/queue-pressure", (DemoQueuePressureController controller)
    => Results.Ok(controller.Status()));
sampleWorkload.MapPost("/queue-pressure/start", (DemoQueuePressureController controller)
    => Results.Ok(controller.Start()));
sampleWorkload.MapPost("/queue-pressure/stop", async (DemoQueuePressureController controller, CancellationToken cancellationToken)
    => Results.Ok(await controller.Stop(cancellationToken)));
sampleWorkload.MapGet("/profiling-pressure", (DemoProfilingPressureController controller)
    => Results.Ok(controller.Status()));
sampleWorkload.MapPost("/profiling-pressure/start", (DemoProfilingPressureController controller, DemoProfilingPressureRequest request)
    => Results.Ok(controller.Start(request)));
sampleWorkload.MapPost("/profiling-pressure/stop", async (DemoProfilingPressureController controller, CancellationToken cancellationToken)
    => Results.Ok(await controller.Stop(cancellationToken)));
sampleWorkload.MapGet("/tight-loops", (DemoTightLoopController controller)
    => Results.Ok(controller.Status()));
sampleWorkload.MapPost("/tight-loops/start", (DemoTightLoopController controller, DemoTightLoopRequest request)
    => Results.Ok(controller.Start(request)));
sampleWorkload.MapPost("/tight-loops/stop", async (DemoTightLoopController controller, CancellationToken cancellationToken)
    => Results.Ok(await controller.Stop(cancellationToken)));
sampleWorkload.MapPost("/force-cancel", async (
    IHttpContextWorkCommandDispatcher commands,
    DemoSampleSystemSelection systemSelection,
    CancellationToken cancellationToken) =>
{
    if (!systemSelection.Current.Operations)
    {
        return Results.Ok(new
        {
            definitionName = "sample.demo.force-cancel",
            workerId = (string?)null,
            status = "Skipped",
            message = "Operations is disabled.",
        });
    }

    var result = await commands.Dispatch<WorkInput, object?>(
        "sample.demo.force-cancel",
        WorkInput.FromValue(new DemoForceCancelInput(), identifiers: [new WorkIdentifier("sample-workload", "force-cancel")]),
        "Queue force-cancel sample work from the sample host.",
        new WorkDispatchOptions(WorkDispatchCompletion.ReturnAfterAccepted),
        cancellationToken: cancellationToken);

    return Results.Ok(new
    {
        definitionName = "sample.demo.force-cancel",
        workerId = result.WorkerId?.ToString(),
        status = result.QueueOutcome?.Status.ToString() ?? result.Status.ToString(),
        message = result.ErrorMessage ?? "Queued force-cancel worker.",
    });
});

app.MapWorkableApi("/workable");
app.MapWorkableMcp();
app.MapWorkableSignalR("/workable/realtime");

await app.RunAsync();

static void ConfigureSampleSystemAuthorization(
    IWorkSystemBuilder builder,
    bool isFulfillment)
{
    if (isFulfillment)
    {
        builder.ConfigureFulfillmentSystemAuthorization();
    }
    else
    {
        builder.ConfigureOperationsSystemAuthorization();
    }
}

static Action<IWorkAuthorizationBuilder>? CreateSampleWorkAuthorization(
    string readGroup,
    string operateGroup)
{
    return authorization => authorization.RequireGroups(
        readGroups: [readGroup],
        operateGroups: [operateGroup]);
}

static WorkDefinition DemoDefinition(string name, string category, string description)
    => WorkDefinition.Create(name, description, category);

static WorkDefinition DemoProfileDefinition(string name, string category, string description)
    => WorkDefinition.Create(
        name,
        description,
        category,
        defaultOptions: new WorkerOptions(ProfilingEnabled: true));

static WorkDefinition DemoRecurringDefinition(string name, string category, string description)
    => WorkDefinition.Create(
        name,
        description,
        category,
        configuration: WorkConfiguration.Default with
        {
            Recurrence = WorkRecurrenceConfiguration.Every(TimeSpan.FromSeconds(4)) with
            {
                RetainedIterations = 1_000,
            },
        });

static WorkflowDefinition DemoWorkflowDefinition(string name, string category, string description)
    => WorkflowDefinition.Create(name, description, category);

static WorkInput DemoWorkflowTimedInput(
    string scenario,
    int delayMilliseconds,
    string stepIdentifier,
    string workflowKey = "operator-lab")
    => WorkInput.FromValue(
        new DemoTimedInput(
            scenario,
            delayMilliseconds,
            DiscoveredIdentifierType: "sample-workflow-step",
            DiscoveredIdentifierValue: stepIdentifier),
        identifiers: DemoWorkflowIdentifiers(stepIdentifier, workflowKey));

static WorkInput DemoWorkflowMessagePanelInput(
    string stepIdentifier,
    string workflowKey = "operator-lab")
    => WorkInput.FromValue(
        new DemoMessagePanelInput(),
        identifiers: DemoWorkflowIdentifiers(stepIdentifier, workflowKey));

static WorkInput DemoWorkflowProfilingInput(
    string workflowKey = "operator-lab",
    string stepIdentifier = "profile-summary")
    => WorkInput.FromValue(
        new DemoProfilingLabInput(
            Scenario: $"{workflowKey}-profile",
            SectionCount: 5,
            StepsPerSection: 4,
            DelayMilliseconds: 65,
            AddDiscoveredIdentifier: true),
        identifiers: DemoWorkflowIdentifiers(stepIdentifier, workflowKey));

static IReadOnlyList<WorkIdentifier> DemoWorkflowIdentifiers(
    string stepIdentifier,
    string workflowKey = "operator-lab")
    =>
    [
        new WorkIdentifier("sample-workflow", workflowKey),
        new WorkIdentifier("sample-workflow-step", stepIdentifier),
    ];

static IReadOnlyList<DemoWorkflowFanOutSeedItem> CreateLargeDataflowSeedItems(int count)
    => Enumerable.Range(1, Math.Max(1, count))
        .Select(index => new DemoWorkflowFanOutSeedItem(
            $"Process dataflow item {index:00}",
            30_000 + ((index - 1) % 6) * 1_000,
            $"dataflow-item-{index:00}"))
        .ToArray();
