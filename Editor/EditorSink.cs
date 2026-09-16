using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEditor;
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

        /// <summary>Kept apart from the runs, which own the directory above it.</summary>
        private const string EditorLogFolder = "editor";

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

        /// <summary>Where that file goes, whether or not one is open.</summary>
        public static string SessionFileDirectory =>
            Path.Combine(Application.persistentDataPath, "logs", EditorLogFolder);

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
        }

        [InitializeOnLoadMethod]
        private static void Install() {
            // Clamped on the way in as well as on the way out, so a preference already holding
            // a bad value from an earlier version repairs itself instead of throwing here.
            int capacity = Mathf.Clamp(EditorPrefs.GetInt(_capacityKey, DefaultCapacity), MinimumCapacity, MaximumCapacity);
            Instance = new EditorSink(capacity);

            // A domain reload is not a new session, so what this editor logged before it is
            // read back out of the session file - where the tag, the channel and the call site
            // survive, none of which Unity's console has anywhere to keep. Anything else, and
            // a fresh editor, falls back to the console.
            //
            // Whatever any of it makes of a Unity version that has moved things, it must not
            // cost us the two lines below: without them the sink is never registered and the
            // window records nothing at all, while still opening and looking healthy.
            bool continuing = SessionState.GetBool(SessionStartedKey, false);
            try {
                if (continuing && SeedFromSessionFile()) {
                    SeedCompilerEntriesFromConsole();
                } else {
                    SeedFromConsole();
                }
            } catch (Exception exception) {
                Debug.LogWarning("KenseiLog: could not seed the window (" + exception.Message + ")");
            }

            LogCore.AddSink(Instance);
            LogCore.Initialize();
            EditorApplication.playModeStateChanged += OnPlayModeChanged;

            OpenSessionFile();
        }

        /// <summary>
        /// Opens the editor's own log file, adding to the one this editor session already has.
        /// <para>
        /// Nothing here is allowed to cost the window its records: a directory that cannot be
        /// written to, or a file another process is holding, leaves the sink unopened and the
        /// rest of the pipeline exactly as it was.
        /// </para>
        /// </summary>
        private static void OpenSessionFile() {
            if (!WriteSessionFile) {
                return;
            }

            try {
                LogConfig config = LogCore.Config;
                config.FileDirectory = SessionFileDirectory;
                // The point of the file is the channel the runtime's own sink leaves out: a
                // dev record written from an editor tool has nowhere else to survive.
                config.FileIncludesDevChannel = true;

                bool alreadyOpenedThisSession = SessionState.GetBool(SessionStartedKey, false);
                _sessionFile = new FileSink(in config, continueExistingFile: alreadyOpenedThisSession);
                SessionState.SetBool(SessionStartedKey, true);

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
            _sessionFile.Dispose();
            _sessionFile = null;
        }

        /// <summary>
        /// Fills the buffer from the file this editor session has been writing.
        /// <para>
        /// The records come back whole - tag, channel, frame, call site, stack trace - which is
        /// the difference between this and reading the console, where none of that exists. Their
        /// original sequence numbers come back with them, and the counter is moved past the
        /// highest: the file carries on being written after the reload, and records repeating
        /// numbers already in it would leave it unsorted and every lookup into it wrong.
        /// </para>
        /// </summary>
        private static bool SeedFromSessionFile() {
            if (!WriteSessionFile) {
                return false;
            }

            string path = NewestSessionFile();
            if (path == null) {
                return false;
            }

            List<LogRecord> records = new List<LogRecord>();
            if (LogSessionReader.ReadTail(path, Instance.Buffer.Capacity, SeedByteBudget, records) == 0) {
                return false;
            }

            long highest = 0;
            for (int i = 0; i < records.Count; i++) {
                Instance.Write(records[i]);
                if (records[i].Sequence > highest) {
                    highest = records[i].Sequence;
                }
            }

            LogCore.ReserveSequencesThrough(highest);
            return true;
        }

        /// <summary>
        /// The file the session is writing: the highest numbered, since numbers rise with time.
        /// </summary>
        private static string NewestSessionFile() {
            string directory = SessionFileDirectory;
            if (!Directory.Exists(directory)) {
                return null;
            }

            string[] files = Directory.GetFiles(directory, "log.*.jsonl");
            string newest = null;
            for (int i = 0; i < files.Length; i++) {
                if (newest == null || string.CompareOrdinal(Path.GetFileName(files[i]), Path.GetFileName(newest)) > 0) {
                    newest = files[i];
                }
            }
            return newest;
        }

        /// <summary>
        /// Adds the console entries that this package can never have seen for itself.
        /// <para>
        /// A compiler message does not arrive through Debug, so it reaches the console by a path
        /// the log pipeline has no sight of - which is why it is missing from the session file,
        /// and why taking it from the console cannot show anything twice.
        /// </para>
        /// </summary>
        private static void SeedCompilerEntriesFromConsole() {
            if (!ConsoleEntryBridge.Available) {
                return;
            }

            List<ConsoleEntryBridge.ConsoleEntry> entries = new List<ConsoleEntryBridge.ConsoleEntry>();
            ConsoleEntryBridge.ReadAll(entries);

            for (int i = 0; i < entries.Count; i++) {
                ConsoleEntryBridge.ConsoleEntry entry = entries[i];
                if (!ConsoleEntryBridge.IsCompilerEntry(entry.Mode)) {
                    continue;
                }
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

        private static void OnPlayModeChanged(PlayModeStateChange change) {
            if (change == PlayModeStateChange.EnteredPlayMode && ClearOnPlay) {
                Instance.Clear();
            }
        }
    }
}
