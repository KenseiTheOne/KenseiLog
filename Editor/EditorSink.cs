using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace KenseiLog.Editor {
    /// <summary>
    /// Keeps recent records in memory for <see cref="LogWindow"/>.
    /// <para>
    /// Registration goes bottom-up: the runtime assembly only knows the
    /// <see cref="ILogSink"/> interface, and the editor assembly plugs itself in on load.
    /// </para>
    /// </summary>
    public sealed class EditorSink : ILogSink {
        private static readonly string _capacityKey = ProjectPrefs.Key("Capacity");
        private static readonly string _clearOnPlayKey = ProjectPrefs.Key("ClearOnPlay");
        private static readonly string _sessionFileKey = ProjectPrefs.Key("EditorSessionFile");

        /// <summary>
        /// Says whether this editor has already opened its file. SessionState outlives a domain
        /// reload and does not outlive the editor, which is the exact line between "carry on
        /// with the file" and "start a new one" - a recompile is not a new session.
        /// </summary>
        private const string SessionStartedKey = "KenseiLog.EditorSessionStarted";

        /// <summary>
        /// Which file this editor session is writing. Kept rather than looked up, because the
        /// newest file in the directory is not reliably ours: an asset import worker shares the
        /// project path the directory is keyed by, and any file it left would be newer. Written
        /// when the file is opened and again when it is closed - a rotation moves it on in
        /// between, from whichever thread was logging, where SessionState cannot be reached.
        /// </summary>
        private const string SessionFilePathKey = "KenseiLog.EditorSessionFilePath";

        /// <summary>
        /// The first file this editor session opened, which is where its history begins.
        /// Seeding reaches back past a rotation, and how far back is not a question the record
        /// ids can answer: the directory outlives the editor, and a short file left in it
        /// yesterday numbers below everything a session that has been up an hour holds. Kept in
        /// SessionState because that is the thing whose lifetime is the session's own.
        /// <para>
        /// Absent only until a file is opened, including in a session this sink joined midway.
        /// A file is then named all the same - see <see cref="OpenSessionFile"/> - so that it is
        /// never read as "no session", which would leave the reach-back off until the editor
        /// is restarted.
        /// </para>
        /// </summary>
        private const string SessionFirstFileKey = "KenseiLog.EditorSessionFirstFile";

        /// <summary>
        /// The highest record id the last domain issued. Carried across so that a session which
        /// keeps to its file keeps to its numbering, whether or not the records came back: the
        /// counter starts again at zero with every domain, and the file does not.
        /// </summary>
        private const string SessionSequenceKey = "KenseiLog.EditorSessionSequence";

        /// <summary>
        /// The newest record dismissed by a Clear. Kept in SessionState so that it lives
        /// exactly as long as the session file it speaks about: the file keeps everything - that
        /// is what it is for - but a reload must not hand back what somebody dismissed.
        /// </summary>
        private const string ClearedThroughKey = "KenseiLog.EditorClearedThrough";

        /// <summary>Kept apart from the runs, which own the directory above it.</summary>
        private const string EditorLogFolder = "editor";

        /// <summary>
        /// Tag for the session boundaries. Top level on purpose: under the engine's own tag they
        /// would be folded away, or filtered out, along with the noise somebody was hiding.
        /// </summary>
        private const string MarkerTag = "Editor";

        /// <summary>
        /// How much of the session file is read back when the domain reloads. Bounded because
        /// this happens on every recompile: a file at the default size limit would put seconds
        /// on each one, and the buffer cannot hold that many records anyway.
        /// </summary>
        private const long SeedByteBudget = 2L * 1024L * 1024L;

        private const int DefaultCapacity = 8192;

        /// <summary>
        /// Bounds on the buffer. The floor is the point below which the window stops being a
        /// log viewer; the ceiling is around a hundred megabytes of records, by which point
        /// the answer is a log file rather than a bigger buffer.
        /// </summary>
        private const int MinimumCapacity = 64;

        private const int MaximumCapacity = 1 << 20;

        private static FileSink _sessionFile;

        private LogRingBuffer _buffer;
        private int _version;

        private EditorSink(int capacity) {
            _buffer = new LogRingBuffer(capacity);
        }

        public static EditorSink Instance { get; private set; }

        public LogRingBuffer Buffer => _buffer;

        /// <summary>Bumped on every record so the window can poll instead of subscribing.</summary>
        public int Version => _version;

        public static bool ClearOnPlay {
            get => EditorPrefs.GetBool(_clearOnPlayKey, true);
            set => EditorPrefs.SetBool(_clearOnPlayKey, value);
        }

        /// <summary>
        /// Whether what the editor logs is written to a file of its own.
        /// <para>
        /// On by default, because without it the editor's own records live nowhere but this
        /// buffer - and the buffer is rebuilt on every domain reload, so a recompile took them
        /// all. The runtime's file sink cannot do this job: it is gated on play mode precisely
        /// because it starts a run by shifting the files aside, and a recompile is not a run.
        /// </para>
        /// <para>
        /// Takes effect on the next domain reload, since the sink is opened with the editor's
        /// session.
        /// </para>
        /// </summary>
        public static bool WriteSessionFile {
            get => EditorPrefs.GetBool(_sessionFileKey, true);
            set => EditorPrefs.SetBool(_sessionFileKey, value);
        }

        /// <summary>The file this editor session is writing, or null when it is not.</summary>
        public static FileSink SessionFile => _sessionFile;

        /// <summary>
        /// Where that file goes, whether or not one is open. Under the project as well as the
        /// product: persistentDataPath is keyed by company and product name, so two checkouts
        /// of one project - two worktrees, two editors - would otherwise write to the same file
        /// and read each other's records back.
        /// </summary>
        public static string SessionFileDirectory =>
            Path.Combine(Application.persistentDataPath, "logs", EditorLogFolder, ProjectPrefs.ProjectId);

        /// <summary>
        /// The file this editor session is writing, or null before it has one.
        /// </summary>
        private static string SessionFilePath {
            get {
                string path = SessionState.GetString(SessionFilePathKey, string.Empty);
                return string.IsNullOrEmpty(path) ? null : path;
            }
        }

        /// <summary>
        /// The file this editor session started with, or null before it has one.
        /// </summary>
        private static string SessionFirstFilePath {
            get {
                string path = SessionState.GetString(SessionFirstFileKey, string.Empty);
                return string.IsNullOrEmpty(path) ? null : path;
            }
        }

        /// <summary>The highest record id the last domain issued, or 0 when there was none.</summary>
        private static long SessionSequence {
            get {
                string stored = SessionState.GetString(SessionSequenceKey, string.Empty);
                return long.TryParse(stored, NumberStyles.None, CultureInfo.InvariantCulture, out long sequence)
                    ? sequence
                    : 0L;
            }
        }

        /// <summary>
        /// How many records the window keeps. Clamped, and clamped before it is stored: an
        /// out-of-range value used to be written to EditorPrefs and only then handed to the
        /// buffer, which threw - leaving a capacity of zero saved, so the sink threw again on
        /// construction on every domain reload and the window stayed dead until someone
        /// cleared the preference by hand.
        /// </summary>
        public int Capacity {
            get => _buffer.Capacity;
            set {
                int capacity = Mathf.Clamp(value, MinimumCapacity, MaximumCapacity);
                if (capacity == _buffer.Capacity) {
                    return;
                }
                EditorPrefs.SetInt(_capacityKey, capacity);
                _buffer = new LogRingBuffer(capacity);
                Interlocked.Increment(ref _version);
            }
        }

        public void Write(in LogRecord record) {
            _buffer.Add(in record);
            Interlocked.Increment(ref _version);
        }

        public void Clear() {
            _buffer.Clear();
            Interlocked.Increment(ref _version);

            // Everything issued so far is dismissed. The counter is past every record written
            // up to now, which is exactly the watermark wanted, and it costs one number.
            SessionState.SetString(ClearedThroughKey, LogCore.NextSequence().ToString(CultureInfo.InvariantCulture));
        }

        private static long ClearedThrough {
            get {
                string stored = SessionState.GetString(ClearedThroughKey, string.Empty);
                return long.TryParse(stored, NumberStyles.None, CultureInfo.InvariantCulture, out long sequence)
                    ? sequence
                    : 0L;
            }
        }

        [InitializeOnLoadMethod]
        private static void Install() {
            // An asset import worker reloads the domain exactly as the editor does, so this
            // runs there too - and it shares the project path the session directory is keyed
            // by. Left alone it opened a session file of its own beside the editor's, which the
            // editor then seeded from, found holding nothing but a header, and answered by
            // starting yet another. A worker has no window to fill and nothing worth keeping,
            // and an out-of-process profiler is a second domain in the same position.
            if (!SessionPlan.Installs(AssetDatabase.IsAssetImportWorkerProcess(),
                                      UnityEditor.MPE.ProcessService.level == UnityEditor.MPE.ProcessLevel.Secondary)) {
                return;
            }

            // Clamped on the way in as well as on the way out, so a preference already holding
            // a bad value from an earlier version repairs itself instead of throwing here.
            int capacity = Mathf.Clamp(EditorPrefs.GetInt(_capacityKey, DefaultCapacity), MinimumCapacity, MaximumCapacity);
            Instance = new EditorSink(capacity);

            // A domain reload is not a new session, so what this editor logged before it is
            // read back out of the session file - where the tag, the channel and the call site
            // survive, none of which Unity's console has anywhere to keep. Anything else, and
            // a fresh editor, falls back to the console.
            bool continuing = SessionState.GetBool(SessionStartedKey, false);
            SessionDecision decision = ResumeSession(continuing, ShouldSeed());

            // Whatever a Unity version that has moved things makes of the line above, it must
            // not cost us the two below: without them the sink is never registered and the
            // window records nothing at all, while still opening and looking healthy.
            LogCore.AddSink(Instance);
            LogCore.Initialize();
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompiled;

            OpenSessionFile(SessionFileDirectory, decision.ContinuePath);

            // After the sink is open, so the marker reaches the file as well as the window. Not
            // when entering play mode: that has a marker of its own a moment later, and this one
            // would be wiped by Clear on Play in between.
            if (!EditorApplication.isPlayingOrWillChangePlaymode) {
                Mark(continuing ? "Scripts reloaded" : "Editor started");
            }
        }

        /// <summary>
        /// Takes up the session this domain is joining: carries its numbering across, and reads
        /// the window's history back out of its file.
        /// <para>
        /// Handed what Install reads off the editor rather than reaching for it again, for the
        /// reason <see cref="SessionPlan"/> is apart from Install at all: a domain reload is the
        /// only thing that runs Install, and this is the wiring every fault in this file has
        /// been in.
        /// </para>
        /// </summary>
        private static SessionDecision ResumeSession(bool continuing, bool worthSeeding) {
            // Before anything is written, and whether or not the records come back below: what
            // this domain logs goes into the same file as what the last one logged, so repeating
            // its numbers would leave the file unsorted and every lookup into it wrong.
            if (continuing) {
                LogCore.ReserveSequencesThrough(SessionSequence);
            }

            SessionDecision decision = new SessionDecision(false, null);
            try {
                List<LogRecord> fromFile = new List<LogRecord>();
                decision = SessionPlan.Resolve(continuing, WriteSessionFile, worthSeeding, SessionFilePath,
                                               File.Exists, path => SeedFromSessionFile(path, fromFile));
                if (decision.Seeded) {
                    SeedRemainingConsoleEntries(fromFile);
                } else if (worthSeeding) {
                    SeedFromConsole();
                }
            } catch (Exception exception) {
                Debug.LogWarning("KenseiLog: could not seed the window (" + exception.Message + ")");
            }
            return decision;
        }

        /// <summary>
        /// Whether reading a session's worth of records back is worth doing at all.
        /// <para>
        /// Entering play mode with Clear on Play set is a reload whose seed is thrown away a
        /// callback later, and that is the reload people do dozens of times a day. Reading a
        /// couple of megabytes of JSON to discard it is the most expensive thing this package
        /// would do all day.
        /// </para>
        /// </summary>
        private static bool ShouldSeed() {
            return !(ClearOnPlay && EditorApplication.isPlayingOrWillChangePlaymode);
        }

        /// <summary>
        /// Opens the editor's own log file, adding to the one this editor session already has.
        /// <para>
        /// Nothing here is allowed to cost the window its records: a directory that cannot be
        /// written to, or a file another process is holding, leaves the sink unopened and the
        /// rest of the pipeline exactly as it was.
        /// </para>
        /// </summary>
        private static void OpenSessionFile(string directory, string continuePath) {
            if (!WriteSessionFile) {
                return;
            }

            try {
                LogConfig config = LogCore.Config;
                config.FileDirectory = directory;
                // The point of the file is the channel the runtime's own sink leaves out: a
                // dev record written from an editor tool has nowhere else to survive.
                config.FileIncludesDevChannel = true;

                _sessionFile = new FileSink(in config, continuePath != null, continuePath);
                if (!_sessionFile.IsWriting) {
                    // No file, so nothing for the next domain to continue: leaving the flag set
                    // would have it append this session into the last one's file.
                    _sessionFile.Dispose();
                    _sessionFile = null;
                    return;
                }

                SessionState.SetBool(SessionStartedKey, true);
                SessionState.SetString(SessionFilePathKey, _sessionFile.CurrentFilePath);
                if (continuePath == null || SessionFirstFilePath == null) {
                    // A file of its own is where this session begins, and seeding never reaches
                    // below it: what is under it in the directory was left by a session that is
                    // over, numbering from a beginning of its own.
                    //
                    // A session already under way with no answer here is one this sink joined
                    // midway - the package resolved again in a running editor - and the file in
                    // hand is the earliest of it anything can vouch for. That gives up the
                    // reach-back until the next rotation, which is the cost of not guessing:
                    // the file behind this one is as likely to belong to an editor that has
                    // closed, and the point of this key is to keep that out.
                    SessionState.SetString(SessionFirstFileKey, _sessionFile.CurrentFilePath);
                }
                LogCore.AddSink(_sessionFile);
            } catch (Exception exception) {
                _sessionFile = null;
                Debug.LogWarning("KenseiLog: no editor log file this session (" + exception.Message + ")");
                return;
            }

            // The handle has to be given up before the next domain takes over, or the sink it
            // builds finds the file held by a domain that no longer exists.
            AssemblyReloadEvents.beforeAssemblyReload += CloseSessionFile;
            EditorApplication.quitting += CloseSessionFile;
        }

        private static void CloseSessionFile() {
            AssemblyReloadEvents.beforeAssemblyReload -= CloseSessionFile;
            EditorApplication.quitting -= CloseSessionFile;

            if (_sessionFile == null) {
                return;
            }
            LogCore.RemoveSink(_sessionFile);
            // Dispose takes the sink's own lock, so a rotation already running on a logging
            // thread has finished by the time it returns and the path cannot move again.
            _sessionFile.Dispose();
            // Which file the session ends in is not the one it started in: Rotate changes it
            // once the size limit is passed, and it cannot record that itself - SessionState is
            // main thread only, and a rotation happens on whichever thread was logging. Written
            // here, where the thread is known and the file is closed. Without it the next domain
            // continued the file from before the rotation, which is already over the limit: the
            // first record rotated it again, so every reload left another file behind, the
            // window came back holding only what was written before the rotation, and pruning
            // worked its way through the full ones.
            SessionState.SetString(SessionFilePathKey, _sessionFile.CurrentFilePath);
            SessionState.SetString(SessionSequenceKey,
                LogCore.CurrentSequence.ToString(CultureInfo.InvariantCulture));
            _sessionFile = null;
        }

        /// <summary>
        /// Fills the buffer from the file this editor session has been writing, and from the
        /// one behind it when a rotation has only just happened.
        /// <para>
        /// The records come back whole - tag, channel, frame, call site, stack trace - which is
        /// the difference between this and reading the console, where none of that exists. Their
        /// original sequence numbers come back with them, and the counter is moved past the
        /// highest: the file carries on being written after the reload, and records repeating
        /// numbers already in it would leave it unsorted and every lookup into it wrong.
        /// </para>
        /// <para>
        /// One budget covers the whole seed, and the file being written is served out of it
        /// first. A budget per file lets the one behind a rotation fill the room left in the
        /// buffer with records older than the ones the budget has just cut off the front of
        /// this one - a window holding an older stretch of the log in place of a newer one,
        /// with a hole between them and nothing in it to say so.
        /// </para>
        /// </summary>
        private static bool SeedFromSessionFile(string path, List<LogRecord> into) {
            LogSessionReader.ReadTail(path, Instance.Buffer.Capacity, SeedByteBudget, into,
                                      out long spent, out bool entire);
            // Whether or not this file gave anything back. A rotation on the last record before
            // the reload leaves it holding a header alone, and that is the case the file behind
            // it exists to cover: reading nothing is the reason to reach back, not to stop.
            //
            // But only behind a file that came back whole. A tail cut at the front already has
            // records missing between it and anything older, and the file behind it would be
            // fitted in front of that gap - an older stretch of the log in place of a newer one,
            // with nothing in the window to say so.
            if (entire) {
                PrependPredecessor(path, SeedByteBudget - spent, into);
            }

            if (into.Count == 0) {
                return false;
            }

            long clearedThrough = ClearedThrough;
            long highest = 0;
            for (int i = 0; i < into.Count; i++) {
                if (into[i].Sequence > highest) {
                    highest = into[i].Sequence;
                }
                // Clearing the window is meant to stay cleared. The file keeps everything - that
                // is the point of it - but a reload must not hand back what was dismissed.
                if (into[i].Sequence > clearedThrough) {
                    Instance.Write(into[i]);
                }
            }

            LogCore.ReserveSequencesThrough(highest);
            return true;
        }

        /// <summary>
        /// Reads the file before this one as well, while there is room left in the buffer and
        /// budget left over.
        /// <para>
        /// A rotation shortly before the reload leaves the file now current holding a handful of
        /// records, and the session's history in the one behind it. Read on its own, the window
        /// would come back all but empty and look as though a recompile had eaten the morning.
        /// </para>
        /// <para>
        /// Never below the file this editor session started with. What is under that file was
        /// left by a session that is over - yesterday's editor, a run of the game - and it
        /// numbers from a beginning of its own, so mixing the two would leave the buffer
        /// unsorted and every lookup into it wrong. The ids cannot be read for this: they rise
        /// within a session, so a short file left behind yesterday sits below everything an
        /// editor that has been up an hour holds, and clears any bar its own history clears.
        /// Where the session began is what answers it, and that is a file rather than a number.
        /// </para>
        /// </summary>
        private static void PrependPredecessor(string path, long byteBudget, List<LogRecord> into) {
            int room = Instance.Buffer.Capacity - into.Count;
            if (room <= 0 || byteBudget <= 0) {
                return;
            }

            string first = SessionFirstFilePath;
            string earlier = FileSink.FileBefore(path);
            if (first == null || earlier == null || FileSink.WrittenBefore(earlier, first)) {
                return;
            }

            List<LogRecord> before = new List<LogRecord>();
            if (LogSessionReader.ReadTail(earlier, room, byteBudget, before) == 0) {
                return;
            }

            into.InsertRange(0, before);
        }

        /// <summary>
        /// Adds the console entries the session file does not already account for.
        /// <para>
        /// Plenty is left over. Whatever was in the console when the editor started, before
        /// this sink existed to write it anywhere. Anything another package logs from its own
        /// load code, which runs before ours. Anything logged in the old domain after the file
        /// was closed for the reload. A compiler message, which may or may not travel through
        /// Debug - the answer is in native code and not worth resting a design on.
        /// </para>
        /// <para>
        /// So nothing is assumed about where an entry came from: every one is matched off
        /// against what the file supplied, by first line and by count. A warning logged three
        /// times and read back three times leaves nothing to add; a fourth in the console is one
        /// the file genuinely missed. That is correct whether or not compiler messages reach the
        /// pipeline, which is the point of doing it this way.
        /// </para>
        /// </summary>
        private static void SeedRemainingConsoleEntries(List<LogRecord> fromFile) {
            if (!ConsoleEntryBridge.Available) {
                return;
            }

            List<ConsoleEntryBridge.ConsoleEntry> entries = new List<ConsoleEntryBridge.ConsoleEntry>();
            ConsoleEntryBridge.ReadAll(entries);
            if (entries.Count == 0) {
                return;
            }

            Dictionary<string, int> accounted = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < fromFile.Count; i++) {
                if (!fromFile[i].Captured) {
                    continue;
                }
                string key = FirstLine(fromFile[i].Message);
                accounted.TryGetValue(key, out int seen);
                accounted[key] = seen + 1;
            }

            for (int i = 0; i < entries.Count; i++) {
                ConsoleEntryBridge.ConsoleEntry entry = entries[i];
                SplitMessage(entry.Message, out string message, out string stackTrace);

                string key = FirstLine(message);
                if (accounted.TryGetValue(key, out int left) && left > 0) {
                    accounted[key] = left - 1;
                    continue;
                }

                Instance.Write(new LogRecord(
                    LogCore.NextSequence(),
                    LogCore.ForeignTag,
                    message,
                    entry.Level,
                    LogChannel.Prod,
                    0.0,
                    0,
                    entry.File,
                    entry.Line,
                    stackTrace,
                    entry.InstanceId,
                    captured: true));
            }
        }

        private static string FirstLine(string message) {
            if (string.IsNullOrEmpty(message)) {
                return string.Empty;
            }
            int newline = message.IndexOf('\n');
            return newline < 0 ? message : message.Substring(0, newline);
        }

        /// <summary>
        /// Fill the buffer with what the console is already holding.
        /// <para>
        /// This buffer is rebuilt on every domain reload, so without it the window starts
        /// blank after each recompile and shows nothing that happened before it opened, while
        /// the console beside it still has all of it. Seeding runs before the sink is
        /// registered, so nothing can be recorded twice.
        /// </para>
        /// </summary>
        private static void SeedFromConsole() {
            if (!ConsoleEntryBridge.Available) {
                return;
            }

            List<ConsoleEntryBridge.ConsoleEntry> entries = new List<ConsoleEntryBridge.ConsoleEntry>();
            ConsoleEntryBridge.ReadAll(entries);

            int first = Mathf.Max(0, entries.Count - Instance.Buffer.Capacity);
            for (int i = first; i < entries.Count; i++) {
                ConsoleEntryBridge.ConsoleEntry entry = entries[i];
                SplitMessage(entry.Message, out string message, out string stackTrace);

                Instance.Write(new LogRecord(
                    LogCore.NextSequence(),
                    LogCore.ForeignTag,
                    message,
                    entry.Level,
                    LogChannel.Prod,
                    0.0,
                    0,
                    entry.File,
                    entry.Line,
                    stackTrace,
                    entry.InstanceId,
                    captured: true));
            }
        }

        /// <summary>
        /// A console entry holds the message and its trace in one string. Splitting at the
        /// first newline matches the shape of a record written through the facade.
        /// </summary>
        private static void SplitMessage(string raw, out string message, out string stackTrace) {
            int newline = raw.IndexOf('\n');
            if (newline < 0) {
                message = raw;
                stackTrace = null;
                return;
            }
            message = raw.Substring(0, newline);
            stackTrace = raw.Substring(newline + 1).Trim();
            if (stackTrace.Length == 0) {
                stackTrace = null;
            }
        }

        /// <summary>
        /// Takes the compiler's own messages as it finishes with each assembly.
        /// <para>
        /// Nothing else can. A compile that fails does not reload the domain, so the seeding at
        /// load never runs for it - and by the time a later compile succeeds and the domain does
        /// reload, Unity has removed those errors from the console. Read at load or polled from
        /// the console, a compile error would never appear in this window at all, which is the
        /// one thing someone using it instead of the Console cannot do without.
        /// </para>
        /// <para>
        /// Straight into the buffer rather than through the pipeline, so they are not written to
        /// the session file and read back after the reload that fixed them - they live exactly
        /// as long as the Console's own copies do.
        /// </para>
        /// </summary>
        private static void OnAssemblyCompiled(string assemblyPath, CompilerMessage[] messages) {
            if (messages == null || Instance == null) {
                return;
            }

            for (int i = 0; i < messages.Length; i++) {
                CompilerMessage message = messages[i];
                Instance.Write(new LogRecord(
                    LogCore.NextSequence(),
                    LogCore.ForeignTag,
                    message.message,
                    LevelOf(message.type),
                    LogChannel.Prod,
                    0.0,
                    0,
                    message.file,
                    message.line,
                    null,
                    0,
                    captured: true));
            }
        }

        private static LogLevel LevelOf(CompilerMessageType type) {
            switch (type) {
                case CompilerMessageType.Error:
                    return LogLevel.Error;
                case CompilerMessageType.Warning:
                    return LogLevel.Warning;
                default:
                    return LogLevel.Log;
            }
        }

        private static void OnPlayModeChanged(PlayModeStateChange change) {
            if (change == PlayModeStateChange.EnteredPlayMode) {
                if (ClearOnPlay) {
                    Instance.Clear();
                }
                // After the clear, so it survives it and is the first line of the run - and so
                // its sequence is above the watermark a clear leaves behind.
                Mark("Entered play mode");
            } else if (change == PlayModeStateChange.EnteredEditMode) {
                Mark("Exited play mode");
            }
        }

        /// <summary>
        /// Writes a line marking a boundary in the session: a reload, or play mode starting and
        /// stopping.
        /// <para>
        /// The clock is a static, so it starts again from zero with every domain, and the Time
        /// column reads 812.33, 812.40, 0.05 with nothing to say why. Carrying the clock across
        /// instead would make the column continuous and false, hiding a recompile that took
        /// eight seconds. This leaves the reset where it is and explains it.
        /// </para>
        /// <para>
        /// The wall clock goes in the text, which makes every row's absolute time derivable -
        /// this marker plus the row's own elapsed - and which is also what stops Collapse
        /// folding every reload of the session into one row.
        /// </para>
        /// </summary>
        private static void Mark(string what) {
            // file: null so that Open on the marker does not offer to show this line of this
            // file, which is not where anything happened.
            Log.DevInfo(MarkerTag, what + " at " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), null, null, 0);
        }
    }
}
