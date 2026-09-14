namespace KenseiLog {
    /// <summary>
    /// A single immutable log entry.
    /// <para>
    /// The tag is a first-class field rather than a prefix inside the message, so filtering
    /// by tag never collides with the same word appearing in the message text.
    /// </para>
    /// <para>
    /// Records are stored by value, so emitting one allocates nothing beyond the message
    /// string that the caller already built.
    /// </para>
    /// </summary>
    public readonly struct LogRecord {
        public readonly string Tag;
        public readonly string Message;
        public readonly string StackTrace;

        /// <summary>Absolute source path from the call site, or null for captured foreign logs.</summary>
        public readonly string File;

        /// <summary>Milliseconds since logging was initialised.</summary>
        public readonly double TimeMs;

        /// <summary>Monotonically increasing id, unique for the lifetime of the app domain.</summary>
        public readonly long Sequence;

        public readonly int Line;
        public readonly int Frame;

        /// <summary>
        /// Instance id of the related UnityEngine.Object, or 0. Stored as an id rather than a
        /// reference so a buffered record never keeps a destroyed object alive.
        /// </summary>
        public readonly int ContextInstanceId;

        public readonly LogLevel Level;
        public readonly LogChannel Channel;

        public LogRecord(
            long sequence,
            string tag,
            string message,
            LogLevel level,
            LogChannel channel,
            double timeMs,
            int frame,
            string file,
            int line,
            string stackTrace,
            int contextInstanceId) {
            Sequence = sequence;
            Tag = tag;
            Message = message;
            Level = level;
            Channel = channel;
            TimeMs = timeMs;
            Frame = frame;
            File = file;
            Line = line;
            StackTrace = stackTrace;
            ContextInstanceId = contextInstanceId;
        }
    }
}
