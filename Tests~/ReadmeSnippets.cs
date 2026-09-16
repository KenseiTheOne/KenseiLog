using KenseiLog;
using KenseiLog.Editor;
using UnityEditor;
using UnityEngine;
using Logger = KenseiLog.Logger;

/// <summary>
/// The README's code, compiled.
/// <para>
/// Nothing here runs. It exists so that a snippet which stopped matching the API cannot sit in
/// the README looking authoritative - the first thing anyone tries is the thing they copied
/// out of it, and a method that has been renamed since is found by the compiler here or by
/// them there.
/// </para>
/// <para>
/// Fragments that are plainly fragments - the ones written against a `target` and a `damage`
/// that belong to the reader's own class - are represented by the smallest thing that gives
/// them a context, since the point of those is the shape of the call.
/// </para>
/// </summary>
public static class ReadmeSnippets {
    // ## Usage
    public static class Tags {
        public const string Combat = "Combat";
        public const string Damage = "Combat.Damage";
        public const string Net = "Net";
    }

    public static void Usage(GameObject target, int damage, int tick) {
        Log.DevInfo(Tags.Damage, "hit " + target.name + " for " + damage, target);
        Log.DevWarning(Tags.Combat, "no hitbox on " + target.name);
        Log.Error(Tags.Net, "desync at tick " + tick);
    }

    // ### One tag per file
    public sealed class OneTagPerFile {
        private static readonly Logger Log = Logger.For(Tags.Combat);

        public void Hit(GameObject target, int damage, int tick) {
            Log.DevInfo("hit " + target.name + " for " + damage);
            Log.Error("desync at tick " + tick);

            // Reaching a different tag from the same file.
            KenseiLog.Log.DevInfo(Tags.Net, "and one under another tag");

            Logger nested = Logger.For(Tags.Combat).Child("AI");
            nested.DevInfo("nested under Combat");
        }
    }

    // ### No tag at all
    public static void Untagged() {
        Log.DevInfo("still here");
    }

    // ## Configuration
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
    private static void SetUpLogging() {
        LogConfig config = LogConfig.Default();
        config.MirrorToUnityConsole = true;
        LogCore.Configure(config);
    }

    // ## Logs on the device, while playing
    public static void TurnOnTheOverlay() {
        LogConfig config = LogConfig.Default();
        config.ShowOverlay = true;
        LogCore.Configure(config);
    }

    // ## Driving it from your own code
    public static void DriveIt() {
        MySink sink = new MySink();
        LogCore.AddSink(sink);
        LogCore.RemoveSink(sink);

        LogCore.FlushSinks();
        FileSink file = LogCore.File;
        Debug.Log(file != null);

        LogOverlay.IsOpen = true;
        LogOverlay.TagPaneVisible = true;
        LogOverlay.SelectNewest(LogLevel.Error);
    }

    public static void DriveTheWindow() {
        LogWindow window = EditorWindow.GetWindow<LogWindow>();
        window.AddTab(new LogFilter { Name = "Net", Tags = { "Net" }, ShowDev = false });
        window.SelectNewest(LogLevel.Error);
    }

    public sealed class MySink : ILogSink {
        public void Write(in LogRecord record) {
            // Called on whichever thread logged, so this has to be safe from any of them.
        }
    }

    public static void ReadRecentRecords() {
        MemorySink recent = new MemorySink(256);
        LogCore.AddSink(recent);

        long watermark = 0;
        LogRecord[] scratch = new LogRecord[recent.Buffer.Capacity];
        int copied = recent.Buffer.CopyNewerThan(watermark, scratch);
        Debug.Log(copied);
    }

    // ### What a record holds
    public static void EveryFieldOnTheTable(in LogRecord record) {
        Debug.Log(
            record.Sequence + " " + record.Tag + " " + record.Message + " " + record.Level + " " +
            record.Channel + " " + record.TimeMs + " " + record.Frame + " " + record.File + " " +
            record.Line + " " + record.StackTrace + " " + record.ContextInstanceId + " " + record.Captured);
    }
}
