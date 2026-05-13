# Workable Sample Host

This sample hosts two in-process Workable systems with the standard adapters enabled:

- HTTP API at `/workable`
- MCP server at `/workable/mcp`
- SignalR realtime hub at `/workable/realtime`

Run it from the repository root:

```powershell
dotnet run --project .\samples\Workable.SampleHost\Workable.SampleHost.csproj
```

The sample registers operation, fulfillment, and demo work definitions. Open the sample host root page in a browser to start or stop a continuous demo workload:

```text
http://localhost:61932/
```

The demo workload queues work continuously while it is enabled. It includes short work, long work, throttled work that creates queued workers, a small fixed set of recurring workers, discovered identifiers, subjects, concurrency keys, supplied identifiers, and a small percentage of intentional failures.

The sample also exposes the toggle endpoints directly:

```powershell
Invoke-RestMethod http://localhost:61932/sample-workload
Invoke-RestMethod http://localhost:61932/sample-workload/toggle -Method Post
```

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

Check that the host is running:

```powershell
Invoke-RestMethod http://localhost:61932/workable/definitions
Invoke-RestMethod http://localhost:61932/workable/workers/status-summary
Invoke-RestMethod http://localhost:61932/workable/systems
```

Point an MCP client that supports HTTP transport at:

```text
http://localhost:61932/workable/mcp
```

List tools and call `workable_work_sample_echo` with:

```json
{
  "message": "hello"
}
```

The tool result should include a completed Workable invocation and an output payload containing the echoed message.
