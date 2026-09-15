# Changelog

All notable changes to this package are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.11.0] - 2026-09-16

### Fixed

- Icons sat on top of button labels. A `Button` paints its own text rather than holding a
  child label, so an icon inserted beside it overlapped the words; the level toggles were fine
  because a `Toggle` does hold one. Buttons now move their caption into a label so the two
  lay out side by side.

### Changed

- The toolbar is grouped and divided: what the view is doing, what it lets through, and what
  it does to the session. It was one undivided row of fifteen controls.
- The tree toggle left the toolbar for an arrow on the tree's own right edge, pointing the way
  it will move. A control called `Tree` sitting next to a `Tag` column told nobody which of
  the two it hid.
- `Compact` moved to the corner of the column header beside the column menu, so the two
  controls that change the list's layout sit together and away from the filters.
- The list no longer scrolls to the tail or rewrites the level counts on ticks where nothing
  changed.

## [0.10.0] - 2026-09-16

### Fixed

- Moving the cursor over the list froze the editor on Windows, flickering both displays as
  something opened and closed. Every row carried a tooltip with its full tag, and a tooltip in
  UI Toolkit is a real operating-system window: crossing rows created and destroyed one per
  row, which on Windows stalls the compositor across every display. macOS shows no symptom,
  which is why this looked like a Windows-only mystery. Rows and tag-tree entries no longer
  carry tooltips; the full tag is in the detail pane when a row is selected.
- Double-click had nothing to open for a log the engine raised against an asset, because it
  only ever looked for source. It now opens the asset, matching Unity's console, and the
  button says `Open` rather than `Open source` since it is no longer only about source.
- The list stopped rebinding every visible row fifteen times a second when the tab had gained
  nothing, which it usually has not.

### Added

- The editor's own icons on the level toggles, Clear, Open file, Open and Ping. A missing icon
  name is ignored, since those move between Unity versions and the labels already carry the
  meaning.

## [0.9.0] - 2026-09-16

### Fixed

- Dragging the selection across rows froze the editor. Every selection change asked the
  console bridge for a context object, and each question took the console's lock and walked
  every entry it held - a few thousand in a real project, repeated per row crossed. Lookups
  now go through an index built in one pass, rebuilt when Unity's entry count changes and no
  more often than five times a second.
- `Compact` wrote `false` into the per-column preferences, so pressing it once left the frame,
  time and tag columns off with no record of ever having chosen that. It stores nothing now,
  and the preference keys were renamed so anyone carrying the damage starts from the defaults
  again - all three columns on.

### Changed

- `Compact` is a mode over the preferences rather than a preset that rewrote them. Leaving it
  gives back exactly what was showing, and while it is on the controls it overrides are
  disabled with a tooltip saying why, instead of appearing to work and half-working.
- The tree toggle reads `Tree`, not `Tags`. A `Tag` column sits two controls along, and one
  word for both left it unclear which the button hid.

## [0.8.0] - 2026-09-16

### Added

- **Compact** in the toolbar hides the tag tree and every column but the message. Turning it
  back off restores what was showing rather than a default, and changing any of those controls
  by hand clears it, since the toggle would no longer describe the screen.

### Changed

- Column visibility moved from a toolbar menu onto the column header: right-click the header,
  or press the button at its right end. A heading is where anyone looks to change the column
  under it, and it frees space on a toolbar that already fills a docked window. A **Show all**
  item brings every column back at once.

## [0.7.0] - 2026-09-16

### Added

- `ConsoleEntryBridge` recovers the context object Unity attached to a captured log, so a
  warning the engine raised against an asset - a USS file naming a StyleSheet - can be pinged
  and opened, which is what Unity's own console does and what this window could not.
  `Application.logMessageReceived` passes no context object, so this reads Unity's own console
  store, `UnityEditor.LogEntries`, by reflection.

  Built to be wrong safely: everything is probed once with its signatures checked, and any
  missing or renamed piece turns the bridge off for good. A Unity upgrade that moves the API
  costs this navigation and nothing else - no log passes through it. The lookup runs when a
  row is selected, not when a record arrives: the store takes a lock and is walked end to end,
  which is fine once per click and absurd once per log line. Results are cached per record.

## [0.6.0] - 2026-09-16

### Added

- Branches in the tag tree fold. A parent carries an arrow, and the arrow swallows its own
  click so folding a branch does not also select it - the two intentions sit a few pixels
  apart. Leaves get an aligned spacer so names keep one left edge. Folded branches are
  remembered in `EditorPrefs`.
- A **Tags** toggle in the toolbar hides the tree entirely, matching what the in-game overlay
  has always had.

## [0.5.2] - 2026-09-16

### Added

- A fixed header above the list names the columns. Its cells carry the same classes as a
  row's, so a width change in the stylesheet moves both and they cannot drift apart. Hiding a
  column through the **Columns** menu hides its heading with it.

## [0.5.1] - 2026-09-16

### Fixed

- Nothing in the window could navigate from a captured log. Records from Unity's own stream
  carry no file or line, because `logMessageReceived` hands over the message, the trace and
  the level and nothing else - so Open source was disabled and double-click did nothing for
  every engine and third-party log. The first project frame in the stack trace is used
  instead, which is what Unity's console navigates by; engine frames pointing at a build
  agent's disk are skipped.

### Known limits

- A log the engine attached an asset to rather than a script - a USS warning naming a
  StyleSheet, for instance - still cannot be followed. Unity's console opens the asset because
  it holds the context object internally; the public capture callback never receives one.

## [0.5.0] - 2026-09-16

### Added

- `Logger`, a logger bound to one tag, so a file can state its tag once rather than at every
  call: `private static readonly Logger Log = Logger.For(Tags.Combat);`. A struct, so the
  field costs a string reference; its `Dev` methods carry `ConditionalAttribute` just as the
  static ones do, since the attribute applies to instance methods too. `Child` nests a tag
  under it, and a default instance falls back to `Untagged` rather than carrying a null into
  the window.

## [0.4.1] - 2026-09-16

### Added

- A **Columns** menu in the log window toggles the frame, time and tag columns. Gathered into
  a menu rather than three more toolbar toggles, because the bar already fills the width of a
  docked window and columns are a view preference worth less permanent space than the filters
  beside them. Remembered in `EditorPrefs`, per window rather than per tab.

## [0.4.0] - 2026-09-16

### Added

- Overloads that take no tag: `Log.Dev("text")` and the five others write under `Untagged`.
  The alternative for a throwaway log was `Debug.Log`, which lands it under the `Unity` tag
  among the engine's own warnings - the noisiest place it could go. The new overloads differ
  from the tagged ones only in the second parameter's type, so a two-string call still means
  "tag, message" and no existing code changes meaning.

## [0.3.8] - 2026-09-16

### Fixed

- Jumping to the source of a record logged on another machine did nothing at all. The
  recorded path comes from `CallerFilePath`, which captures wherever the code was compiled,
  and it was only ever matched against this project's root - so a session file from someone
  else's build, or a path with a different drive letter or home folder, fell through to an
  external open of a path that does not exist here. The path is now anchored on its last
  `Assets/` or `Packages/` segment, which addresses the same file in any project.
- When the source still cannot be found, the window says so instead of doing nothing. The row
  shows a file and a line, so silence reads as either a broken window or a lying line number.

## [0.3.7] - 2026-09-15

### Fixed

- File settings arriving through `LogCore.Configure` were silently ignored. The file sink is
  built during early initialisation so that nothing logged at startup is lost, which means a
  project's own `Configure` always runs after it exists - and `FileSizeLimitKb`,
  `RetainedFileCount`, `FileFlushIntervalSeconds` and `FileIncludesDevChannel` were frozen in
  readonly fields at construction. `FileSink.Reconfigure` applies them to the live sink, so
  the documented way of configuring the package now works for them too.

### Changed

- README rewritten against the code after a review turned up nine places where it promised
  more than the package delivers: the bubble shows one counter rather than two, tabs are one
  click apart rather than side by side, Open file is a toolbar button rather than a menu path,
  Ping is disabled rather than reporting failure after the click, the record capacity has no
  settings UI, the tag hue comes from the root segment, the overlay's tag list offers only
  tags actually logged, console mirroring skips captured records, and the pipeline's cost
  claim ignored the file sink that is on by default.
- README carries screenshots of the window and the overlay, under `Documentation~/images`.
- The license file is `LICENSE.md`, the name Unity's package layout documents, so Package
  Manager recognises it.

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
- The frame column is dropped when the viewer is narrow. On a phone the message needs the
  width more than the frame number does, and the detail pane still has it.

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
