# Workable Views

`Workable.Views` is mostly a transitive package.

Most applications use it indirectly through `Workable.HttpApi` or `Workable.SignalR`, because those adapters already expose the shared view and component contract. Reference `Workable.Views` directly only when you are building your own UI or your own transport on top of Workable's read model.

That direct-use story is powerful, but it is also stringly typed. Views, components, and shapes are identified by canonical names rather than a strongly typed page model. This document collects those names in one place so custom UI authors do not have to reverse-engineer them from the adapter code.

## Why It Exists

`Workable.Views` exists to let a UI ask for exactly the slice of data it needs and nothing more.

Instead of always returning one large dashboard payload, the view contract lets the caller choose:

- which panels are visible
- which scope those panels should read from
- how much detail each panel needs
- which option set a panel should use

That gives Workable room to do less work on the server and send less data over the wire. A collapsed card can ask for `compact` data, an expanded table can ask for `detailed` data, and a hidden panel can be omitted entirely. The result is not just smaller JSON. It is a deliberate projection contract between the UI and the server.

## When To Use It

Use `Workable.Views` directly when:

- you want a custom admin or operator UI without using Workable's built-in HTTP endpoints
- you want to reuse the same component model over a different transport
- you want server-side code to request normalized component payloads without reimplementing the projection rules

Do not reference it directly just to host Workable. If you only need standard HTTP or SignalR surfaces, `Workable.HttpApi` and `Workable.SignalR` already depend on it for you.

## Core Contract

The package exposes a small set of reusable request and result types:

- `WorkViewCriteria`: request a named view with an optional scope and optional component overrides
- `WorkComponentCriteria`: request an arbitrary set of components without binding to a named view
- `WorkComponentRequest`: identify one component by `id`, `type`, `shape`, and optional JSON `options`
- `WorkComponentQueryResult`: a generated-at timestamp plus a component result map
- `WorkComponentResult`: a per-component `ok` or `error` envelope with the normalized shape that was served
- `WorkComponentShapes`: `compact`, `standard`, and `detailed`

`WorkComponentRequest.Id` is chosen by the caller and is echoed back as the key in the result map. It is the client-side stable handle for replacing one panel's data. `Type` is the canonical component name understood by the server.

## Entry Point

Custom hosts typically call `WorkableViewQueryAdapter` directly:

```csharp
var adapter = services.GetRequiredService<WorkableViewQueryAdapter>();

WorkComponentQueryResult result = await adapter.View(
    session,
    "overview",
    new WorkViewCriteria(
        Scope: new WorkSystemCriteria(Category: "Billing", IncludeSubcategories: true),
        Components:
        [
            new("system", "system"),
            new("workers", "workers", Shape: WorkComponentShapes.Compact),
            new(
                "throughput",
                "throughput",
                Shape: WorkComponentShapes.Standard,
                Options: JsonSerializer.SerializeToElement(new
                {
                    windowSeconds = 60,
                    bucketSeconds = 5,
                }))
        ]),
    cancellationToken);
```

The same adapter also exposes targeted methods such as `Worker`, `Workers`, `WorkerIterations`, `WorkInfo`, `WorkDefinitions`, `WorkerKeys`, `WorkIterationKeys`, and `WorkerStatusSummary`. Those are useful when a custom UI needs one specific data set instead of a component envelope.

## Named Views

These view names are built in:

- `overview`: returns a dashboard-style component map
- `workers`: defaults to one `workerGrid` component
- `iterations`: defaults to one `iterationGrid` component
- `diagnostics`: defaults to the compact diagnostics component set

Unknown view names do not throw. They return an error component in the result map instead.

## Component Names

These component names are built in:

- `system`
- `catalog`
- `workers`
- `failedWorkers`
- `iterations`
- `failedIterations`
- `completedIterations`
- `throughput`
- `workerGrid`
- `iterationGrid`
- `systemDiagnostics`
- `queueDiagnostics`
- `readModelDiagnostics`
- `retentionDiagnostics`
- `concurrencyDiagnostics`
- `durabilityDiagnostics`
- `idempotencyDiagnostics`

## Default View Composition

When a caller omits components, Workable fills in defaults:

- `overview` defaults to `system`, `workers`, `failedWorkers`, `iterations`, `failedIterations`, and `completedIterations`
- `workers` defaults to `workerGrid`
- `iterations` defaults to `iterationGrid`
- `diagnostics` defaults to compact `queueDiagnostics`, `readModelDiagnostics`, `retentionDiagnostics`, `concurrencyDiagnostics`, `durabilityDiagnostics`, and `idempotencyDiagnostics`

This makes the default experience convenient, but it also means custom UIs should be explicit about components once the layout diverges from the built-in admin story.

## Shapes And Normalization

Shapes are part of the efficiency contract, not just a display hint.

- `compact` is for collapsed panels, badges, pills, or alert chrome
- `standard` is the normal summary payload
- `detailed` is for tables or expanded diagnostic panels

Not every component supports every shape. Workable normalizes unsupported requests to the nearest supported shape instead of failing:

- `workers` and `throughput` normalize `detailed` to `standard`
- `failedWorkers` normalizes `compact` to `standard`
- `workerGrid` and `iterationGrid` normalize to `detailed`
- several diagnostics components normalize `standard` to `detailed`

Clients should treat the returned `WorkComponentResult.Shape` as authoritative.

The important design point is that shape selection is meant to change the server response materially. When a smaller shape can avoid extra aggregation or projection work, the server is expected to do that smaller amount of work rather than serialize a large payload and trust the client to ignore fields.

## Scope

Views and component queries can be scoped with `WorkSystemCriteria`.

The common scope filters are:

- `definitionId`
- `definitionName`
- `category`
- `includeSubcategories`

This lets a custom UI reuse the same component names for global dashboards, one category slice, or one definition-specific page.

## Options

Most components need only `id`, `type`, and `shape`. A few also accept JSON options:

- `throughput`: `windowSeconds` and `bucketSeconds`
- `workerGrid`: `states`, `configuration`, `skip`, `take`, and optional `keyType`
- `iterationGrid`: `statuses`, `skip`, `take`, and optional `keyType`
- `readModelDiagnostics`: `warningThreshold`
- `retentionDiagnostics`: `warningSeconds`
- `concurrencyDiagnostics`: `warningSeconds`
- `durabilityDiagnostics`: `acceptedWorkerWarningSeconds` and `cleanupWarningSeconds`
- diagnostics components used over realtime can also use `publishMode`

Because options are JSON and component names are strings, custom UIs should centralize these request builders in one place rather than scattering literals across the codebase.

## Recommended Approach For Custom UIs

If you build directly on `Workable.Views`, treat it like a protocol contract:

- define your own constants or wrappers for view names and component names
- centralize request construction for each screen
- keep panel ids stable so diffing and replacement stay predictable
- always read the normalized shape from the result
- omit hidden panels instead of requesting everything and hiding it on the client
- think in terms of "smallest useful slice" instead of "fetch one big dashboard object"

That approach keeps the stringly-typed surface manageable and gives you room to swap between direct adapter usage, HTTP, and SignalR without changing your page model.

## Relationship To HTTP And SignalR

`Workable.HttpApi` and `Workable.SignalR` both reuse this package's view model rather than inventing transport-specific dashboard contracts.

- See [HTTP API](../adapters/http-api.md#ui-views-and-components) for the HTTP route shape.
- See [Realtime](../adapters/realtime.md#component-view-updates) for live pushed view updates over SignalR.

If your custom UI can use those adapters directly, do that first. Reach for `Workable.Views` itself when you specifically need to own the transport or the server-side composition layer.
