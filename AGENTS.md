# Agent Notes

## Test Execution

Before creating any commit in this repository, run the full validation set that covers the UI, API/backend, and integration surfaces:

```powershell
dotnet test .\Workable.slnx --no-restore --logger "console;verbosity=minimal" --blame-hang-timeout 2m
npm.cmd test
```

Run `npm.cmd test` from `src\workable-admin-ui`.

Run the `.NET` solution test command outside the sandbox. It covers the main API/backend test project and the SQL Server integration suite.

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
