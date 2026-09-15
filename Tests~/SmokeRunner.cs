using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using KenseiLog;
using KenseiLog.Editor;
using UnityEditor;

/// <summary>
/// Headless checks over the parts of the logger that do not need a GUI.
/// Run with: Unity.exe -projectPath . -batchmode -quit -executeMethod SmokeRunner.Run
/// </summary>
public static class SmokeRunner {
    private static readonly StringBuilder _report = new StringBuilder();
    private static int _failures;

    public static void Run() {
        RingBufferKeepsNewestAndAddressesBySequence();
        RolledRecordsAreReportedMissing();
        TagFilterMatchesDescendantsButNotNeighbours();
        SearchLooksAtMessageOnlyNotTag();
        CollapseFoldsRepeatsAndTracksNewest();
        PruneDropsRolledEntriesAndKeepsCollapseIndex();
        TagTreeBuildsHierarchyWithRollupCounts();
        FacadeReachesTheEditorSink();
        ForeignLogsAreMarkedAsCaptured();
        ConsoleSinkSkipsCapturedRecords();
        RingBufferHandlesGappedSequences();
        JsonSurvivesRoundTrip();
        FileSinkWritesHeaderAndRotates();
        MemorySinkStoresAndVersions();
        TagColoursAreStableAndDistinct();
        FileSettingsApplyAfterTheSinkExists();
        SourcePathsResolveAcrossMachines();
        TaglessOverloadsLandUnderUntagged();
        ScopedLoggerCarriesItsTag();

        _report.Insert(0, _failures == 0
            ? "SMOKE RESULT: PASS\n"
            : "SMOKE RESULT: FAIL (" + _failures + ")\n");
        System.Console.WriteLine(_report.ToString());

        if (_failures > 0) {
            EditorApplication.Exit(1);
        }
    }

    // =====================================================================

    private static void RingBufferKeepsNewestAndAddressesBySequence() {
        LogRingBuffer buffer = new LogRingBuffer(4);
        for (int i = 1; i <= 6; i++) {
            buffer.Add(Record(i, "Tag", "m" + i, LogLevel.Log, LogChannel.Dev, 0));
        }

        Check("ring holds capacity", buffer.Count == 4);
        Check("ring oldest advanced", buffer.OldestSequence == 3);
        Check("ring addresses by sequence", buffer.TryGetBySequence(5, out LogRecord five) && five.Message == "m5");

        LogRecord[] scratch = new LogRecord[4];
        int copied = buffer.CopyNewerThan(4, scratch);
        Check("copy newer than returns tail", copied == 2 && scratch[0].Message == "m5" && scratch[1].Message == "m6");
    }

    private static void RolledRecordsAreReportedMissing() {
        LogRingBuffer buffer = new LogRingBuffer(2);
        buffer.Add(Record(1, "Tag", "a", LogLevel.Log, LogChannel.Dev, 0));
        buffer.Add(Record(2, "Tag", "b", LogLevel.Log, LogChannel.Dev, 0));
        buffer.Add(Record(3, "Tag", "c", LogLevel.Log, LogChannel.Dev, 0));

        Check("overwritten sequence is missing", !buffer.TryGetBySequence(1, out _));
        Check("retained sequence is found", buffer.TryGetBySequence(3, out _));
    }

    private static void TagFilterMatchesDescendantsButNotNeighbours() {
        LogFilter filter = new LogFilter();
        filter.Tags.Add("Combat");

        Check("exact tag matches", filter.Matches(Record(1, "Combat", "x", LogLevel.Log, LogChannel.Dev, 0)));
        Check("descendant matches", filter.Matches(Record(2, "Combat.Damage", "x", LogLevel.Log, LogChannel.Dev, 0)));
        Check("unrelated prefix does not match", !filter.Matches(Record(3, "CombatLog", "x", LogLevel.Log, LogChannel.Dev, 0)));
        Check("other tag does not match", !filter.Matches(Record(4, "Net", "x", LogLevel.Log, LogChannel.Dev, 0)));
    }

    private static void SearchLooksAtMessageOnlyNotTag() {
        LogFilter filter = new LogFilter { Search = "combat" };

        Check("message hit is kept",
            filter.Matches(Record(1, "Net", "combat handshake rejected", LogLevel.Log, LogChannel.Prod, 0)));
        Check("tag alone is not a text hit",
            !filter.Matches(Record(2, "Combat.Damage", "hit orc for 24", LogLevel.Log, LogChannel.Dev, 0)));
    }

    private static void CollapseFoldsRepeatsAndTracksNewest() {
        LogFilter filter = new LogFilter { Collapse = true };
        TabView view = new TabView(filter);

        view.Append(Record(1, "Combat", "same", LogLevel.Log, LogChannel.Dev, 0));
        view.Append(Record(2, "Combat", "same", LogLevel.Log, LogChannel.Dev, 0));
        view.Append(Record(3, "Combat", "other", LogLevel.Log, LogChannel.Dev, 0));
        view.Append(Record(4, "Combat", "same", LogLevel.Warning, LogChannel.Dev, 0));

        Check("collapse folds identical rows", view.Count == 3);
        Check("collapse counts repeats", view.Repeats[0] == 2);
        Check("collapse points at newest", view.Sequences[0] == 2);
        Check("collapse keys include level", view.Repeats[2] == 1);
    }

    private static void PruneDropsRolledEntriesAndKeepsCollapseIndex() {
        LogFilter filter = new LogFilter { Collapse = true };
        TabView view = new TabView(filter);

        view.Append(Record(1, "A", "first", LogLevel.Log, LogChannel.Dev, 0));
        view.Append(Record(2, "A", "second", LogLevel.Log, LogChannel.Dev, 0));
        view.PruneBelow(2);

        Check("prune drops stale rows", view.Count == 1 && view.Sequences[0] == 2);

        view.Append(Record(3, "A", "second", LogLevel.Log, LogChannel.Dev, 0));
        Check("collapse index survives prune", view.Count == 1 && view.Repeats[0] == 2);
    }

    private static void TagTreeBuildsHierarchyWithRollupCounts() {
        Dictionary<string, int> counts = new Dictionary<string, int> {
            { "Combat.Damage", 3 },
            { "Combat.AI", 2 },
            { "Net", 5 }
        };

        List<TagNode> roots = TagTree.Build(counts);
        TagNode combat = roots.Find(n => n.Segment == "Combat");

        Check("tree has both roots", roots.Count == 2);
        Check("parent rolls up children", combat != null && combat.Count == 5);
        Check("parent keeps children", combat != null && combat.Children.Count == 2);
        Check("child carries full tag", combat != null && combat.Children.Find(n => n.Segment == "AI").FullTag == "Combat.AI");
    }

    private static void FacadeReachesTheEditorSink() {
        EditorSink.Instance.Clear();
        Log.Prod("SmokeRunner", "prod reached the sink");
        Log.Dev("SmokeRunner", "dev reached the sink");

        LogRingBuffer buffer = EditorSink.Instance.Buffer;
        Check("facade writes reach the sink", buffer.Count == 2);

        LogRecord[] scratch = new LogRecord[8];
        int copied = buffer.CopyNewerThan(0, scratch);
        Check("call site is captured", copied == 2 && !string.IsNullOrEmpty(scratch[0].File) && scratch[0].Line > 0);
        Check("channels are recorded", copied == 2 && scratch[0].Channel == LogChannel.Prod && scratch[1].Channel == LogChannel.Dev);
    }

    private static void ForeignLogsAreMarkedAsCaptured() {
        EditorSink.Instance.Clear();
        UnityEngine.Debug.Log("smoke foreign message");
        Log.Prod("SmokeRunner", "smoke facade message");

        LogRecord[] scratch = new LogRecord[8];
        int copied = EditorSink.Instance.Buffer.CopyNewerThan(0, scratch);

        Check("foreign log is picked up", copied == 2);
        Check("foreign log carries the Unity tag", copied == 2 && scratch[0].Tag == LogCore.ForeignTag);
        Check("foreign log is flagged captured", copied == 2 && scratch[0].Captured);
        Check("facade log is not flagged captured", copied == 2 && !scratch[1].Captured);
    }

    /// <summary>
    /// Regression: with console mirroring on, a captured record written back to the console
    /// made every foreign message appear twice.
    /// </summary>
    private static void ConsoleSinkSkipsCapturedRecords() {
        int seen = 0;
        UnityEngine.Application.LogCallback probe = (condition, stack, type) => {
            if (condition.Contains("SINKPROBE")) {
                seen++;
            }
        };
        UnityEngine.Application.logMessageReceivedThreaded += probe;

        UnityConsoleSink sink = new UnityConsoleSink();
        sink.Write(Record(1, "Probe", "SINKPROBE authored", LogLevel.Log, LogChannel.Prod, 0));
        int afterAuthored = seen;
        sink.Write(Record(2, LogCore.ForeignTag, "SINKPROBE captured", LogLevel.Log, LogChannel.Prod, 0, captured: true));

        UnityEngine.Application.logMessageReceivedThreaded -= probe;

        Check("console sink mirrors authored records", afterAuthored == 1);
        Check("console sink skips captured records", seen == 1);
    }

    /// <summary>
    /// The file sink skips the dev channel, so sequences in a file have holes. Lookups must
    /// not assume they are contiguous.
    /// </summary>
    private static void RingBufferHandlesGappedSequences() {
        LogRingBuffer buffer = new LogRingBuffer(8);
        buffer.Add(Record(10, "A", "ten", LogLevel.Log, LogChannel.Prod, 0));
        buffer.Add(Record(20, "A", "twenty", LogLevel.Log, LogChannel.Prod, 0));
        buffer.Add(Record(30, "A", "thirty", LogLevel.Log, LogChannel.Prod, 0));

        Check("gapped lookup finds a present sequence",
            buffer.TryGetBySequence(20, out LogRecord hit) && hit.Message == "twenty");
        Check("gapped lookup rejects a missing sequence", !buffer.TryGetBySequence(15, out _));

        LogRecord[] scratch = new LogRecord[8];
        int copied = buffer.CopyNewerThan(10, scratch);
        Check("gapped copy starts after the watermark", copied == 2 && scratch[0].Message == "twenty");

        copied = buffer.CopyNewerThan(15, scratch);
        Check("gapped copy handles a watermark inside a hole", copied == 2 && scratch[0].Message == "twenty");
    }

    private static void JsonSurvivesRoundTrip() {
        string nasty = "quote \" backslash \\ tab \t newline\nsecond line, число 128,44 и юникод ✓";
        LogRecord original = new LogRecord(
            42, "Combat.Damage", nasty, LogLevel.Warning, LogChannel.Prod,
            128.44, 12043, "Assets/Net/Sync.cs", 88, "at Foo()\nat Bar()", 0, false);

        StringBuilder builder = new StringBuilder();
        LogJson.AppendSessionHeader(builder, "sid", "App 1.0", "2022.3", "WindowsEditor", "PC", "2026-09-15T00:00:00Z");
        builder.Append('\n');
        LogJson.AppendRecord(builder, in original);
        builder.Append('\n');

        string path = Path.Combine(Path.GetTempPath(), "kenseilog-roundtrip.jsonl");
        File.WriteAllText(path, builder.ToString());

        bool read = LogSessionReader.TryRead(path, out LogSession session, out string error);
        Check("session file reads back" + (read ? string.Empty : ": " + error), read);
        if (!read) {
            return;
        }

        Check("header survives", session.App == "App 1.0" && session.Platform == "WindowsEditor");
        Check("no lines were skipped", session.SkippedLines == 0);

        bool found = session.Buffer.TryGetBySequence(42, out LogRecord back);
        Check("record survives round trip", found);
        if (!found) {
            return;
        }

        Check("message survives escaping", back.Message == nasty);
        Check("tag survives", back.Tag == "Combat.Damage");
        Check("level and channel survive", back.Level == LogLevel.Warning && back.Channel == LogChannel.Prod);
        Check("call site survives", back.File == "Assets/Net/Sync.cs" && back.Line == 88);
        Check("stack trace survives", back.StackTrace == "at Foo()\nat Bar()");
        Check("time survives invariant formatting", Math.Abs(back.TimeMs - 128.44) < 0.001);

        File.Delete(path);
    }

    private static void FileSinkWritesHeaderAndRotates() {
        LogConfig config = LogConfig.Default();
        config.FileSizeLimitKb = 64;
        config.RetainedFileCount = 2;

        FileSink sink = new FileSink(in config);
        Check("file sink opened a file", sink.IsWriting);
        if (!sink.IsWriting) {
            return;
        }

        sink.Write(Record(1, "Boot", "dev record must not reach the file", LogLevel.Log, LogChannel.Dev, 0));
        for (int i = 0; i < 3000; i++) {
            sink.Write(Record(i + 2, "Boot", "padding record number " + i + " with enough text to fill the file", LogLevel.Log, LogChannel.Prod, i));
        }
        sink.Flush();

        string rotated = Path.Combine(sink.LogDirectory, "log.1.jsonl");
        Check("rotation produced a previous file", File.Exists(rotated));
        Check("current file exists", File.Exists(sink.CurrentFilePath));

        bool read = LogSessionReader.TryRead(sink.CurrentFilePath, out LogSession session, out string error);
        Check("written file reads back" + (read ? string.Empty : ": " + error), read);
        if (read) {
            Check("rotated file starts with a session header", session.App != null);

            LogRecord[] scratch = new LogRecord[session.Buffer.Capacity];
            int copied = session.Buffer.CopyNewerThan(0, scratch);
            bool anyDev = false;
            for (int i = 0; i < copied; i++) {
                if (scratch[i].Channel == LogChannel.Dev) {
                    anyDev = true;
                }
            }
            Check("dev records stayed out of the file", !anyDev);
        }

        sink.Dispose();
    }

    private static void MemorySinkStoresAndVersions() {
        MemorySink sink = new MemorySink(4);
        int before = sink.Version;

        sink.Write(Record(1, "Overlay", "one", LogLevel.Log, LogChannel.Prod, 0));
        sink.Write(Record(2, "Overlay", "two", LogLevel.Error, LogChannel.Prod, 0));

        Check("memory sink stores records", sink.Buffer.Count == 2);
        Check("memory sink bumps its version", sink.Version != before);

        sink.Clear();
        Check("memory sink clears", sink.Buffer.Count == 0);
    }

    /// <summary>
    /// The overlay and the editor window must agree on which tag is which, and a tag must keep
    /// its colour across restarts, so the hue cannot come from a randomised string hash.
    /// </summary>
    private static void TagColoursAreStableAndDistinct() {
        Check("tag hash is stable", TagPalette.Hash("Combat") == TagPalette.Hash("Combat"));
        Check("different roots hash apart", TagPalette.Hash("Combat") != TagPalette.Hash("Net"));

        UnityEngine.Color parent = TagPalette.For("Combat", 0.5f, 0.85f, -0.08f);
        UnityEngine.Color child = TagPalette.For("Combat.Damage", 0.5f, 0.85f, -0.08f);
        UnityEngine.Color stranger = TagPalette.For("Net", 0.5f, 0.85f, -0.08f);

        UnityEngine.Color.RGBToHSV(parent, out float parentHue, out _, out float parentValue);
        UnityEngine.Color.RGBToHSV(child, out float childHue, out _, out float childValue);
        UnityEngine.Color.RGBToHSV(stranger, out float strangerHue, out _, out _);

        Check("child shares the parent hue", Math.Abs(parentHue - childHue) < 0.001f);
        Check("child differs in brightness", Math.Abs(parentValue - childValue) > 0.01f);
        Check("unrelated tag gets another hue", Math.Abs(parentHue - strangerHue) > 0.001f);
    }

    /// <summary>
    /// Regression: the file sink is built during early startup with defaults, so a project's
    /// own Configure call always arrives after it exists. Its file settings were frozen in
    /// readonly fields at construction and silently ignored from then on.
    /// </summary>
    private static void FileSettingsApplyAfterTheSinkExists() {
        LogConfig config = LogConfig.Default();
        config.FileIncludesDevChannel = false;

        FileSink sink = new FileSink(in config);
        if (!sink.IsWriting) {
            Check("file sink opened for the settings check", false);
            return;
        }

        sink.Write(Record(1, "Cfg", "dev before reconfigure", LogLevel.Log, LogChannel.Dev, 0));

        config.FileIncludesDevChannel = true;
        sink.Reconfigure(in config);
        sink.Write(Record(2, "Cfg", "dev after reconfigure", LogLevel.Log, LogChannel.Dev, 0));
        sink.Flush();

        bool read = LogSessionReader.TryRead(sink.CurrentFilePath, out LogSession session, out string error);
        Check("settings-check file reads back" + (read ? string.Empty : ": " + error), read);
        if (!read) {
            sink.Dispose();
            return;
        }

        bool sawBefore = false;
        bool sawAfter = false;
        LogRecord[] scratch = new LogRecord[session.Buffer.Capacity];
        int copied = session.Buffer.CopyNewerThan(0, scratch);
        for (int i = 0; i < copied; i++) {
            if (scratch[i].Message == "dev before reconfigure") {
                sawBefore = true;
            }
            if (scratch[i].Message == "dev after reconfigure") {
                sawAfter = true;
            }
        }

        Check("dev record stays out while the option is off", !sawBefore);
        Check("dev record is written once the option is turned on later", sawAfter);

        sink.Dispose();
    }

    /// <summary>
    /// CallerFilePath records the path of the machine that compiled the code. Matching it only
    /// against this project's root meant a session file from someone else's build could never
    /// open its source, and the failure was silent.
    /// </summary>
    private static void SourcePathsResolveAcrossMachines() {
        const string root = "D:/Work/MyGame";

        Check("path under this project resolves",
            LogWindow.TryResolveAssetPath("D:/Work/MyGame/Assets/Scripts/Player.cs", root, out string a) &&
            a == "Assets/Scripts/Player.cs");

        Check("windows separators are handled",
            LogWindow.TryResolveAssetPath(@"D:\Work\MyGame\Assets\Scripts\Player.cs", root, out string b) &&
            b == "Assets/Scripts/Player.cs");

        Check("path from another machine resolves by its Assets segment",
            LogWindow.TryResolveAssetPath("/Users/someone/Projects/MyGame/Assets/Scripts/Player.cs", root, out string c) &&
            c == "Assets/Scripts/Player.cs");

        Check("path from a package resolves",
            LogWindow.TryResolveAssetPath("/Users/someone/MyGame/Packages/com.kensei.log/Runtime/Log.cs", root, out string d) &&
            d == "Packages/com.kensei.log/Runtime/Log.cs");

        Check("an already relative path is kept",
            LogWindow.TryResolveAssetPath("Assets/Scripts/Player.cs", root, out string e) &&
            e == "Assets/Scripts/Player.cs");

        Check("a path with nothing to anchor on is refused",
            !LogWindow.TryResolveAssetPath("/tmp/scratch/Thing.cs", root, out _));

        Check("an empty path is refused", !LogWindow.TryResolveAssetPath(null, root, out _));
    }

    /// <summary>
    /// The tagless overloads must not shadow the tagged ones: a two-string call has to keep
    /// meaning "tag, message". They differ only in the second parameter's type, so this check
    /// is really about overload resolution rather than about the tag.
    /// </summary>
    private static void TaglessOverloadsLandUnderUntagged() {
        EditorSink.Instance.Clear();

        Log.Prod("no tag on this one");
        Log.Prod("SmokeRunner", "this one is tagged");
        Log.ProdWarning("no tag, warning");

        LogRecord[] scratch = new LogRecord[8];
        int copied = EditorSink.Instance.Buffer.CopyNewerThan(0, scratch);

        Check("all three records arrived", copied == 3);
        if (copied != 3) {
            return;
        }

        Check("tagless call lands under Untagged", scratch[0].Tag == LogCore.UntaggedTag);
        Check("two strings still mean tag and message", scratch[1].Tag == "SmokeRunner" && scratch[1].Message == "this one is tagged");
        Check("tagless warning keeps its level", scratch[2].Tag == LogCore.UntaggedTag && scratch[2].Level == LogLevel.Warning);
        Check("tagless call still captures its call site", !string.IsNullOrEmpty(scratch[0].File) && scratch[0].Line > 0);
    }

    private static readonly Logger _scoped = Logger.For("Scoped");

    private static void ScopedLoggerCarriesItsTag() {
        EditorSink.Instance.Clear();

        _scoped.Prod("from the scoped logger");
        _scoped.Child("Child").ProdWarning("from a child logger");
        Logger.For("Other").ProdError("from a one-off logger");

        LogRecord[] scratch = new LogRecord[8];
        int copied = EditorSink.Instance.Buffer.CopyNewerThan(0, scratch);

        Check("scoped logger records arrived", copied == 3);
        if (copied != 3) {
            return;
        }

        Check("scoped logger carries its tag", scratch[0].Tag == "Scoped");
        Check("child logger nests under the parent", scratch[1].Tag == "Scoped.Child");
        Check("child keeps the level and channel", scratch[1].Level == LogLevel.Warning && scratch[1].Channel == LogChannel.Prod);
        Check("one-off logger works", scratch[2].Tag == "Other" && scratch[2].Level == LogLevel.Error);
        Check("scoped logger still captures the call site", !string.IsNullOrEmpty(scratch[0].File) && scratch[0].Line > 0);

        // An uninitialised struct has a null tag, which would otherwise surface as a crash in
        // the window rather than anywhere near the code that forgot to assign it.
        Logger uninitialised = default;
        Check("a default logger falls back to Untagged", uninitialised.Tag == LogCore.UntaggedTag);
    }

    // =====================================================================

    private static LogRecord Record(long sequence, string tag, string message, LogLevel level, LogChannel channel, int frame, bool captured = false) {
        return new LogRecord(sequence, tag, message, level, channel, 0.0, frame, null, 0, null, 0, captured);
    }

    private static void Check(string what, bool condition) {
        if (!condition) {
            _failures++;
        }
        _report.AppendLine((condition ? "  ok   " : "  FAIL ") + what);
    }
}
