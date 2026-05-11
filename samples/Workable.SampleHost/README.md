# Workable Sample Host

This sample hosts one in-process Workable system with both adapters enabled:

- HTTP API at `/workable`
- MCP server at `/mcp`

Run it from the repository root:

```powershell
dotnet run --project .\samples\Workable.SampleHost\Workable.SampleHost.csproj
```

The sample registers two work definitions:

- `sample.echo`
- `sample.delay`

Both work definitions allow .NET, HTTP, and MCP invocation. The MCP server exposes them with protocol-safe names:

- `workable_work_sample_echo`
- `workable_work_sample_delay`

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
- `http://localhost:61932/mcp`

Check that the host is running:

```powershell
Invoke-RestMethod http://localhost:61932/workable/definitions
Invoke-RestMethod http://localhost:61932/workable/workers/status-summary
```

Point an MCP client that supports HTTP transport at:

```text
http://localhost:61932/mcp
```

List tools and call `workable_work_sample_echo` with:

```json
{
  "message": "hello"
}
```

The tool result should include a completed Workable invocation and an output payload containing the echoed message.
