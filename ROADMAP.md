# SQLite Browser roadmap

## Current status

The repository has been fully converted from C++/Qt/CMake to C#/.NET 10 and
Avalonia 12. The current application is a functional cross-platform SQLite
browser, but it does not yet have feature-for-feature parity with the original
DB Browser for SQLite.

The database layer uses
[Devolutions Ahtola](https://github.com/Devolutions/ahtola) 0.7.0. Ahtola is an
experimental, pure-managed SQLite-compatible provider. Compatibility and
durability limitations must remain visible to users until the provider is
production-ready.

## Completed

- C# and Avalonia application shell for Windows, Linux, and macOS
- Pure-managed Ahtola database provider
- Create, open, read-only open, close, Save As, commit, and revert
- Schema discovery for tables, views, indexes, triggers, and columns
- Paged table browsing with filters and ordering
- Add, edit, delete, and persist rows
- Read-only BLOB display that prevents accidental text conversion
- SQL execution, cancellation, results, timing, affected-row count, and log
- CSV and SQL import
- CSV, JSON, and SQL dump export
- Common PRAGMA editing and database maintenance commands
- Avalonia headless UI testing
- Self-contained single-file ZIP publishing for six desktop runtime targets
- SLNX solution format and unified GitHub Actions workflow
- Dual MPL-2.0/GPL-3.0-or-later licensing and About dialog notices

The current automated suite contains 104 core tests and 14 Avalonia headless UI
tests.

## Remaining parity work

### 1. Schema designers

Replace the current raw DDL editor with visual table and index designers.

- Create and alter columns, constraints, primary keys, and foreign keys
- Support `STRICT` and `WITHOUT ROWID` tables
- Preview generated DDL before applying it
- Perform safe table-recreation migrations when direct `ALTER TABLE` is
  insufficient
- Preserve dependent indexes, triggers, and views
- Add rollback tests for every migration failure stage

**Complete when:** common schema changes require no handwritten SQL and failed
migrations leave the original schema and data intact.

### 2. Rich value editor

Add a dedicated cell inspector/editor.

- Text and multiline text editing
- Hexadecimal BLOB viewer/editor
- Image preview for supported BLOB formats
- JSON format, minify, and validation
- Import/export individual BLOB values
- Explicit SQL `NULL` handling

**Complete when:** text, numeric, null, JSON, image, and arbitrary binary values
can be safely inspected and edited without changing their SQLite storage class
unexpectedly.

### 3. SQL workspace

Expand the single SQL editor into a persistent multi-tab workspace.

- Multiple named query tabs
- Open/save SQL files and external-change detection
- Execute selection or statement under cursor
- Syntax highlighting, line numbers, folding, and completion
- Find/replace and block commenting
- Per-tab results and cancellation state
- Save a result set as a view

**Complete when:** multiple SQL scripts can be edited, executed, saved, and
restored independently.

### 4. Project files

Restore `.sqbpro`-style workspace persistence in a documented, versioned format.

- Database and attached-database paths
- Open SQL tabs and cursor positions
- Browse filters, sort order, page size, and selected tables
- Window layout and active tab
- Backward-compatible schema migration for future project versions

**Complete when:** closing and reopening a project restores the useful working
context without embedding credentials.

### 5. SQLCipher

Add encrypted database support only after choosing and validating an
Ahtola-compatible encryption approach.

- Password/key entry with secure handling
- Cipher compatibility settings
- Open, create, re-key, and decrypt workflows
- No secrets in logs, project files, crash output, or command history
- Cross-platform encrypted database fixtures and compatibility tests

**Complete when:** encrypted databases can be round-tripped on every supported
runtime without exposing key material.

### 6. Visualization and formatting

- Table/query plotting with line, scatter, and bar charts
- Conditional cell formatting
- Per-column display expressions and formatting
- Column visibility, order, width, and frozen-column persistence

**Complete when:** saved display settings and plots reproduce consistently
across platforms.

### 7. Extensions and attached databases

- UI for attach/detach workflows and schema aliases
- Dynamic extension-loading design compatible with Ahtola
- Clear capability reporting when Ahtola does not support a native SQLite
  extension
- Tests for attached-schema browsing, editing, export, and maintenance

**Complete when:** supported attached databases behave like the main schema and
unsupported extension scenarios fail with actionable messages.

### 8. Preferences and localization

- Persistent theme, font, page-size, editor, and database defaults
- System/light/dark theme selection
- Localizable resource files and runtime language selection
- Restore priority translations from the original project
- Keyboard navigation and screen-reader review

**Complete when:** settings survive restarts and primary workflows pass
accessibility checks in each supported desktop environment.

### 9. End-to-end platform testing

Keep `Avalonia.Headless.XUnit` as the fast CI UI layer, then add Appium tests for
native desktop integration.

- Windows Appium/WinAppDriver smoke tests
- macOS Appium `mac2` smoke tests
- File picker, menu, clipboard, accessibility, and window lifecycle coverage
- Linux desktop smoke testing where a reliable automation backend is available
- Optional visual regression snapshots for critical layouts

**Complete when:** release candidates execute database open, browse, edit,
query, save, and close workflows in real windows on Windows and macOS.

## Release hardening

- Track Ahtola releases and rerun compatibility fixtures before upgrades
- Test databases produced by major SQLite versions and common third-party tools
- Add large-table performance and memory benchmarks
- Add crash-safe and interrupted-write scenarios
- Add macOS signing/notarization and Windows signing when distribution requires
  them
- Produce a third-party notices file from resolved NuGet dependencies
- Review branding and trademark requirements before a public release

## Suggested implementation order

1. Visual schema designers and migration safety
2. Rich value editor
3. Multi-tab SQL workspace
4. Project files
5. Attached databases and provider capability reporting
6. Preferences, accessibility, and localization
7. Visualization and conditional formatting
8. SQLCipher, after provider feasibility is established
9. Appium and release hardening

## Starting a follow-up session

```powershell
dotnet restore SqliteBrowser.slnx
dotnet test SqliteBrowser.slnx --configuration Release
dotnet run --project src\SqliteBrowser.App
```

Read these files before changing behavior:

- `README.md`
- `docs/research/avalonia-testing-and-license.md`
- `src/SqliteBrowser.Core/Services/DatabaseSession.cs`
- `src/SqliteBrowser.App/ViewModels/MainWindowViewModel.cs`
- `.github/workflows/build.yml`

Keep each roadmap area independently buildable, add core and headless UI
regression tests with every behavior change, and preserve the single-file
publishing contract.
