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

builder.Services.AddWorkableSystem(workable =>
{
    workable.StartWithHost();
    workable.AddWork<HealthSnapshotWork>();
    workable.AddWork<SampleEchoWork>();
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
        DemoDefinition("sample.demo.queue-pressure", "Samples:Demo", "Queues faster than concurrency capacity to demonstrate queue pressure."),
        configuration => configuration.LimitConcurrency(
            maximumCapacity: 1,
            scope: WorkConcurrencyScope.PerConcurrencyKey,
            blockingMode: WorkConcurrencyBlockingMode.WhileExecuting,
            limitReachedBehavior: WorkConcurrencyLimitReachedBehavior.DeferStart));
    workable.AddWork<DemoTimedWork>(DemoRecurringDefinition("sample.demo.recurring", "Samples:Demo", "Small recurring pulse for UI waiting/running state testing."));
});

builder.Services.AddWorkableSystem("fulfillment", workable =>
{
    workable.StartWithHost();
    workable.AddWork<OrderPickListWork>();
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
builder.Services.AddWorkableHttpApi();
builder.Services.AddWorkableMcpServer();
builder.Services.AddWorkableSignalR(options =>
{
    options.PublishInterval = TimeSpan.FromMilliseconds(250);
    options.DiagnosticsPublishInterval = TimeSpan.FromMilliseconds(250);
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

app.MapGet("/", (HttpContext context) =>
{
    var workableHost = string.IsNullOrWhiteSpace(context.Request.Host.Host)
        ? "localhost"
        : context.Request.Host.Host;
    var workableUrl = $"http://{workableHost}:{sampleHttpPort}/workable";
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
            <p>Start the Workable UI and add this server: <code>{{workableUrl}}</code></p>
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
                const button = document.getElementById('toggle');
                const forceCancel = document.getElementById('force-cancel');
                const burstCount = document.getElementById('burst-count');
                const burstQueue = document.getElementById('burst-queue');
                const durableBurstCount = document.getElementById('durable-burst-count');
                const durableBurstQueue = document.getElementById('durable-burst-queue');
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
                const pressureStatus = document.getElementById('pressure-status');
                const tightLoopStatus = document.getElementById('tight-loop-status');
                const forceCancelStatus = document.getElementById('force-cancel-status');
                let intervalDirty = false;
                let failurePercentageDirty = false;
                let selectedOperations = true;
                let selectedFulfillment = true;
                let sampleWorkloadRunning = false;
                let pressureRunning = false;
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
                refreshTightLoops();
                setInterval(() => {
                    refresh();
                    refreshPressure();
                    refreshTightLoops();
                }, 2000);
            </script>
        </body>
        </html>
        """,
        "text/html");
});

app.MapGet("/sample-workload", (DemoWorkloadController controller)
    => Results.Ok(controller.Status()));
app.MapPost("/sample-workload/toggle", async (DemoWorkloadController controller, CancellationToken cancellationToken)
    => Results.Ok(await controller.Toggle(cancellationToken)));
app.MapPost("/sample-workload/interval", (DemoWorkloadController controller, DemoWorkloadIntervalRequest request)
    => Results.Ok(controller.SetQueueInterval(request.Milliseconds)));
app.MapPost("/sample-workload/failures", (DemoWorkloadController controller, DemoWorkloadFailureRequest request)
    => Results.Ok(controller.SetFailurePercentage(request.Percentage)));
app.MapPost("/sample-workload/systems", (DemoWorkloadController controller, DemoWorkloadSystemsRequest request)
    => Results.Ok(controller.SetEnabledSystems(request)));
app.MapPost("/sample-workload/burst", async (DemoWorkloadController controller, DemoBurstRequest request, CancellationToken cancellationToken)
    => Results.Ok(await controller.QueueBurst(request.Count, cancellationToken)));
app.MapPost("/sample-workload/durable-burst", async (DemoWorkloadController controller, DemoBurstRequest request, CancellationToken cancellationToken)
    => Results.Ok(await controller.QueueDurableBurst(request.Count, cancellationToken)));
app.MapGet("/sample-workload/queue-pressure", (DemoQueuePressureController controller)
    => Results.Ok(controller.Status()));
app.MapPost("/sample-workload/queue-pressure/start", (DemoQueuePressureController controller)
    => Results.Ok(controller.Start()));
app.MapPost("/sample-workload/queue-pressure/stop", async (DemoQueuePressureController controller, CancellationToken cancellationToken)
    => Results.Ok(await controller.Stop(cancellationToken)));
app.MapGet("/sample-workload/tight-loops", (DemoTightLoopController controller)
    => Results.Ok(controller.Status()));
app.MapPost("/sample-workload/tight-loops/start", (DemoTightLoopController controller, DemoTightLoopRequest request)
    => Results.Ok(controller.Start(request)));
app.MapPost("/sample-workload/tight-loops/stop", async (DemoTightLoopController controller, CancellationToken cancellationToken)
    => Results.Ok(await controller.Stop(cancellationToken)));
app.MapPost("/sample-workload/force-cancel", async (
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

    var handle = await registry.Default.Queue.Enqueue(
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
                RetainedSuccessfulIterations = 1_000,
                RetainedFailedIterations = 25,
            },
        });
