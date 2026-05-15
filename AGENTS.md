# Agent Notes

## Test Execution

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
