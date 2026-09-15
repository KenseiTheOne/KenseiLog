# Kensei Log

Tagged logging for Unity with two channels, an editor window that splits logs into saved filter tabs, and a viewer that runs on the device.

![The in-game overlay open over a running build](Documentation~/images/overlay-open.png)

In Unity's console a tag is only a word in the message, so filtering for `combat` finds every line that happens to mention combat, and there is no way to keep a view around once you have built it. Kensei Log makes the tag a real field on the record, lets you save the views you keep rebuilding, and follows the same logs into a build — into a file, and onto the screen of the device running it.

## Install

Package Manager → **Add package from git URL**:

```
https://github.com/KenseiTheOne/KenseiLog.git
```

Or, for local development, add a path reference to `Packages/manifest.json`:

```json
"com.kensei.log": "file:../../KenseiLog"
```

The runtime assembly is auto-referenced, so `Log` is available from `Assembly-CSharp` without adding an assembly definition of your own.

## Usage

```csharp
using KenseiLog;

public static class Tags {
    public const string Combat = "Combat";
    public const string Damage = "Combat.Damage";
    public const string Net    = "Net";
}

Log.Dev(Tags.Damage, "hit " + target.name + " for " + damage, this);
Log.DevWarning(Tags.Combat, "no hitbox on " + target.name);
Log.ProdError(Tags.Net, "desync at tick " + tick);
```

Six methods, three levels across two channels: `Dev`, `DevWarning`, `DevError`, `Prod`, `ProdWarning`, `ProdError`.

### One tag per file

When a file always logs under the same tag, state it once:

```csharp
private static readonly Logger Log = Logger.For(Tags.Combat);

Log.Dev("hit " + target.name + " for " + damage);
Log.ProdError("desync at tick " + tick);
```

Naming the field `Log` shadows the static `Log` class inside that type, which is the point —
every unqualified call in the file then carries the tag. Reach a different tag from the same
file with the full `KenseiLog.Log.Dev(tag, message)`.

`Logger` is a struct, so the field costs a string reference and no allocation. Its `Dev`
methods carry `[Conditional]` exactly as the static ones do — the attribute applies to
instance methods too — so they leave a release build with their arguments. The field
initialiser does not: it survives as one assignment per type, which is the whole price.

`Logger.For(Tags.Combat).Child("AI")` gives `Combat.AI`, nested under `Combat` in the tree.

### No tag at all

A tag is not required. `Log.Dev("still here")` writes under `Untagged`, which keeps the log
you are about to delete inside the window and apart from the engine's chatter — reaching for
`Debug.Log` instead buries it under the `Unity` tag. A branch of the tag tree filling up with
these is a fair hint about where a real tag belongs.

Tags are plain strings — nothing has to be registered, and any string works. A `const` holder like the one above only buys you autocomplete and safe renames. A dot in a tag builds a hierarchy: selecting `Combat` in the window also selects `Combat.Damage` and `Combat.AI`.

## The two channels

**Dev calls disappear in release.** They carry `[Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]`, so outside the editor and development builds the compiler removes the call *and every argument expression*. Building the message costs nothing because it never runs.

**Prod calls always compile.** In a shipped build they are the only diagnostics you get.

That split only works if it is kept honestly. Decide once what counts as a prod event and write it down — otherwise everything drifts into `Dev` out of habit and the shipped build tells you nothing. A reasonable starting rule:

| Prod | Dev |
| --- | --- |
| state machine transitions | per-frame values |
| network errors, retries, disconnects | tuning and balance numbers |
| purchases, saves, loads | "got here" traces |
| scene and asset load failures | verbose subsystem chatter |
| application lifecycle | anything you would delete after fixing the bug |

## Cost

`Debug.Log` unwinds and formats a stack trace on every single call — `StackTraceLogType.ScriptOnly` is the default for all three levels — marshals the string into native code, and is never stripped from release builds.

The pipeline here allocates nothing of its own: records are structs, the call site is captured by the compiler through `[CallerFilePath]`/`[CallerLineNumber]` at no runtime cost, and a stack trace is only unwound for `Error`. What a call costs is the message string you built, plus whatever the registered sinks do with it. The file sink is on by default and is the one that does real work: a JSON line and a buffered write per prod record.

One trap worth knowing, and it applies to `Debug.Log` too. Unity compiles as C# 9, where `$"hit {damage}"` with an `int` lowers to `string.Format` — boxing, a params array, and format parsing. `"hit " + damage` lowers to `string.Concat` with a direct `ToString`, which is cheaper for the same text.

## The window

**Window → Kensei → Logs**

![The Logs window in the Unity editor](Documentation~/images/editor-window.jpg)

- **Tabs are saved filters.** A tab remembers its tags, levels, channels, search text, collapse setting and any isolated frame. One tab is active at a time, so `Combat` and `Net` are a click apart rather than visible at once — but neither has to be rebuilt.
- **The tag tree** on the left is built from the tags actually seen this session, with counts, and fills in the parents: log `Combat.Damage` and `Combat` appears above it. A typo'd tag shows up there as its own branch instead of silently vanishing. Branches fold, and stay folded across restarts; the **Tree** toggle hides it when the list needs the room.
- **Colour means two things, kept apart.** The tag colours the stripe at the left of the row — a stable hue derived from the tag's root segment, so a family shares a hue and descendants differ only in brightness. The level colours the text, following the editor theme's own warning and error colours.
- **Search looks at the message only.** Tags are a field, so searching for `combat` never pulls in the `Combat` tag by itself.
- **Double-click** opens the source at the exact line. Records written through this API carry
  their call site from the compiler; captured ones carry none, so the first project frame in
  their stack trace is used instead — the same thing Unity's own console navigates by.
- **Ping** highlights the related object. Logs the engine raised against an asset rather than
  a script — a USS warning naming a StyleSheet — reach it too: the capture callback passes no
  context object, so the window asks Unity's own console store for it. That store is internal,
  so the lookup is probed once and disables itself if a Unity version moves it; nothing about
  logging depends on it, only this extra navigation. When the object is gone the button is
  disabled and its tooltip says so, rather than letting you press it for nothing.
- **Right-click a row** to isolate its frame, filter by its tag, or copy the message. Isolating a frame is what you want for a bug that only happens on one.
- **Collapse** folds repeats into one row with a counter, which keeps a stray log in `Update` from drowning the view.
- **Right-click the column header** — or press the button at its right end — to choose which of frame, time and tag to show. It lives on the header because that is where anyone looks to change the column under it. A window setting rather than a per-tab one: which columns you want is a habit, and having the layout change as you switch tabs would only surprise you.
- **Compact** hides the tag tree and every column but the message, for when you only want to read. It is a mode over your preferences, not a rewrite of them: it stores nothing, leaving it gives back exactly what you had, and while it is on the controls it overrides are disabled rather than silently ignored.

Logs that never touched this API — engine exceptions, errors from other packages, anything calling `Debug.Log` directly — are folded in under the `Unity` tag, so the view is not missing the unhandled exception you actually needed.

The window keeps 8192 records by default. There is no settings UI for that yet; change it from an editor script of your own with `EditorSink.Instance.Capacity`, which is remembered in `EditorPrefs`.

## Logs from a build

A shipped build has no console, so the prod channel goes to disk:

```
<persistentDataPath>/logs/current.jsonl    the running session
<persistentDataPath>/logs/log.1.jsonl      the previous one
<persistentDataPath>/logs/log.2.jsonl      ...
```

Every run starts a new file and pushes the old ones down, so a report is never a blend of two sessions. Each file opens with a header line naming the product and version, Unity version, platform, device model, start time and a session id — the answer to "reproduced on what?", which is always the first question.

Lines are buffered, but an **error flushes immediately**, as does the app being paused. On mobile that pause is the last signal you get before the process is killed, and it is usually the one that matters.

In the editor the file sink only runs during play mode. Otherwise every script recompile would start a new session and push the real history out within a few reloads.

Dev records stay out of the file by default. In a release build they do not exist at all, and in the editor they would bury the prod events worth keeping — set `FileIncludesDevChannel` if you want them.

To read a file back, open **Window → Kensei → Logs** and press **Open file** in the toolbar. It loads into the same window with the same tabs, tags and filters as a live run, including files that are still being written.

## Logs on the device, while playing

A file answers "send it to me afterwards". The overlay answers "what just happened, right now, on this phone".

```csharp
LogConfig config = LogConfig.Default();
config.ShowOverlay = true;
LogCore.Configure(config);
```

![The collapsed bubble](Documentation~/images/overlay-bubble.png)
![The overlay's tag list](Documentation~/images/overlay-tags.png)

A small bubble appears in the corner. It reads the error count if there are errors, the warning count if there are warnings but no errors, and the total otherwise — whichever matters most, in one glance. Drag it anywhere, tap it to open the viewer: level toggles with counts, a tag list, tap a row for its detail, and Copy to put the whole thing on the clipboard.

The tag list offers the tags this session actually logged, so unlike the editor window it will not show a `Combat` entry unless something logged under exactly that tag. Selecting one follows the same rule as the window, so `Combat` brings in `Combat.Damage` too.

![A selected record with its stack trace](Documentation~/images/overlay-detail.png)

The detail pane is a fixed height and does not scroll, so a long stack trace is clipped — **Copy** is how you get the whole thing off the device.

![An exception captured under the Unity tag](Documentation~/images/overlay-unity-exception.png)

It is off by default. A debug panel that shows up in someone's game uninvited is worse than one you have to ask for.

**No setup.** No prefab, no Canvas, no `PanelSettings`, no package dependency — turning the flag on is the whole installation. That is also why it is drawn with IMGUI rather than uGUI or UI Toolkit: it cannot collide with your `EventSystem`, it works in every render pipeline, and it reads touches through `Event.current`, so a project on the new Input System is unaffected.

To keep it out of a public build entirely, guard the flag:

```csharp
#if DEVELOPMENT_BUILD || UNITY_EDITOR
config.ShowOverlay = true;
#endif
```

## Configuration

```csharp
[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
private static void SetUpLogging() {
    LogConfig config = LogConfig.Default();
    config.MirrorToUnityConsole = true;
    LogCore.Configure(config);
}
```

| Option | Default | Effect |
| --- | --- | --- |
| `CaptureStackTraceOnError` | `true` | Unwind a stack trace for `Error` records |
| `MirrorToUnityConsole` | `false` | Also write records through `Debug.Log`, except ones captured from it |
| `CaptureForeignLogs` | `true` | Fold logs from outside this API into the pipeline |
| `WriteToFile` | `true` | Write a rolling JSONL file under `persistentDataPath/logs` |
| `FileSizeLimitKb` | `5120` | Rotate the current file once it passes this size (at least 64) |
| `RetainedFileCount` | `3` | How many rotated files to keep besides `current.jsonl` (at least 1) |
| `FileFlushIntervalSeconds` | `5` | How long buffered lines may wait; errors flush at once (at least 0.5) |
| `FileIncludesDevChannel` | `false` | Also write dev records to the file |
| `ShowOverlay` | `false` | Draw the in-game log viewer |
| `OverlayRecordCapacity` | `512` | How many records the overlay keeps (at least 32) |
| `OverlayScale` | `0` | Overlay UI scale, or 0 to derive one from screen DPI, falling back to screen height |

`Configure` can be called at any time; logging starts with the defaults during early initialisation so that nothing is lost before your call arrives.

## Driving it from your own code

```csharp
LogCore.AddSink(new MySink());          // and RemoveSink
LogCore.FlushSinks();                   // push every buffering sink to its destination
LogCore.File;                           // the active FileSink, or null

LogOverlay.IsOpen = true;               // open the on-device viewer from your debug menu
LogOverlay.TagPaneVisible = true;
LogOverlay.SelectNewest(LogLevel.Error);

window.AddTab(new LogFilter { ... });    // seed a project's tabs from an editor script
window.SelectNewest(LogLevel.Error);
```

A sink is anything that takes a record:

```csharp
public sealed class MySink : ILogSink {
    public void Write(in LogRecord record) { /* must be safe on any thread */ }
}
```

Implement `IFlushableSink` as well if it buffers, and `LogCore.FlushSinks` will reach it when the app pauses or quits.

## Try it

The package ships a sample. **Package Manager → Kensei Log → Samples → Overlay Demo → Import**, then open `LogDemo.unity` and press Play.

It drives the logger from several tags, has buttons for a log, a warning, an error, a Unity exception thrown past this API, and a burst of repeats to collapse. It also writes a file while you play, so there is something to open afterwards with **Open file**.

## Requirements

Unity 2022.3 or newer. No third-party dependencies.

## License

MIT
