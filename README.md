# SQLite Browser

SQLite Browser is a cross-platform desktop application for creating, inspecting,
querying, and editing SQLite databases. This fork is a clean C# and
[Avalonia](https://avaloniaui.net/) port of
[DB Browser for SQLite](https://github.com/sqlitebrowser/sqlitebrowser).

> [!IMPORTANT]
> The database provider is
> [Devolutions Ahtola](https://github.com/Devolutions/ahtola), an experimental
> pure-managed SQLite-compatible engine. Review Ahtola's compatibility and
> durability status before using this application with production data.

## Features

- Create, open, and inspect local SQLite databases
- Open databases read-only
- Browse tables and views with paging
- Add, edit, and delete table rows
- Inspect tables, views, indexes, triggers, and columns
- Execute SQL and inspect tabular results, elapsed time, and affected rows
- Commit or revert database edits as a unit
- Import CSV and SQL scripts
- Export CSV, JSON, and SQL dumps
- Run integrity, quick, foreign-key, optimize, and vacuum maintenance commands
- View and edit common SQLite PRAGMA values
- Run on Windows, Linux, and macOS

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

The release ZIPs are self-contained and do not require a separate .NET
installation.

## Build and run

```powershell
dotnet restore SqliteBrowser.slnx
dotnet build SqliteBrowser.slnx
dotnet run --project src\SqliteBrowser.App
```

To open a database at startup:

```powershell
dotnet run --project src\SqliteBrowser.App -- C:\path\to\database.db
```

### Visual Studio Code

Install the recommended C# extension, then use `Ctrl+Shift+B` to build or the
**Run and Debug** view to start **SQLite Browser**. The **SQLite Browser: Open
database** launch target prompts for a database path before starting. The
**Tasks: Run Task** command also provides `test` and `clean` targets.

## Test

```powershell
dotnet test SqliteBrowser.slnx
```

The test suite includes core unit and integration tests plus in-memory Avalonia
UI tests using `Avalonia.Headless.XUnit`. The headless tests exercise real
controls, bindings, layout, and input without requiring a display server.

## Publish a native application

The application is configured for self-contained Native AOT publishing:

```powershell
dotnet publish src\SqliteBrowser.App\SqliteBrowser.App.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true
```

Replace `win-x64` with `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, or
`osx-arm64`. Native AOT requires the platform's native compiler and linker;
publishing must run on the target operating system. GitHub Actions builds and
tests every change and publishes Native AOT ZIP artifacts for all six targets
on pull requests and pushes to `master`; version tags also create a GitHub
release.

## Solution layout

| Project | Purpose |
| --- | --- |
| `src/SqliteBrowser.Core` | Database, schema, paging, editing, import/export, and maintenance |
| `src/SqliteBrowser.App` | Avalonia desktop UI |
| `tests/SqliteBrowser.Core.Tests` | Unit and database integration tests |
| `tests/SqliteBrowser.App.Tests` | Avalonia headless UI tests |

Feature-parity work is tracked in
[GitHub Issues](https://github.com/adamdriscoll/sqlitebrowser/issues).

## License

This project remains dual-licensed under the Mozilla Public License 2.0 and GNU
General Public License 3.0-or-later. See `LICENSE`, `LICENSE-MPL-2.0`, and
`LICENSE-GPL-3.0`.
