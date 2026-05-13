using Workable;
using Workable.SampleHost.Demo;
using Workable.SampleHost.Fulfillment;
using Workable.SampleHost.Operations;

var builder = WebApplication.CreateBuilder(args);
const string sampleCorsPolicy = "WorkableSampleUi";
const int sampleHttpPort = 61932;

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
    workable.AddWork<DemoTimedWork>(
        DemoDefinition("sample.demo.throttled", "Samples:Demo", "Sample work that queues behind a concurrency key."),
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
builder.Services.AddWorkableHttpApi();
builder.Services.AddWorkableMcpServer();
builder.Services.AddWorkableSignalR(options =>
{
    options.DashboardPublishInterval = TimeSpan.FromSeconds(2);
});

var app = builder.Build();

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
                body { font-family: system-ui, sans-serif; margin: 3rem; max-width: 760px; line-height: 1.5; }
                button { font: inherit; padding: .7rem 1rem; border: 1px solid #222; background: #111; color: white; cursor: pointer; }
                button.running { background: #8b1d1d; }
                input { font: inherit; padding: .6rem .7rem; width: 9rem; }
                .controls { display: flex; flex-wrap: wrap; gap: .75rem; align-items: center; margin-top: 1rem; }
                code { background: #f3f3f3; padding: .1rem .25rem; }
                .status { margin-top: 1rem; }
            </style>
        </head>
        <body>
            <h1>Workable Sample Host</h1>
            <p>Start the Workable UI and add this server: <code>{{workableUrl}}</code></p>
            <div class="controls">
                <button id="toggle" type="button">Start sample workers</button>
                <label>
                    Interval
                    <input id="interval" type="number" min="10" max="10000" step="5">
                    ms
                </label>
                <button id="update-interval" type="button">Update interval</button>
            </div>
            <p class="status" id="status">Loading sample workload status...</p>
            <script>
                const button = document.getElementById('toggle');
                const interval = document.getElementById('interval');
                const updateInterval = document.getElementById('update-interval');
                const status = document.getElementById('status');
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

                button.addEventListener('click', async () => {
                    button.disabled = true;
                    try {
                        await fetch('/sample-workload/toggle', { method: 'POST' });
                        await refresh();
                    } finally {
                        button.disabled = false;
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
                setInterval(refresh, 2000);
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
