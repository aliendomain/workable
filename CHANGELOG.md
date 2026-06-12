# Changelog

## 1.1.061226.1 - 2026-06-12

### Breaking Changes

- `Workable.HttpApi` iteration detail routes changed. The old iteration endpoints:
  - `/workers/{workerId}/iterations/{sequence}/detail`
  - `/workers/{workerId}/iterations/{sequence}/messages`
  - `/workers/{workerId}/iterations/{sequence}/logs`
  were replaced by:
  - `/workers/{workerId}/iterations/{sequence}/overview`
  - `/workers/{workerId}/iterations/{sequence}/overview/messages`
  - `/workers/{workerId}/iterations/{sequence}/overview/logs`
- `Workable.HttpApi` removed `WorkableHttpWorkerIterationDetail` and `WorkableHttpWorkerIterationSnapshot`. Consumers should migrate to the iteration overview contract exposed through `Workable.Views` and `Workable.HttpApi`.
- `Workable.HttpApi` discovery now uses `WorkSystemCapabilities` instead of `WorkableHttpSystemCapabilities`. Consumers reading `WorkableHttpSystemDescriptor.Capabilities` will need to update to the new shared capability type.
- Built-in `/workable` HTTP routes now enforce dedicated built-in surface access. Callers that previously relied on ordinary system or work access may now also need `SystemAdministrators(...)`, `WorkAdministrators(...)`, or `AllowBuiltInHttpApiToGroups(...)`.
- `WorkOrigin` and `WorkRequestContext` now include origin-surface classification through `WorkOriginSurface`. Consumers that directly construct these types or call their factory methods should recompile and review those call sites.

### Added

- Added constrained operate requirements for work registration through `IWorkOperateRequirementBuilder`.
- Added granular work operation permissions through `WorkOperationPermissions`, including separate control of queueing, worker actions, and reconfiguration.
- Added built-in HTTP surface authorization controls, including `AllowBuiltInHttpApiToGroups(...)` and host-wide `WorkableHttpApiOptions.SurfaceAccessGroups`.
- Added request origin surface tracking so built-in Workable adapter traffic can be distinguished from host-defined entry points.
- Added iteration overview contracts to `Workable.Views` and `Workable.HttpApi`, with panel-aware landing payloads and narrow paging routes for retained messages and logs.
- Added shared `WorkSystemCapabilities` discovery with capability flags for persistent coordination and SQL profiling.
- Added SQL profiling support for `Workable.SqlServer` through `AddWorkableSqlServerProfiling()`, capturing `Microsoft.Data.SqlClient` activity inside active Workable profiles.
- Added host extensibility points for capability contribution and bootstrap initialization.

### Changed

- `AllowOperateToGroups(...)` remains the broad full-surface grant, but runtime authorization now enforces the specific operation being attempted.
- Built-in `Workable.HttpApi` routes are now intentionally stricter than host-defined HTTP endpoints that dispatch into Workable.
- Iteration detail loading now uses a typed overview model instead of the older stapled detail payload.
- System capability discovery now reports SQL profiling availability in addition to persistent coordination availability.
- In non-production hosts, profiling can now be inherited by default when work definitions do not explicitly opt out.

### Fixed

- Fixed bulk worker action authorization scoping.
- Refined lifecycle observer exception filtering.
- Improved SQL Server integration and test portability across environments.
