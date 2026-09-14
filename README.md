# Kensei Log

Tagged logging for Unity with two channels and an editor window that splits logs into filter tabs.

Unity's own console is a singleton — you cannot open a second one. Keeping "combat" and "networking" visible side by side is impossible there, and filtering by text catches the word `combat` inside a message just as happily as the `Combat` tag. Kensei Log makes the tag a real field on the record and lets you open as many independent views of the stream as you want.

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

Cheaper than `Debug.Log`, which on every single call unwinds and formats a stack trace (`StackTraceLogType.ScriptOnly` is the default for all three levels), marshals the string into native code, and is never stripped from release builds.

Kensei Log pays for the message string and nothing else: records are structs in a pre-allocated ring, the call site is captured by the compiler through `[CallerFilePath]`/`[CallerLineNumber]` at zero runtime cost, and a stack trace is only unwound for `Error`.

One trap worth knowing, and it applies to `Debug.Log` too. Unity compiles as C# 9, where `$"hit {damage}"` with an `int` lowers to `string.Format` — boxing, a params array, and format parsing. `"hit " + damage` lowers to `string.Concat` with a direct `ToString`. On a hot path that is twice as cheap for the same text.

## The window

**Window → Kensei → Logs**

- **Tabs are saved filters.** A tab remembers its tags, levels, channels, search text and collapse setting. `Combat` and `Net` can stay open side by side.
- **The tag tree** on the left is built from the tags actually seen this session, with counts. A typo'd tag shows up there as its own branch instead of silently vanishing.
- **Colour means two things, kept apart.** The tag colours the stripe at the left of the row — a stable hue derived from the tag name, with descendants varying only in brightness. The level colours the text: amber for warnings, red for errors, neutral for plain logs.
- **Search looks at the message only.** Tags are a field, so searching for `combat` never pulls in the `Combat` tag by itself.
- **Double-click** opens the source at the exact line. **Ping** highlights the related object in the hierarchy, and says so plainly if it has been destroyed.
- **Right-click a row** to isolate its frame — useful for bugs that only happen on one frame.
- **Collapse** folds repeats into one row with a counter, which keeps a stray log in `Update` from drowning the view.

Logs that never touched this API — engine exceptions, errors from other packages, anything calling `Debug.Log` directly — are folded in under the `Unity` tag, so the view is not missing the unhandled exception you actually needed.

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
| `MirrorToUnityConsole` | `false` | Also write each record through `Debug.Log` |
| `CaptureForeignLogs` | `true` | Fold logs from outside this API into the pipeline |

How many records the window keeps is an editor setting, not a build one, and lives in `EditorPrefs`.

## Writing your own sink

```csharp
public sealed class MySink : ILogSink {
    public void Write(in LogRecord record) { /* must be safe on any thread */ }
}

LogCore.AddSink(new MySink());
```

## Requirements

Unity 2022.3 or newer. No third-party dependencies.

## Not here yet

A rolling JSONL file sink for shipped builds, and opening such a file back in the window as a session. The record model and the pipeline were built with it in mind.

## License

MIT
