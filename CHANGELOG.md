# Changelog

All notable changes to this package are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.1] - 2026-09-15

### Fixed

- Turning on `MirrorToUnityConsole` made every log written outside this API appear in the
  console twice: it was captured, folded into the pipeline, then written straight back.
  Records now carry `LogRecord.Captured`, and `UnityConsoleSink` skips them.

## [0.1.0] - 2026-09-15

First release. Core pipeline and editor window.

### Added

- `Log` facade with six methods: `Dev`, `DevWarning`, `DevError`, `Prod`, `ProdWarning`, `ProdError`.
- Dev channel stripped by the compiler outside the editor and development builds, arguments included.
- Call site captured through `[CallerFilePath]` and `[CallerLineNumber]` at zero runtime cost.
- Free-form string tags with a dot hierarchy, so `Combat` selects `Combat.Damage` and `Combat.AI`.
- `LogCore` dispatcher with a thread-safe sink list, and `ILogSink` for custom destinations.
- Capture of foreign logs through `Application.logMessageReceivedThreaded` under the `Unity` tag,
  guarded against feedback loops by a thread-local re-entrancy flag.
- Stack traces unwound only for `Error`, and only when configured.
- `UnityConsoleSink` for mirroring records into the Unity console.
- Editor window (**Window → Kensei → Logs**) built on UI Toolkit: tabs as saved filters, tag tree
  with counts, virtualised list, detail pane, jump to source, ping context object, collapse
  repeats, isolate a single frame.
- Incremental per-tab filtering keyed on record sequence, so each record is tested once and the
  ring buffer rolling over cannot leave a tab pointing at the wrong row.

### Not included yet

- Rolling JSONL file sink for shipped builds.
- Opening a device log file back in the window as a session.
