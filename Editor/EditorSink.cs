using System.Collections.Generic;
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
        private const string CapacityKey = "KenseiLog.Capacity";
        private const string ClearOnPlayKey = "KenseiLog.ClearOnPlay";
        private const int DefaultCapacity = 8192;

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
            get => EditorPrefs.GetBool(ClearOnPlayKey, true);
            set => EditorPrefs.SetBool(ClearOnPlayKey, value);
        }

        public int Capacity {
            get => _buffer.Capacity;
            set {
                if (value == _buffer.Capacity) {
                    return;
                }
                EditorPrefs.SetInt(CapacityKey, value);
                _buffer = new LogRingBuffer(value);
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
            Instance = new EditorSink(EditorPrefs.GetInt(CapacityKey, DefaultCapacity));
            SeedFromConsole();
            LogCore.AddSink(Instance);
            LogCore.Initialize();
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
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
