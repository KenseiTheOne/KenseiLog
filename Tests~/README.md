# Test harness

Checks and tooling for working on this package. Not Unity Test Framework tests, and not part
of the package — the `~` keeps Unity from importing this folder, so nothing here compiles into
`KenseiLog.Runtime` or `KenseiLog.Editor`.

They are kept here because they were written against a throwaway sandbox project, and a
throwaway project is exactly the thing that disappears.

## Setting up a project to run them in

Any empty Unity project (2022.3 or newer) will do.

1. Reference the package from `Packages/manifest.json`:
   ```json
   "com.kensei.log": "file:../../KenseiLog"
   ```
2. Import the **Overlay Demo** sample (Package Manager → Kensei Log → Samples) and move
   `LogDemo.cs` to `Assets/Demo/`. A sample import lands under `Assets/Samples/…`, and what
   the tooling needs is the `LogDemo` type somewhere in the project: `DemoSceneBuilder` writes
   its own `Assets/Demo/LogDemo.unity` from script, and `DemoBuilder` builds that. The scene
   that came with the sample is not used.
3. Copy the files from here:

   | File | Goes in | Why there |
   | --- | --- | --- |
   | `SmokeRunner.cs` | `Assets/Editor/` | reaches editor-only types |
   | `MirrorCheck.cs` | `Assets/Editor/` | same |
   | `DemoBuilder.cs` | `Assets/Editor/` | same |
   | `DemoSceneBuilder.cs` | `Assets/Editor/` | same |
   | `ScreenshotRunner.cs` | `Assets/Demo/` | a `MonoBehaviour`, must not live under `Editor` |
   | `ReadmeSnippets.cs` | `Assets/Editor/` | nothing runs it; it only has to compile |
   | `CompileCheck.cs` | `Assets/Editor/` | reaches the build pipeline |
   | `HoverRepro.cs` | `Assets/Editor/` | only when chasing a stutter; see below |
| `PlayEntryCheck.cs` | `Assets/Editor/` | reaches editor-only types |
| `ReloadSeedCheck.cs` | `Assets/Editor/` | reaches editor-only types |
| `PlayEntryBoot.cs` | `Assets/` | a `RuntimeInitializeOnLoadMethod` for play mode to find, must not live under `Editor` |

## The checks

```
Unity.exe -projectPath <project> -batchmode -quit -nographics \
          -executeMethod SmokeRunner.Run -logFile <log>
```

529 assertions over everything that does not need a GUI: the ring buffer including gapped and
out-of-order sequences, tag matching, collapse, the tag tree, JSON round trips, file rotation
including a rotation that is refused, the buffer under four writers and a reader, and the
regressions listed below. Prints
`SMOKE RESULT: PASS` or `FAIL (n)` and exits non-zero on failure, so it is usable as a gate.

Each scenario is run inside a guard of its own, and a scenario that throws is counted as a
failure rather than ending the run. Before that, a throw ended `Run` where it stood: the
report was never printed, `Exit(1)` was never reached, and `-batchmode -quit` returned zero -
a harness reporting success because it had fallen over.

Anything that writes to disk writes under `%TEMP%/kenseilog-smoke.<process id>`, and the run
empties that directory at both ends whether or not it passed. Pointing the file checks at
`persistentDataPath` meant every run pushed the developer's own logs out of the rotation and left
its own behind. The process id and the sweep on the way in are for the other half of it: the
sink names each file from the highest index it finds, so leftovers from a run that was killed
shift the next run's indices and turn checks red with nothing wrong in the package - and two
editors on one machine, a checkout and a worktree, share a temp directory and would otherwise
delete each other's files mid-check.

Each regression check names the bug it guards, because the interesting ones were all silent:

- foreign logs echoed back into the console, doubling every `Debug.Log`
- the console sink mirroring records it had just captured
- ring buffer lookups assuming sequences are contiguous, which they stop being the moment the
  file sink skips the dev channel
- a record that arrives out of order, which one inversion was enough to make a consumer
  re-copy the same batch on every poll for the rest of the session
- file settings frozen at construction, so `LogCore.Configure` did nothing for them
- a rotation that cannot shift the files aside, which used to close the writer for good
- a retained count lowered between runs, which orphaned every file above the new limit
- a sink that throws, which took the file sink and the caller's frame down with it
- a collapsed view's prune, which stopped at the first row pointing at a late record and left
  everything expired behind it
- a row count standing still while a full ring buffer moves underneath it, which is what the
  window repainted on
- a process that is not the editor - an asset import worker, an out-of-process profiler - opening a
  session file beside the editor's in the directory they all resolve to
- a session file path recorded once at open, which a rotation then moved on without it
- a log written from a job that `JobHandle.Complete` or `Run` executes on the main thread: the
  thread id matched, `Time.frameCount` threw, and the record never reached a sink - from a Burst
  job, an abort of the job that logged it, every frame
- carrying on with the session file made conditional on reading it back, which entering play mode
  with Clear on Play set never does
- seeding reaching below the file this editor session started with, on the strength of ids that
  only looked like one run: yesterday's short file numbers below everything an editor that has
  been up an hour holds
- a rotation on the last record before a reload, whose empty read returned before the reach-back
  added for exactly that case
- a window that kept 8192 records, and a recompile that read back 2 MB of the session: a long
  session came back shorter than it went in, with nothing to say so
- a long editor session pruning its own first files at rotation, which are what the window is
  rebuilt from after a recompile - and a sink carrying on with a file, which opened none of the
  ones behind it, has to know them by their headers
- a seed reading the whole day back after every recompile, when Clear on Play had already
  dismissed all but the last run of it
- the reserve that carries the numbering across a reload, which nothing here ran until the
  session checks went through the sink's own wiring rather than reopening the file themselves
- a session joined midway, which is what the package resolving again in a running editor leaves
  behind and therefore what the first session on a new version always is: with nothing saying
  where that session began, the reach-back stayed off until the editor was restarted
- a null tag, which reached the viewers and threw there
- the escapes the writer uses on purpose, which JsonUtility read wrong: a lone high surrogate
  lost the whole record, a lone low one emptied the message, an escaped NUL cut it short - and a
  parser that reads the writer's lines has to take every one of them, since a line it refuses
  goes to JsonUtility and would be slow and silently wrong at once
- a half surrogate pair, which the encoder turned into U+FFFD
- a file written by a newer schema, and a file holding a header and nothing else
- a context id past 32 bits, which an `EntityId` from Unity 6.4 is, coming back whole from the
  file through both the fast parser and JsonUtility (0.17.0)
- a line mirrored into the console coming back after a reload as a second, foreign record
  (0.17.0)
- a record that landed between the overlay's last poll and the viewer opening, or a filter
  change, listed by the rebuild and copied again by the next poll: a second row, or a collapsed
  row counting it twice (0.17.1)
- `LogOverlay.Collapsed` set with the viewer closed, which built the list and left it built, so
  every record after it was formatted into a row nothing read (0.17.1)
- the overlay drawn with a Layout pass it never used, and the screen's DPI, safe area and size read
  in every GUI event: the view must run without `useGUILayout`, a second Ensure must not add a
  second one, and nothing but the once-a-frame refresh may read `Screen` - in the overlay, its
  view, the bubble gesture or anything nested in them - with the safe area read every frame for a
  while after a change of size (0.18.0)
- `LogOverlay.Visible` left `false` from one run into the next when play mode is entered without a
  domain reload (0.18.0)
- `LogOverlay.Visible`: set before the overlay exists, set from another thread, hiding an open
  viewer, refusing `IsOpen` while hidden, and still counting what arrives meanwhile (0.18.0)
- `OverlayBubbleMode.OnProblem` asleep through plain logs, awake on a warning or an error, asleep
  again after a clear, and shown while the viewer is open; and a zeroed `LogConfig` keeping the
  bubble as it was (0.18.0)
- the collapsed bubble's press, drag and release, now that it is no longer a `GUI.Button`, driven
  through the method the view hands each pointer event to: a tap opens, a drag of (50, 30) points
  moves it by exactly that at scale 1 and 2.5 and opens nothing, a press let go off it opens
  nothing, a press that misses it is left to the game - not taken and not used up, while the
  bubble's own press, drag and release are - five or ten pixels on a phone is not a drag, and the
  pointer is let go after every one of them and when the app goes to the background (0.19.0)
- a right click in the middle of a drag on the bubble: the right press and release are used up
  and leave the drag and the pointer as they were, the left release over the bubble then opens
  nothing and the bubble stays where the drag took it; and a left press while the bubble still
  holds the pointer - its release lost - re-arms it, so the next tap opens the viewer, while a
  press of either button off it lets the pointer go and is left to the game, press and release,
  at scale 1 and 2.5 (0.19.0)
- the baked bubble: its size in pixels is its size in points times the scale, rounded up, at 1,
  2.75 and 4; no count shape changes that or spills out of its slot; `12` and `13` differ only
  inside the slot; the plate lights for a warning or an error and for nothing else, with its
  edges on whole pixels - no column or row of half colour - at each scale; and a bake that keeps
  the size keeps the texture (0.19.0)
- the bake's pace: fifty changes of a count in a tenth of a second bake at most twice and the
  last of them is not lost, a first warning or error is baked at once, `1500` to `1501` - taken
  in through the overlay's own intake - bakes nothing while `1600` does, and the number standing in for the count's text changes exactly
  when the text does (0.19.0)
- the package's own digits: every character a count can hold leaves ink at scale 1 and 4, none is
  cut off by the mask it is rasterised into, and no two are alike; at scale 1 the six and the
  nine stay open where the eight is closed (0.19.0)
- closing the viewer bakes and places the bubble for the counts and the scale of that moment,
  for the Repaint of the same frame (0.19.0)
- in a Linear project the bake puts every pixel through the sRGB curve, and in a Gamma one leaves
  it byte for byte (0.19.0)

## Entering play mode

```
Unity.exe -projectPath <project> -batchmode -nographics \
          -executeMethod PlayEntryCheck.RunWithDomainReload -logFile <log>
Unity.exe -projectPath <project> -batchmode -nographics \
          -executeMethod PlayEntryCheck.RunWithoutDomainReload -logFile <log>
```

No `-quit`: each enters play mode for real, reads the window once it has, and leaves through
`Exit` - on its own after a minute if the entry never comes. Prints `PLAYENTRY RESULT: PASS` or
`FAIL` with what was wrong, and exits non-zero on failure.

It guards **Clear on Play running after the run had begun**, which emptied the window of what
the run logged from its first frame. `SmokeRunner` cannot see that: the clear, the domain reload
and `RuntimeInitializeOnLoadMethod` are put in order by the editor, and the fault lived in the
order. The check looks for the line `PlayEntryBoot` logs before the first scene loads, exactly one
*Entered play mode* ahead of it, and nothing from before Play was pressed. It switches the
project's Enter Play Mode Options and Clear on Play to what it needs and leaves them so, which is
another reason it belongs to a sandbox. On 0.16.1 both modes failed with *what the run logged
before its first scene is not in the window*.

## Coming back from a reload

```
Unity.exe -projectPath <project> -batchmode -nographics \
          -executeMethod ReloadSeedCheck.Run -logFile <log>
```

No `-quit`: it logs through the package with MirrorToUnityConsole on, plus one plain Debug.Log,
turns the console's Clear on Recompile off, reloads the domain for real and reads the window.
Prints `RELOADSEED RESULT: PASS` or `FAIL` and exits non-zero on failure. It guards **mirrored
lines coming back after a reload as a second, foreign record**: each logged line must be in the
window once, none under the Unity tag as `Tag: message`, the foreign line once. If the console no
longer holds the mirrored lines after the reload it fails rather than passing with nothing
checked. Clear on Recompile is an EditorPrefs flag shared by every project on the machine: it is
put back at the end, and a killed run has it put back by the next domain load in the sandbox. On
the 0.16.2 matching it failed with *3 mirrored lines came back a second time under the Unity
tag*; it passes on 6000.3.13f1, 6000.5.3f1 and 6000.6.5f1, where batchmode keeps the console and does reload.

## What nothing here checks

The list above is what is guarded. This is the other half: what is believed but has not been
watched happen, what no check would catch, and what was looked at and left. It is here rather
than in the CHANGELOG because the CHANGELOG is for what shipped, and rather than in a commit
because a commit is for what changed. Neither has anywhere to put "not sure yet".

**Every entry says how it leaves.** An entry that cannot say what would remove it is a wish, not
a note, and belongs in neither place. This section is meant to shrink.

### Believed, not watched happen

The headless checks cover these as far as a batchmode process can, which is not as far as a
running editor. Each was reasoned about and shipped; none has been seen working.

- **An out-of-process profiler leaves `logs/editor` alone.** Added to the guard alongside the
  asset import worker in 0.14.2, on the same reasoning, but never observed - the worker half was
  confirmed with `lsof` on a real project and this half was not.
  *Leaves when:* a standalone Profiler has been run against a project and no file carrying
  `Editor started` appeared in `logs/editor` for it.
- **Entering play mode keeps to one file.** 0.14.3. With Clear on Play and domain reload both at
  their defaults this is the reload that happens dozens of times a day, and the failure it fixed
  was the pruning taking the morning's records off the disk on the fourth entry.
  *Leaves when:* five entries into play mode in a row have left one file in the directory, with
  what was logged before the first still in it.
- **A recompile straight after a rotation comes back with its history.** 0.14.4 and 0.14.6. The
  harness drives this, but through its own stand-in for a domain reload rather than a real one.
  *Leaves when:* a recompile immediately after a file passes the size limit has left the window
  holding the session rather than a handful of records, with no new file behind it.
- **A long session comes back whole, at a cost somebody can live with.** 0.16.0. The harness
  seeds twelve thousand records across several files through its stand-in, and the cost of a
  bigger session was measured headless; neither is a real editor on a real project, where the
  session is whatever the project logs and the heap is shared with everything else in it.
  *Leaves when:* a session past several rotations has been recompiled in a real project and came
  back holding everything since the last clear, and the recompile was not noticeably slower.

- **The in-game list keeps following the newest line when the notice at the top appears.**
  The notice takes twenty points out of the list between one poll and the next, and the old
  scroll offset used to read as having scrolled away - the view froze on a window the ring then
  aged out from under it. Pinned now, but it needs play mode and a record landing at a particular
  moment in the frame, which the harness cannot arrange.
  *Leaves when:* the viewer, open at the newest line, has been watched staying there as a burst
  first pushes records out of the ring.
- **The baked bubble is as cheap on a phone as on a desktop, and a finger drives it as the checks
  do.** 0.19.0. Measured in a desktop development player only, and its press, drag and release
  driven by the checks and by synthetic events in that player, where a translated `GUI.matrix`
  stands in for the real pointer. The 2.5 ms a frame that started 0.18.0 and this release was
  measured on a low-end Android phone with 0.17.
  *Leaves when:* the collapsed bubble has been profiled on a low-end Android phone and tapped,
  dragged and released off itself there.
- **KenseiLog compiles and finds context objects on 2022.3 and 6000.4.** 0.17.0. The EntityId
  change was compiled and run on 6000.3 and 6000.5 only. 2022.3 takes the plain int branch, which
  is the code that shipped before; 6000.4 takes the 64-bit branch on the strength of the
  documentation (`EntityId.ToULong`/`FromULong`, `Object.GetEntityId`), and what Unity calls the
  id field of its internal `LogEntry` there is unknown - a name or type the bridge does not
  expect turns it off quietly. Should it still be an int there, the bridge widens it with its
  sign while `ContextId.Of` goes through `EntityId.ToULong`: if 6.4 pads a negative id with
  zeroes instead, the two disagree for runtime-made objects and "open" finds nothing for them.
  *Leaves when:* SmokeRunner has passed on 2022.3 LTS and on a 6000.4 editor, the console bridge
  checks included.

### Known gaps, where a check would not catch it

Mutations that leave the whole harness green. Recorded so that the next person to trust it knows
where the floor is.

- **Nothing sees whether `Install` actually calls `SessionPlan.Resolve`.** The decisions are
  covered and the wiring is not: the harness calls `Resolve` itself, because the only thing that
  calls `Install` is a domain reload.
  *Leaves when:* the harness can drive a real reload, or the call moves somewhere reachable.
- **Recording the session path before `Dispose` rather than after is not caught.** The two differ
  only when a rotation is running on a logging thread at that moment, and nothing here runs one.
  *Leaves when:* a check exists that rotates from another thread while the file is closing.

### Looked at and left

Real, understood, not worth what fixing them costs today. Each says what would change that.

- **`FileSink.WrittenBefore` fails open.** A name it cannot parse answers "not below the floor",
  which lets a file through. Every caller today passes a name this sink wrote, so it is
  unreachable - but the safe answer for a barrier is the other one.
  *Leaves when:* any path can hand it a name from somewhere else.
- **The harness winds the record counter back without unregistering the live sinks.** Standing in
  for a reload means setting the counter to zero, and anything logging through `LogCore` while it
  is down - a foreign log on a background thread - would take a duplicate id in the developer's
  own file. Harmless in batchmode, which is how these are run.
  *Leaves when:* anything offers to run them from a menu inside a live editor.
- **The overlay's notice describes the file sinks as they are, not as they were.** It asks each
  pass whether a writing file takes each channel, which is right for everything logged since -
  but a record that left the ring was written, or not, when it was logged. Turn `WriteToFile` or
  `FileIncludesDevChannel` on after records have gone and the notice sends the reader to a file
  those records never reached. Getting it exactly right means knowing, per record, whether a file
  took it at the time.
  *Leaves when:* anything relies on the notice staying true across a change to the file settings
  in the middle of a session.
- **A killed run leaves its scratch directory behind.** Each run empties its own
  `%TEMP%/kenseilog-smoke.<pid>` at both ends and touches no other, so one that is killed part
  way through is never collected by anything.
  *Leaves when:* the accumulation is ever noticed on a real machine.
- **A foreign line that reads exactly like a mirrored one is taken for it.** After a reload the
  seed counts each of the package's own records as the line the console sink would have mirrored,
  `Tag: message`, whether or not mirroring was on: it runs before the project configures anything,
  and the file does not record it. A genuine Debug.Log whose first line is exactly that, for a
  record of the same session, is matched off and does not come back into the window (the console
  still has it).
  *Leaves when:* the session file records whether each record was mirrored, or such a line is
  reported missing.
- **A mirrored line from before the window's last clear still comes back after a reload.** The
  seed reads the session from the file the window was last cleared in, so a record in an earlier
  file is not there to match its mirror against, and the console, cleared or not by the user,
  still holds it: it returns under the Unity tag. The same for a session the sink joined midway,
  which reads the file in hand alone. It needs a rotation and then a clear in the window but not
  in the Console. Not new in 0.17.0 - before it, captured lines from those files came back too -
  and closing it means reading the files the floor exists to skip.
  *Leaves when:* it is reported, or the seed reads earlier files for another reason.

## The branches a build takes

```
Unity.exe -projectPath <project> -batchmode -quit -nographics \
          -executeMethod CompileCheck.Run -logFile <log>
```

Compiles the package for Standalone, WebGL and Android, in both the release and the
development configuration - they are not the same code, since a release build has the compiler
remove the Dev methods and every argument expression handed to them - and prints
`COMPILE RESULT: PASS` or `FAIL (n)`. What it asserts is that `KenseiLog.Runtime.dll` came out,
not that something did: `CompilePlayerScripts` does not throw on a compiler error, it returns
whichever assemblies were produced. The editor compiles with `UNITY_EDITOR` defined and for no target in particular, so
the code that only exists in a build is never seen there: the call site trimmed outside the
editor, the file sink turned off on WebGL. It compiles rather than builds - seconds against
minutes, and a build would need a scene and an icon to say the same thing about a compiler
error. A target whose module is not installed is reported and skipped.

It earned itself on the first run, on a `#if !UNITY_EDITOR` block that did not parse. Nothing
in the editor had ever looked at it.

## Chasing a stutter

`HoverRepro.cs` opens **Window → Kensei → Hover repro**: a bare `ListView` of 8192 rows with
the pieces this package puts on a row switchable one at a time - a tooltip, six cells rather
than one, a context menu manipulator, a rebind fifteen times a second. It reads out the worst
gap between editor ticks in the last second, which is what a stall is, and needs no profiler.

Nothing in it belongs to the package: no stylesheet, no records, no polling. So anything it
reproduces with every toggle off belongs to Unity's `ListView` or to the machine, and the log
window is not where the answer is.

**Row tooltip** is the positive control. A tooltip in UI Toolkit is a real window of the
operating system, and building and tearing one down on every row crossing is what froze the
editor on Windows before the tooltips came off the rows in 0.10.0. A fault that flickers the
screen - black bands across the monitors, the compositor recomposing - is that shape rather
than a slow frame, so if this toggle reproduces it and the others do not, what to look for is
whatever else is making a window.

Drop it into a project that shows the stutter; it does not need this package to be installed.

## The README

`ReadmeSnippets.cs` is the README's code, compiled. Nothing runs it. It exists so that a
snippet which has stopped matching the API cannot sit in the README looking authoritative -
the first thing anyone tries is the thing they copied out of it. It found one on the way in:
`Logger` collides with `UnityEngine.Logger`, so the one-tag-per-file snippet needed a
`using Logger = KenseiLog.Logger;` above it to compile in an ordinary file.

## The rest

`MirrorCheck.Run` reproduces console mirroring end to end and counts how many times a marker
reaches the console — the shape of the doubling bug, kept because a counter proves it better
than an assertion does.

`DemoSceneBuilder.Build` regenerates `Assets/Demo/LogDemo.unity` from script, so the scene is
reproducible rather than something someone once assembled by hand.

`DemoBuilder.BuildWindows` produces a windowed development player. It writes beside the
project by default; pass `-buildout <path to exe>` to place it elsewhere.

`ScreenshotRunner` only wakes up when the player is launched with `-screenshots`, walks the
overlay through its states and captures each one:

```
KenseiLogDemo.exe -screenshots -shotdir <dir> -screen-width 1000 -screen-height 600 -screen-fullscreen 0
```

That is how the overlay images in `Documentation~/images` were made. There is no equivalent
for the editor window: reading the screen from an editor script captures whatever is actually
in front, which is the desktop as often as the window, so that screenshot is taken by hand.
