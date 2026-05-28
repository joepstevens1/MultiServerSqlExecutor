# Multi-Server SQL Executor

Multi-Server SQL Executor is a Windows desktop application for running the same SQL query across multiple SQL Server or Azure SQL databases and reviewing the combined results in one place.

The WPF UI in `src/MultiServerSqlExecutor.Ui` is the primary experience. The CLI still exists for scripted usage, but the project is centered on the desktop workflow.

## What The UI Does

- Manage saved database connections from a desktop window.
- Support multiple authentication modes:
  - SQL Password
  - Azure MFA
  - Azure Password
  - Azure Interactive
- Organize servers into groups and filter query execution by selected groups.
- Edit SQL in-app with syntax highlighting.
- Open and save `.sql` files from the query editor.
- Prompt before discarding unsaved SQL changes.
- Run one query against multiple databases concurrently.
- Show live per-database execution status while a run is in progress.
- Display combined query results in a grid.
- Copy result cells, including an option to copy with column headers.
- Export combined results to CSV.

## Solution Layout

- `src/MultiServerSqlExecutor.Ui`
  - WPF desktop application and main user experience.
- `src/MultiServerSqlExecutor.Core`
  - Shared models and services for configuration, SQL execution, and CSV export.
- `src/MultiServerSqlExecutor.Cli`
  - Secondary command-line entry point that reuses Core services.

## Requirements

- Windows
- .NET 8 SDK

The UI project targets `net8.0-windows` and is configured for `win-x64`.

## Running The UI

From the repository root:

```powershell
dotnet restore MultiServerSqlExecutor.sln
dotnet run --project src/MultiServerSqlExecutor.Ui
```

To build the full solution:

```powershell
dotnet build MultiServerSqlExecutor.sln -c Debug
```

To publish the desktop app as a single-file executable:

```powershell
dotnet publish src/MultiServerSqlExecutor.Ui/MultiServerSqlExecutor.Ui.csproj -c Release
```

The UI project is configured as:

- self-contained
- single-file publish
- `win-x64`

## Desktop Workflow

### 1. Add Servers

Use `File > Servers...` to create or update saved connections.

Each server entry includes:

- Display name
- Server name
- Database name
- Authentication type
- Username when required
- Password when required

For Azure-based authentication, the app can append `.database.windows.net` when the server name does not already contain a domain.

### 2. Organize Servers Into Groups

Use `File > Groups...` to:

- create groups
- remove groups
- assign servers to groups

Use the `Group Filter` menu in the main window to run queries against all servers or only the selected groups.

### 3. Write Or Open SQL

The main window includes an in-app SQL editor with syntax highlighting. You can:

- type SQL directly
- open an existing `.sql` file
- save changes back to the current file
- save to a new file

The app remembers the last SQL file folder in the local config.

### 4. Run The Query

Use `Run > Run Query` or press `F5`.

During execution, the UI shows:

- elapsed time
- total selected servers
- waiting count
- completed count
- selected server count
- per-server status in the right sidebar

If some servers fail and others succeed, the app still shows successful results and reports the failed databases at the end of the run.

### 5. Review And Export Results

Results are displayed in a grid in the lower pane.

- `Ctrl+C` copies selected cells.
- `Ctrl+Shift+C` copies selected cells with headers.
- `File > Save Combined CSV` exports the combined result set.

When result sets differ by server, the combined export unions columns and adds a `Server` column so each row still identifies its source database.

## Configuration Storage

The application settings file is stored at:

```text
%AppData%\MultiServerSqlExecutor\servers.json
```

This `servers.json` file is the app's settings/configuration file.

It currently stores:

- saved servers
- groups
- last SQL file folder

Credentials are stored in plain text in that JSON file. This is a known limitation of the current implementation.

## CLI

The CLI is still available in `src/MultiServerSqlExecutor.Cli`, but it is a secondary interface.

Available commands:

- `add-server`
- `remove-server`
- `list-servers`
- `execute-query`

Examples:

```powershell
dotnet run --project src/MultiServerSqlExecutor.Cli -- help
dotnet run --project src/MultiServerSqlExecutor.Cli -- list-servers
dotnet run --project src/MultiServerSqlExecutor.Cli -- execute-query --queryFile .\query.sql --outputFile .\combined.csv
```

If the CLI is started without arguments, it attempts to launch the UI executable when available.

## Notes And Current Constraints

- Query execution currently uses no SQL command timeout.
- There is no automated test project in the repository yet.
- Shared behavior lives in `MultiServerSqlExecutor.Core`; UI and CLI both depend on it.
