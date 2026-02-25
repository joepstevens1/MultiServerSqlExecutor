# AGENTS Guide: MultiServerSqlExecutor

This file is for AI agents working in this repository. Follow it to make safe, consistent changes.

## Solution Summary

- Tech stack: C# / .NET 8
- Solution file: `MultiServerSqlExecutor.sln`
- Projects:
1. `src/MultiServerSqlExecutor.Core` (shared business logic)
2. `src/MultiServerSqlExecutor.Cli` (command-line entry point)
3. `src/MultiServerSqlExecutor.Ui` (WPF desktop UI)

Core contains all reusable logic. CLI and UI should stay thin and call Core services.

## Directory Structure

### `src/MultiServerSqlExecutor.Core`

- `Models/ServerConnection.cs`
  - Connection model + `AuthType` enum.
  - Builds SQL connection strings for SQL and Azure auth modes.
- `Services/ConfigStore.cs`
  - Persists server configs to `%AppData%/MultiServerSqlExecutor/servers.json`.
  - Supports load/save/add/update/remove operations.
- `Services/SqlExecutor.cs`
  - Executes SQL on one server or all servers concurrently.
- `Services/CsvExporter.cs`
  - Writes per-server and combined CSV outputs.

### `src/MultiServerSqlExecutor.Cli`

- `Program.cs`
  - CLI command routing:
    - `add-server`
    - `remove-server`
    - `list-servers`
    - `execute-query`
  - If no args are passed, attempts to launch UI executable.

### `src/MultiServerSqlExecutor.Ui`

- `MainWindow.xaml` + `MainWindow.xaml.cs`
  - Query editor, run action, results grid, save combined CSV.
- `ServersWindow.xaml` + `ServersWindow.xaml.cs`
  - Add/update/remove server connection entries.
- `SQL.xshd`
  - SQL syntax highlighting definition for AvalonEdit.
- `App.xaml` + `App.xaml.cs`
  - WPF app bootstrap.

## Modification Rules

1. Put shared behavior changes in Core first.
2. Keep UI and CLI behavior aligned:
   - If a feature is added in one entry point and should apply to both, update both.
3. Keep config contract stable unless explicitly changing schema.
   - If schema changes, add migration/compatibility handling in `ConfigStore`.
4. Do not hardcode environment-specific paths.
5. Preserve async/non-blocking behavior in query execution paths.

## Common Change Playbooks

### Add a new server/auth capability

1. Update `AuthType` and `ServerConnection.BuildConnectionString()` in Core.
2. Update UI auth dropdown handling in `ServersWindow.xaml` and `ServersWindow.xaml.cs`.
3. Update CLI help/argument behavior in `Program.cs` if required.

### Add a new CLI command

1. Add command case in `Program.cs`.
2. Implement command handler with clear usage and exit codes.
3. Reuse Core services; avoid duplicating data access logic in CLI.

### Change query execution/export behavior

1. Modify `SqlExecutor` and/or `CsvExporter` in Core.
2. Validate both:
   - CLI `execute-query`
   - UI Run + Save Combined flow

## Build and Validation

Run from repository root:

```powershell
dotnet restore MultiServerSqlExecutor.sln
dotnet build MultiServerSqlExecutor.sln -c Debug
```

Quick CLI smoke checks:

```powershell
dotnet run --project src/MultiServerSqlExecutor.Cli -- help
dotnet run --project src/MultiServerSqlExecutor.Cli -- list-servers
```

Run UI (Windows):

```powershell
dotnet run --project src/MultiServerSqlExecutor.Ui
```

## Known Constraints

- No test project currently exists; use targeted smoke checks after edits.
- Credentials are currently stored in the config JSON file (plain text). Do not worsen this behavior inadvertently.
- Query timeout is currently unlimited (`SqlExecutor` sets command timeout to `0`).

## PR/Change Quality Checklist

1. Build succeeds for the solution.
2. CLI help/usage text is updated if command surface changed.
3. UI and CLI are behaviorally consistent for shared features.
4. Config compatibility considered for any model/schema change.
5. No secrets committed in source or docs.
