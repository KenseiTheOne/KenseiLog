using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
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
        Scenario(RingBufferKeepsNewestAndAddressesBySequence);
        Scenario(RolledRecordsAreReportedMissing);
        Scenario(RingBufferSettlesARecordThatArrivesLate);
        Scenario(RingBufferHoldsUpUnderThreads);
        Scenario(TagFilterMatchesDescendantsButNotNeighbours);
        Scenario(SearchLooksAtMessageOnlyNotTag);
        Scenario(CollapseFoldsRepeatsAndTracksNewest);
        Scenario(PruneDropsRolledEntriesAndKeepsCollapseIndex);
        Scenario(PruneClearsExpiredRowsInACollapsedView);
        Scenario(ViewRevisionMovesWhenTheContentsDo);
        Scenario(TagTreeBuildsHierarchyWithRollupCounts);
        Scenario(FacadeReachesTheEditorSink);
        Scenario(ANullTagBecomesUntagged);
        Scenario(DevRecordsStillReachASinkThatWantsThem);
        Scenario(ForeignLogsAreMarkedAsCaptured);
        Scenario(ConsoleSinkSkipsCapturedRecords);
        Scenario(RingBufferHandlesGappedSequences);
        Scenario(JsonSurvivesRoundTrip);
        Scenario(LoneSurrogatesAreEscapedNotReplaced);
        Scenario(FileSinkWritesHeaderAndRotates);
        Scenario(RotationThatCannotShiftKeepsWriting);
        Scenario(LoweringTheRetainedCountRemovesTheOrphans);
        Scenario(AThrowingSinkDoesNotStopTheOthers);
        Scenario(SessionFilesCarryASchemaVersion);
        Scenario(AFileWithOnlyAHeaderStillOpens);
        Scenario(MemorySinkStoresAndVersions);
        Scenario(TagColoursAreStableAndDistinct);
        Scenario(FileSettingsApplyAfterTheSinkExists);
        Scenario(FileDirectoryMovesWithTheConfiguration);
        Scenario(SourcePathsResolveAcrossMachines);
        Scenario(CallSitesAreTrimmedForABuild);
        Scenario(TaglessOverloadsLandUnderUntagged);
        Scenario(ScopedLoggerCarriesItsTag);
        Scenario(CapturedLogsNavigateByTheirStackTrace);
        Scenario(ConsoleBridgeFindsTheContextObject);

        CleanUpScratchDirectory();

        _report.Insert(0, _failures == 0
            ? "SMOKE RESULT: PASS\n"
            : "SMOKE RESULT: FAIL (" + _failures + ")\n");
        System.Console.WriteLine(_report.ToString());

        if (_failures > 0) {
            EditorApplication.Exit(1);
        }
    }

    /// <summary>
    /// Runs one scenario and counts a throw as a failure of its own.
    /// <para>
    /// Without this a scenario that threw ended Run where it stood: the report was never
    /// written, Exit(1) was never reached, and -batchmode -quit returned zero. A harness that
    /// reports success when it has fallen over is worse than no harness, and every scenario
    /// after the throw went unrun without anyone being told.
    /// </para>
    /// </summary>
    private static void Scenario(Action body) {
        try {
            body();
        } catch (Exception exception) {
            Check(body.Method.Name + " threw " + exception.GetType().Name + ": " + exception.Message, false);
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
        TagNode ai = combat?.Children.Find(n => n.Segment == "AI");
        Check("child carries full tag", ai != null && ai.FullTag == "Combat.AI");
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
        config.FileDirectory = ScratchDirectory("rotate");
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
        config.FileDirectory = ScratchDirectory("settings");
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

    /// <summary>
    /// Captured records carry no file or line - logMessageReceived does not hand one over -
    /// so nothing in the window could navigate from an engine or third-party log. Unity writes
    /// the location into the stack trace, which is what its own console navigates by.
    /// </summary>
    private static void CapturedLogsNavigateByTheirStackTrace() {
        const string trace =
            "UnityEngine.Debug:LogWarning (object)\n" +
            "Game.Combat.Hitbox:Resolve () (at Assets/Scripts/Combat/Hitbox.cs:128)\n" +
            "Game.Combat.Loop:Tick () (at Assets/Scripts/Combat/Loop.cs:44)\n";

        Check("first project frame is found",
            LogWindow.TryFindSourceInStackTrace(trace, out string path, out int line) &&
            path == "Assets/Scripts/Combat/Hitbox.cs" && line == 128);

        Check("a frame from a package is accepted",
            LogWindow.TryFindSourceInStackTrace(
                "Foo:Bar () (at Packages/com.kensei.log/Runtime/Log.cs:12)", out string p2, out int l2) &&
            p2 == "Packages/com.kensei.log/Runtime/Log.cs" && l2 == 12);

        // Unity's own frames point at a build agent's disk and open nothing here.
        Check("engine frames are skipped in favour of a project one",
            LogWindow.TryFindSourceInStackTrace(
                "UnityEngine.Thing:Do () (at C:/build/output/unity/Runtime/Export/Thing.cs:17)\n" +
                "Game.Boot:Run () (at Assets/Scripts/Boot.cs:9)", out string p3, out int l3) &&
            p3 == "Assets/Scripts/Boot.cs" && l3 == 9);

        Check("a trace with no project frame is refused",
            !LogWindow.TryFindSourceInStackTrace("UnityEditor.EditorApplication:Internal_RestoreLastOpenedScenes ()", out _, out _));

        Check("an empty trace is refused", !LogWindow.TryFindSourceInStackTrace(null, out _, out _));
        Check("a malformed frame does not throw", !LogWindow.TryFindSourceInStackTrace("Foo:Bar () (at nonsense", out _, out _));
    }

    /// <summary>
    /// The context object never reaches us through the public callback, so the window asks
    /// Unity's own console store for it. That store is internal, so this check is really about
    /// whether the reflection still lines up with the editor we are running on.
    /// </summary>
    private static void ConsoleBridgeFindsTheContextObject() {
        Check("console bridge found the internal API", ConsoleEntryBridge.Available);
        if (!ConsoleEntryBridge.Available) {
            return;
        }

        UnityEngine.ScriptableObject target = UnityEngine.ScriptableObject.CreateInstance<UnityEngine.ScriptableObject>();
        target.name = "KenseiLogBridgeProbe";
        string message = "bridge probe " + System.Guid.NewGuid().ToString("N");

        UnityEngine.Debug.LogWarning(message, target);

        bool resolved = ConsoleEntryBridge.TryResolve(message, out int instanceId, out _, out _);
        Check("bridge resolved the entry", resolved);
        Check("bridge returned the object that was logged against",
            resolved && instanceId == target.GetInstanceID());

        Check("an unknown message resolves to nothing",
            !ConsoleEntryBridge.TryResolve("no such message " + System.Guid.NewGuid().ToString("N"), out _, out _, out _));

        UnityEngine.Object.DestroyImmediate(target);
    }


    /// <summary>
    /// Two threads logging at once can reach a sink the other way round, and every read of the
    /// buffer binary-searches on the sequence. One inversion was enough to make a consumer
    /// re-copy the same batch on every poll for the rest of the session.
    /// </summary>
    private static void RingBufferSettlesARecordThatArrivesLate() {
        LogRingBuffer buffer = new LogRingBuffer(8);
        buffer.Add(Record(1, "T", "one", LogLevel.Log, LogChannel.Prod, 0));
        buffer.Add(Record(3, "T", "three", LogLevel.Log, LogChannel.Prod, 0));
        buffer.Add(Record(2, "T", "two", LogLevel.Log, LogChannel.Prod, 0));

        LogRecord[] scratch = new LogRecord[8];
        int copied = buffer.CopyNewerThan(0, scratch);
        Check("a late arrival settles into order",
            copied == 3 && scratch[0].Sequence == 1 && scratch[1].Sequence == 2 && scratch[2].Sequence == 3);

        // The shape of the loop it used to cause: copy from a watermark, take the newest
        // sequence, copy again. With an inversion in place the second copy handed back rows
        // the consumer already had, for as long as it kept asking.
        long watermark = 0;
        copied = buffer.CopyNewerThan(watermark, scratch);
        for (int i = 0; i < copied; i++) {
            watermark = Math.Max(watermark, scratch[i].Sequence);
        }
        Check("a second poll from the new watermark is empty", buffer.CopyNewerThan(watermark, scratch) == 0);

        Check("every sequence is still addressable",
            buffer.TryGetBySequence(1, out _) && buffer.TryGetBySequence(2, out _) && buffer.TryGetBySequence(3, out _));
    }


    /// <summary>
    /// CallerFilePath is resolved by the compiler, so a release build carries the absolute path
    /// of the machine that built it and writes it into the file a tester sends back. Outside
    /// the editor the path is trimmed to what the window navigates by; this is the trimming,
    /// which otherwise only ever runs where nothing can look at it.
    /// </summary>
    private static void CallSitesAreTrimmedForABuild() {
        Check("a windows build path keeps the project-relative part",
            LogCore.ProjectRelativePath(@"C:\build\agent\_work\1\s\Assets\Scripts\Net\Client.cs") ==
            @"Assets\Scripts\Net\Client.cs");

        Check("a unix build path does too",
            LogCore.ProjectRelativePath("/home/runner/work/game/Assets/Scripts/Boot.cs") == "Assets/Scripts/Boot.cs");

        Check("a package path is kept from Packages",
            LogCore.ProjectRelativePath("/home/runner/work/game/Packages/com.kensei.log/Runtime/Log.cs") ==
            "Packages/com.kensei.log/Runtime/Log.cs");

        Check("the last Assets wins, for a checkout inside another project",
            LogCore.ProjectRelativePath("/build/Assets/old/checkout/Assets/Scripts/Boot.cs") == "Assets/Scripts/Boot.cs");

        // NuGetForUnity installs into Assets/Packages, so the inner Packages must not take the
        // anchor - it would cut the Assets off the front and leave a path resolving to nothing.
        Check("a package folder under Assets keeps its Assets",
            LogCore.ProjectRelativePath("D:/proj/Assets/Packages/Newtonsoft.Json/Runtime/Foo.cs") ==
            "Assets/Packages/Newtonsoft.Json/Runtime/Foo.cs");

        Check("a Packages folder above the project does not take the anchor",
            LogCore.ProjectRelativePath("/build/Packages/game/Assets/Scripts/Boot.cs") == "Assets/Scripts/Boot.cs");

        Check("a path under neither keeps only its file name",
            LogCore.ProjectRelativePath(@"C:\Users\builder\Library\PackageCache\com.other\Thing.cs") == "Thing.cs");

        Check("an already relative path is left alone",
            LogCore.ProjectRelativePath("Assets/Scripts/Boot.cs") == "Assets/Scripts/Boot.cs");

        Check("nothing is not a path", LogCore.ProjectRelativePath(null) == null && LogCore.ProjectRelativePath("") == "");
    }

    /// <summary>
    /// The buffer is documented as safe from any thread, and a single-threaded check cannot
    /// say that. Four writers against one reader is the shape that matters: a record is built
    /// on whichever thread logged and read back on the main one.
    /// </summary>
    private static void RingBufferHoldsUpUnderThreads() {
        const int writers = 4;
        const int perWriter = 500;
        const int capacity = 256;

        LogRingBuffer buffer = new LogRingBuffer(capacity);
        Exception failure = null;
        bool ordered = true;
        long next = 0;

        Thread[] threads = new Thread[writers];
        for (int t = 0; t < writers; t++) {
            threads[t] = new Thread(() => {
                try {
                    for (int i = 0; i < perWriter; i++) {
                        buffer.Add(Record(Interlocked.Increment(ref next), "T", "x", LogLevel.Log, LogChannel.Prod, 0));
                    }
                } catch (Exception exception) {
                    Interlocked.CompareExchange(ref failure, exception, null);
                }
            });
        }

        Thread reader = new Thread(() => {
            LogRecord[] scratch = new LogRecord[capacity];
            try {
                for (int i = 0; i < 2000; i++) {
                    int copied = buffer.CopyNewerThan(0, scratch);
                    for (int j = 1; j < copied; j++) {
                        if (scratch[j - 1].Sequence > scratch[j].Sequence) {
                            ordered = false;
                        }
                    }
                }
            } catch (Exception exception) {
                Interlocked.CompareExchange(ref failure, exception, null);
            }
        });

        for (int t = 0; t < writers; t++) {
            threads[t].Start();
        }
        reader.Start();
        for (int t = 0; t < writers; t++) {
            threads[t].Join();
        }
        reader.Join();

        Check("concurrent writers and a reader do not throw" + (failure == null ? string.Empty : ": " + failure.Message),
            failure == null);
        Check("a copy is in sequence order however they arrived", ordered);
        Check("the buffer ends up full", buffer.Count == capacity);
    }

    /// <summary>
    /// Collapse points a row at the newest occurrence of its message, so the list stops being
    /// sorted. A prune that scanned only the leading run stopped at the first row holding a
    /// late sequence and left every expired row behind it on screen for good.
    /// </summary>
    private static void PruneClearsExpiredRowsInACollapsedView() {
        TabView view = new TabView(new LogFilter { Name = "Collapsed", Collapse = true });

        view.Append(Record(1, "T", "a", LogLevel.Log, LogChannel.Prod, 0));
        view.Append(Record(2, "T", "b", LogLevel.Log, LogChannel.Prod, 0));
        view.Append(Record(3, "T", "c", LogLevel.Log, LogChannel.Prod, 0));
        view.Append(Record(40, "T", "a", LogLevel.Log, LogChannel.Prod, 0));

        Check("a collapsed view holds one row per message", view.Count == 3);
        Check("the repeated row points at the newest", view.Sequences[0] == 40);

        view.PruneBelow(10);
        Check("expired rows behind a late one are dropped", view.Count == 1);
        Check("the surviving row is the late one", view.Count == 1 && view.Sequences[0] == 40);

        // The index has to survive the prune, or a later repeat opens a second row for a
        // message that already has one.
        view.Append(Record(41, "T", "a", LogLevel.Log, LogChannel.Prod, 0));
        Check("the collapse index survives a collapsed prune", view.Count == 1 && view.Repeats[0] == 3);
    }

    /// <summary>
    /// Once the ring buffer is full it drops one record per record, so the row count stops
    /// moving while the contents keep moving. The window repaints on the revision instead.
    /// </summary>
    private static void ViewRevisionMovesWhenTheContentsDo() {
        TabView plain = new TabView(new LogFilter { Name = "All" });
        int before = plain.Revision;
        plain.Append(Record(1, "T", "one", LogLevel.Log, LogChannel.Prod, 0));
        Check("a new row moves the revision", plain.Revision != before);

        before = plain.Revision;
        plain.PruneBelow(2);
        Check("dropping a row moves the revision", plain.Revision != before);

        TabView collapsed = new TabView(new LogFilter { Name = "Collapsed", Collapse = true });
        collapsed.Append(Record(1, "T", "same", LogLevel.Log, LogChannel.Prod, 0));
        int count = collapsed.Count;
        before = collapsed.Revision;
        collapsed.Append(Record(2, "T", "same", LogLevel.Log, LogChannel.Prod, 0));
        Check("a repeat leaves the row count alone", collapsed.Count == count);
        Check("a repeat still moves the revision", collapsed.Revision != before);
    }

    /// <summary>
    /// A null tag is easy to pass by accident and used to reach the viewers intact, where it
    /// threw out of a dictionary lookup or a palette hash with a stack that named neither the
    /// tag nor the call that passed it.
    /// </summary>
    private static void ANullTagBecomesUntagged() {
        EditorSink.Instance.Clear();
        Log.Prod(null, "a record with no tag at all");

        LogRecord[] scratch = new LogRecord[8];
        int copied = EditorSink.Instance.Buffer.CopyNewerThan(0, scratch);
        Check("a null tag is normalised where the record is built",
            copied == 1 && scratch[0].Tag == LogCore.UntaggedTag);

        LogFilter filter = new LogFilter { Name = "Probe" };
        filter.Tags.Add("Combat");
        Check("the filter can test it without throwing", copied == 1 && !filter.Matches(in scratch[0]));
    }


    /// <summary>
    /// A record is no longer built for a channel nothing will take - in a development build the
    /// list is the file sink with the dev channel off, and every Log.Dev call there was building
    /// a record, unwinding a stack trace if it was an error, and being dropped on arrival.
    /// <para>
    /// What must not follow is a dev record going missing from something that did want it. That
    /// is what this checks, because a silently dropped record is the failure this package is
    /// most afraid of.
    /// </para>
    /// </summary>
    private static void DevRecordsStillReachASinkThatWantsThem() {
        // The editor's own sink takes every channel, so it has to stand aside to leave the
        // pipeline in the shape a build has.
        LogCore.RemoveSink(EditorSink.Instance);

        LogConfig config = LogConfig.Default();
        config.FileDirectory = ScratchDirectory("devchannel");
        config.FileIncludesDevChannel = false;
        FileSink file = new FileSink(in config);
        MemorySink watcher = new MemorySink(8);

        try {
            LogCore.AddSink(file);

            Log.Dev("Probe", "dev, with only a file sink that does not want it");
            Log.Prod("Probe", "prod, which the file sink does want");
            file.Flush();

            string written = ReadWhileOpen(file.CurrentFilePath);
            Check("a prod record still reaches the file", written.Contains("which the file sink does want"));
            Check("a dev record still stays out of it", !written.Contains("does not want it"));

            // Registering something that takes the dev channel has to bring dev records back.
            LogCore.AddSink(watcher);
            Log.Dev("Probe", "dev, now that something wants it");
            Check("a dev record reaches a sink that wants it", watcher.Buffer.Count == 1);

            LogCore.RemoveSink(watcher);
            Log.Dev("Probe", "dev, with the watcher gone again");
            Check("and stops when that sink goes away", watcher.Buffer.Count == 1);

            // The same, through the setting rather than through the sink list.
            config.FileIncludesDevChannel = true;
            file.Reconfigure(in config);
            LogCore.Configure(in config);
            Log.Dev("Probe", "dev, once the file sink is told to take the channel");
            file.Flush();

            Check("turning the channel on in the config brings them back",
                ReadWhileOpen(file.CurrentFilePath).Contains("told to take the channel"));
        } finally {
            LogCore.RemoveSink(watcher);
            LogCore.RemoveSink(file);
            file.Dispose();
            LogCore.AddSink(EditorSink.Instance);
        }
    }

    /// <summary>
    /// A message cut mid-character leaves half a surrogate pair. Written raw it reached the
    /// UTF-8 encoder, which turns it into U+FFFD - the character that says something was here
    /// and loses what. JSON allows the escape, so the original code unit survives.
    /// </summary>
    private static void LoneSurrogatesAreEscapedNotReplaced() {
        string emoji = char.ConvertFromUtf32(0x1F600);

        StringBuilder lone = new StringBuilder();
        LogJson.AppendRecord(lone, Record(1, "T", "cut here: " + emoji[0], LogLevel.Log, LogChannel.Prod, 0));
        Check("a lone surrogate is written as an escape", lone.ToString().Contains("\\ud83d"));

        StringBuilder pair = new StringBuilder();
        LogJson.AppendRecord(pair, Record(2, "T", "ok " + emoji, LogLevel.Log, LogChannel.Prod, 0));
        Check("a complete pair is left as itself",
            pair.ToString().Contains(emoji) && !pair.ToString().Contains("\\ud83d"));
    }

    /// <summary>
    /// Rotation can be refused - on Windows the window itself holds a file open while it reads
    /// one. Returning there left the writer closed and dropped every later record for the rest
    /// of the run, which is the silence this guards against.
    /// </summary>
    private static void RotationThatCannotShiftKeepsWriting() {
        string directory = ScratchDirectory("blocked");
        Directory.CreateDirectory(directory);

        LogConfig config = LogConfig.Default();
        config.FileDirectory = directory;
        config.FileSizeLimitKb = 64;
        config.RetainedFileCount = 2;

        // Occupy the slot the rotation will want to move the current file into.
        string blocked = Path.Combine(directory, "log.1.jsonl");
        File.WriteAllText(blocked, "held open\n");

        FileSink sink = new FileSink(in config);
        using (new FileStream(blocked, FileMode.Open, FileAccess.Read, FileShare.None)) {
            for (int i = 0; i < 4000; i++) {
                sink.Write(Record(i + 1, "Boot", "padding record " + i + " with enough text to push this file past its limit",
                    LogLevel.Log, LogChannel.Prod, i));
            }
            sink.Flush();
            Check("the sink is still writing after a refused rotation", sink.IsWriting);
        }

        sink.Write(Record(99999, "Boot", "written after the rotation failed", LogLevel.Log, LogChannel.Prod, 0));
        sink.Flush();
        Check("records written after a refused rotation are in the file",
            ReadWhileOpen(sink.CurrentFilePath).Contains("written after the rotation failed"));

        sink.Dispose();
    }

    /// <summary>
    /// The shift only ever touched indices inside the retained count, so lowering it left the
    /// files above the new limit orphaned - holding the disk the setting was lowered to free.
    /// </summary>
    private static void LoweringTheRetainedCountRemovesTheOrphans() {
        string directory = ScratchDirectory("retained");
        Directory.CreateDirectory(directory);

        // What a run with a higher retained count would have left behind, plus a current file
        // with something in it, which is what makes a new sink shift them along.
        for (int i = 1; i <= 4; i++) {
            File.WriteAllText(Path.Combine(directory, "log." + i + ".jsonl"), "old file " + i + "\n");
        }
        File.WriteAllText(Path.Combine(directory, "current.jsonl"), "the run before this one\n");

        LogConfig config = LogConfig.Default();
        config.FileDirectory = directory;
        config.RetainedFileCount = 2;

        FileSink sink = new FileSink(in config);
        sink.Dispose();

        Check("files above the new limit are gone",
            !File.Exists(Path.Combine(directory, "log.3.jsonl")) && !File.Exists(Path.Combine(directory, "log.4.jsonl")));
        Check("files inside it are kept",
            File.Exists(Path.Combine(directory, "log.1.jsonl")) && File.Exists(Path.Combine(directory, "log.2.jsonl")));
    }

    /// <summary>
    /// A sink that throws must cost nothing but itself. It used to take the file sink down
    /// with it - the file being the only diagnostic a shipped build has - and the exception
    /// carried on out of Emit into whatever game code had called Log.
    /// </summary>
    private static void AThrowingSinkDoesNotStopTheOthers() {
        ThrowingSink bad = new ThrowingSink();
        MemorySink behind = new MemorySink(8);

        LogCore.AddSink(bad);
        LogCore.AddSink(behind);
        try {
            LogCore.Emit(Record(1, "T", "past a broken sink", LogLevel.Log, LogChannel.Prod, 0));
            Check("a throwing sink does not reach the caller", true);
        } catch (Exception) {
            Check("a throwing sink does not reach the caller", false);
        } finally {
            LogCore.RemoveSink(bad);
            LogCore.RemoveSink(behind);
        }

        Check("the broken sink was actually asked", bad.Calls > 0);
        Check("the sink behind it still got the record", behind.Buffer.Count == 1);
    }

    private sealed class ThrowingSink : ILogSink {
        public int Calls;

        public void Write(in LogRecord record) {
            Calls++;
            throw new InvalidOperationException("this sink is broken on purpose");
        }
    }

    /// <summary>
    /// Without a version in the header, a file written by a later layout reads back as
    /// plausibly wrong data and says nothing about it.
    /// </summary>
    private static void SessionFilesCarryASchemaVersion() {
        const string versionKey = "\"v\":";

        StringBuilder builder = new StringBuilder();
        LogJson.AppendSessionHeader(builder, "sid", "App", "2022.3", "WindowsEditor", "PC", "2026-09-16T00:00:00Z");
        string header = builder.ToString();
        Check("the header carries a schema version", header.Contains(versionKey + LogJson.SchemaVersion));

        StringBuilder record = new StringBuilder();
        LogJson.AppendRecord(record, Record(1, "T", "from the future", LogLevel.Log, LogChannel.Prod, 0));

        string directory = ScratchDirectory("schema");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "future.jsonl");
        File.WriteAllText(path,
            header.Replace(versionKey + LogJson.SchemaVersion, versionKey + (LogJson.SchemaVersion + 1)) + "\n" +
            record.ToString() + "\n");

        bool read = LogSessionReader.TryRead(path, out _, out string error);
        Check("a file from a newer schema is refused", !read);
        Check("and the refusal says why", !read && error != null && error.Contains("newer"));
    }

    /// <summary>
    /// A header and nothing else is the build that died during startup - the case the package
    /// exists for. Refusing it left the only evidence of that death unreadable.
    /// </summary>
    private static void AFileWithOnlyAHeaderStillOpens() {
        string directory = ScratchDirectory("headeronly");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "header-only.jsonl");

        StringBuilder builder = new StringBuilder();
        LogJson.AppendSessionHeader(builder, "sid", "App 1.0", "2022.3", "Android", "Pixel 8", "2026-09-16T00:00:00Z");
        builder.Append('\n');
        File.WriteAllText(path, builder.ToString());

        bool read = LogSessionReader.TryRead(path, out LogSession session, out string error);
        Check("a header-only file opens" + (read ? string.Empty : ": " + error), read);
        Check("and still names the device it came from", read && session.Device == "Pixel 8");
        Check("with an empty buffer rather than none", read && session.Buffer != null && session.Buffer.Count == 0);
    }

    /// <summary>
    /// Where the files go is a setting like the others: a later Configure has to reach it, or
    /// it is accepted and quietly ignored.
    /// </summary>
    private static void FileDirectoryMovesWithTheConfiguration() {
        LogConfig config = LogConfig.Default();
        config.FileDirectory = ScratchDirectory("move-from");

        FileSink sink = new FileSink(in config);
        if (!sink.IsWriting) {
            Check("file sink opened for the directory check", false);
            return;
        }
        sink.Write(Record(1, "Cfg", "before the move", LogLevel.Log, LogChannel.Prod, 0));

        string moved = ScratchDirectory("move-to");
        config.FileDirectory = moved;
        sink.Reconfigure(in config);
        sink.Write(Record(2, "Cfg", "after the move", LogLevel.Log, LogChannel.Prod, 0));
        sink.Flush();

        Check("the sink writes where it was told to", Normalized(sink.LogDirectory) == Normalized(moved));
        Check("and the file is there", File.Exists(sink.CurrentFilePath));
        Check("holding what was written after the move",
            ReadWhileOpen(sink.CurrentFilePath).Contains("after the move"));

        sink.Dispose();
    }

    // =====================================================================

    /// <summary>
    /// Somewhere to write that is not the developer's own log directory. Pointing the file
    /// checks at persistentDataPath meant every run pushed their real logs out of the rotation
    /// and left what it wrote behind.
    /// </summary>
    private static string ScratchDirectory(string name) =>
        Path.Combine(Path.GetTempPath(), "kenseilog-smoke", name);

    private static void CleanUpScratchDirectory() {
        try {
            string root = Path.Combine(Path.GetTempPath(), "kenseilog-smoke");
            if (Directory.Exists(root)) {
                Directory.Delete(root, true);
            }
        } catch (Exception) {
            // A handle still open on Windows is not worth failing a run over.
        }
    }

    /// <summary>
    /// Reads a file the sink still holds open. It opens with FileShare.Read, so a reader has
    /// to allow the writer in turn - which is what File.ReadAllText does not do, and why the
    /// session reader opens with ReadWrite.
    /// </summary>
    private static string ReadWhileOpen(string path) {
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (StreamReader reader = new StreamReader(stream)) {
            return reader.ReadToEnd();
        }
    }

    private static string Normalized(string path) =>
        path.Replace('\\', '/').TrimEnd('/');

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
