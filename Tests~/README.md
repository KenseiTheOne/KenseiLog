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
2. Import the **Overlay Demo** sample (Package Manager → Kensei Log → Samples), which brings
   in `LogDemo.cs` and `LogDemo.unity`. `DemoSceneBuilder` and `ScreenshotRunner` expect them
   at `Assets/Demo/`.
3. Copy the files from here:

   | File | Goes in | Why there |
   | --- | --- | --- |
   | `SmokeRunner.cs` | `Assets/Editor/` | reaches editor-only types |
   | `MirrorCheck.cs` | `Assets/Editor/` | same |
   | `DemoBuilder.cs` | `Assets/Editor/` | same |
   | `DemoSceneBuilder.cs` | `Assets/Editor/` | same |
   | `ScreenshotRunner.cs` | `Assets/Demo/` | a `MonoBehaviour`, must not live under `Editor` |

## The checks

```
Unity.exe -projectPath <project> -batchmode -quit -nographics \
          -executeMethod SmokeRunner.Run -logFile <log>
```

62 assertions over everything that does not need a GUI: the ring buffer including gapped
sequences, tag matching, collapse, the tag tree, JSON round trips, file rotation, and the
regressions listed below. Prints `SMOKE RESULT: PASS` or `FAIL (n)` and exits non-zero on
failure, so it is usable as a gate.

Each regression check names the bug it guards, because the interesting ones were all silent:

- foreign logs echoed back into the console, doubling every `Debug.Log`
- the console sink mirroring records it had just captured
- ring buffer lookups assuming sequences are contiguous, which they stop being the moment the
  file sink skips the dev channel
- file settings frozen at construction, so `LogCore.Configure` did nothing for them

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
