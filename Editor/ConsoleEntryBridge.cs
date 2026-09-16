using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;

namespace KenseiLog.Editor {
    /// <summary>
    /// Reads the context object Unity attached to a log, which the public capture callback
    /// never passes on.
    /// <para>
    /// <c>Application.logMessageReceived</c> hands over the message, the trace and the level,
    /// so a warning the engine raised against an asset - a USS file, a prefab - arrives with
    /// nothing to navigate to. Unity's own console opens those because it holds an instance id
    /// alongside each entry. That store is <c>UnityEditor.LogEntries</c>, and it is internal.
    /// </para>
    /// <para>
    /// So this reaches it by reflection, and is built to be wrong safely: everything is probed
    /// once, and if any piece is missing or has been renamed the bridge turns itself off for
    /// good. A Unity upgrade that moves this API costs the extra navigation and nothing else -
    /// logging itself never runs through here.
    /// </para>
    /// <para>
    /// Lookups go through an index built in one pass. Reading the store takes its lock and
    /// walks every entry, so doing that per question froze the editor as soon as a drag moved
    /// the selection across a few rows of a console holding thousands of lines.
    /// </para>
    /// </summary>
    public static class ConsoleEntryBridge {
        /// <summary>
        /// Floor on how often the index may be rebuilt. Unity's entry count is the real signal;
        /// this is the backstop for an editor version that does not expose one, so a stale
        /// index costs a fifth of a second of freshness rather than a walk per lookup.
        /// </summary>
        private const double MinRebuildSeconds = 0.2;

        private static bool _probed;
        private static bool _available;

        private static Type _entryType;
        private static MethodInfo _startGettingEntries;
        private static MethodInfo _endGettingEntries;
        private static MethodInfo _getEntryInternal;
        private static MethodInfo _getCount;
        private static FieldInfo _messageField;
        private static FieldInfo _instanceIdField;
        private static FieldInfo _fileField;
        private static FieldInfo _lineField;
        private static FieldInfo _modeField;

        private static Dictionary<string, Entry> _index;
        private static int _indexedCount = -1;
        private static double _lastBuild;

        public static bool Available {
            get {
                Probe();
                return _available;
            }
        }

        /// <summary>
        /// What Unity knows about the console entry carrying this message: the object it was
        /// logged against, and a source location when the entry has one.
        /// </summary>
        public static bool TryResolve(string message, out int instanceId, out string file, out int line) {
            instanceId = 0;
            file = null;
            line = 0;

            Probe();
            if (!_available || string.IsNullOrEmpty(message)) {
                return false;
            }

            EnsureIndex();
            if (_index == null || !_index.TryGetValue(FirstLine(message), out Entry entry)) {
                return false;
            }

            instanceId = entry.InstanceId;
            file = entry.File;
            line = entry.Line;
            return instanceId != 0 || !string.IsNullOrEmpty(file);
        }

        /// <summary>
        /// Every entry the console is holding, oldest first.
        /// <para>
        /// Used to seed the window so it shows what the console shows, including what was
        /// logged before it opened and what survived the last domain reload. Our own records
        /// keep coming through the pipeline, which is the only place tags and channels exist.
        /// </para>
        /// </summary>
        public static int ReadAll(List<ConsoleEntry> into) {
            Probe();
            if (!_available) {
                return 0;
            }

            object[] args = new object[2];

            // Inside the guard with the rest of it. This is a type found by reflection, so
            // its constructor is no more ours to count on than the methods below - and this
            // call is the only one that was left outside, which put an exception from it into
            // the InitializeOnLoadMethod that seeds the window, before the sink is registered.
            // The window then opened looking healthy and recorded nothing, once per reload.
            object entry;
            int count;
            try {
                entry = Activator.CreateInstance(_entryType);
                count = (int)_startGettingEntries.Invoke(null, null);
            } catch (Exception) {
                _available = false;
                return 0;
            }

            int read = 0;
            try {
                for (int row = 0; row < count; row++) {
                    args[0] = row;
                    args[1] = entry;
                    if (!(bool)_getEntryInternal.Invoke(null, args)) {
                        continue;
                    }
                    if (!(_messageField.GetValue(args[1]) is string message) || message.Length == 0) {
                        continue;
                    }

                    into.Add(new ConsoleEntry(
                        message,
                        LevelFromMode(_modeField != null ? (int)_modeField.GetValue(args[1]) : 0),
                        (int)_instanceIdField.GetValue(args[1]),
                        _fileField?.GetValue(args[1]) as string,
                        _lineField != null ? (int)_lineField.GetValue(args[1]) : 0));
                    read++;
                }
            } catch (Exception) {
                _available = false;
                return read;
            } finally {
                try {
                    _endGettingEntries.Invoke(null, null);
                } catch (Exception) {
                    _available = false;
                }
            }

            return read;
        }

        /// <summary>
        /// Unity keeps the severity as bits on the entry. The values are internal, so a
        /// version that renumbers them costs a wrong icon on seeded rows and nothing more.
        /// <para>
        /// The error mask is the one the console's own GetStyleForErrorMode carries: Error,
        /// Assert, Fatal, AssetImportError, ScriptingError, ScriptCompileError,
        /// ScriptingException, GraphCompileError and ScriptingAssertion. The last two were
        /// missing here, so a shader graph that failed to compile, and an assertion that
        /// outlived a domain reload, arrived as ordinary log lines - and disappeared the moment
        /// anyone unticked Log to hunt for errors.
        /// </para>
        /// </summary>
        private static LogLevel LevelFromMode(int mode) {
            const int errorBits = (1 << 0) | (1 << 1) | (1 << 4) | (1 << 6) | (1 << 8) |
                                  (1 << 11) | (1 << 13) | (1 << 17) |
                                  (1 << 20) | (1 << 21);
            const int warningBits = (1 << 7) | (1 << 9) | (1 << 12);

            if ((mode & errorBits) != 0) {
                return LogLevel.Error;
            }
            if ((mode & warningBits) != 0) {
                return LogLevel.Warning;
            }
            return LogLevel.Log;
        }

        public readonly struct ConsoleEntry {
            public readonly string Message;
            public readonly LogLevel Level;
            public readonly int InstanceId;
            public readonly string File;
            public readonly int Line;

            public ConsoleEntry(string message, LogLevel level, int instanceId, string file, int line) {
                Message = message;
                Level = level;
                InstanceId = instanceId;
                File = file;
                Line = line;
            }
        }

        /// <summary>Drops the index so the next lookup reads the console again.</summary>
        public static void Invalidate() {
            _index = null;
            _indexedCount = -1;
        }

        private static void EnsureIndex() {
            int count = CurrentCount();
            if (_index != null && count >= 0 && count == _indexedCount) {
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            if (_index != null && now - _lastBuild < MinRebuildSeconds) {
                return;
            }

            BuildIndex(now);
        }

        private static void BuildIndex(double now) {
            Dictionary<string, Entry> index = new Dictionary<string, Entry>(StringComparer.Ordinal);
            object[] args = new object[2];

            object entry;
            int count;
            try {
                entry = Activator.CreateInstance(_entryType);
                count = (int)_startGettingEntries.Invoke(null, null);
            } catch (Exception) {
                _available = false;
                return;
            }

            try {
                // Oldest first, so a message logged repeatedly leaves the newest entry in the
                // index - the one the row in front of you came from.
                for (int row = 0; row < count; row++) {
                    args[0] = row;
                    args[1] = entry;
                    if (!(bool)_getEntryInternal.Invoke(null, args)) {
                        continue;
                    }

                    if (!(_messageField.GetValue(args[1]) is string entryMessage) || entryMessage.Length == 0) {
                        continue;
                    }

                    int instanceId = (int)_instanceIdField.GetValue(args[1]);
                    string file = _fileField?.GetValue(args[1]) as string;
                    int line = _lineField != null ? (int)_lineField.GetValue(args[1]) : 0;
                    if (instanceId == 0 && string.IsNullOrEmpty(file)) {
                        continue;
                    }

                    index[FirstLine(entryMessage)] = new Entry(instanceId, file, line);
                }
            } catch (Exception) {
                _available = false;
                return;
            } finally {
                try {
                    _endGettingEntries.Invoke(null, null);
                } catch (Exception) {
                    _available = false;
                }
            }

            _index = index;
            _indexedCount = count;
            _lastBuild = now;
        }

        private static int CurrentCount() {
            if (_getCount == null) {
                return -1;
            }
            try {
                return (int)_getCount.Invoke(null, null);
            } catch (Exception) {
                _getCount = null;
                return -1;
            }
        }

        private static void Probe() {
            if (_probed) {
                return;
            }
            _probed = true;

            try {
                Assembly editorAssembly = typeof(UnityEditor.Editor).Assembly;
                Type entries = editorAssembly.GetType("UnityEditor.LogEntries");
                _entryType = editorAssembly.GetType("UnityEditor.LogEntry");
                if (entries == null || _entryType == null) {
                    return;
                }

                const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                _startGettingEntries = entries.GetMethod("StartGettingEntries", statics);
                _endGettingEntries = entries.GetMethod("EndGettingEntries", statics);
                _getEntryInternal = entries.GetMethod("GetEntryInternal", statics);
                _messageField = _entryType.GetField("message", instance);
                _instanceIdField = _entryType.GetField("instanceID", instance);

                // Optional. Without it the index falls back to the time-based floor above.
                _getCount = entries.GetMethod("GetCount", statics, null, Type.EmptyTypes, null);
                if (_getCount != null && _getCount.ReturnType != typeof(int)) {
                    _getCount = null;
                }

                // Optional: only used to offer a jump when the entry carries a location.
                _fileField = _entryType.GetField("file", instance);
                _lineField = _entryType.GetField("line", instance);
                _modeField = _entryType.GetField("mode", instance);

                _available = _startGettingEntries != null && _endGettingEntries != null &&
                             _getEntryInternal != null && _messageField != null && _instanceIdField != null &&
                             _startGettingEntries.ReturnType == typeof(int) &&
                             _getEntryInternal.ReturnType == typeof(bool) &&
                             _instanceIdField.FieldType == typeof(int);
            } catch (Exception) {
                _available = false;
            }
        }

        private static string FirstLine(string message) {
            int newline = message.IndexOf('\n');
            return newline < 0 ? message : message.Substring(0, newline);
        }

        private readonly struct Entry {
            public readonly int InstanceId;
            public readonly string File;
            public readonly int Line;

            public Entry(int instanceId, string file, int line) {
                InstanceId = instanceId;
                File = file;
                Line = line;
            }
        }
    }
}
