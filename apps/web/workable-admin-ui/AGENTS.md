<!-- BEGIN:nextjs-agent-rules -->
# This is NOT the Next.js you know

This version has breaking changes â€” APIs, conventions, and file structure may all differ from your training data. Read the relevant guide in `node_modules/next/dist/docs/` before writing any code. Heed deprecation notices.
<!-- END:nextjs-agent-rules -->

## Commit Validation

Before creating any commit in this repository, do not stop at the UI tests. Run all of the following:

```powershell
npm.cmd test
dotnet test .\Workable.slnx --no-restore --logger "console;verbosity=minimal" --blame-hang-timeout 2m
```

Run `npm.cmd test` from `apps\web\workable-admin-ui`.

Run the `.NET` solution test command from the repository root and outside the sandbox so the API/backend and SQL Server integration suites are covered.
