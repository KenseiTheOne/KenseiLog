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

            // Seeding reads Unity's console through reflection. Whatever it makes of a version
            // that has moved things, it must not cost us the two lines below it: without them
            // the sink is never registered and the window records nothing at all, while still
            // opening and looking perfectly healthy.
            try {
                SeedFromConsole();
            } catch (Exception exception) {
                Debug.LogWarning("KenseiLog: could not seed the window from the console (" + exception.Message + ")");
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
