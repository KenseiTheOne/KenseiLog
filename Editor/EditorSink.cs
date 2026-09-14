using System.Threading;
using UnityEditor;

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
            LogCore.AddSink(Instance);
            LogCore.Initialize();
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private static void OnPlayModeChanged(PlayModeStateChange change) {
            if (change == PlayModeStateChange.EnteredPlayMode && ClearOnPlay) {
                Instance.Clear();
            }
        }
    }
}
