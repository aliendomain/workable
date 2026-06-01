# Frontend Test Audit

Date: 2026-06-01

## Scope

Frontend app audited: `src/workable-admin-ui`.

This is a TypeScript, Next.js App Router, React, Tailwind, shadcn/ui admin console. The current goal is refactor safety, not 100% coverage.

## Project Structure And Versions

- Package manager: npm (`package-lock.json` present).
- Routing model: Next.js App Router under `src/app`; no Pages Router found.
- Main app route: `/` renders `WorkableConsole`.
- Login route: `/login` renders async `LoginPage` plus client `LoginForm`.
- Route handlers: `/api/auth/login`, `/api/auth/logout`, `/api/auth/entra/login`, `/api/auth/entra/callback`, `/api/auth/entra/workable-token`, `/api/workable/[...path]`.
- Proxy: `src/proxy.ts`.
- shadcn/ui config: `components.json`, style `radix-nova`, icon library `lucide`, Tailwind CSS v4.

Important versions from `package.json` / `npm ls --depth=0`:

- `next`: 16.2.6
- `react`: 19.2.4
- `react-dom`: 19.2.4
- `typescript`: 5.9.3
- `jsdom`: 29.1.1
- `radix-ui`: 1.4.3
- `shadcn`: 4.7.0

## Test Tools Currently Configured

- Unit/component runner: Node native `node:test`.
- TypeScript/TSX loading: custom `test/tsx-register.mjs` using TypeScript `transpileModule`.
- DOM environment: custom `src/test/dom.tsx` using jsdom and `react-dom/client`.
- Server/static component rendering: custom `src/test/render.ts` using `renderToStaticMarkup`.
- Next mocks: `test/next-image-mock.mjs`, `test/next-navigation-mock.mjs`.
- No Vitest, Jest, React Testing Library, Playwright, Cypress, Storybook, or MSW config found.
- No e2e test command found.

The local Next.js docs recommend e2e tests for async Server Components where unit tooling is weak; that matters for `/login` page-level behavior and full console route flows.

## Commands Discovered

From `src/workable-admin-ui/package.json`:

- `npm run dev`: `set NODE_OPTIONS=--use-system-ca && next dev`
- `npm run build`: `next build`
- `npm run start`: `next start`
- `npm run lint`: `eslint`
- `npm run test`: `node --import ./test/tsx-register.mjs --test --test-isolation=none "src/**/*.test.ts" "src/**/*.test.tsx"`

Additional practical command:

- `.\node_modules\.bin\tsc.cmd --noEmit`

Repository CI currently runs .NET restore/build/test only. It does not install, lint, typecheck, build, or test `src/workable-admin-ui`.

## Commands Run And Current State

Baseline before changes:

- `npm.cmd run test`: pass, 131/131.
- `npm.cmd run lint`: pass.
- `.\node_modules\.bin\tsc.cmd --noEmit`: pass.
- `npm.cmd run build` inside sandbox: failed after successful compile with `spawn EPERM` during Next worker/page data phase. Classified as environment/sandbox issue.
- `npm.cmd run build` outside sandbox with approval: pass. Routes generated: `/`, `/_not-found`, `/login`, `/icon.png`, and the dynamic API routes listed above.

Final after this audit's test batch:

- `npm.cmd run test`: pass, 137/137.
- `npm.cmd run lint`: pass.
- `.\node_modules\.bin\tsc.cmd --noEmit`: pass.
- `npm.cmd run build` outside sandbox with approval: pass.

After this audit's focused test batch:

- Added login page search-param normalization coverage, login submit success/failure/request-exception coverage, and global error boundary retry coverage.
- Extended the in-repo jsdom helper with small role/label/submit helpers.
- Extended the existing `next/navigation` mock to record `replace` and `refresh`.
- Fixed the custom TS/TSX test resolver so extensionless aliases such as `@/lib/admin-security` prefer files over same-named directories, matching the app's Next/Bundler resolution behavior more closely.

Follow-up strict coverage audit after reopening the whole frontend:

- No tests were added, deleted, merged, or refactored in this follow-up pass.
- `npm.cmd run test`: pass, 137/137.
- `npm.cmd run lint`: pass.
- `.\node_modules\.bin\tsc.cmd --noEmit`: pass.
- `npm.cmd run build` inside sandbox: failed after successful compile during Next worker startup with `spawn EPERM`; classified as environment/sandbox issue.
- `npm.cmd run build` outside sandbox with approval: pass. Routes generated: `/`, `/_not-found`, `/login`, `/icon.png`, `/api/auth/login`, `/api/auth/logout`, `/api/auth/entra/login`, `/api/auth/entra/callback`, `/api/auth/entra/workable-token`, `/api/workable/[...path]`, plus Proxy middleware.

Medium-priority burn-down batch:

- Added mounted `ServerDialog` edit-mode coverage for discovery refresh, preserving matched system IDs, dropping missing systems, cancel/no-save behavior, loading-disabled actions, retry after authorization failure, and save payload after retry.
- Added mounted `OverviewCatalogFilter` coverage for opening the catalog filter, drilling into a category, selecting a definition, applying the definition scope, and clearing an active scope.
- Added mounted `RealtimePayloadWindow` coverage for Payloads/Events tab switching, search filtering, max-message changes, pin/unpin state, clear behavior, disabled pinned menu after clear, and close callbacks.
- Added mounted `WorkableConsole` shell coverage for Catalog navigation, definition loading, header refresh re-fetch, back/forward history, and persisted active view state in `localStorage`.
- Targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console/navigation.test.tsx`: pass, 15/15.
- Targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console/filters-dom.test.tsx`: pass, 3/3.
- Targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/features/console/realtime-payload-window.test.tsx`: pass, 6/6.
- Targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console-dom.test.tsx`: pass, 6/6.
- `npm.cmd run test`: pass, 187/187. The run still emits non-failing React `act(...)` warnings from Radix Tooltip/Presence/Portal/Popper/DismissableLayer cleanup around mounted menu/tooltip tests.
- `npm.cmd run lint`: initially failed on a new realtime test harness prop mutation caught by `react-hooks/immutability`; fixed by moving mutable bookkeeping outside the component and passing callbacks. Rerun pass.
- Targeted rerun after lint fix: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/features/console/realtime-payload-window.test.tsx`: pass, 6/6.
- `.\node_modules\.bin\tsc.cmd --noEmit`: pass.
- `npm.cmd run build` inside sandbox: failed after successful compile during Next worker startup with `spawn EPERM`; classified as environment/sandbox issue.
- `npm.cmd run build` outside sandbox with approval: pass. Routes generated: `/`, `/_not-found`, `/login`, `/icon.png`, `/api/auth/login`, `/api/auth/logout`, `/api/auth/entra/login`, `/api/auth/entra/callback`, `/api/auth/entra/workable-token`, `/api/workable/[...path]`, plus Proxy middleware.

Component refactor batch: navigation/server dialog:

- Selected `components/workable/console/navigation.tsx` because the audit marked ServerDialog and navigation behavior covered, while detail screens, auth/proxy, realtime dock/JSON inspector, and mobile/sidebar refactors still have conditional gates.
- Extracted `ServerDialog` plus host discovery, Workable API URL normalization, discovered-host validation, access-badge helpers, and stored-host reconciliation into `components/workable/console/server-dialog.tsx`.
- Preserved the existing `components/workable/console/navigation.tsx` export surface by re-exporting `ServerDialog`, `discoverHost`, `reconcileStoredHostWithDiscovery`, and the related helper functions from the new module.
- Reduced `navigation.tsx` from about 1535 lines to about 996 lines while keeping sidebar/tree/header concerns in place for a later gated refactor.
- Pre-refactor targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console/navigation.test.tsx`: pass, 15/15.
- Post-refactor targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console/navigation.test.tsx`: pass, 15/15.
- Post-refactor shell command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console-dom.test.tsx`: pass, 6/6.
- `npm.cmd run test`: pass, 187/187. The run still emits non-failing React `act(...)` warnings from Radix Tooltip/Presence/Portal/Popper/DismissableLayer cleanup around mounted menu/tooltip tests.
- `npm.cmd run lint`: pass.
- `.\node_modules\.bin\tsc.cmd --noEmit`: pass.
- `npm.cmd run build` outside sandbox with approval: pass. Routes generated: `/`, `/_not-found`, `/login`, `/icon.png`, `/api/auth/login`, `/api/auth/logout`, `/api/auth/entra/login`, `/api/auth/entra/callback`, `/api/auth/entra/workable-token`, `/api/workable/[...path]`, plus Proxy middleware.

Component refactor batch: query data:

- Selected `components/workable/console/query-screens.tsx` because the audit marks Workers and Iterations query views as Covered and they are in the recommended safe refactor lane.
- Extracted infinite worker/iteration query hooks, query filter types, row merge helpers, iteration row keys, and freshness comparison helpers into `components/workable/console/query-data.ts`.
- Preserved existing `query-screens.tsx` helper exports (`appendUniqueWorkers`, `appendUniqueIterations`, `getIterationRowKey`, `isNewerWorkerRow`, `isNewerIterationRow`) through re-exports so existing tests and callers remain stable.
- Reduced `query-screens.tsx` from about 1832 lines to about 1181 lines; it now focuses more on page/view state, panels, virtual tables, row actions, and display helpers.
- No conditional gate was triggered: this did not touch detail screens, auth/proxy, realtime dock/JSON inspector behavior, or mobile/sidebar layout.
- Pre-refactor targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console/query-screens.test.tsx`: pass, 5/5.
- Pre-refactor targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console/query-screens-dom.test.tsx`: pass, 5/5.
- Post-refactor targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console/query-screens.test.tsx`: pass, 5/5.
- Post-refactor targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console/query-screens-dom.test.tsx`: pass, 5/5.
- `npm.cmd run lint`: pass.
- `.\node_modules\.bin\tsc.cmd --noEmit`: pass.
- `npm.cmd run test`: pass, 187/187. The run still emits non-failing React `act(...)` warnings from Radix Tooltip/Presence/Portal/Popper/DismissableLayer cleanup; this run also emitted one non-failing `WorkerTable` act warning from the mounted query test path.
- `npm.cmd run build` outside sandbox with approval: pass. Routes generated: `/`, `/_not-found`, `/login`, `/icon.png`, `/api/auth/login`, `/api/auth/logout`, `/api/auth/entra/login`, `/api/auth/entra/callback`, `/api/auth/entra/workable-token`, `/api/workable/[...path]`, plus Proxy middleware.

Component refactor batch: query tables:

- Continued in the same covered Workers and Iterations query-view lane; no conditional gate was triggered because this did not touch detail screens, auth/proxy, realtime dock/JSON inspector behavior, or mobile/sidebar layout.
- Extracted virtualized Workers/Iterations tables, query table status/placeholder/total controls, worker row action menu, duration/identifier summaries, worker action helpers, and not-found purge helpers into `components/workable/console/query-tables.tsx`.
- Preserved the existing `query-screens.tsx` public helper surface by re-exporting table/display/action helpers from `query-tables.tsx`, so existing tests and callers continue to import from `query-screens.tsx`.
- Reduced `query-screens.tsx` from about 1181 lines to about 521 lines; `query-screens.tsx` now focuses on query view state, panel composition, data hook usage, mutation orchestration, and selection/highlight state.
- Pre-refactor targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console/query-screens.test.tsx`: pass, 5/5.
- Pre-refactor targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console/query-screens-dom.test.tsx`: pass, 5/5.
- Post-refactor targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console/query-screens.test.tsx`: pass, 5/5.
- Post-refactor targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console/query-screens-dom.test.tsx`: pass, 5/5.
- `npm.cmd run lint`: pass.
- `.\node_modules\.bin\tsc.cmd --noEmit`: pass.
- `npm.cmd run test`: pass, 187/187. The run still emits non-failing React `act(...)` warnings from the mounted query and Radix Tooltip/Presence/Portal/Popper/DismissableLayer test paths.
- `npm.cmd run build` outside sandbox with approval: pass. Routes generated: `/`, `/_not-found`, `/login`, `/icon.png`, `/api/auth/login`, `/api/auth/logout`, `/api/auth/entra/login`, `/api/auth/entra/callback`, `/api/auth/entra/workable-token`, `/api/workable/[...path]`, plus Proxy middleware.

Component refactor batch: overview workers:

- Selected the covered `components/workable/console/overview-screen.tsx` worker summary/action area because mounted Overview tests cover loading, errors, failed-worker action mutation/refresh, and no-operate action hiding. No conditional gate was triggered: this did not touch detail screens, auth/proxy, realtime dock/JSON inspector behavior, mobile/sidebar layout, or throughput chart controls.
- Extracted the failed-worker overview panel, worker tables, row action menu, worker action target type, failed-worker duration helper, and row-action helper functions into `components/workable/console/overview-workers.tsx`.
- Preserved the existing `overview-screen.tsx` helper export surface by re-exporting `formatFailedWorkerDuration`, `getWorkerRowActions`, `isDetailedWorkerOverviewItem`, and `toFailedWorkerActionTarget` from the new module.
- Reduced `overview-screen.tsx` from about 3087 lines to about 2664 lines; `overview-screen.tsx` now keeps the Overview route data shell, panel orchestration, compact worker strip, iteration panels, throughput chart, and realtime registration logic, while worker table/action details live in a cohesive worker module.
- Pre-refactor targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console/overview-screen.test.ts`: pass, 5/5.
- Pre-refactor targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console/overview-screen-dom.test.tsx`: pass, 4/4, with non-failing Radix `act(...)` warnings.
- Post-refactor targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console/overview-screen.test.ts`: pass, 5/5.
- Post-refactor targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console/overview-screen-dom.test.tsx`: pass, 4/4, with non-failing Radix `act(...)` warnings.
- `npm.cmd run lint`: pass.
- `.\node_modules\.bin\tsc.cmd --noEmit`: pass.
- `npm.cmd run test`: pass, 187/187. The run still emits non-failing React `act(...)` warnings from mounted Radix and virtual-table paths.
- `npm.cmd run build` outside sandbox with approval: pass. Routes generated: `/`, `/_not-found`, `/login`, `/icon.png`, `/api/auth/login`, `/api/auth/logout`, `/api/auth/entra/login`, `/api/auth/entra/callback`, `/api/auth/entra/workable-token`, `/api/workable/[...path]`, plus Proxy middleware.

Component refactor batch: console state:

- Selected the covered `components/workable/console.tsx` storage/navigation helper area because `console.test.ts` covers persisted state normalization, host/system normalization, diagnostics target creation, compact diagnostics helpers, and navigation-entry comparison, while `console-dom.test.tsx` covers mounted empty/authenticated/restricted shell flows. No conditional gate was triggered: this did not touch detail screens, auth/proxy, realtime dock/JSON inspector behavior, or mobile/sidebar layout.
- Extracted console storage normalization, default state/system creation, overview panel shape/visibility normalization, host/system lookup helpers, diagnostics target helpers, view title/readiness helpers, scroll helpers, and navigation-entry comparison into `components/workable/console/console-state.ts`.
- Preserved the existing `console.tsx` helper/type export surface by re-exporting the moved helpers and types from the new module.
- Removed a stale `navItems` copy from `console.tsx`; navigation items already live in `components/workable/console/navigation.tsx`.
- `console.tsx` now has 4368 lines and the extracted state helper module has 491 lines; the main shell still remains large and should only be split further after the applicable conditional gates are satisfied.
- Pre-refactor targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console.test.ts`: pass, 6/6.
- Pre-refactor targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console-dom.test.tsx`: pass, 6/6.
- Post-refactor targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console.test.ts`: pass, 6/6.
- Post-refactor targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console-dom.test.tsx`: pass, 6/6.
- `npm.cmd run test`: pass, 187/187. The run still emits non-failing React `act(...)` warnings from mounted Radix Tooltip/Presence/Portal/Popper/DismissableLayer paths.
- `npm.cmd run lint`: pass.
- `.\node_modules\.bin\tsc.cmd --noEmit`: pass.
- `npm.cmd run build` outside sandbox with approval: pass. Routes generated: `/`, `/_not-found`, `/login`, `/icon.png`, `/api/auth/login`, `/api/auth/logout`, `/api/auth/entra/login`, `/api/auth/entra/callback`, `/api/auth/entra/workable-token`, `/api/workable/[...path]`, plus Proxy middleware.

Component refactor batch: schema form data:

- Selected `components/workable/schema-form.tsx` because the audit marks SchemaForm as Covered with helper, render, and mounted interaction coverage for shadcn select, boolean/number/string controls, arrays, dictionaries, and presets. No conditional gate was triggered.
- Extracted pure schema parsing, default value creation, field-path traversal, schema type/format helpers, dictionary key generation, and JSON compaction into `components/workable/schema-form-data.ts`.
- Preserved the existing `schema-form.tsx` public helper imports by re-exporting `parseJsonSchema`, `createDefaultValue`, and `compactJson` from the new helper module.
- Reduced `schema-form.tsx` from about 847 lines to 576 lines; `schema-form-data.ts` has 298 lines.
- Pre-refactor targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/schema-form.test.tsx`: pass, 10/10.
- Post-refactor targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/schema-form.test.tsx`: pass, 10/10.
- `npm.cmd run lint`: pass.
- `.\node_modules\.bin\tsc.cmd --noEmit`: pass.
- `npm.cmd run test`: pass, 187/187. The run still emits non-failing React `act(...)` warnings from mounted virtual table and Radix Tooltip/Presence/Portal/Popper/DismissableLayer paths.
- `npm.cmd run build` outside sandbox with approval: pass. Routes generated: `/`, `/_not-found`, `/login`, `/icon.png`, `/api/auth/login`, `/api/auth/logout`, `/api/auth/entra/login`, `/api/auth/entra/callback`, `/api/auth/entra/workable-token`, `/api/workable/[...path]`, plus Proxy middleware.

Component refactor batch: filter panels:

- Selected `components/workable/console/filters.tsx` because it is non-gated and already had helper coverage plus mounted apply/clear and Overview catalog-filter coverage. Before moving UI components, added one focused DOM test for facet selection and shadcn key-kind select changes because the audit marked that interaction thin.
- Extracted query-filter helper types, layout class constants, active-count calculation, description formatting, key-kind labels, value truncation, and ordered array comparison into `components/workable/console/filter-data.ts`.
- Extracted shared filter panel UI building blocks into `components/workable/console/filter-panels.tsx`: `CatalogFilterPanel`, `FilterPanelFrame`, `FilterPanelSection`, and `QueryFilterSections`.
- Preserved the existing `filters.tsx` helper/type export surface by re-exporting the moved helpers from `filter-data.ts`.
- Reduced `filters.tsx` from about 904 lines to 493 lines; `filter-panels.tsx` has 375 lines and `filter-data.ts` has 77 lines.
- Pre-refactor targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console/filters.test.ts`: pass, 4/4.
- Pre-refactor targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console/filters-dom.test.tsx`: pass, 3/3.
- Added targeted coverage: `query filter panel content applies facet and key kind draft changes`.
- Post-test-add targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console/filters-dom.test.tsx`: pass, 4/4.
- Post-refactor targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console/filters.test.ts src/components/workable/console/filters-dom.test.tsx`: pass, 8/8.
- `npm.cmd run lint`: pass.
- `.\node_modules\.bin\tsc.cmd --noEmit`: pass.
- `npm.cmd run test`: pass, 188/188. The run still emits non-failing React `act(...)` warnings from mounted virtual table and Radix Tooltip/Presence/Portal/Popper/DismissableLayer/Menu/FocusScope paths.
- `npm.cmd run build` outside sandbox with approval: pass. Routes generated: `/`, `/_not-found`, `/login`, `/icon.png`, `/api/auth/login`, `/api/auth/logout`, `/api/auth/entra/login`, `/api/auth/entra/callback`, `/api/auth/entra/workable-token`, `/api/workable/[...path]`, plus Proxy middleware.

Component refactor batch: overview throughput chart:

- Selected the Overview throughput chart/control area because the coverage map marked rendered chart modes, series toggles, and window controls as thin, and the next refactor moved those controls out of `overview-screen.tsx`.
- Added mounted `OverviewView` throughput coverage for completion chart rendering, `Completed` series toggle callback, `5m` window request options (`bucketSeconds: 5`, `windowSeconds: 300`), shadcn/Radix tab switch to Execution mode, execution chart rendering, and execution metrics.
- Extracted throughput chart composition, compact strip, legend/metric pills, chart math, axis/time/rate formatting, and throughput types/constants into `components/workable/console/overview-throughput.tsx`.
- Preserved the existing `overview-screen.tsx` helper export surface by re-exporting throughput helpers and types from `overview-throughput.tsx`.
- Reduced `overview-screen.tsx` from about 2664 lines to 1727 lines; `overview-throughput.tsx` has 991 lines.
- Pre-refactor targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console/overview-screen-dom.test.tsx`: pass, 5/5, with non-failing Radix Tooltip/Presence/Portal/Popper/DismissableLayer `act(...)` warnings.
- Post-refactor targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console/overview-screen.test.ts`: pass, 5/5.
- Post-refactor targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console/overview-screen-dom.test.tsx`: pass, 5/5, with non-failing Radix Tooltip/Presence/Portal/Popper/DismissableLayer `act(...)` warnings.
- `npm.cmd run test`: pass, 189/189. The run still emits non-failing React `act(...)` warnings from mounted Radix and virtual-table paths.
- `npm.cmd run lint`: pass.
- `.\node_modules\.bin\tsc.cmd --noEmit`: pass.
- `npm.cmd run build` outside sandbox with approval: pass. Routes generated: `/`, `/_not-found`, `/login`, `/icon.png`, `/api/auth/login`, `/api/auth/logout`, `/api/auth/entra/login`, `/api/auth/entra/callback`, `/api/auth/entra/workable-token`, `/api/workable/[...path]`, plus Proxy middleware.

Component refactor batch: detail configuration data:

- Selected a pure helper slice from `components/workable/console/detail-screens.tsx` because `detail-screens.test.ts` already covers configuration descriptor grouping, queue JSON parsing, cloning, sanitization, persistent-concurrency rules, worker reconfiguration request creation, and configuration diffing.
- No conditional detail-view gate was triggered: this did not refactor `DefinitionView`, `WorkerConsoleView`, `IterationConsoleView`, detail-screen mounted UI, action controls, logs, timelines, or configuration editor interaction behavior.
- Extracted configuration field-section helpers, schema/manual JSON parsing helpers, queue request cloning/sanitization/rule helpers, worker reconfiguration/diff helpers, and `defaultWorkConfiguration` into `components/workable/console/detail-configuration-data.ts`.
- Preserved the existing `detail-screens.tsx` helper/type export surface by re-exporting the moved helpers and types from `detail-configuration-data.ts`.
- Reduced `detail-screens.tsx` from 8760 lines to 8272 lines; `detail-configuration-data.ts` has 583 lines.
- Post-refactor targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console/detail-screens.test.ts`: pass, 9/9.
- Post-refactor targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console/detail-screens-dom.test.tsx`: pass, 4/4.
- `npm.cmd run lint`: pass.
- `.\node_modules\.bin\tsc.cmd --noEmit`: pass.
- `npm.cmd run test`: pass, 189/189. The run still emits non-failing React `act(...)` warnings from mounted Radix and virtual-table paths.
- `npm.cmd run build` outside sandbox with approval: pass. Routes generated: `/`, `/_not-found`, `/login`, `/icon.png`, `/api/auth/login`, `/api/auth/logout`, `/api/auth/entra/login`, `/api/auth/entra/callback`, `/api/auth/entra/workable-token`, `/api/workable/[...path]`, plus Proxy middleware.

Component refactor batch: Workable client boundary:

- Selected `src/lib/workable.ts` because the audit marks the Workable HTTP client boundary as Covered and non-gated; `src/lib/workable.test.ts` covers scoped requests, `x-workable-api-url`, in-flight query coalescing, auth-required redirect, hosted realtime token cache reuse, and hosted token error handling.
- No auth/proxy conditional gate was triggered: this did not change route guards, route handlers, proxy behavior, permission wrappers, or app-shell auth flow. Existing behavior remains exported from `src/lib/workable.ts`.
- Extracted runtime client behavior into `src/lib/workable-client.ts`: `WorkableApiError`, `workableFetch`, `workableQueryFetch`, Workable realtime URL creation, hosted realtime access-token caching, `formatDateTime`, `stateTone`, and `safeJsonParse`.
- Preserved the existing `src/lib/workable.ts` export surface by re-exporting the moved client helpers from `workable-client.ts`; consumers can keep importing from `@/lib/workable`.
- Reduced `workable.ts` from about 1457 lines to 1111 lines; `workable-client.ts` has 357 lines.
- Pre-refactor targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/lib/workable.test.ts`: pass, 5/5.
- Post-refactor targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/lib/workable.test.ts`: pass, 5/5.
- `npm.cmd run lint`: pass.
- `.\node_modules\.bin\tsc.cmd --noEmit`: pass.
- `npm.cmd run test`: pass, 189/189. The run still emits non-failing React `act(...)` warnings from mounted Radix and virtual-table paths.
- `npm.cmd run build` outside sandbox with approval: pass. Routes generated: `/`, `/_not-found`, `/login`, `/icon.png`, `/api/auth/login`, `/api/auth/logout`, `/api/auth/entra/login`, `/api/auth/entra/callback`, `/api/auth/entra/workable-token`, `/api/workable/[...path]`, plus Proxy middleware.

Component refactor batch: overview iterations:

- Selected the Overview iteration status/key/recent-list area because the coverage map marked iteration links and filters thin, and the next refactor moved those controls out of `overview-screen.tsx`.
- Added mounted `OverviewView` iteration coverage for status filter callbacks, key-type filter callbacks, and opening a recent failed-iteration worker row.
- Fixed the new test fixture before refactoring by removing a duplicated `failedIterations` component key and adding explicit `WorkOverviewIteration`/`WorkIterationKeyTypeFacet` fixtures.
- Extracted iteration status strips, compact iteration strip, key-type pills/tooltips, recent iteration panel/table, duration display, and `formatIterationCount` into `components/workable/console/overview-iterations.tsx`.
- Preserved the existing `overview-screen.tsx` helper export surface by re-exporting `formatIterationCount` from `overview-iterations.tsx`.
- Reduced `overview-screen.tsx` from about 1727 lines after the throughput extraction to 1281 lines; `overview-iterations.tsx` has 508 lines.
- Initial targeted command after adding the test: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console/overview-screen-dom.test.tsx`: failed, 5/6, because the new fixture referenced missing iteration helper data. Classified as incomplete test fixture, fixed before refactor.
- Pre-refactor targeted rerun: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console/overview-screen-dom.test.tsx`: pass, 6/6, with non-failing Radix `act(...)` warnings.
- Post-refactor targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console/overview-screen.test.ts src/components/workable/console/overview-screen-dom.test.tsx`: pass, 11/11, with non-failing Radix/virtual-table `act(...)` warnings.
- `npm.cmd run lint`: pass. An intermediate run reported two unused-symbol warnings from the move; both were removed and lint reran clean.
- `.\node_modules\.bin\tsc.cmd --noEmit`: pass.
- `npm.cmd run test`: pass, 190/190. The run still emits non-failing React `act(...)` warnings from mounted Radix and virtual-table paths.
- `npm.cmd run build` outside sandbox with approval: pass. Routes generated: `/`, `/_not-found`, `/login`, `/icon.png`, `/api/auth/login`, `/api/auth/logout`, `/api/auth/entra/login`, `/api/auth/entra/callback`, `/api/auth/entra/workable-token`, `/api/workable/[...path]`, plus Proxy middleware.

Implementation batch after strict audit:

- Added route-handler tests for `/api/auth/login` and `/api/auth/logout`: CSRF/same-origin rejection, JSON login success, form login success, bad credentials, and logout cookie clearing.
- Added `workableFetch`, `workableQueryFetch`, and hosted realtime access-token tests for scoped paths, `x-workable-api-url`, in-flight coalescing, auth-required redirect, token cache reuse, and token error handling.
- Added proxy tests for public admin routes, unauthenticated page redirect with `next`, unauthenticated API JSON failures, and authenticated Basic pass-through.
- Added mounted `WorkableConsole` tests for the empty `/` console state, Add server entrypoint, and Sign out posting `/api/auth/logout` plus router replacement/refresh.
- Added `ServerDialog` DOM tests for discovery success, default system selection, save payload, disabled save when all systems are unchecked, and authorization failure messaging.
- Added `SchemaForm` DOM interaction tests for boolean, number, URL, array add/edit/remove, dictionary add/rename/edit/remove, shadcn select enum selection, and preset default application.
- Fixed a real client boundary bug where failed hosted realtime access-token requests could produce an unhandled rejection from cache cleanup.
- Improved test harness fidelity for client components: baseline jsdom for Radix module import timing, `next/server` resolution, `matchMedia`, `NodeFilter`, `DocumentFragment`, pointer events, `aria-label` lookup, better text matching, and `spinbutton` role detection.
- Added accessible remove labels for schema array/dictionary item buttons.
- `npm.cmd run test`: pass, 162/162.
- `npm.cmd run lint`: pass.
- `.\node_modules\.bin\tsc.cmd --noEmit`: pass.
- `npm.cmd run build` inside sandbox: failed after successful compile during Next worker startup with `spawn EPERM`; classified as environment/sandbox issue.
- `npm.cmd run build` outside sandbox with approval: pass. Routes generated: `/`, `/_not-found`, `/login`, `/icon.png`, `/api/auth/login`, `/api/auth/logout`, `/api/auth/entra/login`, `/api/auth/entra/callback`, `/api/auth/entra/workable-token`, `/api/workable/[...path]`, plus Proxy middleware.

Authenticated console safety-net batch:

- Added mounted `WorkableConsole` coverage for persisted authenticated host restoration from `localStorage`, host revalidation, active system selection, overview data loading, and navigation from Overview to Workers and Iterations empty query states.
- Added mounted restricted-permission coverage proving the overview shows the no-work-access message, hides work-query UI, and requests only the `system` overview component when the user cannot read work.
- Tightened the jsdom harness for richer client components with per-render window setup, async `waitFor`, and a `ResizeObserver` fallback used by the overview panel layout.
- Targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console-dom.test.tsx`: pass, 4/4.
- `npm.cmd run test`: pass, 164/164.
- `npm.cmd run lint`: pass.
- `.\node_modules\.bin\tsc.cmd --noEmit`: pass.
- `npm.cmd run build` inside sandbox: failed after successful compile during Next worker startup with `spawn EPERM`; classified as environment/sandbox issue.
- `npm.cmd run build` outside sandbox with approval: pass. Routes generated: `/`, `/_not-found`, `/login`, `/icon.png`, `/api/auth/login`, `/api/auth/logout`, `/api/auth/entra/login`, `/api/auth/entra/callback`, `/api/auth/entra/workable-token`, `/api/workable/[...path]`, plus Proxy middleware.

Workers and Iterations query-view burn-down batch:

- Added mounted `WorkersView` coverage for filtered request shape, populated rows, total count, infinite append via scroll, row open behavior, shadcn/Radix action menu interaction, and Start & View mutation behavior.
- Added mounted `WorkersView` error-state coverage for failed query responses and empty-state fallback.
- Added mounted `IterationsView` coverage for filtered request shape, populated rows, total count, infinite append via scroll, and opening a final iteration row.
- Added mounted `IterationsView` error-state coverage for failed query responses and empty-state fallback.
- Tightened the jsdom harness for virtualized query tables with `IntersectionObserver`, richer `ResizeObserver` entries, element `scrollTo`, and a scroll helper.
- Targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console/query-screens-dom.test.tsx`: pass, 4/4.
- `npm.cmd run test`: pass, 168/168.

QueueDialog burn-down batch:

- Added mounted `QueueDialog` coverage for input-schema defaults, editing the generated input form, Queue submit payload, and close callback behavior.
- Added manual JSON validation coverage that prevents posting invalid JSON and shows the Queue failed banner.
- Added Watch submit coverage for manual `subjectId` and `concurrencyKey` data, worker-id callback behavior, and close callback behavior.
- Added server failure coverage that keeps the dialog open, shows the Workable error message, and avoids worker navigation.
- Tightened the jsdom harness with mouse down/up helpers for Radix Tabs interactions.
- Targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console/detail-screens-dom.test.tsx`: pass, 4/4.
- `npm.cmd run test`: pass, 172/172.

Overview and permission burn-down batch:

- Added mounted `OverviewView` coverage for deferred loading state, component-level error rendering, request-level error rendering, `onConnectionError`, `onReady`, `onStateLoaded`, panel next-view controls, and hide-panel controls.
- Added mounted `OverviewView` failed-worker mutation coverage for opening the shadcn/Radix action menu, posting the Start action payload, refreshing the failed-worker slice, and hiding failed-worker action controls without operate access.
- Tightened role enforcement in the app by hiding sidebar lifecycle controls unless `canControlSystem` is true, hiding Catalog queue shortcuts unless the user has operate access, and hiding Workers table mutation controls for read-only users.
- Added mounted navigation coverage for restricted systems proving lifecycle and Catalog queue controls are hidden, plus positive coverage proving those controls remain available for matching access.
- Added mounted console and `WorkersView` coverage proving read-only work access can load Workers data without exposing mutation action controls.
- Targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console/overview-screen-dom.test.tsx`: pass, 4/4.
- Targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console/navigation.test.tsx`: pass, 12/12.
- Targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console-dom.test.tsx`: pass, 5/5.
- Targeted command: `node --import ./test/tsx-register.mjs --test --test-isolation=none src/components/workable/console/query-screens-dom.test.tsx`: pass, 5/5.
- `npm.cmd run test`: pass, 181/181. The run still emits non-failing React `act(...)` warnings from Radix Tooltip/Presence/Portal/Popper/DismissableLayer cleanup around mounted menu/tooltip tests.
- `npm.cmd run lint`: pass.
- `.\node_modules\.bin\tsc.cmd --noEmit`: pass.
- `npm.cmd run build` inside sandbox: failed after successful compile during Next worker startup with `spawn EPERM`; classified as environment/sandbox issue.
- `npm.cmd run build` outside sandbox with approval: pass. Routes generated: `/`, `/_not-found`, `/login`, `/icon.png`, `/api/auth/login`, `/api/auth/logout`, `/api/auth/entra/login`, `/api/auth/entra/callback`, `/api/auth/entra/workable-token`, `/api/workable/[...path]`, plus Proxy middleware.

## Strict Coverage Criteria

This follow-up pass does not count generic "renders" checks, Tailwind class checks, or unrelated helper tests as meaningful feature coverage. Pure helper tests count only when the area itself is a pure helper boundary. Large user-facing features need route/component, DOM, or e2e coverage that exercises user-visible behavior, data states, mutations, permissions, and interactions.

Status legend:

- Covered: meaningful behavior coverage exists for the route/feature at the right level for current risk.
- Partially Covered: useful tests exist, but they miss important user-visible behavior or mostly cover helpers.
- Missing: no meaningful route/feature coverage; helper-only or class-only tests are not enough.
- Not Worth Testing: static/minimal plumbing where build/typecheck is enough unless logic is added.

## Complete Route And Page Coverage Map

| Route/page | Source | Status | Meaningful coverage | Highest-value tests to add |
| --- | --- | --- | --- | --- |
| Root layout | `src/app/layout.tsx` | Not Worth Testing | Build/typecheck verify the static wrapper. No route logic beyond metadata, body classes, and `TooltipProvider`. | None unless layout gains auth/data logic. |
| `/` | `src/app/page.tsx` -> `WorkableConsole` | Partially Covered | Mounted console coverage now proves the empty server state, Add server entrypoint, Sign out flow, authenticated persisted-host restore, host revalidation, overview success data, Workers/Iterations empty-state navigation, Catalog navigation, Catalog refresh, back/forward history, persisted view state, no-work-access overview, and read-only Workers mutation-control hiding. | Add e2e/integration coverage for unauthenticated redirect in a browser, populated/error query views through the shell, and detail/definition transitions. |
| `/login` | `src/app/login/page.tsx`, `login-form.tsx` | Covered | Page-level `searchParams.next/error/reason` normalization; Basic/Entra render states; Basic submit success, server validation failure, and request failure. | Optional e2e that proves browser-required fields, real route handler wiring, and post-login redirect. |
| Global error boundary | `src/app/error.tsx` | Covered | DOM test verifies user-facing failure copy, error message display, and retry click. | Optional browser smoke if error boundary behavior changes. |
| `/_not-found` | framework default | Not Worth Testing | No custom `not-found.tsx`. | None unless custom UI is added. |
| `/icon.png` | `src/app/icon.png` | Not Worth Testing | Static asset included in Next build. | None. |
| `POST /api/auth/login` | `src/app/api/auth/login/route.ts` | Covered | Direct route tests cover unsafe Origin rejection, JSON credentials, form credentials, bad credentials, JSON response, and session cookie creation. Login form tests cover the client submit behavior. | Add only if provider behavior changes, such as Entra-only login route interactions or additional credential body formats. |
| `POST /api/auth/logout` | `src/app/api/auth/logout/route.ts` | Covered | Direct route tests cover unsafe Origin rejection and expired admin-session cookie output. Mounted console test covers Sign out posting `/api/auth/logout`, replacing to `/login`, and refreshing. | Add browser/e2e coverage if the auth flow moves beyond the current route/helper boundary. |
| `GET /api/auth/entra/login` | `src/app/api/auth/entra/login/route.ts` | Partially Covered | `createEntraAuthorizationResponse` tests cover redirect URL, state/nonce/PKCE cookies, scopes, and multi-target scope behavior. The route wrapper is not directly tested. | Route-handler smoke for request URL to response status/location/cookies if auth route code changes. |
| `GET /api/auth/entra/callback` | `src/app/api/auth/entra/callback/route.ts` | Missing | Entra authorization start is covered, but callback completion/token/JWKS/cookie behavior is not covered by current tests. | Callback tests for bad state/nonce, token exchange failure, disallowed user, successful session cookie, target-token cookies, and redirect next path. |
| `GET /api/auth/entra/workable-token` | `src/app/api/auth/entra/workable-token/route.ts` | Partially Covered | Helper tests cover no binding, token forwarding, refresh, host binding, multiple APIs, and oversized token state. The route wrapper and auth failure branch are not directly tested. | Route-handler tests for unauthenticated failure, successful token JSON, and session renewal cookie append. |
| `GET/POST /api/workable/[...path]` | `src/app/api/workable/[...path]/route.ts` | Partially Covered | `proxyWorkableRequest` has meaningful tests for target allow-listing, auth failures, token forwarding/refresh, TLS guidance, and unsafe metadata. The App Router wrapper and path param handling are not directly tested. | Thin route tests for `params.path` forwarding, GET/POST parity, query preservation, and proxy error passthrough. |
| Request proxy/auth gate | `src/proxy.ts` | Partially Covered | Direct proxy tests cover public admin routes, unauthenticated page redirect with `next`, unauthenticated API JSON failures, and authenticated Basic pass-through. Session-renewal cookie append and matcher exclusions are not directly tested. | Add direct proxy tests for near-expiry session renewal cookie append and matcher/static-asset exclusions before auth/proxy refactors. |

## Complete Feature And Component Coverage Map

| Feature/component area | Source | Status | Meaningful coverage | Highest-value tests to add |
| --- | --- | --- | --- | --- |
| Auth shell and logo | `components/layout/auth-shell.tsx`, `components/shared/workable-logo.tsx` | Partially Covered | Static markup checks exist, but they mostly assert classes and image path. | Fold into `/login` visual/e2e coverage; keep only accessibility/image-alt assertions if these components are refactored. |
| Login form/page | `app/login/*` | Covered | Meaningful tests cover provider-specific UI, safe next-path normalization, errors, submit success/failure, and router calls. | Optional e2e with real route handlers. |
| Main console route orchestration | `components/workable/console.tsx` | Partially Covered | Mounted tests cover empty server state, Add server entrypoint, Sign out mutation/router behavior, persisted authenticated host hydration, active system selection, overview data load, Workers/Iterations empty navigation, Catalog navigation, Catalog refresh, back/forward history, persisted view state, no-work-access overview, and read-only Workers mutation-control hiding. Helper tests cover storage and notifications. | Add mounted/e2e coverage for populated/error query data through the shell and details/definition transitions. |
| Console persistence and navigation history | `components/workable/console.tsx` helper exports | Partially Covered | Pure storage normalization and navigation-entry equality are tested. Mounted tests now prove seeded `localStorage` restore of host/system/view, back/forward buttons, state saving after navigation, and preserved active view. Scroll restoration remains untested. | Add scroll restoration only if that behavior is refactored. |
| Sidebar server explorer and header breadcrumbs | `components/workable/console/navigation.tsx` | Partially Covered | Render checks cover expanded tree/header text; helper tests cover badges, names, lifecycle labels, and reconciliation. Mounted tests now prove lifecycle and Catalog queue controls are hidden for restricted access and visible/clickable for matching access. Expand/collapse, edit/remove, and breadcrumb callbacks remain thin. | DOM tests for expanding/collapsing hosts/systems, opening views, opening definition scopes, edit/remove buttons, lifecycle loading state, and breadcrumb/back/forward callbacks. |
| Add/edit server dialog and host discovery | `ServerDialog`, `discoverHost` | Covered | DOM tests now cover Add dialog URL/name entry, discovery success, target API header, default selected systems, save payload, disabled save when all systems are unchecked, authorization failure messaging, edit-mode discovery refresh, matched-system preservation, missing-system removal, cancel/no-save behavior, loading-disabled actions, and retry after failed discovery. | Add only if host discovery UX expands, such as destructive removal through the main shell or additional per-system editing. |
| Empty/no-access server state | `EmptyServerState`, main console empty branch | Partially Covered | Static empty state text and the mounted no-host branch are covered, including the Add server entrypoint. The saved-host/no-connect-access variant is not mounted. | Main console test for saved-host/no-connect-access variant with Add server action. |
| Delete host/system and stop system dialogs | `DeleteTargetDialog`, `StopSystemDialog` | Partially Covered | Text helper/render paths are covered. Confirm/cancel behavior through `WorkableConsole` and actual removal/lifecycle calls are not covered. | DOM tests for confirm/cancel callbacks and integration test proving host/system removal updates sidebar/localStorage; stop calls lifecycle endpoint and reports errors. |
| Sign-out flow | `WorkableConsole.signOut` | Covered | Mounted console test clicks Sign out, asserts POST `/api/auth/logout`, router replace `/login`, and refresh. Logout route tests cover cookie clearing. | Add pending disabled/loading and failure-still-navigates branches if sign-out UX changes. |
| Header capabilities and realtime/refresh controls | `header-capabilities.tsx`, `header-capability-controls.tsx` | Covered | Provider recency/unregister behavior, hiding, accessible labels, refresh click, and single realtime action click are covered. | Add menu-open coverage only if multi-item header menus are refactored. |
| Console layout/panel primitives | `console-primitives.tsx`, `panel-shell.tsx`, `panel-aggregate-frame.tsx`, `panel-visibility-*` | Partially Covered | There are many class-heavy markup tests plus helper state tests. User-visible menu/visibility/settings behavior is only partially covered. | Replace class assertions with role/name/state tests for panel options menu, view-state cycling, close/hide/show panels, infinite footer/load-more sentinel. |
| Reusable console empty/form/loading/icon controls | `empty-state.tsx`, `form-controls.tsx`, `stacked-skeleton.tsx`, `toolbar-icon-button.tsx` | Partially Covered | Static/server-render tests cover labels, readonly values, empty states, skeleton row count, icon button labels, and shared control markup. They mostly prove primitive markup, not composed workflows. | Keep direct tests light; cover through dialogs, schema/config forms, query panels, and toolbar actions that use these controls. |
| Console formatting, catalog path, and component-result helpers | `console-format.ts`, `catalog-path.ts`, `catalog-browser-data.ts`, `component-results.ts` | Covered | These are pure helper boundaries with targeted tests for time spans, diagnostic/queue age formatting, catalog path/scope normalization, request creation, result extraction, and failure descriptions. | No extra direct tests unless helper behavior changes; composed views should still be tested separately. |
| Live relative time display | `live-relative-time.tsx` | Partially Covered | Formatter edge cases and server-rendered output are tested. Client interval ticking and cleanup are not. | Add a fake-timer DOM test only if live-updating time display is refactored. |
| Overview route/view data shell | `OverviewView` | Covered | Mounted tests now cover overview success data through the console; no-work-access system-only requests; deferred loading controls; request-level errors; component-level errors; `onReady`; `onStateLoaded`; panel next-view and hide-panel controls; failed-worker Start mutation, slice refresh, no-operate action hiding, throughput mode/window/series controls, and execution chart rendering. Pure helpers cover panel shape, throughput math, worker actions, realtime messages, and empty metrics. | Add only if Overview behavior expands, such as realtime fallback details, explicit refresh-disabled assertions, additional chart empty-state behavior, or catalog filter interactions. |
| Overview worker/iteration summary panels | `OverviewView` child panels, `overview-iterations.tsx`, `overview-workers.tsx` | Covered | Helper tests cover row actions, durations, counts, chart series, and labels. Mounted Overview tests cover failed-worker action menu behavior, action refresh, panel hide/shape controls, iteration status filters, key-type filters, and opening a recent failed-iteration worker row. Worker and iteration child UI now live in focused modules while `overview-screen.tsx` orchestrates the data shell. | Add completed-iteration row coverage and additional compact/detailed shape combinations only if those specific branches are refactored. |
| Overview throughput chart | `overview-throughput.tsx` | Partially Covered | Chart path/math/metrics helpers are tested. Mounted Overview coverage now proves completion chart rendering, series toggle callbacks, window-control request options, shadcn/Radix Execution tab switching, execution chart rendering, and execution metrics. Empty chart state, initial hidden-series rendering, and detailed axis-label assertions remain thin. | Add empty-data and initial hidden-series DOM coverage only if chart rendering changes again. |
| Overview catalog filter | `OverviewCatalogFilter`, `CatalogFilterPanel` | Covered | Scope normalization and active count helpers are tested. DOM coverage opens the filter, loads catalog levels, drills into a category, selects a definition, applies the scope, and clears an active scope. | Add loading/empty/error catalog variants only if the filter UX changes. |
| Definitions catalog view | `DefinitionsView`, `DefinitionCatalogBrowser` | Partially Covered | `CatalogBrowser` static loading/root/empty/category/definition branches are covered. A mounted shell test now proves the Catalog route loads and displays definitions. `DefinitionsView` search/auto-open/queue/open-worker flows are not mounted directly. | Component/integration test for search, auto-open scoped definition, open definition, and open queue dialog before refactoring `DefinitionsView`. |
| Definition detail/configuration | `DefinitionView`, `detail-configuration-data.ts` | Missing | No meaningful mounted `DefinitionView` coverage. Configuration descriptor/request helpers are tested and now live in `detail-configuration-data.ts`, but `DefinitionView` loading, metadata, reconfigure, and queue entrypoint behavior are not mounted. | Mounted test for definition loading/error, metadata, default configuration tabs, reconfigure submit success/failure, queue action entrypoint. |
| Queue dialog | `QueueDialog` | Covered | Mounted tests cover input-schema defaults, generated input submit payloads, manual JSON validation, manual subject/concurrency payload, Watch worker navigation, close callbacks, and server failure staying open with error messaging. | Add only if queue configuration tabs, persistent-concurrency locking, or wait-for-completion UX changes. |
| Schema form | `schema-form.tsx`, `schema-form-data.ts` | Covered | Parsing/default-value helpers, server-rendered branches, and DOM interactions now cover shadcn enum select, boolean toggle, number edit, URL edit, array add/edit/remove, dictionary add/rename/edit/remove, and preset apply. | Add date/date-time and deeply nested path interaction tests only if those controls are refactored. |
| Query filters | `filters.tsx`, `filter-panels.tsx`, `filter-data.ts` | Partially Covered | Active count/descriptions/helpers plus DOM apply/clear for text filters are covered. DOM coverage now also proves facet toggles and shadcn key-kind selection in `QueryFilterPanelContent`, and Overview catalog category/definition selection. Query-filter catalog selection through the shared panel and `QueryFilterPopover` trigger/close behavior remain thin. | DOM tests for query-filter catalog category/definition selection through `QueryFilterPanelContent`/`QueryFilterPopover`, popover trigger/close behavior, and apply disabled state after catalog/facet reversions. |
| Workers query view | `WorkersView`, `VirtualWorkerTable` | Covered | Mounted tests now prove filtered request shape, populated rows, total count, infinite append via scroll, row open, shadcn/Radix action menu interaction, Start & View mutation, read-only action hiding, empty state, and query error state. Helper tests cover not-found purge detection, row merge, action options, and status/totals placeholders. | Add only if worker table behavior expands, such as dedicated refresh UI assertions, all action variants, or 404 purge fallback through the mounted table. |
| Iterations query view | `IterationsView`, `VirtualIterationTable` | Covered | Mounted tests now prove filtered request shape, populated rows, total count, infinite append via scroll, final-row open behavior, empty state, and query error state. Helper tests cover row merge and status/totals placeholders. | Add only if iteration table behavior expands, such as dedicated refresh UI assertions or non-final row messaging. |
| Worker detail route/view | `WorkerConsoleView` | Partially Covered | Many pure helpers cover configuration diffs, timelines, logs, durations, hidden panels, merge/sort/cap, and action option rules. No mounted worker detail test covers panels, data load, realtime updates, actions, or save flows. | Component/integration test for worker loading/error, controls panel, Start/Pause/Cancel/Push/Purge actions, latest output, panel visibility/focus, realtime update merge, system notification reporting. |
| Worker configuration editor | `WorkerConsoleView`, `detail-configuration-data.ts` | Partially Covered | Request/diff/clone/configuration-rule helpers are tested and now isolated in `detail-configuration-data.ts`. UI editing, validation, reset-to-defaults, save, and success/error banners are not. | DOM test for editing fields/tabs, invalid JSON, reset defaults, save reconfiguration success/failure, persistent-concurrency locked fields. |
| Worker logs/timeline/duration panels | `WorkerConsoleView` child panels | Partially Covered | Sorting/filtering/capping/timeline visibility/duration helpers are tested. User-visible filters, panel actions, focus mode, copy/expand behavior, and load-more behavior are not mounted. | DOM tests for log level filters, timeline category filters, asc/desc sort, focus buttons, empty states, loading more. |
| Iteration detail route/view | `IterationConsoleView` | Partially Covered | Iteration status, message/log helper functions are indirectly covered; no mounted iteration detail behavior. | Component test for summary, messages, input/output, logs, loading/error/empty, breadcrumb definition link, message filters. |
| Diagnostics tray and notifications | `SystemNotificationTray`, diagnostics summary helpers | Partially Covered | Diagnostics summary primitives and notification helper branches are covered. Tray popover interactions, acknowledgement, expand/collapse, active target filtering, and cache invalidation integration are not. | DOM/integration test for tray open, expanded diagnostics, acknowledge rejected work, alert transitions, active-system filtering. |
| Feedback/error banners | `feedback-panel.tsx` | Partially Covered | Message filtering/deduping and banner render branches are covered. Dismiss interaction and integration with failing actions are thin. | DOM test for dismiss callbacks and action failure surfacing in overview/worker/server flows. |
| Realtime connection pool and hooks | `realtime-view-pool.ts`, `page-realtime-view.tsx`, `realtime.ts` | Partially Covered | Shared connection pooling, handler fan-out, state listeners, and provider recency have useful tests. Actual SignalR integration is mocked/indirect; one remount-gap test uses real timing. | Replace timing sleep with controllable timers; add component test proving active view descriptor drives subscription and access-token factory errors surface. |
| Realtime payload/events window | `RealtimePayloadWindow`, `RealtimeEventsTabPanel` | Partially Covered | Payload message/text/JSON helpers, stats menu, closed-state render, metrics/position helpers are covered. DOM coverage now proves open/close, Payloads/Events tabs, search, max messages, clear, pin/unpin state, and disabled pinned menu after clear. Dock side, resize/drag, JSON inspector toggles, and event filter details remain untested. | Add dock/resize/JSON-inspector/event-filter coverage only if those controls are refactored. |
| Workable HTTP client helpers | `lib/workable.ts`, `lib/workable-client.ts` | Covered | Direct tests cover scoped paths, `x-workable-api-url`, default JSON header, query request coalescing, API error parsing, auth-required redirect to `/login`, hosted realtime token fetch, token cache reuse, and token failure messaging. Runtime client behavior now lives in `workable-client.ts` and is re-exported from `workable.ts`. A failed-token cleanup bug was fixed. | Add non-JSON success/error body coverage if response parsing is refactored. |
| Workable proxy/security server helpers | `lib/admin-security*`, `lib/workable-proxy.ts` | Covered | Meaningful security/proxy tests cover default-deny auth, sessions, Basic/Entra provider selection, Entra authorization start, target token forwarding/refresh, allow-listing, CSRF, TLS guidance, and hostile realtime metadata. | Add Entra callback completion tests; route wrapper tests if handler logic changes. |
| shadcn/ui wrappers | `components/ui/*` | Not Worth Testing | Wrappers are mostly generated primitives with styling. Testing each directly would duplicate Radix/shadcn. | Test through composed app features. Add direct tests only if custom behavior is added. |
| Responsive/mobile behavior | `use-mobile.ts`, `sidebar.tsx`, layout classes | Missing | No mobile viewport/e2e coverage. | Playwright mobile smoke for sidebar open/close, nav visibility, dialog sizing, table/panel usability. Fix before layout/sidebar refactors. |
| Static assets/global CSS | `public`, `globals.css`, icon/logo assets | Not Worth Testing | Build/static asset handling is enough. Logo alt/path has a small smoke test. | Visual/e2e only if branding/layout changes. |

## Form And Validation Coverage

Covered:

- Basic login renders username/password fields, required metadata, error state.
- Entra login renders Microsoft sign-in link with encoded `next`.
- Login page rejects unsafe `next` values, uses the first repeated search parameter value, preserves safe paths, and normalizes unauthorized/session-expired messaging.
- Basic login now submits JSON credentials, redirects/refreshes on success, displays server validation errors without navigation, and shows a recoverable message on request failure.
- Schema form renders empty, object, enum, boolean, numeric, formatted string, array, dictionary, path field, preset, and compact JSON branches.
- Schema form now exercises user interactions for enum select, boolean, number, URL, array add/edit/remove, dictionary add/rename/edit/remove, and preset default application.
- Server dialog now covers add-host discovery success, selected systems, save payload, unchecked-system disabled state, authorization failure messaging, edit-mode reconciliation, cancel/no-save, loading-disabled actions, and retry after failure.
- Query filter panel applies and clears draft filters in jsdom.
- Realtime message controls dispatch search text and normalized limits.

Missing high-value form tests:

- Browser-native required-field behavior is not meaningfully covered by the custom submit helper.
- `SchemaForm` date/date-time and deeply nested path interaction tests remain optional unless those controls are refactored.
- Configuration dialogs in detail screens at the DOM level. QueueDialog has mounted coverage for schema defaults, manual JSON validation, Queue, Watch, success, and server failure.

## Data Loading, Error, And Empty State Coverage

Covered:

- Catalog browser loading/root/empty/category/definition states.
- Diagnostics summary collapsed/expanded/loading/empty/warning states.
- Feedback/error panels for filtered/deduped/dismissible messages.
- Query table status, totals, placeholders.
- Overview helper behavior for empty throughput and missing metrics.
- Mounted OverviewView loading, request error, component error, panel controls, failed-worker action refresh, and no-operate action hiding.
- Global error boundary retry UI added.
- Mounted main console empty-server state.
- Mounted main console persisted-host restore, overview success data, Workers empty state, Iterations empty state, no-work-access overview state, and read-only Workers data state.
- Mounted main console Catalog navigation, Catalog refresh, back/forward history, and persisted active-view state.
- Server dialog discovery success, authorization failure, loading-disabled, retry, and edit-reconciliation states.

Missing:

- Populated/error query states through the main console route beyond the read-only Workers data smoke.
- Route-level async Server Component states should be covered with e2e/integration rather than forced into component tests.

## Mutation And Action Coverage

Covered:

- Login client submit success/failure added.
- Logout route and mounted Sign out flow added.
- Server add/discover/save interaction added for the add-host path, plus edit-mode reconciliation, cancel/no-save, and retry after failed discovery.
- QueueDialog mutation flow now covers generated input submit, manual JSON validation, manual subject/concurrency payload, Watch navigation, and server failure.
- WorkersView action coverage now covers Start & View mutation success and read-only action hiding.
- Overview failed-worker action coverage now covers Start mutation success, slice refresh, and no-operate action hiding.
- Security/proxy library tests cover authentication, session renewal/expiry, CSRF origin validation, Entra authorization, token forwarding/refresh, allow-listing, unsafe realtime metadata, and proxy error preservation.
- Helper/mounted coverage for worker actions, lifecycle action labels, sidebar lifecycle gating, Catalog queue gating, queue request creation, worker reconfiguration requests, and filter apply/clear.

Missing:

- Server remove interactions.
- Full Start/Stop/Pause/Cancel/Push/Purge UI action matrix and error states beyond the covered Start paths.
- Realtime payload dock/resize/JSON-inspector interactions at DOM level.

## Permission And Role-Specific UI Coverage

Covered:

- Access badge helper behavior (`Connect`, `Work admin`, diagnostics, control system, read/operate definitions, no work access).
- Diagnostics alert target creation filters by `canViewDiagnostics`.
- Admin authentication/authorization behavior is strongly covered in `admin-security.test.ts`.
- Overview helper detects no readable work access.
- Mounted console overview test verifies restricted users see the no-work-access message, do not see work-query UI, and only request system data.
- Mounted console read-only Workers test verifies Workers data can load without exposing mutation action controls.
- Mounted navigation tests verify lifecycle controls are hidden without `canControlSystem`, Catalog queue shortcuts are hidden without operate access, and both controls are available for matching access.
- Mounted OverviewView tests verify failed-worker action controls are hidden without operate access.

Missing:

- Mounted UI assertions for detail-screen reconfigure controls and full diagnostics tray interactions under restricted access.
- No e2e flow for Basic vs Entra auth UI beyond form rendering.

## shadcn/ui Interaction Coverage

Covered through composed components:

- Alert/Dialog/AlertDialog text paths.
- Popover/menu-trigger markup and some button clicks.
- Dropdown menu open/select behavior in WorkersView and Overview failed-worker action menus.
- Sidebar tree/header rendered branches.
- Sidebar Catalog queue shortcut gating and lifecycle button click behavior.
- Select combobox render in `SchemaForm`.
- Select combobox open/select interaction in `SchemaForm`.
- Overview throughput shadcn/Radix tabs and control buttons are covered through mode switching, window selection, and series toggles.
- Tabs/table/card/button/input/textarea/badge primitives are exercised indirectly.

Missing:

- Actual Radix/shadcn open/select/close keyboard and pointer behavior for dialogs, selects, popovers, dropdown menus, tabs, command menus, and sidebar sheet/mobile behavior.
- These should be e2e or Testing Library-style interaction tests, not raw snapshot/class assertions.

## Existing Test Classification

Keep:

- `src/lib/admin-security.test.ts`: meaningful security/proxy/session behavior.
- Pure helper tests for catalog paths, component results, console formatting, detail screens, overview screen, filters, realtime payload, realtime view pool, panel visibility state, and console storage/diagnostic helpers.
- DOM interaction tests in `filters-dom.test.tsx`, `header-capability-controls.test.tsx`, `header-capabilities.test.tsx`, `page-realtime-view.test.tsx`, and `shared-components.test.tsx`.
- Added login submit tests and error boundary retry test.
- Added auth route, proxy, Workable HTTP client, mounted console, ServerDialog, and SchemaForm interaction tests.
- Added authenticated mounted console tests for persisted host restore, overview data, Workers/Iterations empty states, and no-work-access permission enforcement.
- Added mounted Workers/Iterations query-view, QueueDialog, OverviewView data/action, and permission-gating tests for the High burn-down queue.

Fix:

- Markup-heavy tests in `auth-shell`, `workable-logo`, `console-primitives`, `panel-*`, `shared-components`, `diagnostics-summary`, `feedback-panel`, `navigation`, and `catalog-browser` should be preserved for now but gradually shifted from exact class strings to accessible names, roles, states, and user-visible behavior.
- `login-form.test.tsx` was weak static coverage; partially fixed by adding submit success/failure.
- `schema-form.test.tsx` now has interaction coverage; add only focused cases for date/date-time or nested path behavior if those controls change.
- `realtime-view-pool.test.ts` uses a real sleep for remount-gap behavior; useful, but brittle under load.

Merge:

- No safe merge candidates found. Some omnibus helper tests are broad, but they cover dense transformation logic without clear duplicate scenarios.

Delete:

- No tests should be deleted in this pass. The brittle markup tests still encode layout/accessibility expectations and should be replaced only when better behavior tests exist.

Investigate:

- Whether frontend validation belongs in CI. Current GitHub Actions omit the Next.js app entirely.
- Whether to adopt React Testing Library/Vitest for future component tests. Do not add until the project decides; current harness is adequate for small batches but weak for accessible queries and async UI.
- Whether to add Playwright for `/login` and main console flows. This is the best fit for Next async route/page behavior and Radix interactions.

## Top 10 Frontend Test Gaps By Refactor Risk

| Rank | Gap | Status | Why it matters for refactoring | Fix before component refactor? | Highest-value tests |
| --- | --- | --- | --- | --- | --- |
| 1 | Main `/` console route mounted flow | Partially Covered | Empty state, Add server entrypoint, Sign out, persisted host restore, overview data, Workers empty state, Iterations empty state, Catalog navigation, Catalog refresh, back/forward history, persisted view state, no-work-access overview, and read-only Workers mutation hiding are now covered. Refactors can still break populated/error tables through the shell and detail transitions. | Largely reduced; finish remaining branches only if shell-to-detail or populated route transitions are refactored | Mounted/e2e coverage for populated/error data through the shell, details/definition transitions, and browser-level unauthenticated redirect. |
| 2 | Add/edit server discovery dialog | Covered | Add discovery success, save payload, unchecked disabled state, authorization failure, edit discovery refresh, cancel/no-save, loading-disabled actions, retry, and saved-system reconciliation are covered. | No, unless dialog behavior expands | Optional DOM tests for additional per-system editing or destructive removal through the main shell. |
| 3 | Workers and Iterations query views | Covered | Mounted tests now cover filtered populated results, error states, infinite append, row open, and a worker Start & View action. Helper tests cover action eligibility, not-found detection, and row merging. | No, unless the table/action behavior expands | Optional mounted tests for dedicated refresh UI, all worker action variants, 404 purge fallback, and non-final iteration row messaging. |
| 4 | Worker detail view actions/config/logs | Partially Covered | The worker detail screen has high state density: overview fetch, realtime merge, mutation actions, configuration editor, logs, timelines, and panel visibility. | Yes, if detail screens are in scope | Mounted test for load/error/populated worker, Start/Pause/Cancel/Push/Purge, reconfigure form save/failure, log/timeline filters, panel hide/focus. |
| 5 | Queue dialog plus SchemaForm interactions | Covered | SchemaForm interaction coverage is strong, including shadcn select, booleans, numbers, URL, arrays, dictionaries, and presets. QueueDialog now has mounted coverage for schema defaults, generated input submit, manual JSON validation, manual subject/concurrency payload, Watch navigation, close callbacks, and server failure. | No, unless QueueDialog behavior expands | Optional follow-ups for configuration tab field editing and WaitForCompletion waiting banner. |
| 6 | Overview dashboard mounted data and permission states | Covered | Mounted OverviewView tests now cover loading, request error, component error, panel controls, failed-worker Start mutation/refresh, no-operate action hiding, throughput mode/window/series controls, and execution chart rendering; mounted console covers overview success and no-work-access system-only requests; `OverviewCatalogFilter` covers category/definition apply and clear. | No, unless Overview chart/filter behavior is refactored further | Optional tests for realtime fallback details, refresh-disabled assertions, empty throughput chart state, and initial hidden-series rendering. |
| 7 | Permission/role-specific UI enforcement | Covered | Mounted tests now cover no-work-access messaging/system-only requests, read-only Workers mutation hiding, hidden sidebar lifecycle controls without `canControlSystem`, hidden Catalog queue shortcuts without operate access, diagnostics target filtering, and Overview failed-worker no-operate action hiding. | No, unless detail reconfiguration or diagnostics tray behavior is refactored | Add detail-screen reconfigure permission tests and diagnostics tray restricted-interaction tests before touching those areas. |
| 8 | Sign-out and auth/proxy route behavior | Partially Covered | Sign-out, login/logout route wrappers, public proxy routes, page redirects, API JSON failures, and Basic pass-through are covered. Session-renewal proxy cookies, matcher exclusions, and Entra callback remain. | Yes, if auth/proxy is touched | Add session-renewal cookie and matcher tests for `proxy()`, plus Entra callback completion/error route tests. |
| 9 | Workable HTTP client boundary | Covered | Data hooks now have coverage for scoped requests, target API headers, query coalescing, auth redirect, realtime token fetching/cache, and token errors. | No, unless response parsing is refactored | Add non-JSON response parsing coverage if `workableFetch` changes. |
| 10 | Realtime payload/events window and realtime UI integration | Partially Covered | DOM coverage now proves the payload window opens/closes, switches Payloads/Events tabs, filters, pins, clears, and changes limits. Active view registration, dock/resize/JSON inspector details, and connection error state remain. | Only if realtime integration, docking, or event filtering is refactored | DOM/e2e tests for active registration, connection error state, dock/resize, JSON inspector toggles, and event filter details. |

## Gaps To Fix Before The Component Refactor Goal

Before broad component refactoring, all High burn-down gaps are now Done: gap 1 is materially reduced by authenticated mounted console and shell history/Catalog coverage, gap 3 is covered by mounted Workers/Iterations query-view tests, gap 5 is covered by QueueDialog DOM tests, gap 6 is covered by mounted OverviewView data/panel/action and catalog-filter tests, and gap 7 is covered by mounted permission enforcement tests. Finish remaining gap-1 branches only if shell-to-detail transitions or populated/error data through the shell are in the refactor scope.

Gap 4 should be fixed before refactoring `detail-screens.tsx`, worker action controls, configuration editing, logs, or timelines. Gap 8 should be finished before auth/proxy refactors. Gap 10 should be expanded only before refactoring realtime integration, dock/resize behavior, event filters, or JSON inspector controls; the main payload-window interactions now have a DOM safety net.

Tests that can wait until after the refactor: direct tests for generated shadcn/ui wrappers, static assets/global CSS, route-wrapper tests that only duplicate already-covered helpers, and client interval ticking for `LiveRelativeTime` unless that component changes.

## Test Gap Burn-down

This table is the active implementation queue. High gaps must be Done or Blocked before this burn-down goal can be complete. Medium gaps are conditional on the component areas being refactored next. Low gaps are optional unless their local code changes. For the Medium burn-down, the selected pre-refactor queue is `Todo`; lower-value Medium rows stay `Deferred` only when their notes explain the condition that should promote them.

| Priority | Area/route/component | Gap | Planned tests | Status | Notes |
| --- | --- | --- | --- | --- | --- |
| High | Main `/` console authenticated shell | Persisted authenticated host, overview data, Workers/Iterations empty navigation, and no-work-access overview needed mounted coverage. | Mount `WorkableConsole` with seeded `localStorage` and mocked API; assert host restore, overview success, Workers/Iterations empty states, target API header, and restricted system-only request. | Done | Completed in authenticated console safety-net batch. Remaining shell branches are tracked separately as Medium. |
| High | Workers query view | Populated/error/filter/load-more/refresh/row-open/action behavior is not mounted. | Mounted console or view tests with mocked API for populated workers, error state, filters, load more, refresh, open worker, and Start/Pause/Cancel/Push/Purge action success/failure. | Done | Added filtered populated rows, error, load-more, row open, and Start & View action coverage. Full action matrix and 404 purge fallback remain optional follow-ups. |
| High | Iterations query view | Populated/error/filter/load-more/row-open behavior is not mounted. | Mounted console or view tests with mocked API for populated iterations, error state, status/key filters, load more, refresh, and open iteration. | Done | Added filtered populated rows, error, load-more, and final iteration open coverage. Dedicated refresh UI and non-final row assertions remain optional follow-ups. |
| High | QueueDialog mutation flow | Core queue mutation UI is untested even though `SchemaForm` interactions are covered. | DOM tests for opening the dialog from a definition, subject/concurrency inputs, schema defaults, JSON validation, submit success, submit failure, and notification/close behavior. | Done | Added schema default input submit, manual JSON validation, manual subject/concurrency Watch submit, close callbacks, and server failure coverage. Configuration-tab field editing remains Medium. |
| High | OverviewView data and panel states | Loading/error/component-error/panel controls/failed-worker actions are not mounted. | Mounted `OverviewView` or console tests for loading, error, empty/component-error, refresh, panel hide/shape/settings, throughput toggles, and failed-worker action menu. | Done | Added deferred loading, request error, component error, panel next-view/hide controls, `onReady`, `onStateLoaded`, failed-worker Start mutation, failed-worker slice refresh, and no-operate action hiding. Throughput toggles remain Medium. |
| High | Permission/role-specific UI enforcement | Restricted users may still see diagnostics, lifecycle, queue, reconfigure, or work-operation controls. | Mounted restricted-system tests for hidden/disabled diagnostics, lifecycle controls, queue/reconfigure buttons, worker actions, and broader no-access messaging. | Done | Added mounted no-work-access overview, read-only Workers action hiding, sidebar lifecycle gating, Catalog queue gating, Overview failed-worker no-operate hiding, and diagnostics target filtering. Detail reconfigure permission coverage remains Medium under detail-screen risk. |
| Medium | Main console shell remaining branches | Catalog navigation, refresh, back/forward history, state saving after navigation, populated/error route states, and details/definition transitions are thin. | Mounted shell test for Catalog navigation, header refresh/back/forward behavior, and persisted view state. | Done | Added mounted shell coverage for Catalog navigation, definition loading, Refresh catalog re-fetch, Go back/Go forward, and stored view changes. Populated/error shell data and detail transitions remain deferred unless those areas are refactored. |
| Medium | Add/edit server dialog | Edit mode, cancel/no-save, retry after failed discovery, loading state, and removed-system reconciliation are untested. | DOM tests for edit existing host, cancel, loading spinner, retry, and save payload after host reconciliation. | Done | Added edit-mode discovery refresh, matched-system preservation, missing-system removal, cancel/no-save, loading-disabled actions, retry after failed discovery, and save-after-retry coverage. |
| Medium | Empty/no-access server state | Saved-host/no-connect-access variant is not mounted. | Main console test with saved host whose system access has `canConnect: false`; assert no-access messaging and Add server action. | Deferred | Lower refactor risk after no-work-access permission coverage; promote only if empty-state or host discovery filtering changes. |
| Medium | Delete host/system and stop system dialogs | Confirm/cancel behavior and actual removal/lifecycle calls are not covered. | DOM tests for confirm/cancel and integration tests proving sidebar/localStorage update and lifecycle error reporting. | Deferred | Destructive action behavior is stable and text helpers exist; promote if removal or lifecycle mutation flows are refactored. |
| Medium | Console layout and panel primitives | Class-heavy tests do not prove user-visible menu/visibility/settings behavior. | Role/name/state tests for panel options menu, view-state cycling, close/hide/show, and infinite footer/load-more sentinel. | Deferred | Covered indirectly by Overview panel controls; promote when panel primitives are refactored directly. |
| Medium | Overview child panels and throughput chart | Iteration links, chart modes, legend/window toggles, and empty chart state are not mounted. | DOM tests for summary panel links, throughput mode/window/series toggles, and chart empty state. | Done | Added throughput control coverage for chart rendering, series toggles, window request options, Execution tab switching, and execution metrics before extracting throughput controls. Added iteration status/key/recent-worker flow coverage before extracting iteration panels. Empty chart state and initial hidden-series rendering are deferred unless those branches are refactored. |
| Medium | Overview catalog filter | Category/definition selection UI is not covered. | DOM test opening catalog filter, selecting category/definition, clearing scope, and loading/empty catalog states. | Done | Added category drill-in, definition selection, apply callback, active-scope rerender, and clear callback coverage. Loading/empty variants can wait unless the filter UX changes. |
| Medium | Definitions catalog view | `DefinitionsView` fetch/search/auto-open/open-definition/open-queue flows are not mounted. | Integration test with mocked definitions for loading, search, category navigation, scoped definition auto-open, open definition, and open queue dialog. | Deferred | Basic Catalog route load is now covered through the shell. Queue dialog and catalog primitives are covered; promote if `DefinitionsView` search, scoped auto-open, or queue entrypoint is refactored. |
| Medium | Definition detail/configuration | Mounted definition loading/error, metadata, tabs, reconfigure, queue, and worker navigation are missing. | Mounted tests for loading/error/populated detail, configuration tabs, reconfigure success/failure, QueueDialog entry, and worker navigation. | Deferred | Broad detail fixture cost is high; promote before definition detail or reconfigure UI refactors. |
| Medium | Worker detail view actions/config/logs | Worker detail panels, realtime merge, actions, config editor, logs, timelines, and panel visibility are not mounted. | Mounted tests for load/error/populated worker, Start/Pause/Cancel/Push/Purge, reconfigure save/failure, log/timeline filters, latest output, panel hide/focus. | Deferred | Existing helper coverage characterizes dense logic; promote before worker detail, logs, timeline, or configuration refactors. |
| Medium | Iteration detail route/view | Mounted summary/messages/input/output/logs/loading/error behavior is missing. | Component tests for populated/error/empty iteration detail, message filters, logs, breadcrumb definition link, and input/output rendering. | Deferred | Promote before iteration detail refactors; current component-refactor safety is stronger in query/overview shells. |
| Medium | Diagnostics tray and notifications | Tray popover interactions, acknowledgement, expanded diagnostics, active-target filtering, and invalidation integration are thin. | DOM/integration tests for tray open, expand, acknowledge rejected work, alert transitions, and active-system filtering. | Deferred | Promote if diagnostics tray changes. |
| Medium | Feedback/error banners integration | Dismiss interaction and surfacing from failing actions are thin. | DOM tests for dismiss callbacks and action failure surfacing from overview/worker/server flows. | Deferred | Basic banner helper coverage exists. |
| Medium | Realtime payload/events window | Open window UI, tabs, search, clear, pin, dock, max messages, and JSON inspector toggles are not covered. | DOM test for open window UI, Payloads/Events tabs, search filtering, pin/unpin, clear, and max message controls. | Done | Added open window coverage for Payloads/Events tabs, search, pin/unpin state, max messages, clear, disabled pinned menu after clear, and close callback. Dock, resize, JSON inspector toggles, and event filter details remain deferred unless refactored. |
| Medium | Realtime connection UI integration | Active view descriptor subscription and access-token error surfacing are not covered; one timing test is brittle. | Component test for active descriptor registration, subscription state, access-token error, and replacement of real sleep with controllable timing. | Deferred | Core pool helpers are covered. |
| Medium | Auth/proxy route wrappers | Entra callback completion, token route auth failure, `/api/workable` wrapper params, proxy session renewal, and matcher exclusions are not directly tested. | Route/proxy tests for callback error/success, token route auth failures, GET/POST path forwarding, query preservation, session-renewal cookie, and static exclusions. | Deferred | Security helpers are strong; promote before auth/proxy refactors. |
| Medium | Responsive/mobile behavior | No mobile viewport or sidebar sheet coverage. | Browser/e2e smoke for mobile sidebar open/close, nav visibility, dialog sizing, and table/panel usability. | Deferred | Requires adopting browser automation ownership. |
| Low | Auth shell and logo | Mostly class/image smoke tests. | Keep only accessibility and image-alt assertions, or cover visually through login e2e. | Deferred | Low risk unless branding/layout changes. |
| Low | Login browser-native validation | Required-field behavior is not tested in a real browser. | Optional e2e for required fields, route-handler wiring, and post-login redirect. | Deferred | Login route/form behavior is otherwise covered. |
| Low | Live relative time display | Client interval ticking and cleanup are not tested. | Fake-timer DOM test for ticking and cleanup. | Deferred | Only needed if live time component changes. |
| Low | Reusable primitive wrappers and generated shadcn/ui | Direct wrapper tests would duplicate Radix/shadcn behavior. | Test through composed features; add direct tests only for custom behavior. | Deferred | Not worth expanding directly. |
| Low | Workable HTTP non-JSON parsing | Non-JSON response parsing branches are optional. | Add direct client tests only if response parsing changes. | Deferred | Core HTTP boundary is covered. |
| Low | Static assets/global CSS | Static asset and global CSS behavior is build-covered. | None unless branding or global layout behavior changes. | Deferred | Not worth direct tests now. |

## Recommended Implementation Order

1. Proceed with component refactoring against the covered High and selected Medium safety net: mounted console auth shell, Catalog/history/refresh, Workers/Iterations query views, QueueDialog, ServerDialog, Overview data/panel/action/filter states, realtime payload window interactions, and permission gating.
2. Add DefinitionView, WorkerConsoleView, and IterationConsoleView mounted coverage before touching detail screens; keep the existing helper tests as characterization until then.
3. Add detail-screen reconfigure permission tests before refactoring worker/definition configuration editors.
4. Finish remaining main console shell branches only if shell-to-detail or populated/error route transitions are refactored.
5. Add realtime active-registration, connection-error, dock/resize, JSON-inspector, and event-filter coverage before refactoring those specific realtime areas.
6. The Overview throughput-control and iteration-panel refactor gates are done; add empty chart, initial hidden-series, completed-iteration row, or extra shape-variant tests only before changing those specific branches.
7. Add responsive/mobile browser smoke coverage before sidebar/layout/mobile refactors.
8. Add auth/proxy route-wrapper tests before auth/proxy refactors.
9. Add frontend validation to CI: `npm ci`, `npm run lint`, `npm run test`, `tsc --noEmit`, `npm run build`.
10. Replace brittle class-string assertions during component refactors with role/name/state assertions or focused visual/e2e checks.

## Risks, Assumptions, And Open Questions

- Assumption: future refactoring targets React components, so tests should favor public UI behavior over private helper shape.
- Risk: current tests run with `--test-isolation=none`; global fetch, DOM, router mock, and timers must be restored carefully.
- Risk: many tests assert Tailwind class strings, which can produce false failures during legitimate shadcn/layout refactors.
- Risk: mounted Radix dropdown/tooltip tests currently pass but emit non-failing React `act(...)` cleanup warnings; replace the custom harness or isolate Radix cleanup if the warning noise starts hiding real failures.
- Risk: there is no browser e2e coverage for the main console or Radix interactions.
- Risk: frontend tests are not in repository CI.
- Open question: should the project standardize on Vitest + React Testing Library, or keep evolving the custom Node/jsdom harness?
- Open question: what fake/mocked Workable API fixture should drive e2e server discovery and main console scenarios?
