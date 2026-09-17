# Changelog

All notable changes to this package are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.14.1] - 2026-09-17

### Fixed

- An asset import worker no longer writes into the editor's session directory. A worker
  reloads the domain exactly as the editor does, so the `[InitializeOnLoadMethod]` that
  installs the editor sink ran there too - and the directory is keyed by the project path,
  which a worker shares. Each one opened a session file of its own holding a header and
  nothing else. The editor's next reload then continued whatever file was newest, found the
  worker's, read no records out of it and answered by starting another. Three things followed:
  the file count climbed with every import, the window fell back to the console on each
  recompile instead of restoring its own history, and pruning could delete the file the editor
  was writing to. Workers now leave `Install` where it starts; there is no window in one to
  fill and nothing in one worth keeping.
- The editor continues the file it opened rather than whatever is newest beside it. The path
  is remembered in `SessionState`, which closes the same hole from the other end: a batchmode
  run after the editor was shut, or a tool of somebody's own writing into that directory, can
  no longer be appended to by mistake. `FileSink` takes the file to continue as a third
  constructor argument, and without one still takes the newest - the right answer while it is
  the only writer there.

### Changed

- README corrections, all of them places where it asserted something the code does not do.
  The install snippet pins a tag, since tracking `main` across 0.14.0 silently renames every
  call in a project. `IChannelFilteredSink` is documented - it was public, it is what keeps a
  dev call in a development build down to the message string, and the page on writing a sink
  named only the other two interfaces. The overlay's detail pane is about a third of the
  overlay height to a ceiling of 180 points, not the fixed height claimed. The `file:` example
  says what the path is relative to. And "one file per editor session" now says per editor
  *process*, which is what the fix above makes true.

## [0.14.0] - 2026-09-16

### Changed

- **Breaking.** Every method on the facade is renamed, on both channels: `Info`, `Warning` and
  `Error` for prod, where they were `Prod`, `ProdWarning` and `ProdError`; `DevInfo`,
  `DevWarning` and `DevError` for dev, where the first of those was `Dev`. `Logger` carries the
  same six. Nothing else about either channel moves - the dev calls are still the ones the
  compiler removes from a release build, arguments and all.

  The old names asked for the channel first and the level second, so the level at the plain end
  of the prod names was missing entirely: `Log.Prod` was a log, `Log.Info` says so. And with
  both channels equally awkward to type, the one that won was whichever you had the muscle
  memory for - which is `Dev`, the channel that never reaches a shipped build. The plain names
  belong to prod, and the dev channel now spells itself out.

  Every call site has to be renamed and the compiler finds all of them. A find-and-replace does
  it, longest name first so a shorter one does not eat the others: `ProdWarning` -> `Warning`,
  `ProdError` -> `Error`, then `Prod(` -> `Info(`, then `Dev(` -> `DevInfo(` - that last one
  paren-anchored, or it will hit `DevWarning` and `DevError` too.

## [0.13.1] - 2026-09-16

### Fixed

- The session file carries the related object, so Ping works after a domain reload on the
  records this package wrote itself. It was the one field of a record the file left out: the
  window read everything else back after a recompile - tag, channel, frame, call site - and
  then offered every one of its own records a Ping button that could only say nothing had been
  recorded. The id is honoured only when the editor reads its own session file back. An
  instance id means something inside the editor session that issued it and nowhere else, so a
  file opened with **Open file** comes back without it: from another machine's build it would
  resolve here to whatever happens to hold that number, and Ping would jump to an unrelated
  object - worse than a button that does nothing. The field is written only when there is one,
  and the schema version does not move: an older reader ignores a key it does not know, and an
  older file reads as it always did.

## [0.13.0] - 2026-09-16

A pass over everything away from the happy path: a failing file sink, a full ring buffer, a
domain reload, a second play session, a phone with a notch, a tester's file from a newer
build. Most of what follows was silent - the setting applied, no error appeared, and the
behaviour simply stayed where it was.

### Added

- Compiler messages reach the window as the compiler raises them, through
  `CompilationPipeline.assemblyCompilationFinished`. Nothing else could: a failed compile does
  not reload the domain, so the seeding at load never runs for it, and by the time a later
  compile succeeds Unity has removed those errors from the console. Read at load or polled from
  the console, a compile error would never have appeared here at all - which is the one thing
  someone using this window instead of the Console cannot do without. They go into the buffer
  rather than the file, so they live exactly as long as the Console's own copies.
- The session's boundaries are marked in the log: `Editor started`, `Scripts reloaded`,
  `Entered play mode`, `Exited play mode`, each carrying the wall-clock time. The clock is a
  static and starts again from zero with every domain, so the Time column read 812.33, 812.40,
  0.05 with nothing to say why. Carrying the clock across would have made the column continuous
  and false, hiding a recompile that took eight seconds; this leaves the reset where it is and
  explains it. The wall clock in the text also makes every row's absolute time derivable, and
  keeps Collapse from folding every reload of a session into one row.

- The window reads its own history back from the session file when the domain reloads, instead
  of starting empty and recovering what it can from Unity's console. The difference is what
  comes back: a record from the file keeps its tag, its channel, its frame and its call site,
  none of which the console has anywhere to store - and a `Log.Dev` call was never in the
  console to recover at all. The console still supplies the compiler messages, which arrive by
  a path this package has no sight of and so cannot be in the file.

- The editor writes what it logs to a file of its own, under `logs/editor`, and keeps one file
  per editor session rather than per domain reload. Without it the editor's records lived
  nowhere but a buffer that is rebuilt on every reload, so a recompile took all of them - and
  the runtime's file sink cannot do the job, being gated on play mode precisely because it
  starts a run by shifting the files aside. `EditorSink.WriteSessionFile` turns it off.
- `FileSink` can be opened to add to the file it finds rather than starting a run
  (`continueExistingFile`), which is what makes one file per editor session possible: no shift,
  no second header, and the byte count carried over so the size limit still means the size of
  the file.

- `LogConfig.FileDirectory`: where the log files go, empty meaning `persistentDataPath/logs`.
  Applied on construction and by a later `Configure`, which starts a file in the new place.
  The checks use it so that running them no longer writes into the developer's own log
  directory and pushes their real logs out of the rotation.
- Session headers carry a schema version, and the reader refuses a file written by a newer one
  instead of parsing it into plausibly wrong data and reporting nothing.
- `Tests~/ReadmeSnippets.cs`: the README's code, compiled. Nothing runs it; it exists so a
  snippet that has stopped matching the API cannot sit in the README looking authoritative.
  It found one immediately - see below.
- `Tests~/CompileCheck.cs`: compiles the package for Standalone, WebGL and Android. The editor
  compiles with `UNITY_EDITOR` defined and for no target in particular, so the branches that
  only exist in a build - the trimmed call site, the WebGL default below - were never compiled
  by anything until someone built. It found one that did not parse.
- `LogCore.ProjectRelativePath`, which is that trimming, public and compiled everywhere so it
  can be checked. It only runs in a build, where nothing can look at it.
- `LogCore.ReserveSequencesThrough`, which moves the sequence counter past a record that
  already exists - what the editor's seeding needs, since the counter restarts with every app
  domain and the session file does not.
- `LogSessionReader.ReadTail`, for reading the end of a file without the session around it.
- `Documentation~/index.md`, and `documentationUrl`, `changelogUrl` and `licensesUrl` in
  `package.json`.

### Removed

- `current.jsonl`. The file being written is now simply the highest-numbered one, and nothing is
  renamed - see the entry under Changed for why. **This is the breaking change in this release.**

  A directory written by 0.12.x needs nothing done to it: the next run gives the old
  `current.jsonl` a number of its own, so the last run before the upgrade is kept rather than
  orphaned under a name this version does not look for. The old `log.1`…`log.3` are numbered the
  other way round, oldest last, so for a few runs the sequence is out of order until they age
  out. Anything of your own that reads `current.jsonl` by name needs to read the highest number
  instead.

### Changed

- Reading the session file back parses only the lines it keeps, and is skipped entirely when the
  reload is the one that enters play mode with Clear on Play set - the reload people do dozens
  of times a day, whose seed is thrown away a callback later. It was the most expensive thing
  the package did: up to a hundred milliseconds and ten megabytes of garbage per recompile.
- A sink says for itself which channels it takes, through `IChannelFilteredSink`. The gate that
  skips building a record nothing would accept used to recognise only this package's own file
  sink, so `LogCore.File.Reconfigure` could open the dev channel without the gate hearing about
  it - and no sink anyone else wrote could close it.

- Log files are numbered in the order they were written - `log.0001.jsonl`, `log.0002.jsonl` -
  and the highest is the one being written. There is no `current.jsonl` and nothing is ever
  renamed: rotation opens the next number and housekeeping deletes what falls past
  `RetainedFileCount` behind it.

  The old scheme shifted every file along on each rotation, which numbered them backwards -
  `log.1` was the newest of the old ones - and meant a file could be renamed under anything
  holding it. Renaming is also what fails on Windows when another process has the file open,
  which is the failure two of the entries under Fixed are about. A file now keeps its name for
  life:
  the one a tester mentions stays the one they meant, and a session opened in the window cannot
  turn into a different session while it is open.

- A record is not built for a channel nothing will take. In a development build the sink list
  is the file sink with the dev channel off, so every `Log.Dev` call built a record - and
  unwound a stack trace, if it was an error - only to be dropped by the first sink that looked
  at it. A development build is what gets profiled on a device, so it was the one build type
  that misreported what logging costs.
- The file sink builds its JSON line before taking its lock rather than inside it. A shared
  `StringBuilder` was what forced the lock to span the whole write, so every thread that logged
  waited on another thread's formatting as well as on the disk. The flush on an error stays
  where it is.
- The window's tag tree is rebuilt when a tag appears for the first time, and an arriving
  record adds one to the nodes its tag passes through. Rebuilding the tree to read its own
  totals cost a node, a list and a substring per segment per tag, fifteen times a second, for
  as long as anything was logging.
- A row already showing a record is left alone instead of being bound to it again. A rebind
  takes a lock on the ring, binary-searches it and formats five strings, and every visible row
  is rebound whenever anything in the tab changes.
- A stale file that cannot be deleted during housekeeping is skipped rather than taking the
  run's log down with it. Housekeeping also happens after the file is open rather than before,
  so a directory that cannot be tidied costs the run nothing.
- The trimmed call site is worked out once per call site rather than once per record. The
  compiler hands the same interned string to every call from a given line, so it can be looked
  up by reference.

- The file sink is off by default on WebGL. `persistentDataPath` there is a virtual filesystem
  inside the page, so at the default limits the rolling history held around twenty megabytes
  of browser heap for a build with no way to fetch any of it back.
- Outside the editor, a record's call site is trimmed to the part from `Assets` or `Packages`
  onwards. `CallerFilePath` is resolved by the compiler, so the `Prod` methods - which carry no
  `Conditional` attribute - shipped the absolute path of the machine that built them, and wrote
  it into the file a tester sends back.
- The overlay follows the newest record unless the reader has scrolled away from it, matching
  the window's Follow. It also builds its display rows when it opens rather than as records
  arrive, so a build shipped with the overlay enabled pays for the counts and nothing else.
- The overlay lays itself out inside `Screen.safeArea`.
- The tag pane in the overlay takes its width out of the list instead of covering it.
- `EditorPrefs` keys are scoped to the project. They are stored per Unity install, so the
  folded-tag set collected the tags of every project the editor had ever opened. Existing
  settings start again from the defaults once.
- The search box in the window waits 150ms before filtering.
- The rename popup closes on a domain reload rather than returning with a null callback.
- `LogCore.Configure` is documented as main thread only.
- The tree handle's tooltip sits on the arrow rather than on the 13px strip that runs the full
  height of the window, in the path the pointer takes between the tree and the list. It was
  moved while chasing a stutter in the editor that turned out to be the display's adaptive
  sync, and nothing to do with this package - but a tooltip belongs on something the size of a
  control either way.
- The one-tag-per-file README snippet gained `using Logger = KenseiLog.Logger;`. `Logger`
  collides with `UnityEngine.Logger`, so it did not compile in an ordinary file. The README now
  documents `LogRecord` field by field and `MemorySink`, and its API snippets compile as
  written.

### Fixed

- The report about a sink that threw reaches the sinks. It was kept out of the pipeline so it
  could not come back through the foreign-log handler and take a sequence ahead of the record
  still being written - but the re-entrancy flag already ends that recursion and the ring buffer
  settles a late arrival back into place, so both hazards are covered without it. Suppressed, the
  one diagnostic about a broken sink reached `Player.log` and never the file a tester sends back.
- The overlay measures a drag from where the finger went down rather than from one event to the
  next. A single frame's movement is a flick detector: at a phone's scale the threshold came to
  most of a thousand pixels a second, so a slow deliberate scroll never crossed it and the
  release selected a row the reader meant only to pass.
- The window keeps its place when the ring buffer drops rows from under it. The list addresses
  rows by position, so on a full buffer the contents slid up under a reader who had scrolled
  back - and the selection moved with them, leaving the highlight and the Open button on one
  record while the detail pane showed another, since a changed selected index raises no event.
  Both are followed by sequence across the prune.
- The Ping button no longer claims a record was written without a related object when what
  actually happened is that the object did not survive a domain reload.

- A list row bound from one source and reused for another kept the text it already had. Every
  source numbers its records from one - a loaded file, a fresh editor - so a pooled row matched
  by sequence and took the rebind for a no-op, leaving a whole page of a build's log on screen
  under another build's tabs, counts and detail pane.
- Console entries the session file cannot account for are seeded again after a reload. Reading
  the file alone dropped whatever reached the console without reaching the pipeline: what was
  there before this sink existed, what another package logs from its own load code, what was
  logged after the file closed for the reload. Everything is matched off against the file by
  first line and count, so nothing is shown twice - and that is true whether or not compiler
  messages travel through `Debug`, which is native behaviour this package should not be resting
  a design on either way.
- Bit 13 is no longer treated as an error when reading the console. It is `StickyError`, which
  says an entry survives a manual clear rather than anything about severity, so a sticky warning
  drew as an error.
- `Clear` stays cleared across a domain reload. The file keeps everything, which is the point of
  it, but a reload was handing back what had been dismissed - and with Clear on Play set, every
  play session ended with the cleared records back in the list.
- Housekeeping no longer deletes a file it did not write. A `log.crash.jsonl` somebody kept by
  hand matches the pattern without matching the scheme, and was being deleted as the oldest.
- The file left by the previous naming scheme is given a number instead of sitting in the
  directory for good, holding a size limit of disk and the last run before the upgrade.
- Two checkouts of one project no longer share an editor session file. `persistentDataPath` is
  keyed by company and product, so two worktrees in two editors were writing to the same file
  and reading each other's records back.
- Continuing a session file that could not be read back would have written records numbered from
  one after records numbered in the hundreds, leaving the file unsorted; it starts a new one.
  The session flag is only set once a file has actually opened, for the same reason.
- A warning about a file that does not exist yet named `null`; a rotation that could not open the
  next file said logging was off and then carried on writing.

- A rotation that could not proceed closed the writer and returned without reopening it, so
  the rest of the run wrote nothing. It reopens either way - carrying on with the file it
  already had - and counts from zero, so a stuck rotation is retried once per size limit rather
  than once per record.
- The warning that reported it went out through `Debug` without the suppression flag, came
  back through the foreign-log handler and took a sequence ahead of the record still being
  written. The ring buffer binary-searches on that sequence, so one inversion was enough to
  make a consumer re-copy the same batch on every poll for the rest of the session. `Warn`
  raises the flag, and the buffer settles a late arrival back into order as it lands.
- Writing, flushing and closing the file are guarded. Only opening was, so a full disk raised
  an `IOException` out of `Write`, out of `Emit`, and into whatever game code had called `Log`.
  `Emit` and `FlushSinks` also isolate each sink from the others, so one bad sink cannot cost
  the file sink the records around a fault.
- A null tag is normalised where the record is built. It used to reach the viewers intact and
  throw there, out of a dictionary lookup or a palette hash, with a stack that named neither
  the tag nor the call that passed it.
- The window's list stopped repainting once the buffer was full: it repainted on a changed row
  count, and a full ring drops one record per record, so the count held still while the
  contents moved beneath it. Views count revisions now, which also covers Collapse, where a
  repeat changes a row without changing how many rows there are.
- Pruning a collapsed view stopped at the first row pointing at a late record and left
  everything expired behind it, so tabs filled with a band of `(record expired)` that nothing
  would ever clear.
- The counts in the tag tree froze at whatever they held when each tag was first seen.
- Seeding the window from Unity's console ran one reflection call outside its guard. A throw
  there came out of the `InitializeOnLoadMethod` before the sink was registered, so the window
  opened looking healthy and recorded nothing, once per domain reload.
- An open log file survives a domain reload instead of dropping back to live logs in silence.
- Unpausing takes what arrived while the view was paused.
- Deleting a tab in front of the active one moves the active index with it instead of landing
  on the tab that slid into its place. Renaming captures the tab rather than its position.
- `EditorSink.Capacity` clamps before it stores. An out-of-range value was written to
  `EditorPrefs` and only then handed to the buffer, which threw - leaving zero saved, and the
  sink throwing on every reload afterwards until the preference was cleared by hand.
- The window's copy buffer grows with its source. `CopyNewerThan` stops when its destination is
  full, so raising `EditorSink.Capacity` silently lost the newest records.
- The Open and Ping buttons slid out of the window under a long stack trace. The detail pane
  is a fixed 140px, the body is a `Label` - as tall as its text - and a flex item does not
  shrink below its content unless it is told to, so the text grew past the pane and carried the
  row beneath it out of sight. The text scrolls inside the pane now and the buttons keep their
  corner.
- An expired row no longer keeps the colour, severity and columns of the record that had it
  before, and the stylesheet lookup no longer dereferences a path that a package compiled into
  a DLL does not have.
- Statics that describe one play session are reset at the start of the next. With Reload Domain
  turned off they survived, so the overlay opened on the previous session's records with the
  previous session's frame numbers, the scene-systems flag sent `ApplyConfig` into a phase with
  nowhere to put a `GameObject`, and a sink muted after throwing stayed muted.
- `OverlayRecordCapacity` was accepted and ignored on every `Configure` after the first. A new
  capacity builds a new sink and carries the records across.
- Removing the overlay left its sink registered, filling a buffer nobody reads - and made the
  next `Configure` believe an overlay sink was already in place.
- Lowering `RetainedFileCount` orphaned every file above the new limit, since the shift only
  ever touched indices inside the count.
- A file holding a header and no records opens as an empty session. That is the build that died
  during startup, which is the case this package exists for, and its header still names the
  device.
- A lone surrogate - a message cut mid-character - is written as a `\u` escape rather than
  reaching the encoder, which turned it into U+FFFD.
- The overlay's tag pane was drawn on top of the list, so every tap meant for a tag was taken
  by the row button behind it and the pane did nothing at all. Its drag threshold compared a
  squared distance against an unsquared constant, making the real threshold about two and a
  half pixels, so the tremor in a tap discarded it as a scroll. Its list slid under the finger
  whenever the ring wrapped. And it rebuilt the selected record's detail, the collapsed
  bubble's label and every tag row on each of the two-plus `OnGUI` passes a frame.
- `SmokeRunner` reported `PASS` when it fell over: a scenario that threw ended the run where it
  stood, so the report was never printed, `Exit(1)` was never reached and `-batchmode -quit`
  returned zero. Each scenario is guarded, and a throw is a failure with a name. The suite is
  167 assertions, up from the 91 the suite actually ran before - `Tests~/README.md` had been
  claiming 62 for some time - and everything it writes goes to a scratch directory it deletes
  afterwards.

## [0.12.1] - 2026-09-16

### Fixed

- Icons on the level toggles sat above their captions and looked smeared. They had no
  cross-axis alignment, so they rode to the top of the control, and they were the .sml source
  images stretched to fit. Centred now, and drawn from the full-size icons.

## [0.12.0] - 2026-09-16

### Added

- The window seeds itself from Unity's console on load, so it holds what the console holds:
  entries from before it was opened, and entries that outlived the last domain reload. The
  buffer is rebuilt on every reload, so without this the window went blank after each
  recompile while the console beside it kept everything.

  It reads the console rather than replacing the pipeline with it. A console entry has nowhere
  to carry a tag or a channel, so sourcing the window from it would cost both - the two things
  the package exists for.

### Changed

- The detail pane is a selectable `Label` instead of a read-only `TextField`. A `TextField`
  carries a text-editing engine, a selection model and a caret that schedules its own
  repaints, and a focused editor window pays for that every frame whether or not there is
  anything in it. Copying still works.
- Row messages are cut before they reach a label. Clipping happens after layout, so a
  two-hundred-character engine warning was measured and turned into a mesh in full to show
  ninety characters of it, on every repaint, for every visible row.

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
