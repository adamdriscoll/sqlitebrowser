# Avalonia testing and port licensing

Research date: August 2026.

This note records primary-source findings for the C# and Avalonia port. It is an
engineering summary, not legal advice.

## License

The repository's `LICENSE` offers DB Browser for SQLite under MPL-2.0 and
GPL-3.0-or-later and expressly permits modification and redistribution under
those terms.

Under MPL-2.0:

- A source file produced by modifying covered source, or a new source file that
  contains covered source, is a Modification (`LICENSE-MPL-2.0`, section 1.10).
- Covered source must remain available under MPL-2.0, and executable
  distribution must tell recipients how to obtain that source
  (`LICENSE-MPL-2.0`, sections 3.1 and 3.2).
- Separate files that do not contain covered source may be part of a Larger
  Work under other terms, while covered files remain under MPL-2.0
  (`LICENSE-MPL-2.0`, section 3.3).
- Existing notices must be retained (`LICENSE-MPL-2.0`, section 3.4).

Under GPL-3.0-or-later:

- A translated or adapted work is a modified covered work
  (`LICENSE-GPL-3.0`, sections 0 and 5).
- Distribution of the combined work must be under GPL-3.0-or-later, and
  distribution of object code must include or offer the Corresponding Source
  (`LICENSE-GPL-3.0`, sections 5 and 6).
- Modified files require prominent modification notices, and an interactive
  interface must display the applicable legal notices
  (`LICENSE-GPL-3.0`, section 5).

This port preserves the repository's dual-license notice and complete license
texts. It also exposes the notice from its About dialog.

## Avalonia UI testing

Avalonia documents four useful testing levels:

1. Ordinary unit tests for view models and services.
2. In-memory control and interaction tests using the Avalonia headless platform.
3. Visual regression tests by enabling Skia drawing and capturing rendered
   frames.
4. End-to-end desktop automation with Appium and native platform drivers.

The official xUnit integration is `Avalonia.Headless.XUnit`. A test assembly
registers an `AppBuilder` with `AvaloniaTestApplication`, and UI-thread tests use
`AvaloniaFact` or `AvaloniaTheory`. The headless platform supports real control
trees, data binding, layout, focus, and simulated keyboard and pointer input
without a display server.

This repository uses `Avalonia.Headless.XUnit` for repeatable CI tests. Primary
controls also receive `AutomationProperties.AutomationId` values so Appium can
be added later for release-level operating-system automation without changing
the UI contract.

## Primary sources

- Repository license declaration: [`LICENSE`](../../LICENSE)
- MPL-2.0 text: [`LICENSE-MPL-2.0`](../../LICENSE-MPL-2.0)
- GPL-3.0 text: [`LICENSE-GPL-3.0`](../../LICENSE-GPL-3.0)
- [Avalonia testing overview](https://docs.avaloniaui.net/docs/testing/)
- [Setting up the Avalonia headless platform](https://docs.avaloniaui.net/docs/testing/setting-up-the-headless-platform)
- [Avalonia headless xUnit integration](https://docs.avaloniaui.net/docs/testing/headless-xunit)
- [Avalonia UI testing with Appium](https://docs.avaloniaui.net/docs/testing/ui-testing-with-appium)
