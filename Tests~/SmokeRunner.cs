using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
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
        // Both of these outlive a run, and a second Run in the same editor - which is all it
        // takes to ask for one from a menu item - met the first one's files and reported the
        // first one's counts on top of its own.
        CleanUpScratchDirectory();
        _report.Length = 0;
        _failures = 0;

        Scenario(RingBufferKeepsNewestAndAddressesBySequence);
        Scenario(RolledRecordsAreReportedMissing);
        Scenario(RingBufferSettlesARecordThatArrivesLate);
        Scenario(RingBufferHoldsUpUnderThreads);
        Scenario(TheUnboundedBufferLetsNothingGo);
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
        Scenario(ASessionFileComesBackWholeForSeeding);
        Scenario(TheRelatedObjectComesBackForTheEditorsOwnFileOnly);
        Scenario(AWrittenLineReadsBackAsItWasWritten);
        Scenario(ALineFromElsewhereIsReadAsItAlwaysWas);
        Scenario(MemorySinkStoresAndVersions);
        Scenario(TagColoursAreStableAndDistinct);
        Scenario(FileSettingsApplyAfterTheSinkExists);
        Scenario(FileDirectoryMovesWithTheConfiguration);
        Scenario(AContinuedSessionAddsToTheFileItFound);
        Scenario(AContinuedSessionKeepsToItsOwnFileNotAStrangersNewerOne);
        Scenario(AnEditorSessionFollowsItsOwnRotation);
        Scenario(AReloadThatReadsNothingBackStillKeepsItsFile);
        Scenario(AReloadThatReadsNothingBackKeepsOneRunOfNumbering);
        Scenario(SeedingReachesBackThroughTheSessionButNotIntoAnother);
        Scenario(ASessionJoinedMidwayLearnsWhereItBeganAtTheNextRotation);
        Scenario(SeedingReachesBackWhenTheCurrentFileHoldsOnlyItsHeader);
        Scenario(TheWholeSessionComesBackHoweverLongItRan);
        Scenario(SeedingStartsAtTheFileTheWindowWasClearedIn);
        Scenario(AnEditorSessionKeepsEveryFileItFills);
        Scenario(SessionPlanLeavesTheFileToTheEditorProcess);
        Scenario(TheBubbleStillOpensAfterItHasBeenDragged);
        Scenario(CountsSurviveABurstLongerThanTheBuffer);
        Scenario(ACountNeverOutgrowsTheChipItIsDrawnIn);
        Scenario(CountsSurviveAChangeOfCapacity);
        Scenario(TheTagCensusHoldsOnlyWhatTheBufferHolds);
        Scenario(TheIndexSaysWhichSlotItTouched);
        Scenario(TheRingKnowsWhenItHasLetARecordGo);
        Scenario(OnlyAFileThatIsWritingCountsAsKeepingRecords);
        Scenario(ARotatedFileKeepsItsSessionsIdentity);
        Scenario(TheRunsFileLeavesDevToTheEditorsOwn);
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

    /// <summary>
    /// The editor window kept 8192 records and let the oldest go, where Unity's console keeps
    /// everything until it is cleared. Its buffer has no end now: it grows a block at a time, and
    /// every read has to find its way across the joins between blocks - which a buffer smaller
    /// than one block would never have to do, so these go well past one.
    /// </summary>
    private static void TheUnboundedBufferLetsNothingGo() {
        LogRingBuffer buffer = LogRingBuffer.Unbounded();
        const int Total = 10000;
        for (int i = 1; i <= Total; i++) {
            buffer.Add(Record(i, i % 3 == 0 ? "A" : "B", "m" + i, LogLevel.Log, LogChannel.Dev, 0));
        }

        Check("an unbounded buffer holds everything it was given", buffer.Count == Total);
        Check("and has let nothing go", !buffer.HasEvicted && buffer.OldestSequence == 1);
        Check("and says it has no end", buffer.Capacity == int.MaxValue);
        Check("a record either side of a join is found by its sequence",
            buffer.TryGetBySequence(4096, out LogRecord before) && before.Message == "m4096" &&
            buffer.TryGetBySequence(4097, out LogRecord after) && after.Message == "m4097");

        LogRecord[] batch = new LogRecord[3000];
        int copied = buffer.CopyNewerThan(4000, batch);
        bool ordered = copied == 3000;
        for (int i = 0; ordered && i < copied; i++) {
            ordered = batch[i].Sequence == 4001 + i;
        }
        Check("a copy runs across a join in order", ordered);

        List<LogRingBuffer.TagCount> census = new List<LogRingBuffer.TagCount>();
        buffer.CopyTagCounts(census);
        Check("the tags count every record held",
            census.Count == 2 && census.Exists(t => t.Tag == "A" && t.Held == Total / 3) &&
            census.Exists(t => t.Tag == "B" && t.Held == Total - Total / 3));

        // Even numbers fill the first block exactly; the next record opens the second, and the
        // one after it is late and has to move back across the join to be in order.
        LogRingBuffer late = LogRingBuffer.Unbounded();
        for (int i = 1; i <= 4096; i++) {
            late.Add(Record(i * 2, "Tag", "m", LogLevel.Log, LogChannel.Dev, 0));
        }
        late.Add(Record(8193, "Tag", "m", LogLevel.Log, LogChannel.Dev, 0));
        late.Add(Record(8191, "Tag", "late", LogLevel.Log, LogChannel.Dev, 0));
        LogRecord[] tail = new LogRecord[4];
        int settled = late.CopyNewerThan(8190, tail);
        Check("a record that arrives late settles back across a join",
            settled == 3 && tail[0].Sequence == 8191 && tail[1].Sequence == 8192 && tail[2].Sequence == 8193);

        buffer.Clear();
        buffer.CopyTagCounts(census);
        Check("a clear empties it", buffer.Count == 0 && buffer.OldestSequence == 0 && census.Count == 0);
        buffer.Add(Record(Total + 1, "A", "again", LogLevel.Log, LogChannel.Dev, 0));
        Check("and it fills again from there",
            buffer.Count == 1 && buffer.TryGetBySequence(Total + 1, out LogRecord again) && again.Message == "again");
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
        LogIndex view = new LogIndex(filter);

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
        LogIndex view = new LogIndex(filter);

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
        Log.Info("SmokeRunner", "prod reached the sink");
        Log.DevInfo("SmokeRunner", "dev reached the sink");

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
        Log.Info("SmokeRunner", "smoke facade message");

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

        string directory = ScratchDirectory("roundtrip");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "roundtrip.jsonl");
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

        string[] files = Directory.GetFiles(sink.LogDirectory, "log.*.jsonl");
        Check("rotation produced more than one file", files.Length > 1);
        Check("the file in hand exists", File.Exists(sink.CurrentFilePath));
        Check("and it is the highest numbered one", IsHighestNumbered(sink.CurrentFilePath, files));
        Check("no more are kept than the retained count allows", files.Length == config.RetainedFileCount + 1);

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

        Log.Info("no tag on this one");
        Log.Info("SmokeRunner", "this one is tagged");
        Log.Warning("no tag, warning");

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

        _scoped.Info("from the scoped logger");
        _scoped.Child("Child").Warning("from a child logger");
        Logger.For("Other").Error("from a one-off logger");

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
        LogIndex view = new LogIndex(new LogFilter { Name = "Collapsed", Collapse = true });

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
        LogIndex plain = new LogIndex(new LogFilter { Name = "All" });
        int before = plain.Revision;
        plain.Append(Record(1, "T", "one", LogLevel.Log, LogChannel.Prod, 0));
        Check("a new row moves the revision", plain.Revision != before);

        before = plain.Revision;
        plain.PruneBelow(2);
        Check("dropping a row moves the revision", plain.Revision != before);

        LogIndex collapsed = new LogIndex(new LogFilter { Name = "Collapsed", Collapse = true });
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
        Log.Info(null, "a record with no tag at all");

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
    /// list is the file sink with the dev channel off, and every Log.DevInfo call there was building
    /// a record, unwinding a stack trace if it was an error, and being dropped on arrival.
    /// <para>
    /// What must not follow is a dev record going missing from something that did want it. That
    /// is what this checks, because a silently dropped record is the failure this package is
    /// most afraid of.
    /// </para>
    /// </summary>
    private static void DevRecordsStillReachASinkThatWantsThem() {
        // Everything the editor registers takes every channel, so all of it has to stand aside
        // to leave the pipeline in the shape a build has: the window's buffer, and the editor's
        // own session file, which asks for the dev channel on purpose.
        LogCore.RemoveSink(EditorSink.Instance);
        FileSink sessionFile = EditorSink.SessionFile;
        if (sessionFile != null) {
            LogCore.RemoveSink(sessionFile);
        }

        LogConfig config = LogConfig.Default();
        config.FileDirectory = ScratchDirectory("devchannel");
        config.FileIncludesDevChannel = false;
        FileSink file = new FileSink(in config);
        MemorySink watcher = new MemorySink(8);
        CountingSink counter = new CountingSink();

        try {
            LogCore.AddSink(file);

            // The gate is shut when every sink says it turns the channel away, so the counter
            // has to refuse it too - and it counts what actually arrives, which is how the gate
            // is told apart from a sink dropping the record on its own doorstep.
            counter.Takes = LogChannel.Prod;
            LogCore.AddSink(counter);

            Log.DevInfo("Probe", "dev, with nothing that wants it");
            Log.Info("Probe", "prod, which both of them want");
            file.Flush();

            Check("no record is built for a channel nothing takes", counter.Calls == 1);

            string written = ReadWhileOpen(file.CurrentFilePath);
            Check("a prod record still reaches the file", written.Contains("which both of them want"));
            Check("a dev record still stays out of it", !written.Contains("with nothing that wants it"));

            // Registering something that takes the dev channel has to bring dev records back.
            LogCore.AddSink(watcher);
            Log.DevInfo("Probe", "dev, now that something wants it");
            Check("a dev record reaches a sink that wants it", watcher.Buffer.Count == 1);
            Check("and the gate opened for the sinks beside it", counter.Calls == 2);

            LogCore.RemoveSink(watcher);
            Log.DevInfo("Probe", "dev, with the watcher gone again");
            Check("and stops when that sink goes away", watcher.Buffer.Count == 1);
            Check("the gate shuts again behind it", counter.Calls == 2);

            // The same, through the setting rather than through the sink list: Reconfigure has
            // to tell LogCore that its answer changed.
            config.FileIncludesDevChannel = true;
            file.Reconfigure(in config);
            Log.DevInfo("Probe", "dev, once the file sink is told to take the channel");
            file.Flush();

            Check("turning the channel on in the config brings them back",
                ReadWhileOpen(file.CurrentFilePath).Contains("told to take the channel"));
        } finally {
            LogCore.RemoveSink(counter);
            LogCore.RemoveSink(watcher);
            LogCore.RemoveSink(file);
            file.Dispose();
            LogCore.AddSink(EditorSink.Instance);
            if (sessionFile != null) {
                LogCore.AddSink(sessionFile);
            }
        }
    }

    /// <summary>Counts what reaches it, and turns away everything but one channel.</summary>
    private sealed class CountingSink : ILogSink, IChannelFilteredSink {
        public LogChannel Takes;
        public int Calls;

        public bool Accepts(LogChannel channel) =>
            channel == Takes;

        public void Write(in LogRecord record) {
            Calls++;
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
    /// A rotation can be refused - something else holds the name the next file wants, or the
    /// disk has filled. Leaving the writer closed there dropped every later record for the rest
    /// of the run, which is the silence this guards against.
    /// </summary>
    private static void RotationThatCannotShiftKeepsWriting() {
        string directory = ScratchDirectory("blocked");
        Directory.CreateDirectory(directory);

        LogConfig config = LogConfig.Default();
        config.FileDirectory = directory;
        config.FileSizeLimitKb = 64;
        config.RetainedFileCount = 2;

        // Take the name the rotation will reach for, with something that cannot be opened as a
        // file and is not listed as one either - so the sink still picks log.0002 as next, and
        // still cannot have it. Holding a *file* there would not do: the sink would see it in
        // the directory and go to log.0003 instead, and nothing would be refused.
        string blocked = Path.Combine(directory, "log.0002.jsonl");
        Directory.CreateDirectory(blocked);

        FileSink sink = new FileSink(in config);
        Check("the sink opened the first file", sink.CurrentFilePath.EndsWith("log.0001.jsonl", StringComparison.Ordinal));

        for (int i = 0; i < 4000; i++) {
            sink.Write(Record(i + 1, "Boot", "padding record " + i + " with enough text to push this file past its limit",
                LogLevel.Log, LogChannel.Prod, i));
        }
        sink.Flush();

        Check("the sink is still writing after a refused rotation", sink.IsWriting);
        Check("and is still writing to the file it had",
            sink.CurrentFilePath.EndsWith("log.0001.jsonl", StringComparison.Ordinal));

        sink.Write(Record(99999, "Boot", "written after the rotation failed", LogLevel.Log, LogChannel.Prod, 0));
        sink.Flush();
        Check("records written after a refused rotation are in the file",
            ReadWhileOpen(sink.CurrentFilePath).Contains("written after the rotation failed"));

        sink.Dispose();
        Directory.Delete(blocked);
    }

    /// <summary>
    /// Housekeeping keeps the newest files and deletes the rest, so a project that lowers
    /// RetainedFileCount is tidied at the next run rather than leaving the files above the new
    /// limit orphaned for good - holding the disk the setting was lowered to free.
    /// </summary>
    private static void LoweringTheRetainedCountRemovesTheOrphans() {
        string directory = ScratchDirectory("retained");
        Directory.CreateDirectory(directory);

        // What runs with a higher retained count left behind.
        for (int i = 1; i <= 5; i++) {
            File.WriteAllText(Path.Combine(directory, "log." + i.ToString("0000") + ".jsonl"), "old file " + i + "\n");
        }

        LogConfig config = LogConfig.Default();
        config.FileDirectory = directory;
        config.RetainedFileCount = 2;

        // This run takes log.0006, and keeps the two behind it.
        FileSink sink = new FileSink(in config);
        Check("a new run takes the next number", sink.CurrentFilePath.EndsWith("log.0006.jsonl", StringComparison.Ordinal));
        sink.Dispose();

        Check("the newest are kept",
            File.Exists(Path.Combine(directory, "log.0006.jsonl")) &&
            File.Exists(Path.Combine(directory, "log.0005.jsonl")) &&
            File.Exists(Path.Combine(directory, "log.0004.jsonl")));
        Check("everything older is gone",
            !File.Exists(Path.Combine(directory, "log.0003.jsonl")) &&
            !File.Exists(Path.Combine(directory, "log.0002.jsonl")) &&
            !File.Exists(Path.Combine(directory, "log.0001.jsonl")));
    }

    /// <summary>The highest-numbered of the files, by the number in its name.</summary>
    private static bool IsHighestNumbered(string path, string[] files) {
        for (int i = 0; i < files.Length; i++) {
            if (string.CompareOrdinal(Path.GetFileName(files[i]), Path.GetFileName(path)) > 0) {
                return false;
            }
        }
        return true;
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
        Check("the sink behind it still got the record", Holds(behind, "past a broken sink"));

        // The report is the diagnostic, and it used to be kept out of the pipeline - so it
        // reached Player.log and never the file a tester sends back.
        Check("and the report about the failure reaches the sinks too",
            Holds(behind, "threw and will not be reported again"));
    }

    private static bool Holds(MemorySink sink, string text) {
        LogRecord[] scratch = new LogRecord[sink.Buffer.Capacity];
        int copied = sink.Buffer.CopyNewerThan(0, scratch);
        for (int i = 0; i < copied; i++) {
            if (scratch[i].Message != null && scratch[i].Message.Contains(text)) {
                return true;
            }
        }
        return false;
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
    /// What the editor logged before a domain reload is read back out of its session file, so
    /// the window keeps its history across a recompile. The console cannot do this job: it has
    /// nowhere to keep a tag or a channel, and it never saw a Log.DevInfo call at all.
    /// </summary>
    private static void ASessionFileComesBackWholeForSeeding() {
        string directory = ScratchDirectory("seed");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "log.0001.jsonl");

        StringBuilder builder = new StringBuilder();
        LogJson.AppendSessionHeader(builder, "sid", "App 1.0", "2022.3", "WindowsEditor", "PC", "2026-09-16T00:00:00Z");
        builder.Append('\n');
        for (int i = 1; i <= 50; i++) {
            LogJson.AppendRecord(builder, Record(i, "Seed.Tag", "record " + i, LogLevel.Log,
                i % 2 == 0 ? LogChannel.Dev : LogChannel.Prod, i));
            builder.Append('\n');
        }
        File.WriteAllText(path, builder.ToString());

        List<LogRecord> records = new List<LogRecord>();
        int read = LogSessionReader.ReadForSeeding(path, records);
        Check("every record in the file comes back", read == 50 && records.Count == 50);
        Check("the header is not taken for a record", records[0].Message == "record 1");
        Check("and the last is the last written", records[49].Message == "record 50");
        Check("the channel survives, which the console could not carry", records[49].Channel == LogChannel.Dev);
        Check("and so does the tag", records[49].Tag == "Seed.Tag");
        Check("sequences come back as they were written", records[49].Sequence == 50);
        // A window holding a whole session holds tens of thousands of these, and a copy of the
        // tag in each of them was a large share of what it cost.
        Check("records of one tag share the one string for it, as records logged live do",
            ReferenceEquals(records[0].Tag, records[49].Tag));

        // What a writer that died mid-line leaves behind.
        File.AppendAllText(path, "{\"t\":51.0,\"sq\":51,\"f\":0,\"lv\"");
        records.Clear();
        Check("a torn last line is passed over rather than costing the file",
            LogSessionReader.ReadForSeeding(path, records) == 50);

        records.Clear();
        Check("a file that is not there reads as nothing",
            LogSessionReader.ReadForSeeding(Path.Combine(directory, "log.9999.jsonl"), records) == 0);

        // The counter restarts with every app domain, but the file outlives one: what is
        // written after a reload has to carry on above what is already in it.
        long before = LogCore.NextSequence();
        LogCore.ReserveSequencesThrough(before + 500);
        long after = LogCore.NextSequence();
        Check("reserving moves the counter past what the file holds", after > before + 500);

        LogCore.ReserveSequencesThrough(1);
        Check("and never moves it backwards", LogCore.NextSequence() > after);
    }

    /// <summary>
    /// After a domain reload the Ping button was dead for every record the package itself had
    /// written: the related object was the one field of a record the session file left out.
    /// It comes back for the editor's own file and not for one opened by hand - an instance id
    /// means something only inside the editor session that issued it, and from another
    /// machine's build it would resolve here to whatever holds that number.
    /// </summary>
    private static void TheRelatedObjectComesBackForTheEditorsOwnFileOnly() {
        UnityEngine.ScriptableObject target = UnityEngine.ScriptableObject.CreateInstance<UnityEngine.ScriptableObject>();
        target.name = "KenseiLogContextProbe";

        LogRecord against = new LogRecord(1, "Ctx", "logged against an object", LogLevel.Warning, LogChannel.Prod,
            0.0, 0, null, 0, null, target.GetInstanceID());
        LogRecord alone = Record(2, "Ctx", "logged against nothing", LogLevel.Log, LogChannel.Prod, 0);

        StringBuilder plain = new StringBuilder();
        LogJson.AppendRecord(plain, in alone);
        Check("a record with no related object gains no field for it", !plain.ToString().Contains("\"ctx\""));

        string directory = ScratchDirectory("context");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "log.0001.jsonl");

        StringBuilder builder = new StringBuilder();
        LogJson.AppendSessionHeader(builder, "sid", "App 1.0", "2022.3", "WindowsEditor", "PC", "2026-09-16T00:00:00Z");
        builder.Append('\n');
        LogJson.AppendRecord(builder, in against);
        builder.Append('\n');
        builder.Append(plain.ToString());
        builder.Append('\n');
        File.WriteAllText(path, builder.ToString());

        List<LogRecord> seeded = new List<LogRecord>();
        int read = LogSessionReader.ReadForSeeding(path, seeded);
        Check("the editor's own file hands the related object back",
            read == 2 && seeded[0].ContextInstanceId == target.GetInstanceID());
        Check("and it is still the object that was logged against",
            read == 2 && EditorUtility.InstanceIDToObject(seeded[0].ContextInstanceId) == target);
        Check("a record that had none comes back with none", read == 2 && seeded[1].ContextInstanceId == 0);

        bool opened = LogSessionReader.TryRead(path, out LogSession session, out string error);
        Check("the same file opens by hand" + (opened ? string.Empty : ": " + error), opened);
        Check("and a file opened by hand carries no object to ping",
            opened && session.Buffer.TryGetBySequence(1, out LogRecord back) && back.ContextInstanceId == 0);

        UnityEngine.Object.DestroyImmediate(target);
    }

    /// <summary>
    /// A recompile reads the whole session back, and JsonUtility was nearly all of what that
    /// cost, so the lines the writer produces are read by a parser for that one shape. What it
    /// has to get right is everything the writer can put in a line - every escape, every sign,
    /// every field that is sometimes there - so thousands of records are made out of the worst
    /// of it, written, and read back field by field.
    /// <para>
    /// Against the record itself rather than against JsonUtility, because JsonUtility is wrong
    /// about two of the escapes the writer uses on purpose: it threw on a lone high surrogate,
    /// losing the record, returned the whole message empty for a lone low one, and cut a
    /// message off at an escaped NUL.
    /// </para>
    /// </summary>
    private static void AWrittenLineReadsBackAsItWasWritten() {
        LineReaders readers = LineReaders.Find();
        Check("the reader still has the parts these lean on", readers != null);
        if (readers == null) {
            return;
        }

        string emoji = char.ConvertFromUtf32(0x1F600);
        List<LogRecord> records = new List<LogRecord> {
            new LogRecord(1, "Text", "cut mid-character: " + emoji[0], LogLevel.Log, LogChannel.Prod, 0.0, 0, null, 0, null, 0),
            new LogRecord(2, "Text", "the other half: " + emoji[1] + " and on", LogLevel.Log, LogChannel.Prod, 0.0, 0, null, 0, null, 0),
            new LogRecord(3, "Text", "a NUL \0 in the middle", LogLevel.Log, LogChannel.Prod, 0.0, 0, null, 0, null, 0),
            new LogRecord(4, "Text", "whole: " + emoji, LogLevel.Log, LogChannel.Prod, 0.0, 0, null, 0, null, 0),
            new LogRecord(long.MaxValue / 2, "", "", LogLevel.Error, LogChannel.Dev, 0.001, int.MaxValue,
                          "C:\\Users\\dev\\Project\\Assets\\A.cs", int.MaxValue, "at A()\r\n\tat B()", int.MinValue, true),
            new LogRecord(6, "Deep.Nested.Tag", "\"\\/\b\f\n\r\t\u001f", LogLevel.Warning, LogChannel.Dev,
                          999999999999.999, -1, "Assets/B.cs", 0, "", -42, false),
            // The writer's null, which only a record built by hand can carry - and with half a
            // surrogate beside it, a record JsonUtility would have lost outright.
            new LogRecord(7, null, null, LogLevel.Log, LogChannel.Prod, 0.0, 0, null, 0, null, 0),
            new LogRecord(8, "T" + emoji[0], null, LogLevel.Log, LogChannel.Prod, 1.5, 1, null, 0, null, 0),
        };

        // The rest from a fixed seed, so a failure can be run again exactly as it was.
        System.Random random = new System.Random(20260924);
        string[] pieces = {
            "a", "Z", "9", " ", "\"", "\\", "/", "\n", "\r", "\t", "\b", "\f", "\0", "\u0001", "\u001f",
            "ж", "✓", "€", emoji, emoji.Substring(0, 1), emoji.Substring(1, 1), "\ufffd", "\uffff", "{", "}", ",", ":"
        };
        string Text(int longest) {
            StringBuilder text = new StringBuilder();
            int length = random.Next(0, longest);
            for (int i = 0; i < length; i++) {
                text.Append(pieces[random.Next(pieces.Length)]);
            }
            return text.ToString();
        }
        // Tags, call sites and traces recur in a real file, and a recurring one is found by its raw
        // text - escapes and all - rather than read again. Half of them come from here, so that
        // what is found is checked as well as what is read.
        string[] recurring = {
            "Net.Sync", "", "q\"uote", "back\\slash\\", "C:\\Users\\dev\\Project\\Assets\\A.cs", "/Users/dev/A.cs",
            "at A()\r\n\tat B()\n", "\"\\\"", emoji, emoji.Substring(0, 1), "tab\there", "nul\0here"
        };
        string Recurring(int longest) =>
            random.Next(2) == 0 ? recurring[random.Next(recurring.Length)] : Text(longest);
        for (int i = 0; i < 4000; i++) {
            double time = random.NextDouble() * Math.Pow(10, random.Next(0, 12));
            records.Add(new LogRecord(
                ((long)random.Next() << 31) | (long)random.Next(),
                Recurring(12),
                Text(80),
                (LogLevel)random.Next(0, 3),
                (LogChannel)random.Next(0, 2),
                time,
                random.Next(int.MinValue, int.MaxValue),
                random.Next(3) == 0 ? null : Recurring(40),
                random.Next(int.MinValue, int.MaxValue),
                random.Next(3) == 0 ? null : Recurring(200),
                random.Next(int.MinValue, int.MaxValue),
                random.Next(2) == 0));
        }

        int refusedByTheParser = 0;
        int wrong = 0;
        int disagreedWithJson = 0;
        string firstWrong = null;
        StringBuilder builder = new StringBuilder();
        for (int i = 0; i < records.Count; i++) {
            LogRecord original = records[i];
            builder.Length = 0;
            LogJson.AppendRecord(builder, in original);
            string line = builder.ToString();

            if (!readers.Written(line, out LogRecord fast)) {
                refusedByTheParser++;
                continue;
            }
            string difference = Difference(original, fast);
            if (difference != null) {
                wrong++;
                firstWrong = firstWrong ?? "#" + i + " " + difference;
            }

            // Where JsonUtility is right, the two have to agree as well.
            if (!HasEscapeJsonUtilityGetsWrong(original) && readers.AnyJson(line, out LogRecord slow) &&
                Difference(slow, fast) != null) {
                disagreedWithJson++;
            }
        }

        Check("the parser takes every line the writer writes (" + refusedByTheParser + " refused)", refusedByTheParser == 0);
        Check("and reads every one back as it was written" + (firstWrong != null ? ": " + firstWrong : string.Empty), wrong == 0);
        Check("agreeing with JsonUtility wherever JsonUtility is right (" + disagreedWithJson + " did not)", disagreedWithJson == 0);

        builder.Length = 0;
        LogJson.AppendRecord(builder, records[0]);
        Check("a lone surrogate comes back where JsonUtility lost the record",
            readers.Read(builder.ToString(), out LogRecord lone) && lone.Message == records[0].Message);

        LogRecord windows = new LogRecord(7, "Net.Sync", "one", LogLevel.Log, LogChannel.Prod, 0.0, 0,
                                          "C:\\Users\\dev\\Project\\Assets\\A.cs", 1, null, 0);
        LogRecord again = new LogRecord(8, "Net.Sync", "two", LogLevel.Log, LogChannel.Prod, 0.0, 0,
                                        "C:\\Users\\dev\\Project\\Assets\\A.cs", 2, null, 0);
        builder.Length = 0;
        LogJson.AppendRecord(builder, in windows);
        string first = builder.ToString();
        builder.Length = 0;
        LogJson.AppendRecord(builder, in again);
        Check("a call site seen before is the one string it was the first time, escapes and all",
            readers.Written(first, out LogRecord one) && readers.Written(builder.ToString(), out LogRecord two) &&
            one.File == windows.File && ReferenceEquals(one.File, two.File) && ReferenceEquals(one.Tag, two.Tag));

        // "Aa" and "BB" hash alike under h * 31 + c, as every pair of their doublings does. Random
        // text never collides often enough to show a table that trusted the hash alone.
        string[] colliding = { "Aa", "BB", "AaBB", "BBAa", "AaAa", "BBBB" };
        bool apart = true;
        for (int i = 0; i < colliding.Length; i++) {
            builder.Length = 0;
            LogJson.AppendRecord(builder, new LogRecord(20 + i, colliding[i], "m", LogLevel.Log, LogChannel.Prod, 0.0, 0,
                                                        colliding[colliding.Length - 1 - i], 1, null, 0));
            apart &= readers.Written(builder.ToString(), out LogRecord told) &&
                     told.Tag == colliding[i] && told.File == colliding[colliding.Length - 1 - i];
        }
        Check("two texts that hash alike are still told apart", apart);
    }

    /// <summary>
    /// What the parser does not recognise goes to JsonUtility, and has to come back exactly as it
    /// did before there was a parser: a line from a schema this build does not know, one somebody
    /// edited, a torn last line. Each is refused by the parser - taking one would mean guessing at
    /// a shape the writer never produces - and the result is JsonUtility's own.
    /// </summary>
    private static void ALineFromElsewhereIsReadAsItAlwaysWas() {
        LineReaders readers = LineReaders.Find();
        if (readers == null) {
            return;
        }

        const string Head = "{\"t\":1.5,\"sq\":7,\"f\":2,\"lv\":0,\"ch\":1,\"tag\":\"T\",";
        string[] elsewhere = {
            "{ \"t\":1.5,\"sq\":7,\"f\":2,\"lv\":0,\"ch\":1,\"tag\":\"T\",\"msg\":\"spaced\" }",
            Head + "\"msg\":\"a field from later\",\"zz\":5}",
            Head + "\"msg\":\"first\",\"msg\":\"twice\"}",
            "{\"t\":1.5,\"sq\":007,\"f\":2,\"lv\":0,\"ch\":1,\"tag\":\"T\",\"msg\":\"m\"}",
            "{\"t\":1e3,\"sq\":7,\"f\":2,\"lv\":0,\"ch\":1,\"tag\":\"T\",\"msg\":\"m\"}",
            "{\"t\":1.5,\"sq\":7,\"f\":2.5,\"lv\":0,\"ch\":1,\"tag\":\"T\",\"msg\":\"m\"}",
            "{\"t\":1.5,\"sq\":7,\"f\":3000000000,\"lv\":0,\"ch\":1,\"tag\":\"T\",\"msg\":\"m\"}",
            "{\"t\":1.5,\"sq\":7,\"f\":2,\"lv\":0,\"ch\":1,\"msg\":\"no tag\"}",
            "{\"t\":1.5,\"sq\":7,\"f\":2,\"lv\"",
            Head + "\"msg\":\"m\"}trailing",
            Head + "\"msg\":\"a raw \u0001 control\"}",
            Head + "\"msg\":\"a bad \\x escape\"}",
            "{\"session\":\"abc\",\"v\":1,\"app\":\"A\"}",
            string.Empty,
        };

        int taken = 0;
        int changed = 0;
        for (int i = 0; i < elsewhere.Length; i++) {
            if (readers.Written(elsewhere[i], out _)) {
                taken++;
            }
            bool before = readers.AnyJson(elsewhere[i], out LogRecord old);
            bool now = readers.Read(elsewhere[i], out LogRecord read);
            if (before != now || (now && Difference(old, read) != null)) {
                changed++;
            }
        }
        Check("the parser refuses every line the writer would not have written (" + taken + " taken)", taken == 0);
        Check("and each reads exactly as JsonUtility read it (" + changed + " did not)", changed == 0);
    }

    /// <summary>The first field in which two records differ, or null when none does.</summary>
    private static string Difference(LogRecord expected, LogRecord actual) {
        double time = double.Parse(expected.TimeMs.ToString("0.###", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        if (BitConverter.DoubleToInt64Bits(time) != BitConverter.DoubleToInt64Bits(actual.TimeMs)) {
            return "time " + time.ToString("R", CultureInfo.InvariantCulture) + " came back " + actual.TimeMs.ToString("R", CultureInfo.InvariantCulture);
        }
        if (expected.Sequence != actual.Sequence) {
            return "sequence";
        }
        // A null tag or message is written as null and read as empty, as JsonUtility read it.
        if ((expected.Tag ?? string.Empty) != actual.Tag) {
            return "tag";
        }
        if ((expected.Message ?? string.Empty) != actual.Message) {
            return "message";
        }
        if (expected.Level != actual.Level || expected.Channel != actual.Channel) {
            return "level or channel";
        }
        if (expected.Frame != actual.Frame) {
            return "frame";
        }
        // The writer leaves the line out with the file, and an empty file or trace reads as none.
        string file = string.IsNullOrEmpty(expected.File) ? null : expected.File;
        if (file != actual.File || (file != null && expected.Line != actual.Line)) {
            return "call site";
        }
        string trace = string.IsNullOrEmpty(expected.StackTrace) ? null : expected.StackTrace;
        if (trace != actual.StackTrace) {
            return "stack trace";
        }
        if (expected.ContextInstanceId != actual.ContextInstanceId) {
            return "related object";
        }
        if (expected.Captured != actual.Captured) {
            return "captured";
        }
        return null;
    }

    private static bool HasEscapeJsonUtilityGetsWrong(LogRecord record) {
        foreach (string text in new[] { record.Tag, record.Message, record.File, record.StackTrace }) {
            if (text == null) {
                continue;
            }
            for (int i = 0; i < text.Length; i++) {
                if (text[i] == '\0' || (char.IsSurrogate(text[i]) &&
                    !(char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])))) {
                    return true;
                }
                if (char.IsHighSurrogate(text[i])) {
                    i++;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// The reader's three ways into a line, reached by reflection since none of them is public:
    /// the parser alone, JsonUtility alone, and the two in the order the reader tries them. All
    /// three share one pool of strings, as the lines of one file do - a pool made fresh for each
    /// line would never be asked for a string it had seen, which is the half of it that can go
    /// wrong quietly.
    /// </summary>
    private sealed class LineReaders {
        private MethodInfo _written;
        private MethodInfo _anyJson;
        private MethodInfo _read;
        private object _shared;

        public static LineReaders Find() {
            const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Static;
            Type parser = typeof(LogSessionReader).GetNestedType("WrittenLine", BindingFlags.NonPublic);
            Type pool = typeof(LogSessionReader).GetNestedType("SharedStrings", BindingFlags.NonPublic);
            LineReaders readers = new LineReaders {
                _written = parser?.GetMethod("TryRead", BindingFlags.Public | BindingFlags.Static),
                _anyJson = typeof(LogSessionReader).GetMethod("TryReadAnyJson", Hidden),
                _read = typeof(LogSessionReader).GetMethod("TryReadRecord", Hidden),
                _shared = pool != null ? Activator.CreateInstance(pool, nonPublic: true) : null,
            };
            return readers._written != null && readers._anyJson != null && readers._read != null && readers._shared != null
                ? readers
                : null;
        }

        public bool Written(string line, out LogRecord record) =>
            Invoke(_written, line, out record);

        public bool AnyJson(string line, out LogRecord record) =>
            Invoke(_anyJson, line, out record);

        public bool Read(string line, out LogRecord record) =>
            Invoke(_read, line, out record);

        private bool Invoke(MethodInfo method, string line, out LogRecord record) {
            object[] arguments = { line, true, _shared, null };
            bool read = (bool)method.Invoke(null, arguments);
            record = read ? (LogRecord)arguments[3] : default;
            return read;
        }
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
    /// Remembering the session file made the editor immune to a stranger's, and stopped being
    /// true the moment its own rotated: the path was written once, at open, while Rotate moves
    /// the file on as soon as the size limit is passed. Every reload after that carried on with
    /// a file already over the limit, rotated it again on its first record, and left another
    /// behind - so the count climbed, the window came back holding only what was written before
    /// the rotation, and pruning worked through the full ones.
    /// <para>
    /// Driven through the editor sink's own closing code and through <see cref="SessionPlan"/>,
    /// so that taking either apart shows up here as behaviour rather than as a compiler error.
    /// </para>
    /// </summary>
    private static void AnEditorSessionFollowsItsOwnRotation() {
        EditorSessionHarness harness = EditorSessionHarness.Open("rotation-follow");
        if (harness == null) {
            return;
        }

        try {
            string opened = harness.Sink.CurrentFilePath;
            string began = harness.FirstFile;
            harness.FillUntilRotation();
            Check("writing past the limit moves the file on", harness.Sink.CurrentFilePath != opened);

            for (int reload = 0; reload < 5; reload++) {
                string live = harness.Sink.CurrentFilePath;
                harness.Reload();

                Check("closing remembers the file the session ended in", harness.Remembered == live);
                Check("so the next domain carries on with that one", harness.Sink.CurrentFilePath == live);
                Check("while the file the session began with stays where it was", harness.FirstFile == began);
                harness.Write("after reload " + reload);
            }

            harness.Close();
            Check("and five reloads leave no trail of files behind", harness.FileCount == 2);
            Check("with the last record still in the file that was remembered",
                File.ReadAllText(harness.Remembered).Contains("after reload 4"));
        } finally {
            harness.Dispose();
        }
    }

    /// <summary>
    /// Carrying on with the file used to require reading it back, and two ordinary reloads read
    /// nothing: entering play mode with Clear on Play set, which skips the seed deliberately and
    /// is the reload people do dozens of times a day, and a rotation on the last record before a
    /// reload, which leaves a file holding a header and nothing else. Both started a file
    /// instead, so a few entries into play mode the morning's editor logs had been pruned away -
    /// the very failure the file exists to prevent.
    /// </summary>
    private static void AReloadThatReadsNothingBackStillKeepsItsFile() {
        EditorSessionHarness playMode = EditorSessionHarness.Open("no-seed-play");
        if (playMode == null) {
            return;
        }

        try {
            playMode.Write("logged in the editor before play");
            string before = playMode.Sink.CurrentFilePath;

            for (int entry = 0; entry < 5; entry++) {
                playMode.Reload(worthSeeding: false);
                playMode.Write("entered play " + entry);
            }
            playMode.Close();

            Check("entering play mode keeps to the one file", playMode.FileCount == 1);
            Check("so what was logged before play is still on disk",
                File.ReadAllText(before).Contains("logged in the editor before play"));
            Check("and closing recorded the numbering for the next domain to carry on from",
                playMode.StoredSequence == LogCore.CurrentSequence);
        } finally {
            playMode.Dispose();
        }

        EditorSessionHarness rotated = EditorSessionHarness.Open("no-seed-rotated");
        if (rotated == null) {
            return;
        }

        try {
            rotated.FillUntilRotation();
            string headerOnly = rotated.Sink.CurrentFilePath;
            rotated.Reload();

            Check("a file holding only a header is still the one to carry on with",
                rotated.Sink.CurrentFilePath == headerOnly);
            // The window is the point of the read. A file holding a header alone is the one
            // case the reach-back past a rotation was added for, so a blank window here is the
            // reach-back never happening. Counted by what only the file can supply: a window
            // filled from the console instead would pass a plain count, and did.
            Check("and the window comes back holding what was written before the rotation",
                rotated.WindowCountFromTheFile > 1);
            Check("out of the file rather than off the console", rotated.Seeded);

            rotated.Write("first record after the rotation");
            rotated.Reload();
            // No companion assertion on Seeded here: by now the current file holds a record of
            // its own, so it seeds whether or not anything reaches back, and a check that cannot
            // fail is worse than no check.
            Check("a second reload reaches back past the same rotation again",
                rotated.WindowCountFromTheFile > 1);
            rotated.Close();

            Check("so a rotation on the last record leaves no empty file behind", rotated.FileCount == 2);
            Check("and the record after it went into that file",
                File.ReadAllText(headerOnly).Contains("first record after the rotation"));
        } finally {
            rotated.Dispose();
        }
    }

    /// <summary>
    /// The numbering has to cross a reload that read nothing back, and the only thing carrying
    /// it is the reserve the sink makes out of what the last domain wrote down - before a record
    /// is written, and whether or not any came back. Nothing here used to run that: the checks
    /// reopened the file with a FileSink of their own, so the reserve could be taken out with
    /// every one of them still green while the editor wrote records numbered 1, 2, 3 into a file
    /// that already had records numbered 1, 2, 3 in it.
    /// </summary>
    private static void AReloadThatReadsNothingBackKeepsOneRunOfNumbering() {
        EditorSessionHarness harness = EditorSessionHarness.Open("install-numbering");
        if (harness == null) {
            return;
        }

        try {
            string file = harness.Sink.CurrentFilePath;
            Check("opening a session writes down the file it began with", harness.FirstFile == file);

            for (int i = 0; i < 20; i++) {
                harness.Write("before the reload " + i);
            }

            // Entering play mode with Clear on Play set: the seed is skipped on purpose, so this
            // reload has nothing to take its numbering from but what the last domain recorded.
            harness.Reload(worthSeeding: false);

            Check("the reload carries on with the file it was given", harness.Sink.CurrentFilePath == file);
            Check("and leaves the file the session began with where it was", harness.FirstFile == file);

            for (int i = 0; i < 20; i++) {
                harness.Write("after the reload " + i);
            }
            harness.Close();

            List<LogRecord> written = new List<LogRecord>();
            LogSessionReader.ReadForSeeding(file, written);
            Check("both domains wrote into the one file", written.Count >= 40);
            Check("with the ids rising and never repeating", IsAscending(written));
        } finally {
            harness.Dispose();
        }
    }

    /// <summary>
    /// A package resolved again in a running editor - a git dependency moved on, and nobody
    /// restarts for that - leaves this sink joining a session that has been writing its file
    /// since before anything wrote down where it began. Read as "no session", that turns the
    /// reach-back off for the rest of the editor's life, which is the whole of the first
    /// session anybody runs a new version in.
    /// <para>
    /// The file in hand is the earliest of the session anything can vouch for, so it becomes
    /// the floor: the reach-back is off until the next rotation and right from then on, and
    /// nothing belonging to an editor that has closed is let in on the way.
    /// </para>
    /// </summary>
    private static void ASessionJoinedMidwayLearnsWhereItBeganAtTheNextRotation() {
        EditorSessionHarness harness = EditorSessionHarness.Open("joined-midway");
        if (harness == null) {
            return;
        }

        try {
            harness.FillUntilRotation();
            string continued = harness.Sink.CurrentFilePath;
            harness.Close();

            // What an editor that was already running when this version arrived looks like: a
            // file to carry on with, and nothing saying which session it belongs to.
            harness.ForgetsWhereItBegan();
            Check("a session joined midway does not know where it began", harness.FirstFile == null);
            Check("so the file behind the one in hand stays out of the window",
                harness.Seed(continued).Count == RecordsIn(continued));

            harness.Reload();
            Check("carrying on with a file makes that file the floor", harness.FirstFile == continued);
            Check("and the reload still keeps to it", harness.Sink.CurrentFilePath == continued);

            harness.FillUntilRotation();
            string rotated = harness.Sink.CurrentFilePath;
            Check("a rotation leaves the floor behind the file now in hand",
                harness.FirstFile != null && FileSink.WrittenBefore(harness.FirstFile, rotated));
            Check("so the reach-back works from there on", harness.Seed(rotated).Count > 0);
        } finally {
            harness.Dispose();
        }
    }

    /// <summary>How many records a file holds, for a check that has to know what it asked for.</summary>
    private static int RecordsIn(string path) {
        List<LogRecord> records = new List<LogRecord>();
        return LogSessionReader.ReadForSeeding(path, records);
    }

    /// <summary>
    /// A rotation shortly before a reload leaves the file now current holding a handful of
    /// records, and the session's history in the ones behind it. Read on its own, the window came
    /// back all but empty and looked as though the recompile had eaten the morning - and reaching
    /// back one file only did the same a rotation later.
    /// <para>
    /// How far back to reach is the part that has to be right, and reading it off the record ids
    /// is not enough. The directory outlives the editor, so the file behind the current one is
    /// as likely to be yesterday's as this morning's - and a short file left behind yesterday
    /// numbers below everything an editor that has been up an hour holds, which is exactly the
    /// shape that was being taken as proof of one run. Where this session began answers it, and
    /// nothing else does.
    /// </para>
    /// </summary>
    private static void SeedingReachesBackThroughTheSessionButNotIntoAnother() {
        EditorSessionHarness harness = EditorSessionHarness.Borrow("seed-chain");
        if (harness == null) {
            return;
        }

        try {
            LogConfig config = ScratchConfig(harness.Directory, 64);
            FileSink sink = new FileSink(in config);
            string first = sink.CurrentFilePath;
            string filler = new string('x', 512);
            long sequence = 1;
            while (sink.CurrentFilePath == first) {
                sink.Write(Record(sequence++, "Editor", filler, LogLevel.Log, LogChannel.Dev, 0));
            }
            string second = sink.CurrentFilePath;
            while (sink.CurrentFilePath == second) {
                sink.Write(Record(sequence++, "Editor", filler, LogLevel.Log, LogChannel.Dev, 0));
            }
            string third = sink.CurrentFilePath;
            sink.Write(Record(sequence++, "Editor", "the only record after the rotations", LogLevel.Log, LogChannel.Dev, 0));
            sink.Dispose();
            long written = sequence - 1;

            Check("the file before one is the one before it",
                FileSink.FileBefore(third) == second && FileSink.FileBefore(second) == first);
            Check("and the first file has nothing before it", FileSink.FileBefore(first) == null);

            harness.StartedWith(first);
            List<LogRecord> reached = harness.Seed(third);
            Check("seeding reaches back past every rotation to where the session began", reached.Count == written);
            Check("and brings the run back in order, with nothing missing out of it",
                reached.Count > 0 && reached[0].Sequence == 1 && IsUnbroken(reached));

            harness.StartedWith(third);
            Check("a session that began after the rotations keeps to the file it began with",
                harness.Seed(third).Count == 1);
        } finally {
            harness.Dispose();
        }

        // Yesterday's editor, left in the directory this one resolves to: every id in its file
        // below every id in ours, which is what used to stand for proof that the two were one
        // run. An editor that has been up an hour makes that true of any short file behind it.
        EditorSessionHarness today = EditorSessionHarness.Borrow("seed-yesterday");
        if (today == null) {
            return;
        }

        try {
            LogConfig config = ScratchConfig(today.Directory, 64);
            FileSink yesterday = new FileSink(in config);
            string theirs = yesterday.CurrentFilePath;
            for (long id = 1; id <= 40; id++) {
                yesterday.Write(Record(id, "Editor", "from the editor before this one", LogLevel.Log, LogChannel.Dev, 0));
            }
            yesterday.Dispose();

            FileSink mine = new FileSink(in config);
            string ours = mine.CurrentFilePath;
            for (long id = 1000; id <= 1050; id++) {
                mine.Write(Record(id, "Editor", "from this one", LogLevel.Log, LogChannel.Dev, 0));
            }
            mine.Dispose();

            Check("the two are one behind the other on disk", FileSink.FileBefore(ours) == theirs);

            today.StartedWith(ours);
            List<LogRecord> guarded = today.Seed(ours);
            Check("seeding stops at the file this session began with", guarded.Count == 51);
            Check("so yesterday's records stay out of today's window",
                guarded.Count > 0 && guarded[0].Sequence == 1000);
        } finally {
            today.Dispose();
        }
    }

    /// <summary>
    /// The read-back was unreachable in the one case it was added for. A rotation on the last
    /// record before a reload leaves the file now current holding a header and nothing else;
    /// the read came back empty, and the empty read returned before the reach-back was ever
    /// called. So that reload seeded from the console instead - no tag, no channel, no call
    /// site, no stack trace on any of it - which is the outcome the file exists to prevent.
    /// </summary>
    private static void SeedingReachesBackWhenTheCurrentFileHoldsOnlyItsHeader() {
        EditorSessionHarness harness = EditorSessionHarness.Borrow("seed-header-only");
        if (harness == null) {
            return;
        }

        try {
            LogConfig config = ScratchConfig(harness.Directory, 64);
            FileSink sink = new FileSink(in config);
            string first = sink.CurrentFilePath;
            string filler = new string('x', 512);
            long sequence = 1;
            while (sink.CurrentFilePath == first) {
                sink.Write(Record(sequence++, "Editor", filler, LogLevel.Log, LogChannel.Dev, 0));
            }
            string headerOnly = sink.CurrentFilePath;
            sink.Dispose();

            Check("the rotation left a file holding its header and nothing else",
                ReadWhileOpen(headerOnly).Trim().IndexOf('\n') < 0);

            harness.StartedWith(first);
            List<LogRecord> seeded = harness.Seed(headerOnly);
            Check("the window still comes back holding the session", seeded.Count > 1);
            Check("and the reload says so, rather than falling back to the console",
                harness.Seeded);
            Check("so the records keep their channel, which the console has nowhere to put",
                seeded.Count > 0 && seeded[0].Channel == LogChannel.Dev);
        } finally {
            harness.Dispose();
        }
    }

    /// <summary>
    /// A recompile brought back the last two megabytes of the session and no more, into a window
    /// that kept 8192 records - so a long session came back shorter than it went in, with nothing
    /// to say so. A limit, however it was worded, and the kind this package has learned is worst.
    /// The seed reads the whole session now: every file it filled, from the one it began with.
    /// </summary>
    private static void TheWholeSessionComesBackHoweverLongItRan() {
        EditorSessionHarness harness = EditorSessionHarness.Borrow("seed-whole");
        if (harness == null) {
            return;
        }

        try {
            // Past both of the old bounds at once, over several rotations.
            const int Written = 12000;
            string filler = new string('x', 300);
            LogConfig config = ScratchConfig(harness.Directory, 1024);
            config.RetainedFileCount = 32;
            FileSink sink = new FileSink(in config);
            string first = sink.CurrentFilePath;
            for (long id = 1; id <= Written; id++) {
                sink.Write(Record(id, "Editor", filler, LogLevel.Log, LogChannel.Dev, 0));
            }
            string last = sink.CurrentFilePath;
            sink.Dispose();

            long bytes = 0;
            foreach (string file in Directory.GetFiles(harness.Directory, "log.*.jsonl")) {
                bytes += new FileInfo(file).Length;
            }
            Check("the session filled several files", harness.FileCount >= 3);
            Check("and more than the seed used to be allowed to read", bytes > 2L * 1024L * 1024L);

            harness.StartedWith(first);
            List<LogRecord> seeded = harness.Seed(last);
            Check("every record the session wrote comes back", seeded.Count == Written);
            Check("from its very first, in one unbroken run",
                seeded.Count > 0 && seeded[0].Sequence == 1 && IsUnbroken(seeded));
            Check("and the window holds all of it", harness.WindowCount == Written);
        } finally {
            harness.Dispose();
        }
    }

    /// <summary>
    /// Clear on Play is on by default, so the window is cleared on every entry to play mode - and
    /// a seed that read from the beginning of the session read the whole day back after each
    /// recompile, only to drop nearly all of it. It starts at the file that was being written when
    /// the window was cleared, since nothing it keeps can be in an earlier one.
    /// </summary>
    private static void SeedingStartsAtTheFileTheWindowWasClearedIn() {
        EditorSessionHarness harness = EditorSessionHarness.Open("seed-after-clear");
        if (harness == null) {
            return;
        }

        try {
            string began = harness.FirstFile;
            harness.FillUntilRotation();
            harness.FillUntilRotation();
            string cleared = harness.Sink.CurrentFilePath;
            harness.Write("before the clear");
            harness.Clear();
            harness.Write("after the clear");
            harness.Close();

            Check("a clear writes down the file it happened in", harness.ClearedInFile == cleared);
            Check("which is not where the session began",
                began != null && FileSink.WrittenBefore(began, cleared));

            List<LogRecord> seeded = harness.Seed(cleared);
            Check("the seed reads from that file and not from the files before it",
                seeded.Count == 2 && seeded[0].Message == "before the clear");
            Check("and what the clear dismissed stays dismissed", harness.WindowCount == 1);
        } finally {
            harness.Dispose();
        }
    }

    /// <summary>
    /// RetainedFileCount deleted the oldest files at every rotation, whoever had written them, so
    /// a long editor session pruned its own beginning - and the window, rebuilt from those files
    /// after a recompile, came back without it. The editor's sink keeps every file of its own
    /// session and prunes only what earlier sessions left; a build's sink still keeps to the
    /// count, which is what stands between a device and a full disk.
    /// </summary>
    private static void AnEditorSessionKeepsEveryFileItFills() {
        LogConfig config = ScratchConfig(ScratchDirectory("keep-session"), 64);
        config.RetainedFileCount = 1;

        FileSink yesterday = new FileSink(in config);
        string theirs = yesterday.CurrentFilePath;
        yesterday.Write(Record(1, "Editor", "from an earlier session", LogLevel.Log, LogChannel.Dev, 0));
        yesterday.Dispose();

        FileSink today = new FileSink(in config, continueExistingFile: false, keepsSessionFiles: true);
        string began = today.CurrentFilePath;
        long sequence = RotateTimes(today, 4, 1);
        string reached = today.CurrentFilePath;
        today.Dispose();

        Check("every file the session filled is still there",
            File.Exists(began) && Directory.GetFiles(config.FileDirectory, "log.*.jsonl").Length == 5);
        Check("while the earlier session's file went as the count says", !File.Exists(theirs));

        // Carrying on, as the editor does after every recompile: this sink opened none of the
        // files behind the one it continues, and has only their headers to know them by.
        FileSink continued = new FileSink(in config, continueExistingFile: true, continueFilePath: reached,
                                          keepsSessionFiles: true);
        RotateTimes(continued, 2, sequence);
        continued.Dispose();
        Check("and a sink that carries on with the session keeps them as well",
            File.Exists(began) && Directory.GetFiles(config.FileDirectory, "log.*.jsonl").Length == 7);

        config.FileDirectory = ScratchDirectory("keep-session-build");
        FileSink run = new FileSink(in config);
        string runBegan = run.CurrentFilePath;
        RotateTimes(run, 4, 1);
        run.Dispose();
        Check("a build's sink still prunes its own run to the count",
            !File.Exists(runBegan) && Directory.GetFiles(config.FileDirectory, "log.*.jsonl").Length == 2);

        // And through the editor's own wiring, across a reload, which is the only place the
        // choice is made: a sink built here with the flag set says nothing about whether the
        // editor still sets it.
        EditorSessionHarness harness = EditorSessionHarness.Open("keep-session-editor");
        if (harness == null) {
            return;
        }

        try {
            string first = harness.FirstFile;
            int retained = LogCore.Config.RetainedFileCount;
            for (int i = 0; i < retained + 2; i++) {
                harness.FillUntilRotation();
            }
            harness.Reload();
            harness.FillUntilRotation();
            harness.Close();
            Check("the editor's session keeps its first file past the retained count, reload and all",
                first != null && File.Exists(first) && harness.FileCount == retained + 4);
        } finally {
            harness.Dispose();
        }
    }

    /// <summary>Writes filler until the sink has moved on this many files; returns the next id.</summary>
    private static long RotateTimes(FileSink sink, int rotations, long sequence) {
        string filler = new string('x', 512);
        for (int i = 0; i < rotations; i++) {
            string opened = sink.CurrentFilePath;
            while (sink.CurrentFilePath == opened) {
                sink.Write(Record(sequence++, "Editor", filler, LogLevel.Log, LogChannel.Dev, 0));
            }
        }
        return sequence;
    }

    private static bool IsAscending(List<LogRecord> records) {
        for (int i = 1; i < records.Count; i++) {
            if (records[i].Sequence <= records[i - 1].Sequence) {
                return false;
            }
        }
        return true;
    }

    /// <summary>Ascending and with nothing missing out of the middle.</summary>
    private static bool IsUnbroken(List<LogRecord> records) {
        for (int i = 1; i < records.Count; i++) {
            if (records[i].Sequence != records[i - 1].Sequence + 1) {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The decisions themselves, away from the domain reload that is the only thing that makes
    /// them. Every fault here has been a decision rather than a mechanic: a process opening a
    /// file it had no business opening, a path that was no longer the file being written, and
    /// carrying on with a file made conditional on reading it back.
    /// </summary>
    private static void SessionPlanLeavesTheFileToTheEditorProcess() {
        Check("the editor installs", SessionPlan.Installs(false, false));
        Check("an asset import worker does not", !SessionPlan.Installs(true, false));
        Check("nor does an out-of-process profiler", !SessionPlan.Installs(false, true));

        Func<string, bool> there = path => true;
        Func<string, bool> gone = path => false;
        Func<string, bool> read = path => true;
        Func<string, bool> readNothing = path => false;
        const string Remembered = "log.0007.jsonl";

        Check("a reload reads its history back out of the file it remembers",
            SessionPlan.Resolve(true, true, true, Remembered, there, read).Seeded);
        Check("and carries on writing it",
            SessionPlan.Resolve(true, true, true, Remembered, there, read).ContinuePath == Remembered);

        Check("a read that found nothing still carries on with the file",
            SessionPlan.Resolve(true, true, true, Remembered, there, readNothing).ContinuePath == Remembered);
        Check("and says plainly that it seeded nothing",
            !SessionPlan.Resolve(true, true, true, Remembered, there, readNothing).Seeded);

        Check("a seed that would be thrown away is skipped, the file kept",
            SessionPlan.Resolve(true, true, false, Remembered, there, read).ContinuePath == Remembered);
        Check("and skipped means not seeded",
            !SessionPlan.Resolve(true, true, false, Remembered, there, read).Seeded);

        Check("a remembered file that has gone starts one",
            SessionPlan.Resolve(true, true, true, Remembered, gone, read).ContinuePath == null);
        Check("a fresh editor starts one",
            SessionPlan.Resolve(false, true, true, Remembered, there, read).ContinuePath == null);
        Check("with the session file turned off there is no file and no seed",
            SessionPlan.Resolve(true, false, true, Remembered, there, read).ContinuePath == null);

        int reads = 0;
        SessionPlan.Resolve(true, true, false, Remembered, there, path => { reads++; return true; });
        Check("a skipped seed does not touch the disk", reads == 0);
    }

    /// <summary>
    /// An asset import worker reloads the domain exactly as the editor does and shares the
    /// project path the session directory is keyed by, so it used to open a session file of its
    /// own in there. The editor's next reload then continued whatever was newest - the worker's
    /// file, holding a header and nothing else - read no records back, and answered by starting
    /// another. Files piled up and the window fell back to the console on every recompile.
    /// </summary>
    private static void AContinuedSessionKeepsToItsOwnFileNotAStrangersNewerOne() {
        string directory = ScratchDirectory("continued-stranger");
        LogConfig config = LogConfig.Default();
        config.FileDirectory = directory;
        config.FileIncludesDevChannel = true;

        FileSink ours = new FileSink(in config);
        string ourPath = ours.CurrentFilePath;
        ours.Write(Record(1, "Editor", "before the reload", LogLevel.Log, LogChannel.Dev, 0));
        ours.Dispose();

        FileSink stranger = new FileSink(in config);
        string strangerPath = stranger.CurrentFilePath;
        stranger.Dispose();
        Check("the stranger's file is the newer one", strangerPath != ourPath);

        FileSink continued = new FileSink(in config, continueExistingFile: true, continueFilePath: ourPath);
        Check("continuing keeps to the file it was given", continued.CurrentFilePath == ourPath);
        continued.Write(Record(2, "Editor", "after the reload", LogLevel.Log, LogChannel.Dev, 0));
        continued.Dispose();

        string written = File.ReadAllText(ourPath);
        Check("so one session stays in one file",
            written.Contains("before the reload") && written.Contains("after the reload"));
        Check("and the stranger's file is left as it was",
            !File.ReadAllText(strangerPath).Contains("after the reload"));

        // A named file that has gone - pruned, or cleared by hand - is not a reason to lose
        // the run; what is there is still better than nothing.
        File.Delete(ourPath);
        FileSink afterDeletion = new FileSink(in config, continueExistingFile: true, continueFilePath: ourPath);
        Check("a named file that has gone falls back to what is there",
            afterDeletion.CurrentFilePath == strangerPath);
        afterDeletion.Dispose();
    }

    /// <summary>
    /// The editor's sink is rebuilt on every domain reload, and a sink that starts a run by
    /// shifting the files aside would push a morning's logs out of the history by lunchtime.
    /// Continuing adds to the file that is there: no shift, no second header, and the byte
    /// count carried over so the size limit still means the size of the file.
    /// </summary>
    private static void AContinuedSessionAddsToTheFileItFound() {
        string directory = ScratchDirectory("continued");
        LogConfig config = LogConfig.Default();
        config.FileDirectory = directory;
        config.FileIncludesDevChannel = true;

        FileSink first = new FileSink(in config);
        string opened = first.CurrentFilePath;
        first.Write(Record(1, "Editor", "before the reload", LogLevel.Log, LogChannel.Dev, 0));
        first.Dispose();

        long afterFirst = new FileInfo(opened).Length;

        FileSink second = new FileSink(in config, continueExistingFile: true);
        Check("continuing opens the file that is there", second.CurrentFilePath == opened);
        second.Write(Record(2, "Editor", "after the reload", LogLevel.Log, LogChannel.Dev, 0));
        second.Dispose();

        string written = File.ReadAllText(opened);
        Check("what was there before is still there", written.Contains("before the reload"));
        Check("and what came after is added to it", written.Contains("after the reload"));
        Check("no second file was started", Directory.GetFiles(directory, "log.*.jsonl").Length == 1);

        int headers = 0;
        int at = 0;
        while (true) {
            at = written.IndexOf(SessionKeyText, at, StringComparison.Ordinal);
            if (at < 0) {
                break;
            }
            headers++;
            at++;
        }
        Check("with one session header rather than two", headers == 1);
        Check("and the size counted from what the file already held", new FileInfo(opened).Length > afterFirst);

        // Nothing to carry on from is a session like any other.
        string empty = ScratchDirectory("continued-empty");
        config.FileDirectory = empty;
        FileSink fresh = new FileSink(in config, continueExistingFile: true);
        string freshPath = fresh.CurrentFilePath;
        fresh.Write(Record(3, "Editor", "a session of its own", LogLevel.Log, LogChannel.Dev, 0));
        fresh.Dispose();

        Check("continuing with no file starts one",
            File.ReadAllText(freshPath).Contains(SessionKeyText));
    }

    private const string SessionKeyText = "\"session\":";

    /// <summary>
    /// Somewhere to write that is not the developer's own log directory - pointing the file
    /// checks at persistentDataPath meant every run pushed their real logs out of the rotation -
    /// and not somewhere another run is writing either.
    /// <para>
    /// Named after the process, because two editors on one machine share a temp directory: a
    /// checkout and a worktree of it, run together, tidied each other's files away mid-check.
    /// One process id is one editor, and it holds across the domain reloads inside it.
    /// </para>
    /// </summary>
    private static readonly string _scratchRoot = Path.Combine(
        Path.GetTempPath(),
        "kenseilog-smoke." +
        System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));

    private static string ScratchDirectory(string name) =>
        Path.Combine(_scratchRoot, name);

    /// <summary>
    /// Empties the root. Run at both ends, not only at the end: a run killed part way through -
    /// or a second run in the same editor - leaves files whose indices the next run's sink
    /// counts on from, so it opens log.0004 where a check is waiting for log.0001 and reports a
    /// fault nobody wrote.
    /// </summary>
    private static void CleanUpScratchDirectory() {
        try {
            if (Directory.Exists(_scratchRoot)) {
                Directory.Delete(_scratchRoot, true);
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

    /// <summary>A file sink pointed somewhere harmless, with a limit small enough to rotate.</summary>
    private static LogConfig ScratchConfig(string directory, int sizeLimitKb) {
        LogConfig config = LogConfig.Default();
        config.FileDirectory = directory;
        config.FileIncludesDevChannel = true;
        config.FileSizeLimitKb = sizeLimitKb;
        return config;
    }

    /// <summary>
    /// Stands in for a domain reload, and does it through the editor sink's own wiring: the file
    /// is opened, closed and reopened by <c>OpenSessionFile</c>, <c>CloseSessionFile</c> and
    /// <c>ResumeSession</c> rather than by a FileSink of the check's own. Reopening it by hand
    /// was how the reserve that carries the numbering across a reload could be taken out of the
    /// sink with every check here still passing.
    /// <para>
    /// It borrows everything the running editor session is using - the sink's field, its
    /// SessionState keys, the window it fills, the file settings and the record counter - and
    /// puts all of it back. Including the reload subscriptions, which CloseSessionFile takes off
    /// on its way out: without restoring them, running these inside an open editor would leave
    /// it unable to close its own file for the rest of the session, and in batchmode would lose
    /// the tail of the buffer at quit.
    /// </para>
    /// <para>
    /// <see cref="Borrow"/> takes all of that without opening a file, for the checks that build
    /// their files themselves and only want the seeding to run against a window and a session of
    /// their own.
    /// </para>
    /// </summary>
    private sealed class EditorSessionHarness {
        private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Static;

        private readonly FieldInfo _held;
        private readonly MethodInfo _open;
        private readonly MethodInfo _close;
        private readonly MethodInfo _resume;
        private readonly MethodInfo _seed;
        private readonly PropertyInfo _remembered;
        private readonly PropertyInfo _window;
        private readonly FieldInfo _counter;
        private readonly string _pathKey;
        private readonly string _sequenceKey;
        private readonly string _firstFileKey;
        private readonly string _startedKey;
        private readonly string _clearedKey;
        private readonly string _clearedInFileKey;

        private readonly object _standingSink;
        private readonly object _standingWindow;
        private readonly string _standingPath;
        private readonly string _standingSequence;
        private readonly string _standingFirstFile;
        private readonly string _standingCleared;
        private readonly string _standingClearedInFile;
        private readonly bool _standingStarted;
        private readonly long _standingCounter;
        private readonly LogConfig _standingConfig;
        private bool _movedTheSettings;

        private EditorSessionHarness(string directory) {
            Type sink = typeof(EditorSink);
            _held = sink.GetField("_sessionFile", Hidden);
            _open = sink.GetMethod("OpenSessionFile", Hidden);
            _close = sink.GetMethod("CloseSessionFile", Hidden);
            _resume = sink.GetMethod("ResumeSession", Hidden);
            _seed = sink.GetMethod("SeedFromSessionFile", Hidden);
            _remembered = sink.GetProperty("SessionFilePath", Hidden);
            _window = sink.GetProperty("Instance");
            _counter = typeof(LogCore).GetField("_sequence", Hidden);

            FieldInfo pathKey = sink.GetField("SessionFilePathKey", Hidden);
            FieldInfo sequenceKey = sink.GetField("SessionSequenceKey", Hidden);
            FieldInfo firstFileKey = sink.GetField("SessionFirstFileKey", Hidden);
            FieldInfo startedKey = sink.GetField("SessionStartedKey", Hidden);
            FieldInfo clearedKey = sink.GetField("ClearedThroughKey", Hidden);
            FieldInfo clearedInFileKey = sink.GetField("ClearedInFileKey", Hidden);

            Complete = _held != null && _close != null && _remembered != null && _seed != null &&
                       _open != null && _open.GetParameters().Length == 2 &&
                       _resume != null && _resume.GetParameters().Length == 2 &&
                       _window != null && _window.GetSetMethod(true) != null && _counter != null &&
                       pathKey != null && sequenceKey != null && firstFileKey != null &&
                       startedKey != null && clearedKey != null && clearedInFileKey != null;
            if (!Complete) {
                return;
            }

            Directory = directory;
            _pathKey = (string)pathKey.GetValue(null);
            _sequenceKey = (string)sequenceKey.GetValue(null);
            _firstFileKey = (string)firstFileKey.GetValue(null);
            _startedKey = (string)startedKey.GetValue(null);
            _clearedKey = (string)clearedKey.GetValue(null);
            _clearedInFileKey = (string)clearedInFileKey.GetValue(null);

            // Every one of these is a value shared with the editor running this. Left alone, a
            // check that fails here reaches for whatever path SessionState holds and opens the
            // live editor's own file - which is how this first reported a sharing violation
            // rather than the mismatch it had actually found.
            _standingSink = _held.GetValue(null);
            _standingWindow = _window.GetValue(null);
            _standingPath = SessionState.GetString(_pathKey, string.Empty);
            _standingSequence = SessionState.GetString(_sequenceKey, string.Empty);
            _standingFirstFile = SessionState.GetString(_firstFileKey, string.Empty);
            _standingCleared = SessionState.GetString(_clearedKey, string.Empty);
            _standingClearedInFile = SessionState.GetString(_clearedInFileKey, string.Empty);
            _standingStarted = SessionState.GetBool(_startedKey, false);
            _standingCounter = LogCore.CurrentSequence;
            _standingConfig = LogCore.Config;

            SessionState.SetString(_pathKey, string.Empty);
            SessionState.SetString(_sequenceKey, string.Empty);
            SessionState.SetString(_firstFileKey, string.Empty);
            SessionState.SetString(_clearedKey, string.Empty);
            // Of all of these the one most able to pass for this harness's own: a file name from
            // the developer's editor log, compared by its number alone, would put the floor of
            // every seed here wherever their session happens to have got to.
            SessionState.SetString(_clearedInFileKey, string.Empty);
            SessionState.SetBool(_startedKey, false);
            SetWindow(NewWindow());
            // Taken out of reach, not just remembered. Left in place, an OpenSessionFile that
            // declines to open one - WriteSessionFile turned off mid-session, the flag being read
            // inside - leaves the field holding the developer's live sink, and everything here
            // writes filler into their real editor log until it rotates at five megabytes.
            _held.SetValue(null, null);
        }

        /// <summary>False when the sink no longer has a part these lean on.</summary>
        public bool Complete { get; }

        /// <summary>Where this one writes, well away from the developer's own logs.</summary>
        public string Directory { get; }

        public FileSink Sink => (FileSink)_held.GetValue(null);

        public string Remembered => (string)_remembered.GetValue(null);

        /// <summary>Whether the last <see cref="Seed"/> reported a window filled from the file.</summary>
        public bool Seeded { get; private set; }

        public string FirstFile {
            get {
                string path = SessionState.GetString(_firstFileKey, string.Empty);
                return string.IsNullOrEmpty(path) ? null : path;
            }
        }

        public int FileCount => System.IO.Directory.GetFiles(Directory, "log.*.jsonl").Length;

        /// <summary>The file the window was last cleared in, as the sink wrote it down.</summary>
        public string ClearedInFile => SessionState.GetString(_clearedInFileKey, string.Empty);

        /// <summary>Clears the window the way its Clear button does.</summary>
        public void Clear() {
            ((EditorSink)_window.GetValue(null)).Clear();
        }

        /// <summary>How many records the window holds - what this reload's seed put there.</summary>
        public int WindowCount => ((EditorSink)_window.GetValue(null)).Buffer.Count;

        /// <summary>
        /// How many of those could only have come out of the file.
        /// <para>
        /// A count on its own says nothing about where they came from: a seed that found nothing
        /// falls back to the console, which fills the window too - and in a run of these checks
        /// the console is never empty, since earlier scenarios put entries in it on purpose. What
        /// the console has nowhere to keep is the tag and the channel, so a record still carrying
        /// both is one the file supplied.
        /// </para>
        /// </summary>
        public int WindowCountFromTheFile {
            get {
                LogRingBuffer buffer = ((EditorSink)_window.GetValue(null)).Buffer;
                LogRecord[] scratch = new LogRecord[buffer.Count];
                int copied = buffer.CopyNewerThan(0, scratch);
                int fromTheFile = 0;
                for (int i = 0; i < copied; i++) {
                    if (scratch[i].Channel == LogChannel.Dev && scratch[i].Tag == "Editor") {
                        fromTheFile++;
                    }
                }
                return fromTheFile;
            }
        }

        public long StoredSequence {
            get {
                string stored = SessionState.GetString(_sequenceKey, string.Empty);
                return long.TryParse(stored, NumberStyles.None, CultureInfo.InvariantCulture, out long value)
                    ? value
                    : 0L;
            }
        }

        /// <summary>Takes over the session and opens a file of its own through the sink's code.</summary>
        public static EditorSessionHarness Open(string name) {
            EditorSessionHarness harness = Borrow(name);
            if (harness == null) {
                return null;
            }
            // Through LogCore rather than around it: OpenSessionFile reads its file settings
            // from there, and at a limit of five megabytes rotating would take all morning.
            LogConfig config = ScratchConfig(harness.Directory, 64);
            LogCore.Configure(in config);
            harness._movedTheSettings = true;
            harness._open.Invoke(null, new object[] { harness.Directory, null });
            if (harness.Sink == null) {
                Check("the session opened a file of its own - is WriteSessionFile off?", false);
                harness.Dispose();
                return null;
            }
            return harness;
        }

        /// <summary>Takes over the session without opening a file.</summary>
        public static EditorSessionHarness Borrow(string name) {
            EditorSessionHarness harness = new EditorSessionHarness(ScratchDirectory(name));
            Check("the editor sink still has the parts these lean on", harness.Complete);
            return harness.Complete ? harness : null;
        }

        /// <summary>Says which file the session being stood in for began with.</summary>
        public void StartedWith(string path) {
            SessionState.SetString(_firstFileKey, path);
        }

        /// <summary>
        /// Leaves the session with no answer to where it began, which is what one that was
        /// already running when this version of the package arrived has.
        /// </summary>
        public void ForgetsWhereItBegan() {
            SessionState.SetString(_firstFileKey, string.Empty);
        }

        /// <summary>Seeds the window from a file as a reload does, and hands back what it read.</summary>
        public List<LogRecord> Seed(string path) {
            List<LogRecord> into = new List<LogRecord>();
            Seeded = (bool)_seed.Invoke(null, new object[] { path, into });
            return into;
        }

        public void Write(string message) {
            Sink.Write(Record(LogCore.NextSequence(), "Editor", message, LogLevel.Log, LogChannel.Dev, 0));
        }

        public void FillUntilRotation() {
            FileSink sink = Sink;
            string opened = sink.CurrentFilePath;
            string filler = new string('x', 512);
            while (sink.CurrentFilePath == opened) {
                sink.Write(Record(LogCore.NextSequence(), "Editor", filler, LogLevel.Log, LogChannel.Dev, 0));
            }
        }

        public void Close() {
            if (Sink == null) {
                return;
            }
            _close.Invoke(null, null);
        }

        public void Reload(bool worthSeeding = true) {
            Close();
            // A domain reload builds the window again and starts the record counter over at
            // zero; carrying the numbering across that is the first thing ResumeSession does.
            // Nothing here reloads a domain, so both are put back where a new one would find
            // them - and an empty window is what makes the seed's own work visible.
            SetWindow(NewWindow());
            _counter.SetValue(null, 0L);
            SessionDecision decision = (SessionDecision)_resume.Invoke(null, new object[] { true, worthSeeding });
            Seeded = decision.Seeded;
            _open.Invoke(null, new object[] { Directory, decision.ContinuePath });
        }

        public void Dispose() {
            Sink?.Dispose();
            _held.SetValue(null, _standingSink);
            SetWindow(_standingWindow);
            SessionState.SetString(_pathKey, _standingPath);
            SessionState.SetString(_sequenceKey, _standingSequence);
            SessionState.SetString(_firstFileKey, _standingFirstFile);
            SessionState.SetString(_clearedKey, _standingCleared);
            SessionState.SetString(_clearedInFileKey, _standingClearedInFile);
            SessionState.SetBool(_startedKey, _standingStarted);
            if (_movedTheSettings) {
                LogCore.Configure(in _standingConfig);
            }
            // Forwards only, and through the sink's own door: the counter has been wound back to
            // stand in for a reload, and leaving the editor's below a record it has already
            // issued would have it write that number a second time.
            LogCore.ReserveSequencesThrough(_standingCounter);

            if (_standingSink == null) {
                return;
            }
            AssemblyReloadEvents.beforeAssemblyReload +=
                (AssemblyReloadEvents.AssemblyReloadCallback)Delegate.CreateDelegate(
                    typeof(AssemblyReloadEvents.AssemblyReloadCallback), _close);
            EditorApplication.quitting += (Action)Delegate.CreateDelegate(typeof(Action), _close);
        }

        private object NewWindow() {
            return Activator.CreateInstance(typeof(EditorSink), nonPublic: true);
        }

        private void SetWindow(object sink) {
            _window.GetSetMethod(true).Invoke(null, new[] { sink });
        }
    }

    /// <summary>
    /// Found with a finger on an Android device, and by reading, and neither by running anything:
    /// the overlay has no play-mode coverage at all, and a tap that fails to open the viewer
    /// looks exactly like a tap that did nothing, with the button lighting up under it either way.
    /// <para>
    /// Two faults, both here. The drag flag was cleared on a MouseUp read AFTER GUI.Button, and
    /// the button consumes the MouseUp it answers - so the clear never ran, and one drag left the
    /// bubble unopenable until the app was restarted. And any movement at all started a drag,
    /// while a finger never lands without a pixel or two of travel, so ordinary taps became drags.
    /// </para>
    /// </summary>
    private static void TheBubbleStillOpensAfterItHasBeenDragged() {
        BubbleGesture gesture = new BubbleGesture();
        UnityEngine.Vector2 start = new UnityEngine.Vector2(12f, 12f);

        gesture.Press(true, start);
        Check("a press on its own is not a drag", !gesture.Dragging);
        Check("and would open the viewer", gesture.Opens(true));

        bool moved = gesture.TryDrag(true, new UnityEngine.Vector2(40f, 6f), out UnityEngine.Vector2 dragged);
        Check("travel past the threshold drags it", moved && gesture.Dragging);
        Check("to where the finger is, measured from the press", dragged == start + new UnityEngine.Vector2(40f, 6f));
        Check("and the release that ends a drag opens nothing", !gesture.Opens(true));

        // The whole bug: this release used to be swallowed by the button, so the flag stayed set.
        // Both ends of the gesture clear it, and each is checked on its own - together they were
        // masking one another, and a mutation to either passed with the pair still in place.
        gesture.Release();
        Check("a release ends the drag", !gesture.Dragging);

        // Pressed again first, or the release just above has already disarmed the drag and this
        // would be asking a question whose answer cannot be no.
        gesture.Press(true, dragged);
        gesture.TryDrag(true, new UnityEngine.Vector2(40f, 6f), out UnityEngine.Vector2 _);
        gesture.Press(true, dragged);
        Check("and so does the next press, whether or not the release was seen", !gesture.Dragging);
        Check("so the tap after a drag opens the viewer again", gesture.Opens(true));

        // Measured from the press rather than accumulated, or the bubble lags the finger by
        // however far it travelled to cross the threshold.
        gesture.Press(true, start);
        gesture.TryDrag(true, new UnityEngine.Vector2(30f, 0f), out UnityEngine.Vector2 first);
        gesture.TryDrag(true, new UnityEngine.Vector2(60f, 0f), out UnityEngine.Vector2 second);
        Check("a drag tracks the finger rather than accumulating",
            first == start + new UnityEngine.Vector2(30f, 0f) && second == start + new UnityEngine.Vector2(60f, 0f));

        gesture.Release();
        gesture.Press(true, start);
        Check("a wobble below the threshold is not a drag",
            !gesture.TryDrag(false, new UnityEngine.Vector2(2f, 1f), out UnityEngine.Vector2 held));
        Check("and leaves the bubble where it was", held == start);
        Check("so a tap with a shaking finger still opens it", gesture.Opens(true));

        gesture.Release();
        gesture.Press(false, start);
        Check("a drag that began off the bubble does not move it",
            !gesture.TryDrag(true, new UnityEngine.Vector2(40f, 0f), out UnityEngine.Vector2 _));
    }

    /// <summary>
    /// The overlay used to count what it read, and a reader only ever sees what survived. A
    /// burst longer than the ring pushes its own beginning out before anything polls, so the
    /// records that went that way were counted nowhere: a thousand logs in one frame showed on
    /// the badge as the couple of dozen still in the buffer. Counted on the way in now.
    /// </summary>
    private static void CountsSurviveABurstLongerThanTheBuffer() {
        MemorySink sink = new MemorySink(8);
        for (long i = 1; i <= 100; i++) {
            sink.Write(Record(i, "Burst", "record " + i, LogLevel.Log, LogChannel.Prod, 0));
        }
        for (long i = 101; i <= 130; i++) {
            sink.Write(Record(i, "Burst", "warn " + i, LogLevel.Warning, LogChannel.Prod, 0));
        }
        sink.Write(Record(131, "Burst", "the one error", LogLevel.Error, LogChannel.Prod, 0));

        Check("the ring kept only what fits", sink.Buffer.Count == 8);
        Check("but every log was counted", sink.LevelCount(LogLevel.Log) == 100L);
        Check("and every warning", sink.LevelCount(LogLevel.Warning) == 30L);
        Check("and the error that would have been the only one left",
            sink.LevelCount(LogLevel.Error) == 1L);

        sink.Clear();
        Check("clearing takes the counts with it",
            sink.LevelCount(LogLevel.Log) == 0L && sink.LevelCount(LogLevel.Warning) == 0L &&
            sink.LevelCount(LogLevel.Error) == 0L);
        Check("and empties the buffer", sink.Buffer.Count == 0);
    }

    /// <summary>
    /// The chip's digit slot is measured once, over the shapes the formatter is supposed to
    /// produce, and the label clips rather than overflows. So a shape the measuring never saw is
    /// not a layout glitch but a wrong number: the ladder used to run off its top end and return
    /// "1000M" at a billion, five characters into a slot measured for four, and the reader saw a
    /// thousand where a billion had happened.
    /// </summary>
    private static void ACountNeverOutgrowsTheChipItIsDrawnIn() {
        const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Static;
        MethodInfo compact = typeof(LogOverlay).GetMethod("Compact", Hidden);
        FieldInfo shapes = typeof(LogOverlay).GetField("_countShapes", Hidden);
        Check("the overlay still has the parts this leans on", compact != null && shapes != null);
        if (compact == null || shapes == null) {
            return;
        }

        string[] known = (string[])shapes.GetValue(null);
        int widest = 0;
        for (int i = 0; i < known.Length; i++) {
            widest = Math.Max(widest, known[i].Length);
        }

        long[] boundaries = {
            0L, 1L, 999L, 1000L, 1099L, 1100L, 9999L, 10000L, 999999L, 1000000L,
            999999999L, 1000000000L, 1073741824L, long.MaxValue
        };
        string offender = null;
        for (int i = 0; i < boundaries.Length; i++) {
            string text = (string)compact.Invoke(null, new object[] { boundaries[i] });
            if (text.Length > widest) {
                offender = boundaries[i] + " -> " + text;
            }
        }
        Check("no count is ever wider than the slot measured for it", offender == null);

        Check("under a thousand it is exact", (string)compact.Invoke(null, new object[] { 999L }) == "999");
        Check("then thousands to one decimal", (string)compact.Invoke(null, new object[] { 1200L }) == "1.2k");
        Check("then whole thousands", (string)compact.Invoke(null, new object[] { 47000L }) == "47k");
        Check("then whole millions", (string)compact.Invoke(null, new object[] { 3000000L }) == "3M");
        Check("and it pins rather than running off the end",
            (string)compact.Invoke(null, new object[] { long.MaxValue }) == "1B+");
    }

    /// <summary>
    /// Changing OverlayRecordCapacity builds a sink of the new size and moves what the old one
    /// held across. Replaying those records would count them again and nothing else, so a
    /// settings change quietly reset the totals to whatever had survived the ring - undoing, in
    /// one call, the undercount that counting on the way in exists to prevent.
    /// </summary>
    private static void CountsSurviveAChangeOfCapacity() {
        MemorySink small = new MemorySink(8);
        for (long i = 1; i <= 500; i++) {
            small.Write(Record(i, "Burst", "log " + i, LogLevel.Log, LogChannel.Prod, 0));
        }
        small.Write(Record(501, "Burst", "the error", LogLevel.Error, LogChannel.Prod, 0));

        MemorySink resized = new MemorySink(64, small);
        Check("the totals come across whole",
            resized.LevelCount(LogLevel.Log) == 500L && resized.LevelCount(LogLevel.Error) == 1L);
        Check("and are not counted twice", resized.LevelCount(LogLevel.Log) == small.LevelCount(LogLevel.Log));
        Check("with what the old ring still held", resized.Buffer.Count == small.Buffer.Count);
        Check("and room for the new size", resized.Buffer.Capacity == 64);

        resized.Write(Record(502, "Burst", "after the change", LogLevel.Warning, LogChannel.Prod, 0));
        Check("counting carries on from there", resized.LevelCount(LogLevel.Warning) == 1L);
    }

    /// <summary>
    /// The overlay's tag pane used to be built from a dictionary it filled as it polled, and
    /// nothing ever took a tag out of it: a tag whose every record had been pushed out of the
    /// ring stayed listed, and tapping it filtered the rows down to nothing. The ring is the
    /// only place that sees a record arrive and the record it displaced leave, so the census
    /// lives there.
    /// </summary>
    private static void TheTagCensusHoldsOnlyWhatTheBufferHolds() {
        LogRingBuffer buffer = new LogRingBuffer(8);
        List<LogRingBuffer.TagCount> census = new List<LogRingBuffer.TagCount>();

        for (long i = 1; i <= 8; i++) {
            buffer.Add(Record(i, "Early", "early " + i, LogLevel.Log, LogChannel.Prod, 0));
        }
        buffer.CopyTagCounts(census);
        Check("a full ring counts what it holds", census.Count == 1 && census[0].Tag == "Early" && census[0].Held == 8);

        // Eight of another tag push the first out entirely.
        for (long i = 9; i <= 16; i++) {
            buffer.Add(Record(i, "Later", "later " + i, LogLevel.Log, LogChannel.Prod, 0));
        }
        buffer.CopyTagCounts(census);
        Check("a tag whose last record has left is gone from the census",
            census.Count == 1 && census[0].Tag == "Later" && census[0].Held == 8);

        // Half and half, so both are held at once.
        for (long i = 17; i <= 20; i++) {
            buffer.Add(Record(i, "Early", "early again " + i, LogLevel.Log, LogChannel.Prod, 0));
        }
        buffer.CopyTagCounts(census);
        census.Sort((left, right) => string.CompareOrdinal(left.Tag, right.Tag));
        Check("and two tags are counted apart",
            census.Count == 2 && census[0].Tag == "Early" && census[0].Held == 4 &&
            census[1].Tag == "Later" && census[1].Held == 4);

        int total = 0;
        for (int i = 0; i < census.Count; i++) {
            total += census[i].Held;
        }
        Check("the census adds up to what the buffer holds", total == buffer.Count);

        // The same tag arriving as the same tag leaves must not drop the key.
        for (long i = 21; i <= 40; i++) {
            buffer.Add(Record(i, "Early", "one tag only " + i, LogLevel.Log, LogChannel.Prod, 0));
        }
        buffer.CopyTagCounts(census);
        Check("one tag replacing itself keeps its place",
            census.Count == 1 && census[0].Tag == "Early" && census[0].Held == 8);

        buffer.Clear();
        buffer.CopyTagCounts(census);
        Check("clearing takes the census with it", census.Count == 0);
    }

    /// <summary>
    /// The index is shared by both viewers now, and the in-game one keeps a formatted row beside
    /// every slot - it redraws per frame where the window polls fifteen times a second, so it
    /// cannot format a row per pass. That only works while the index says which slot it touched:
    /// a repeat folds into an earlier slot and must replace the row there, not add one. And a
    /// prune has to say what went, because a collapsed view loses rows from anywhere rather than
    /// off the front.
    /// </summary>
    private static void TheIndexSaysWhichSlotItTouched() {
        LogIndex plain = new LogIndex(new LogFilter { Name = "Plain" });
        Check("an accepted record takes the next slot",
            plain.Append(Record(1, "A", "one", LogLevel.Log, LogChannel.Prod, 0)) == 0 &&
            plain.Append(Record(2, "A", "two", LogLevel.Log, LogChannel.Prod, 0)) == 1);

        LogFilter narrow = new LogFilter { Name = "Narrow" };
        narrow.SetLevel(LogLevel.Log, false);
        LogIndex refused = new LogIndex(narrow);
        Check("a record the filter refuses takes none",
            refused.Append(Record(3, "A", "three", LogLevel.Log, LogChannel.Prod, 0)) == -1);

        LogIndex folded = new LogIndex(new LogFilter { Name = "Folded", Collapse = true });
        folded.Append(Record(10, "A", "same", LogLevel.Log, LogChannel.Prod, 0));
        folded.Append(Record(11, "B", "other", LogLevel.Log, LogChannel.Prod, 0));
        Check("a repeat folds into the slot it first took",
            folded.Append(Record(12, "A", "same", LogLevel.Log, LogChannel.Prod, 0)) == 0);
        Check("and the slot points at the newest of them",
            folded.Sequences[0] == 12 && folded.Repeats[0] == 2);

        List<int> kept = new List<int>();
        Check("a prune that drops nothing says so",
            !folded.PruneBelow(0, kept, out int front) && front == -1);

        // The first slot is the newer record now, so a collapsed prune loses the middle.
        Check("a collapsed prune names the survivors",
            folded.PruneBelow(12, kept, out front) && front == -1 &&
            kept.Count == 1 && kept[0] == 0);
        Check("and leaves the index holding them", folded.Count == 1 && folded.Sequences[0] == 12);

        LogIndex ordered = new LogIndex(new LogFilter { Name = "Ordered" });
        for (long i = 1; i <= 5; i++) {
            ordered.Append(Record(i, "A", "n " + i, LogLevel.Log, LogChannel.Prod, 0));
        }
        Check("an uncollapsed prune counts off the front instead",
            ordered.PruneBelow(3, kept, out front) && front == 2 && kept.Count == 0);
        Check("and drops exactly that many", ordered.Count == 3 && ordered.Sequences[0] == 3);
    }

    /// <summary>
    /// The in-game viewer shows one line at its top once records have started to leave the
    /// ring, saying where the earlier ones can still be found. It has to appear at the first
    /// eviction and not before - a notice from the start would be noise, and one that never came
    /// would leave the reader to discover the hole by finding a list shorter than they expected.
    /// </summary>
    private static void TheRingKnowsWhenItHasLetARecordGo() {
        LogRingBuffer buffer = new LogRingBuffer(8);
        for (long i = 1; i <= 8; i++) {
            buffer.Add(Record(i, "T", "fits " + i, LogLevel.Log, LogChannel.Prod, 0));
        }
        Check("a full ring that has lost nothing says so", !buffer.HasEvicted);

        buffer.Add(Record(9, "T", "one too many", LogLevel.Log, LogChannel.Prod, 0));
        Check("the first record pushed out is noticed", buffer.HasEvicted);

        buffer.Clear();
        Check("and a clear starts it over", !buffer.HasEvicted);

        // A sink rebuilt larger from one that had already lost records - which is what a later
        // Configure with a bigger OverlayRecordCapacity does - holds all that was left and has
        // evicted nothing itself. The records the old one dropped are gone all the same.
        MemorySink small = new MemorySink(8);
        for (long i = 1; i <= 20; i++) {
            small.Write(Record(i, "T", "n " + i, LogLevel.Log, LogChannel.Prod, 0));
        }
        MemorySink larger = new MemorySink(64, small);
        Check("a larger sink carried from one that lost records still says so", larger.Buffer.HasEvicted);

        MemorySink whole = new MemorySink(8);
        whole.Write(Record(1, "T", "only one", LogLevel.Log, LogChannel.Prod, 0));
        Check("and one carried from a sink that lost nothing does not",
            !new MemorySink(64, whole).Buffer.HasEvicted);
    }

    /// <summary>
    /// The overlay's notice tells the reader where earlier records went, and it used to work that
    /// out from the configuration. The configuration keeps a file sink registered after its
    /// writer has failed - a full disk, an unwritable path - so the notice pointed at a file that
    /// was not being written. Asked of the sinks now, over a list built here so that what else
    /// happens to be registered in the editor running the checks cannot answer for it.
    /// </summary>
    private static void OnlyAFileThatIsWritingCountsAsKeepingRecords() {
        MethodInfo keeps = typeof(LogCore).GetMethod("AnyFileKeeps", BindingFlags.NonPublic | BindingFlags.Static,
            null, new[] { typeof(ILogSink[]), typeof(LogChannel) }, null);
        Check("LogCore still has the question this leans on", keeps != null);
        if (keeps == null) {
            return;
        }

        LogConfig prodOnly = LogConfig.Default();
        prodOnly.FileDirectory = ScratchDirectory("keeps-prod");
        prodOnly.FileIncludesDevChannel = false;
        LogConfig everything = LogConfig.Default();
        everything.FileDirectory = ScratchDirectory("keeps-all");
        everything.FileIncludesDevChannel = true;

        FileSink prod = new FileSink(in prodOnly);
        FileSink all = new FileSink(in everything);
        FileSink stopped = new FileSink(in everything);
        stopped.Dispose();

        try {
            bool Ask(ILogSink[] sinks, LogChannel channel) =>
                (bool)keeps.Invoke(null, new object[] { sinks, channel });

            Check("with no file sink nothing is kept", !Ask(new ILogSink[0], LogChannel.Prod));
            Check("a writing prod-only file keeps prod", Ask(new ILogSink[] { prod }, LogChannel.Prod));
            Check("and not dev", !Ask(new ILogSink[] { prod }, LogChannel.Dev));
            Check("a file that has stopped writing keeps nothing, whatever it was set to take",
                !Ask(new ILogSink[] { stopped }, LogChannel.Dev) && !Ask(new ILogSink[] { stopped }, LogChannel.Prod));
            Check("and any writing file that takes dev is enough for dev",
                Ask(new ILogSink[] { prod, stopped, all }, LogChannel.Dev));
            Check("sinks that are not files are not asked",
                !Ask(new ILogSink[] { new MemorySink(8) }, LogChannel.Prod));
        } finally {
            prod.Dispose();
            all.Dispose();
        }
    }

    /// <summary>
    /// Each file opens with a header naming the session it belongs to. The identity was minted
    /// per file, so a run long enough to rotate looked, from its files, like two runs - and after
    /// a restart nothing could put them back together. One identity per session now, carried by
    /// every file it fills, and read back out of the header when the editor carries on with a
    /// file across a domain reload.
    /// </summary>
    private static void ARotatedFileKeepsItsSessionsIdentity() {
        string directory = ScratchDirectory("session-identity");
        LogConfig config = ScratchConfig(directory, 64);
        string filler = new string('x', 512);

        FileSink sink = new FileSink(in config);
        string first = sink.CurrentFilePath;
        long sequence = 1;
        while (sink.CurrentFilePath == first) {
            sink.Write(Record(sequence++, "T", filler, LogLevel.Log, LogChannel.Prod, 0));
        }
        string second = sink.CurrentFilePath;
        sink.Dispose();

        string session = HeaderField(first, "session");
        Check("the first file names its session", !string.IsNullOrEmpty(session));
        Check("and the file a rotation opens names the same one", HeaderField(second, "session") == session);
        Check("with the same start, since the session did not start again",
            HeaderField(second, "started") == HeaderField(first, "started"));

        // Carrying on with the file, as the editor does after every domain reload, then rotating.
        FileSink resumed = new FileSink(in config, true, second);
        while (resumed.CurrentFilePath == second) {
            resumed.Write(Record(sequence++, "T", filler, LogLevel.Log, LogChannel.Prod, 0));
        }
        string third = resumed.CurrentFilePath;
        resumed.Dispose();
        Check("a session carried on across a reload keeps its identity through the next rotation",
            HeaderField(third, "session") == session);

        FileSink fresh = new FileSink(in config);
        string fourth = fresh.CurrentFilePath;
        fresh.Dispose();
        Check("while a session that really is new gets one of its own", HeaderField(fourth, "session") != session);
    }

    private static string HeaderField(string path, string key) {
        string header = ReadWhileOpen(path).Split('\n')[0];
        string marker = "\"" + key + "\":\"";
        int at = header.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) {
            return null;
        }
        int start = at + marker.Length;
        return header.Substring(start, header.IndexOf('"', start) - start);
    }

    /// <summary>
    /// Dev records go into the file by default in a development build, where they have nowhere
    /// else to go, and stay out of it in the editor, where the editor's own session file already
    /// takes them - the run's file taking them too would write each one twice in play mode. This
    /// half is the only one a check in the editor can see; the other is compiled into a player
    /// alone, and CompileCheck builds it without running it.
    /// </summary>
    private static void TheRunsFileLeavesDevToTheEditorsOwn() {
        Check("in the editor the run's file takes prod only by default", !LogConfig.Default().FileIncludesDevChannel);
    }

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
