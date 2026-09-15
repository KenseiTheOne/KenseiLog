using System.Threading;

namespace KenseiLog {
    /// <summary>
    /// Keeps recent records in memory for something to display. Used by the in-game overlay;
    /// the editor window has its own, because the two have different lifetimes and sizes.
    /// </summary>
    public sealed class MemorySink : ILogSink {
        private int _version;

        public MemorySink(int capacity) {
            Buffer = new LogRingBuffer(capacity);
        }

        public LogRingBuffer Buffer { get; }

        /// <summary>Changes on every record, so a view can poll instead of subscribing.</summary>
        public int Version => _version;

        public void Write(in LogRecord record) {
            Buffer.Add(in record);
            Interlocked.Increment(ref _version);
        }

        public void Clear() {
            Buffer.Clear();
            Interlocked.Increment(ref _version);
        }
    }
}
