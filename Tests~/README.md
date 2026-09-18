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

## The checks

```
Unity.exe -projectPath <project> -batchmode -quit -nographics \
          -executeMethod SmokeRunner.Run -logFile <log>
```

254 assertions over everything that does not need a GUI: the ring buffer including gapped and
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
- carrying on with the session file made conditional on reading it back, which entering play mode
  with Clear on Play set never does
- seeding reaching below the file this editor session started with, on the strength of ids that
  only looked like one run: yesterday's short file numbers below everything an editor that has
  been up an hour holds
- a rotation on the last record before a reload, whose empty read returned before the reach-back
  added for exactly that case
- a seed budget spent again on every file it touched, which put an older stretch of the log in
  the window in place of a newer one
- the reserve that carries the numbering across a reload, which nothing here ran until the
  session checks went through the sink's own wiring rather than reopening the file themselves
- a session joined midway, which is what the package resolving again in a running editor leaves
  behind and therefore what the first session on a new version always is: with nothing saying
  where that session began, the reach-back stayed off until the editor was restarted
- a null tag, which reached the viewers and threw there
- a half surrogate pair, which the encoder turned into U+FFFD
- a file written by a newer schema, and a file holding a header and nothing else

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
