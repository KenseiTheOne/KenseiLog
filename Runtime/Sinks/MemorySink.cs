using System.Threading;

namespace KenseiLog {
    /// <summary>
    /// Keeps recent records in memory for something to display. Used by the in-game overlay;
    /// the editor window has its own, because the two have different lifetimes and sizes.
    /// </summary>
    public sealed class MemorySink : ILogSink {
        private readonly long[] _levelCounts = new long[3];
        private int _version;

        public MemorySink(int capacity) {
            Buffer = new LogRingBuffer(capacity);
        }

        /// <summary>
        /// A sink of a different size carrying on from this one: the records it still holds, and
        /// the totals of everything it ever took.
        /// <para>
        /// The totals are the point. Records a burst pushed out are not in the buffer to be
        /// replayed, so a new sink that counted only what replayed would undo, on a settings
        /// change, the very undercount counting on the way in exists to prevent - and it would
        /// undo it silently, which is how the first version of this went wrong.
        /// </para>
        /// </summary>
        public MemorySink(int capacity, MemorySink carryingFrom) : this(capacity) {
            LogRecord[] carried = new LogRecord[carryingFrom.Buffer.Count];
            int copied = carryingFrom.Buffer.CopyNewerThan(0, carried);
            for (int i = 0; i < copied; i++) {
                Buffer.Add(in carried[i]);
            }
            for (int level = 0; level < _levelCounts.Length; level++) {
                _levelCounts[level] = carryingFrom.LevelCount((LogLevel)level);
            }
        }

        public LogRingBuffer Buffer { get; }

        /// <summary>Changes on every record, so a view can poll instead of subscribing.</summary>
        public int Version => _version;

        /// <summary>
        /// How many records of this level have been written, whether or not they are still in
        /// the buffer.
        /// <para>
        /// Counted here rather than by whatever reads the buffer, because a reader only sees
        /// what survived. A burst longer than the ring pushes its own beginning out before
        /// anything polls, and a viewer counting what it reads misses every record that went
        /// the other way: a thousand logs in one frame arrived as the couple of dozen that
        /// happened to be left, which is the opposite of what a counter is for.
        /// </para>
        /// <para>
        /// Interlocked because Write runs on whichever thread logged, and read the same way:
        /// a long is not read atomically on a 32 bit runtime, and Android and WebGL still ship
        /// as one.
        /// </para>
        /// </summary>
        public long LevelCount(LogLevel level) =>
            Interlocked.Read(ref _levelCounts[(int)level]);

        public void Write(in LogRecord record) {
            Buffer.Add(in record);
            Interlocked.Increment(ref _levelCounts[(int)record.Level]);
            Interlocked.Increment(ref _version);
        }

        public void Clear() {
            Buffer.Clear();
            for (int i = 0; i < _levelCounts.Length; i++) {
                Interlocked.Exchange(ref _levelCounts[i], 0L);
            }
            Interlocked.Increment(ref _version);
        }
    }
}
