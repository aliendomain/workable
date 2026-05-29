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
const string samplePersistenceConnectionString = "Server=(localdb)\\MSSQLLocalDB;Database=WorkableSampleHost;Integrated Security=true;TrustServerCertificate=true";

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.FormatterName = WorkableSampleConsoleFormatter.FormatterName);
builder.Logging.AddConsoleFormatter<WorkableSampleConsoleFormatter, Microsoft.Extensions.Logging.Console.ConsoleFormatterOptions>();
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("System", LogLevel.Warning);
builder.Logging.AddFilter("Workable", LogLevel.Information);

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

builder.Services.AddWorkableSqlServerDurableQueue(samplePersistenceConnectionString);
builder.Services.AddSingleton<DemoRecurringIterationPlanStore>();

builder.Services.AddWorkableSystem(workable =>
{
    workable.StartWithHost();
    ConfigureSampleSystemAuthorization(workable, isFulfillment: false);
    workable.AddWork<HealthSnapshotWork>();
    workable.AddWork<SampleEchoWork>(
        configure: null,
        authorize: CreateSampleWorkAuthorization(
            SampleFakeAuth.OperationsCustomReadGroup,
            SampleFakeAuth.OperationsCustomOperateGroup));
    workable.AddWork<SampleDelayWork>();
    workable.AddWork<WelcomeEmailWork>();
    workable.AddWork<InvoiceGenerateWork>();
    workable.AddWork<InventoryAdjustWork>();
    workable.AddWork<CustomerSegmentWork>();
    workable.AddWork<ReportExportWork>();
    workable.AddWork<DataImportWork>();
    workable.AddWork<FlakyValidationWork>();
    workable.AddWork<DemoTimedWork>(DemoDefinition("sample.demo.quick", "Samples:Demo", "Short sample work for UI state testing."));
    workable.AddWork<DemoTimedWork>(DemoDefinition("sample.demo.long", "Samples:Demo", "Longer sample work for UI state testing."));
    workable.AddWork<DemoForceCancelWork>(DemoDefinition("sample.demo.force-cancel", "Samples:Demo", "Ignores cancellation so shutdown must force-cancel it."));
    workable.AddWork<DemoTimedWork>(DemoDefinition("sample.demo.throttled", "Samples:Demo", "Longer sample work without an artificial concurrency bottleneck."));
    workable.AddWork<DemoTimedWork>(
        DemoDefinition("sample.demo.durable", "Samples:Demo", "Durable sample work persisted through SQL Server LocalDB."),
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
builder.Services.AddSingleton<DemoTightLoopController>();
builder.Services.AddSingleton<DemoDurabilityWarningController>(services => new DemoDurabilityWarningController(
    services.GetRequiredService<IWorkSystemRegistry>(),
    services.GetRequiredService<DemoSampleSystemSelection>(),
    samplePersistenceConnectionString,
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
                                    <input id="interval" type="number" min="5" max="10000" step="5">
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
                            <div class="action-description">Persist demo workers through SQL Server LocalDB before they start.</div>
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
                const tightLoopStart = document.getElementById('tight-loop-start');
                const tightLoopStop = document.getElementById('tight-loop-stop');
                const tightLoopYield = document.getElementById('tight-loop-yield');
                const interval = document.getElementById('interval');
                const failurePercentage = document.getElementById('failure-percentage');
                const updateSettings = document.getElementById('update-settings');
                const systemOperations = document.getElementById('system-operations');
                const systemFulfillment = document.getElementById('system-fulfillment');
                const status = document.getElementById('status');
                const burstStatus = document.getElementById('burst-status');
                const durableBurstStatus = document.getElementById('durable-burst-status');
                const durabilityWarningStatus = document.getElementById('durability-warning-status');
                const idempotencyWarningStatus = document.getElementById('idempotency-warning-status');
                const pressureStatus = document.getElementById('pressure-status');
                const tightLoopStatus = document.getElementById('tight-loop-status');
                const forceCancelStatus = document.getElementById('force-cancel-status');
                let intervalDirty = false;
                let failurePercentageDirty = false;
                let selectedOperations = true;
                let selectedFulfillment = true;
                let sampleWorkloadRunning = false;
                let pressureRunning = false;
                let durabilityWarningRunning = false;
                let tightLoopRunning = false;

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
                    forceCancel.disabled = !selectedOperations;
                    tightLoopStart.disabled = tightLoopRunning || !anySelected;
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

                refresh();
                refreshPressure();
                refreshDurabilityWarning();
                refreshTightLoops();
                setInterval(() => {
                    refresh();
                    refreshPressure();
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
sampleWorkload.MapGet("/tight-loops", (DemoTightLoopController controller)
    => Results.Ok(controller.Status()));
sampleWorkload.MapPost("/tight-loops/start", (DemoTightLoopController controller, DemoTightLoopRequest request)
    => Results.Ok(controller.Start(request)));
sampleWorkload.MapPost("/tight-loops/stop", async (DemoTightLoopController controller, CancellationToken cancellationToken)
    => Results.Ok(await controller.Stop(cancellationToken)));
sampleWorkload.MapPost("/force-cancel", async (
    IWorkSystemRegistry registry,
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

    var session = registry.Default.CreateSession("Queue force-cancel sample work from the sample host.");
    var handle = await session.Queue.Enqueue(
        "sample.demo.force-cancel",
        WorkInput.FromValue(new DemoForceCancelInput(), identifiers: [new WorkIdentifier("sample-workload", "force-cancel")]),
        cancellationToken: cancellationToken);

    return Results.Ok(new
    {
        definitionName = "sample.demo.force-cancel",
        workerId = handle.WorkerId?.ToString(),
        status = handle.QueueOutcome.Status.ToString(),
        message = "Queued force-cancel worker.",
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
