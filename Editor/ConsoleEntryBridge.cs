using System;
using System.Reflection;

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
    /// </summary>
    public static class ConsoleEntryBridge {
        private static bool _probed;
        private static bool _available;

        private static Type _entryType;
        private static MethodInfo _startGettingEntries;
        private static MethodInfo _endGettingEntries;
        private static MethodInfo _getEntryInternal;
        private static FieldInfo _messageField;
        private static FieldInfo _instanceIdField;
        private static FieldInfo _fileField;
        private static FieldInfo _lineField;

        public static bool Available {
            get {
                Probe();
                return _available;
            }
        }

        /// <summary>
        /// Find what Unity knows about the console entry carrying this message: the object it
        /// was logged against, and a source location when the entry has one.
        /// </summary>
        public static bool TryResolve(string message, out int instanceId, out string file, out int line) {
            instanceId = 0;
            file = null;
            line = 0;

            Probe();
            if (!_available || string.IsNullOrEmpty(message)) {
                return false;
            }

            string wanted = FirstLine(message);
            object entry = Activator.CreateInstance(_entryType);
            object[] args = new object[2];

            int count;
            try {
                count = (int)_startGettingEntries.Invoke(null, null);
            } catch (Exception) {
                _available = false;
                return false;
            }

            try {
                // Newest first: a message logged repeatedly should resolve to the latest one,
                // which is the entry the row in front of you came from.
                for (int row = count - 1; row >= 0; row--) {
                    args[0] = row;
                    args[1] = entry;
                    if (!(bool)_getEntryInternal.Invoke(null, args)) {
                        continue;
                    }

                    string entryMessage = _messageField.GetValue(args[1]) as string;
                    if (entryMessage == null || !entryMessage.StartsWith(wanted, StringComparison.Ordinal)) {
                        continue;
                    }

                    instanceId = (int)_instanceIdField.GetValue(args[1]);
                    file = _fileField?.GetValue(args[1]) as string;
                    line = _lineField != null ? (int)_lineField.GetValue(args[1]) : 0;
                    return instanceId != 0 || !string.IsNullOrEmpty(file);
                }
            } catch (Exception) {
                _available = false;
                return false;
            } finally {
                try {
                    _endGettingEntries.Invoke(null, null);
                } catch (Exception) {
                    _available = false;
                }
            }

            return false;
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

                // Optional: only used to offer a jump when the entry carries a location.
                _fileField = _entryType.GetField("file", instance);
                _lineField = _entryType.GetField("line", instance);

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
    }
}
