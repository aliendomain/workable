using Workable;
using Workable.SampleHost;
using Workable.SampleHost.Demo;
using Workable.SampleHost.Fulfillment;
using Workable.SampleHost.Operations;

var builder = WebApplication.CreateBuilder(args);
const string sampleCorsPolicy = "WorkableSampleUi";
const int sampleHttpPort = 61932;

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
    workable.AddWork<DemoTimedWork>(
        DemoDefinition("sample.demo.throttled", "Samples:Demo", "Sample work that queues behind a concurrency key."),
        configuration => configuration.LimitConcurrency(
            maximumCapacity: 1,
            scope: WorkConcurrencyScope.PerConcurrencyKey,
            blockingMode: WorkConcurrencyBlockingMode.WhileExecuting,
            limitReachedBehavior: WorkConcurrencyLimitReachedBehavior.DeferStart));
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
    workable.AddWork<DemoTimedWork>(
        DemoDefinition("fulfillment.demo.throttled", "Fulfillment:Demo", "Fulfillment sample work that queues behind a concurrency key."),
        configuration => configuration.LimitConcurrency(
            maximumCapacity: 1,
            scope: WorkConcurrencyScope.PerConcurrencyKey,
            blockingMode: WorkConcurrencyBlockingMode.WhileExecuting,
            limitReachedBehavior: WorkConcurrencyLimitReachedBehavior.DeferStart));
    workable.AddWork<DemoTimedWork>(DemoRecurringDefinition("fulfillment.demo.recurring", "Fulfillment:Demo", "Small recurring fulfillment pulse for UI waiting/running state testing."));
});

builder.Services.AddSingleton<DemoWorkloadController>();
builder.Services.AddHostedService(static services => services.GetRequiredService<DemoWorkloadController>());
builder.Services.AddSingleton<DemoQueuePressureController>();
builder.Services.AddWorkableHttpApi();
builder.Services.AddWorkableMcpServer();
builder.Services.AddWorkableSignalR(options =>
{
    options.DashboardPublishInterval = TimeSpan.FromSeconds(2);
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
                .sample-workload-controls { display: grid; grid-template-columns: max-content max-content max-content; }
                .pressure-controls { display: grid; grid-template-columns: max-content max-content; }
                .burst-controls { display: grid; grid-template-columns: max-content max-content; }
                .interval-control { display: grid; grid-template-columns: max-content 8.5rem max-content; gap: .5rem; align-items: center; }
                .number-control { display: grid; grid-template-columns: max-content 8.5rem; gap: .5rem; align-items: center; }
                .interval-control input { width: 100%; box-sizing: border-box; }
                .number-control input { width: 100%; box-sizing: border-box; }
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
                    .interval-control { grid-template-columns: max-content 1fr max-content; }
                    .number-control { grid-template-columns: max-content 1fr; }
                }
            </style>
        </head>
        <body>
            <h1>Workable Sample Host</h1>
            <p>Start the Workable UI and add this server: <code>{{workableUrl}}</code></p>
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
                                    <input id="interval" type="number" min="10" max="10000" step="5">
                                    ms
                                </label>
                                <button id="update-interval" type="button">Update interval</button>
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
                const pressureStart = document.getElementById('pressure-start');
                const pressureStop = document.getElementById('pressure-stop');
                const interval = document.getElementById('interval');
                const updateInterval = document.getElementById('update-interval');
                const status = document.getElementById('status');
                const burstStatus = document.getElementById('burst-status');
                const pressureStatus = document.getElementById('pressure-status');
                const forceCancelStatus = document.getElementById('force-cancel-status');
                let intervalDirty = false;

                async function refresh() {
                    const response = await fetch('/sample-workload');
                    const data = await response.json();
                    button.textContent = data.isRunning ? 'Stop sample workers' : 'Start sample workers';
                    button.classList.toggle('running', data.isRunning);
                    if (!intervalDirty && document.activeElement !== interval) {
                        interval.value = data.queueIntervalMilliseconds;
                    }
                    status.textContent = `${data.isRunning ? 'Running' : 'Stopped'} - queued ${data.queuedCount} - tracking ${data.trackedWorkerCount} - interval ${data.queueIntervalMilliseconds}ms`;
                }

                async function refreshPressure() {
                    const response = await fetch('/sample-workload/queue-pressure');
                    const data = await response.json();
                    pressureStart.disabled = data.isRunning;
                    pressureStop.disabled = !data.isRunning;
                    pressureStatus.textContent = `${data.isRunning ? 'Running' : 'Stopped'} - queued ${data.queuedCount} - tracking ${data.trackedWorkerCount} - ${data.workerDelayMilliseconds}ms work every ${data.queueIntervalMilliseconds}ms`;
                }

                button.addEventListener('click', async () => {
                    button.disabled = true;
                    try {
                        await fetch('/sample-workload/toggle', { method: 'POST' });
                        await refresh();
                    } finally {
                        button.disabled = false;
                    }
                });

                forceCancel.addEventListener('click', async () => {
                    forceCancel.disabled = true;
                    try {
                        const response = await fetch('/sample-workload/force-cancel', { method: 'POST' });
                        const data = await response.json();
                        forceCancelStatus.textContent = `Queued ${data.definitionName} worker ${data.workerId}`;
                    } catch (error) {
                        forceCancelStatus.textContent = 'Unable to queue force-cancel worker.';
                    } finally {
                        forceCancel.disabled = false;
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
                        burstStatus.textContent = `Queued ${data.queuedCount}/${data.submittedCount} in ${data.elapsedMilliseconds}ms`;
                        await refresh();
                    } catch (error) {
                        burstStatus.textContent = 'Unable to queue burst workers.';
                    } finally {
                        burstQueue.disabled = false;
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

                interval.addEventListener('blur', async () => {
                    if (!intervalDirty) {
                        await refresh();
                    }
                });

                updateInterval.addEventListener('click', async () => {
                    updateInterval.disabled = true;
                    try {
                        await fetch('/sample-workload/interval', {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: JSON.stringify({ milliseconds: Number(interval.value) })
                        });
                        intervalDirty = false;
                        await refresh();
                    } finally {
                        updateInterval.disabled = false;
                    }
                });

                refresh();
                refreshPressure();
                setInterval(() => {
                    refresh();
                    refreshPressure();
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
app.MapPost("/sample-workload/burst", async (DemoWorkloadController controller, DemoBurstRequest request, CancellationToken cancellationToken)
    => Results.Ok(await controller.QueueBurst(request.Count, cancellationToken)));
app.MapGet("/sample-workload/queue-pressure", (DemoQueuePressureController controller)
    => Results.Ok(controller.Status()));
app.MapPost("/sample-workload/queue-pressure/start", (DemoQueuePressureController controller)
    => Results.Ok(controller.Start()));
app.MapPost("/sample-workload/queue-pressure/stop", async (DemoQueuePressureController controller, CancellationToken cancellationToken)
    => Results.Ok(await controller.Stop(cancellationToken)));
app.MapPost("/sample-workload/force-cancel", async (IWorkSystemRegistry registry, CancellationToken cancellationToken) =>
{
    var handle = await registry.Default.Queue.Enqueue(
        "sample.demo.force-cancel",
        WorkInput.FromValue(new DemoForceCancelInput(), identifiers: [new WorkIdentifier("sample-workload", "force-cancel")]),
        cancellationToken: cancellationToken);

    return Results.Ok(new
    {
        definitionName = "sample.demo.force-cancel",
        workerId = handle.WorkerId?.ToString(),
        status = handle.QueueOutcome.Status.ToString(),
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
