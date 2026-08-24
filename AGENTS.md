# Agent Notes

## Test Execution

Before creating any commit in this repository, run the full validation set that covers the UI, API/backend, and integration surfaces:

```powershell
dotnet test .\Workable.slnx --no-restore --logger "console;verbosity=minimal" --blame-hang-timeout 2m
npm.cmd test
npm.cmd run test:coverage
npm.cmd run build
```

Run the UI test and build commands from `apps\web\workable-admin-ui`.

- On Windows, use `npm.cmd test`.
- On Windows, use `npm.cmd run test:coverage`.
- On Windows, use `npm.cmd run build`.
- On macOS or Linux, use `npm test`.
- On macOS or Linux, use `npm run test:coverage`.
- On macOS or Linux, use `npm run build`.

The UI unit test harness transpiles TypeScript/TSX quickly for DOM tests, but it does not replace the Next.js compiler/type-check pass. Run `npm run build` after UI changes to catch JSX parser and strict TypeScript errors that may not surface in `npm test`.

## Check-In Documentation Sweep

Before creating any commit, sweep the complete diff for documentation impact. Compare changed behavior, public APIs, configuration, routes, permissions, UI labels, and screen locations against the root and app/sample `README.md` files, `docs`, XML documentation, examples, and the active release notes. Search for old terminology and workflows with `rg`, update every stale reference, and report the documentation files checked. A code-only change is not ready to check in when affected documentation still describes the prior behavior.

## Check-In Coverage Requirement

Non-UI production assemblies must have at least 95% branch coverage before check-in. Enforce the core/API/backend report and the `Workable.SqlServer` extension package independently so one surface cannot hide another surface's gap. New or modified admin UI behavior must also have at least 95% changed-branch coverage; the unrelated legacy UI baseline is not required to reach 95%. Map uncovered branch outcomes back to the production decisions in `git diff`, and add behavior-focused tests for branches that can affect users, security, state, or failure handling. Do not add assertions that merely execute a branch without verifying an outcome, and document any genuinely unreachable defensive branch excluded from the calculation.

For .NET changes, collect coverage outside the sandbox:

```powershell
dotnet test tests\Workable.Tests\Workable.Tests.csproj --no-restore --collect "XPlat Code Coverage" --logger "console;verbosity=minimal" --blame-hang-timeout 2m
dotnet test tests\extensions\sqlserver\Workable.SqlServer.Tests\Workable.SqlServer.Tests.csproj --no-restore --collect "XPlat Code Coverage" --logger "console;verbosity=minimal" --blame-hang-timeout 2m
```

The first report's root `branches-covered` and `branches-valid` values measure the core/API/backend gate. The second report includes dependencies and the CLI; calculate the extension gate by summing the covered and valid outcomes in the `condition-coverage` attributes under the `Workable.SqlServer` package. Those are the package's source-mapped branch outcomes. Do not use the second report's root, the `Workable.SqlServer.Cli` package, or Coverlet's package `branch-rate`, which also counts compiler-synthesized points that are not emitted as source conditions and therefore cannot be mapped to a production decision or behavior-focused test.

For admin UI changes, run `npm run test:coverage` from `apps\web\workable-admin-ui`. Node's native report includes the whole UI source tree and currently exposes a legacy repository-wide baseline below 95%; use its per-file uncovered-branch output with the diff to enforce the 95% changed-code requirement. Preserve or improve the whole-surface baseline, and never raise coverage by excluding production files merely to make the number pass.

Run the `.NET` solution test command outside the sandbox. It covers the main API/backend test project and the SQL Server integration suite.

The SQL Server integration suite is cross-platform. It uses `WORKABLE_SQLSERVER_TEST_CONNECTION_STRING` when that environment variable is set; otherwise it auto-starts a local SQL Server container through `docker` or `podman`.

The auto-managed test container is named `workable-sqlserver-tests` and uses the SQL Server 2022 Linux image. If you want to avoid leaving the container running for reuse between test runs, set `WORKABLE_SQLSERVER_TEST_CONTAINER_REUSE=false`.

When running .NET tests from Codex, run `dotnet test` outside the sandbox. The Codex sandbox causes the .NET CLI/MSBuild build phase to spawn large numbers of `MSBuild.dll /nodemode:1` worker processes and stall before test execution.

Use this command with escalated permissions:

```powershell
dotnet test tests\Workable.Tests\Workable.Tests.csproj --no-restore --logger "console;verbosity=minimal" --blame-hang-timeout 2m
```

Observed behavior:

- Inside the Codex sandbox, `dotnet test` can hang during MSBuild orchestration and leave orphaned `dotnet.exe` MSBuild node processes.
- Outside the Codex sandbox, the same command completes normally.
- Visual Studio Test Explorer is not affected because it does not run through the Codex sandboxed CLI path.

If a sandboxed test run is interrupted, clean up only orphaned MSBuild node processes matching `MSBuild.dll`, `/nodemode:1`, and `/nodeReuse:true`.

## GitHub Operations

Run all GitHub operations outside the sandbox.

Observed behavior:

- GitHub authentication and keyring-backed `gh` flows may fail inside the Codex sandbox even when they work normally outside it.
- Issue creation, pull request work, and other `gh` commands should be executed with escalated permissions.
