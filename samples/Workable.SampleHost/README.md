# Workable Sample Host

This sample hosts two in-process Workable systems with the standard adapters enabled:

- HTTP API at `/workable`
- MCP server at `/workable/mcp`
- SignalR realtime hub at `/workable/realtime`

By default the sample uses fake path-based authentication so local authorization scenarios are easy to exercise without an identity provider. It can also run like a Microsoft Entra protected target app by setting `Workable:SampleHost:Authentication` to `Entra` and configuring `Workable:Entra`.

Run it from the repository root:

```powershell
dotnet run --project .\samples\Workable.SampleHost\Workable.SampleHost.csproj
```

The sample registers operation, fulfillment, and demo work definitions. Open the sample host root page in a browser to start or stop a continuous demo workload:

```text
http://localhost:61932/
```

The demo workload queues work continuously while it is enabled. It includes short work, long work, a small fixed set of recurring workers, discovered identifiers, subjects, supplied identifiers, globally selectable target systems, and a configurable intentional failure percentage.

The default sample system is also configured with SQL Server LocalDB persistence using the `WorkableSampleHost` database. The normal sample workload does not queue durable work. Use the Durable burst control on the root page to queue `sample.demo.durable` workers through the durable SQL queue. The durable sample is intentionally configured without idempotency, so its SQL rows have `IsDurableQueued = 1` and `HasIdempotencyReservation = 0`.

The root page also includes shared Operations/Fulfillment system checkboxes plus burst, durable burst, durability warning, idempotency duplicate, tight queue loop, and queue pressure controls. Every producer respects the shared system selection. The durability warning control queues `sample.demo.durable` workers inside an uncommitted caller-owned SQL transaction and immediately waits on their handles. That keeps them in the "accepted but not yet materialized" state long enough for the durability diagnostics section in the Workable popover to show `Accepted waiters` and, after roughly 30 seconds, the waiter-age warning. The idempotency duplicate control queues `sample.demo.idempotent` twice with the same subject so the second request is rejected and the idempotency diagnostics section in the Workable popover has sample data to display. Tight queue loops submit quick demo workers as fast as the selected systems accept them. By default those workers use a 500ms delay; the Task.Yield option switches them to a single async yield for tighter scheduling-overhead testing. Queue pressure starts a dedicated producer that queues `sample.demo.queue-pressure` workers every 250ms when Operations is enabled. Each pressure worker takes 1 second and shares one concurrency key with capacity 1, so the queue grows until you press Stop pressure. The stop action explicitly cancels the tracked queued/running pressure workers. Stopping the whole Workable system interrupts active workers instead; durable interrupted workers remain replayable after their SQL lease expires.

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
```

## Running As An Entra Target App

The sample references `Workable.Entra`, so it can validate target-audience Microsoft Entra access tokens the same way a hosted application would.

Set configuration with user secrets, environment variables, or `appsettings.json`:

```json
{
  "Workable": {
    "SampleHost": {
      "Authentication": "Entra"
    },
    "Entra": {
      "TenantId": "00000000-0000-0000-0000-000000000000",
      "Audience": "api://target-app-client-id"
    }
  }
}
```

Configure the target app so access tokens include the claim values you want Workable to authorize against. This sample uses these Workable authorization group values:

- `sample.target.connect`
- `sample.target.read-all`
- `sample.target.operate-all`
- `sample.target.diagnostics`
- `sample.target.control`
- `sample.target.system-admin`
- `sample.target.work-admin`

In a real host, those values can be Entra security group object IDs, app role values, or delegated scope values. Workable just consumes the claim values that appear in the token.

When Entra mode is active, the sample Workable URL is:

```text
http://localhost:61932/workable
```

Requests to `/workable`, `/workable/mcp`, `/workable/realtime`, and the sample `/sample-workload/*` helper endpoints must include a valid bearer token for the configured audience. The token's `scp`, `roles`, or `groups` claims become Workable authorization groups, and the sample relies on normal Workable system authorization to decide what that caller can do.

MCP exposes work definitions with protocol-safe names such as:

- `workable_work_sample_echo`
- `workable_work_sample_delay`
- `workable_work_fulfillment_picklist_create`

The MCP server also exposes Workable query tools such as `workable_query_workers` and `workable_get_worker_status_summary`.

MCP work calls wait for completion by default. Calling `workable_work_sample_echo` returns the completed worker output in the tool result. HTTP work calls return after queue acceptance by default.

The HTTP API exposes the standard Workable routes. For example:

- `GET /workable/definitions`
- `POST /workable/work/sample.echo`
- `POST /workable/workers/query`
- `POST /workable/workers/{workerId}/actions/{action}`

## Testing MCP Locally

Start the sample host:

```powershell
dotnet run --project .\samples\Workable.SampleHost\Workable.SampleHost.csproj
```

The launch profile exposes:

- `http://localhost:61932/workable`
- `http://localhost:61932/workable/realtime`
- `http://localhost:61932/workable/mcp`

## Testing The Admin UI Locally

The admin UI is secure by default and will not proxy requests until you configure admin authentication. The hosted sample Workable API remains responsible for deciding which authenticated profile may read, operate, configure, run lifecycle actions, or inspect diagnostics. For local sample testing, copy `src/workable-admin-ui/workable-admin.basic.config.example.json` to `src/workable-admin-ui/workable-admin.config.local.json`, keep it uncommitted, and use this sample-oriented configuration:

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

This sample configuration uses `authProvider: "basic"` for local convenience. The admin UI can also use `authProvider: "entra"` with Microsoft Entra ID; see `src/workable-admin-ui/README.md` for the Entra app registration and config shape.

Then start the admin UI:

```powershell
npm --prefix .\src\workable-admin-ui run dev
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
