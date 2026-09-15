# Changelog

All notable changes to this package are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.3.6] - 2026-09-15

### Fixed

- Ping on a record whose object was gone appended "(context object no longer exists)" to the
  detail header, once per click, so repeated clicks stacked copies of it into what is
  otherwise structured metadata. It shows an editor notification instead.
- The Ping button was enabled whenever a record carried a context id, without checking that
  the object still resolved - so it offered a click that could not do anything and only said
  so afterwards. Resolution now happens when the row is selected, and the button is disabled
  with a tooltip explaining why.
- The detail header's timestamp is formatted in invariant culture, matching the rest.

## [0.3.5] - 2026-09-15

### Changed

- The overlay works out a row's display data once, when the record arrives, instead of on
  every draw. OnGUI runs at least twice per frame, so each visible row was taking the ring
  buffer's lock, re-deriving the tag colour from its name and cutting two substrings
  thousands of times a second - to produce the same characters every time, since nothing
  about a written record changes. Drawing a row now touches no lock and allocates nothing.

## [0.3.4] - 2026-09-15

### Added

- `LogWindow.AddTab(LogFilter)` and `LogWindow.SelectNewest(LogLevel)`, mirroring what the
  overlay already exposes. The first lets a project seed its own set of tabs from an editor
  script instead of rebuilding them by hand on every machine; the second suits a "jump to the
  last error" shortcut.

## [0.3.3] - 2026-09-15

### Added

- Overlay rows carry the frame number and the timestamp, and the detail pane carries the
  timestamp alongside the frame. Both were already on the record and simply were not shown;
  the editor window had them from the start.

The frame column is dropped when the viewer is narrow. On a phone the message needs the width
more than the frame number does, and the detail pane still has it.

## [0.3.2] - 2026-09-15

### Fixed

- The overlay's tag pane was translucent and the log rows underneath showed through it,
  which made the tag names hard to read. It is opaque now, with an edge against the list.

### Changed

- Tags in the overlay's pane keep their colour whether selected or not - the colour is the
  tag's identity, and selection is carried by a highlight and text brightness instead.
- The collapsed bubble reads "2 errors" / "1 warning" / "14 logs" rather than "! 2" / "? 1".

### Added

- `LogOverlay.IsOpen`, `LogOverlay.TagPaneVisible` and `LogOverlay.SelectNewest(level)` for
  driving the viewer from a debug menu of your own.

## [0.3.1] - 2026-09-15

### Added

- `Overlay Demo` sample: a scene that drives the logger from several tags with buttons for a
  log, a warning, an error, a Unity exception thrown past this API, and a burst of repeats to
  collapse. Import it from Package Manager, open the scene, press Play.

## [0.3.0] - 2026-09-15

### Added

- In-game overlay (`LogConfig.ShowOverlay`, off by default): a draggable bubble showing error
  and warning counts that expands into a log viewer on the device - level toggles, tag list,
  full message and stack trace on tap, copy to clipboard. No prefab, no Canvas, no
  PanelSettings, no package dependency; turning the flag on is the whole installation.
- `MemorySink` for views that need recent records at runtime.
- `TagPalette`, the shared tag-to-colour rule, so a tag looks the same in the overlay and in
  the editor window.

### Changed

- The filter moved to the runtime assembly as `LogFilter`, shared by the editor window and the
  overlay. The tag rules have a subtle edge - a selected "Net" must not pull in an unrelated
  "Network" - and two copies of that would have drifted apart. The editor's `TabFilter` is
  gone; saved window layouts start again with a single All tab.
- Pieces backed by a GameObject are now created during `BeforeSceneLoad` rather than
  `SubsystemRegistration`, which runs before there is anywhere to put one. Sinks are still set
  up in the earlier pass so nothing is missed.

## [0.2.0] - 2026-09-15

### Added

- `FileSink`: rolling JSONL files under `persistentDataPath/logs`. Each run starts a new file
  and pushes the previous ones down, so a report is never a blend of two sessions. Every file
  opens with a header naming app version, Unity version, platform, device and start time.
- Errors flush to disk immediately, and so does an app being paused - on mobile that is the
  last signal before the process is killed.
- `IFlushableSink` and `LogCore.FlushSinks`, driven by `LogLifecycleHooks` for the pause and
  quit callbacks that have no static equivalent.
- **Open file** in the log window loads a build's log as a session, with the same tabs, tags
  and filters as a live run - including a file the running app still has open.
- `LogJson` writes records by hand in invariant culture; a decimal-comma locale would
  otherwise produce lines that are not valid JSON.

### Changed

- `LogRingBuffer` looks records up by binary search instead of arithmetic on the oldest
  sequence. Sequences are only guaranteed to rise, not to be contiguous: the file sink skips
  the dev channel, so a loaded session has gaps where the old lookup silently missed.
- In the editor the file sink only runs during play mode. Otherwise every script recompile
  would start a session and rotate the real history away within a few reloads.

### Fixed

- Reading a log file that was still being written failed with a sharing violation. The reader
  now opens with `FileShare.ReadWrite` and streams the file instead of slurping it, so a torn
  last line is counted and skipped rather than failing the whole read.

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
