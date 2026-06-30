# Workable Sample Host

This sample hosts two in-process Workable systems:

- the default `Operations` system
- the named `fulfillment` system

The sample enables the standard adapters, but not every adapter is exposed the same way for both systems:

- HTTP API at `/workable` for the default system, with named-system routes under `/workable/systems/{systemName}`
- MCP server at `/workable/mcp` for the default system
- SignalR realtime hub at `/workable/realtime`, where subscriptions can target either system by `systemName`

The sample uses fake path-based authentication so local authorization scenarios are easy to exercise without an identity provider.

Run it from the repository root:

```powershell
dotnet run --project .\apps\samples\Workable.SampleHost\Workable.SampleHost.csproj
```

The sample registers operation, fulfillment, and demo work definitions. Open the sample host root page in a browser to start or stop a continuous demo workload:

```text
http://localhost:61932/
```

The demo workload queues work continuously while it is enabled. It includes short work, long work, a small fixed set of recurring workers, discovered identifiers, subjects, supplied identifiers, globally selectable target systems, and a configurable intentional failure percentage. One Operations recurring worker, `sample.demo.iteration-lab`, runs every 2 seconds and is tuned for iteration/logging demos: about 90% of its cycles succeed after 10 log entries, about 5% fail non-transiently after a handful of logs, and about 5% fail transiently before recovering within the configured retry limit.

For the iteration profile viewer, queue `sample.demo.profiling-lab`. That definition enables profiling by default and builds a deeper tree with logical scopes, timing leaves, result nodes, injected-service contributions, and real SQL command nodes captured through `Microsoft.Data.SqlClient`. One service intentionally writes a constructor-time profile node so you can also see how root-level service activation differs from execution-time method scopes in the UI.

The default sample system is also configured with SQL Server persistence for the `sample.demo.durable` queue. If `WORKABLE_SAMPLE_SQLSERVER_CONNECTION_STRING` is set, the sample host uses that connection string. When that connection string omits a database name, the sample defaults it to `WorkableSampleHost`. Otherwise it first tries SQL Server LocalDB and, when LocalDB is unavailable, falls back to a managed Docker or Podman SQL Server container named `workable-samplehost-sqlserver` using `mcr.microsoft.com/mssql/server:2022-latest`. The target database is created automatically when it does not already exist.

Set `WORKABLE_SAMPLE_SQLSERVER_CONTAINER_RUNTIME` to force a specific container runtime, `WORKABLE_SAMPLE_SQLSERVER_CONTAINER_IMAGE` to override the SQL Server image, or `WORKABLE_SAMPLE_SQLSERVER_CONTAINER_REUSE=false` to stop a newly created managed container when the sample host exits.

The normal sample workload does not queue durable work. Use the Durable burst control on the root page to queue `sample.demo.durable` workers through the durable SQL queue. The durable sample is intentionally configured without idempotency, so it writes a `WorkQueueEntries` row and a `WorkEntries` payload row with `HasIdempotencyReservation = 0`.

The root page also includes shared Operations/Fulfillment system checkboxes plus burst, durable burst, durability warning, idempotency duplicate, tight queue loop, and queue pressure controls. Every producer respects the shared system selection. It also includes three sample workflow launchers: `sample.demo.workflow.operator-lab` for the long operator-focused graph, `sample.demo.workflow.dataflow-lab` for the dynamic fan-out path that builds a list in one step and expands it with `DispatchEach`, and `sample.demo.workflow.large-dataflow-lab` for a larger `DispatchEach` run that generates enough child workers to exercise paging in the workflow node inspector. The durability warning control queues `sample.demo.durable` workers inside an uncommitted caller-owned SQL transaction and immediately waits on their handles. That keeps them in the "accepted but not yet materialized" state long enough for the durability diagnostics section in the Workable popover to show `Accepted waiters` and, after roughly 30 seconds, the waiter-age warning. The idempotency duplicate control queues `sample.demo.idempotent` twice with the same subject so the second request is rejected and the idempotency diagnostics section in the Workable popover has sample data to display. Tight queue loops submit quick demo workers as fast as the selected systems accept them. By default those workers use a 500ms delay; the Task.Yield option switches them to a single async yield for tighter scheduling-overhead testing. Queue pressure starts a dedicated producer that queues `sample.demo.queue-pressure` workers every 250ms when Operations is enabled. Each pressure worker takes 1 second and shares one concurrency key with capacity 1, so the queue grows until you press Stop pressure. The stop action explicitly cancels the tracked queued/running pressure workers. Stopping the whole Workable system interrupts active workers instead; durable interrupted workers remain replayable after their SQL lease expires.

The sample also exposes the toggle endpoints directly:

```powershell
Invoke-RestMethod http://localhost:61932/sample-workload
Invoke-RestMethod http://localhost:61932/sample-workload/toggle -Method Post
Invoke-RestMethod http://localhost:61932/sample-workload/systems -Method Post -ContentType application/json -Body '{"operations":true,"fulfillment":false}'
Invoke-RestMethod http://localhost:61932/sample-workload/durable-burst -Method Post -ContentType application/json -Body '{"count":25}'
Invoke-RestMethod http://localhost:61932/sample-workload/durability-warning/start -Method Post
Invoke-RestMethod http://localhost:61932/sample-workload/durability-warning
Invoke-RestMethod http://localhost:61932/sample-workload/durability-warning/stop -Method Post
Invoke-RestMethod http://localhost:61932/sample-workload/idempotency-warning -Method Post
Invoke-RestMethod http://localhost:61932/sample-workload/queue-pressure
Invoke-RestMethod http://localhost:61932/sample-workload/queue-pressure/start -Method Post
Invoke-RestMethod http://localhost:61932/sample-workload/queue-pressure/stop -Method Post
Invoke-RestMethod http://localhost:61932/sample-workload/tight-loops
Invoke-RestMethod http://localhost:61932/sample-workload/tight-loops/start -Method Post -ContentType application/json -Body '{"useTaskYield":true}'
Invoke-RestMethod http://localhost:61932/sample-workload/tight-loops/stop -Method Post
Invoke-RestMethod http://localhost:61932/sample-workload/force-cancel -Method Post
Invoke-RestMethod http://localhost:61932/sample-workload/interval -Method Post -ContentType application/json -Body '{"milliseconds":85}'
Invoke-RestMethod http://localhost:61932/sample-workload/failures -Method Post -ContentType application/json -Body '{"percentage":8}'
```

If you want a concrete example of the recommended "queue work from my own HTTP endpoint" path, see the `/sample-workload/force-cancel` endpoint in `apps/samples/Workable.SampleHost/Program.cs`. It uses `IHttpContextWorkCommandDispatcher` instead of building a session by hand, which is the intended pattern for custom ASP.NET Core endpoints that just need to dispatch Workable work.

MCP exposes default-system work definitions with protocol-safe names such as:

- `workable_work_sample_echo`
- `workable_work_sample_delay`
- `workable_work_fulfillment_picklist_create`

The MCP server also exposes Workable query tools such as `workable_query_workers` and `workable_get_worker_status_summary`.

MCP work calls wait for completion by default. Calling `workable_work_sample_echo` returns the completed worker output in the tool result. HTTP work calls return after queue acceptance by default.

The HTTP API exposes the standard Workable routes. For example:

- `GET /workable/definitions`
- `GET /workable/systems/fulfillment/definitions`
- `POST /workable/work/sample.echo`
- `POST /workable/workers/query`
- `POST /workable/workers/{workerId}/actions/{action}`

## Testing MCP Locally

Start the sample host:

```powershell
dotnet run --project .\apps\samples\Workable.SampleHost\Workable.SampleHost.csproj
```

The launch profile exposes:

- `http://localhost:61932/workable`
- `http://localhost:61932/workable/realtime`
- `http://localhost:61932/workable/mcp`

## Testing The Admin UI Locally

The admin UI is secure by default and will not proxy requests until you configure admin authentication. The hosted sample Workable API remains responsible for deciding which authenticated profile may read, operate, configure, run lifecycle actions, or inspect diagnostics. For local sample testing, copy `apps/web/workable-admin-ui/workable-admin.basic.config.example.json` to `apps/web/workable-admin-ui/workable-admin.config.local.json`, keep it uncommitted, and use this sample-oriented configuration:

```json
{
  "authProvider": "basic",
  "apiUrl": "http://localhost:61932/fake-auth/system-admin/workable",
  "basicAuth": {
    "username": "admin",
    "password": "replace-with-a-long-random-password"
  },
  "sessionSecret": "replace-with-a-different-long-random-secret",
  "sessionMaxAgeSeconds": 28800
}
```

Then start the admin UI:

```powershell
npm --prefix .\apps\web\workable-admin-ui install
npm --prefix .\apps\web\workable-admin-ui run dev
```

Open the admin UI and sign in with the configured username and password. Use the sample host root page to copy a fake-auth Workable URL for a lower-privilege profile when you want to verify the hosted Workable system still rejects restricted operations.

Check that the host is running:

```powershell
Invoke-RestMethod http://localhost:61932/fake-auth/system-admin/workable/definitions
Invoke-RestMethod http://localhost:61932/fake-auth/system-admin/workable/workers/status-summary
Invoke-RestMethod http://localhost:61932/fake-auth/system-admin/workable/systems
```

Point an MCP client that supports HTTP transport at:

```text
http://localhost:61932/fake-auth/system-admin/workable/mcp
```

List tools and call `workable_work_sample_echo` with:

```json
{
  "message": "hello"
}
```

The tool result should include a completed Workable invocation and an output payload containing the echoed message.
