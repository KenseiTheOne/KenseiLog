using System;

namespace KenseiLog {
    /// <summary>
    /// Fixed-capacity circular store of log records, safe to use from any thread.
    /// <para>
    /// Reads are addressed by <see cref="LogRecord.Sequence"/> rather than by position:
    /// positions shift as the ring overwrites itself, so a consumer that remembered an
    /// index would silently start reading a different record.
    /// </para>
    /// </summary>
    public sealed class LogRingBuffer {
        private readonly object _lock = new object();
        private readonly LogRecord[] _records;
        private int _head;
        private int _count;

        public LogRingBuffer(int capacity) {
            if (capacity < 1) {
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Ring buffer capacity must be at least 1.");
            }
            _records = new LogRecord[capacity];
        }

        public int Capacity => _records.Length;

        public int Count {
            get {
                lock (_lock) {
                    return _count;
                }
            }
        }

        /// <summary>Sequence of the oldest retained record, or 0 when the buffer is empty.</summary>
        public long OldestSequence {
            get {
                lock (_lock) {
                    return _count == 0 ? 0L : _records[_head].Sequence;
                }
            }
        }

        public void Add(in LogRecord record) {
            lock (_lock) {
                if (_count < _records.Length) {
                    _records[(_head + _count) % _records.Length] = record;
                    _count++;
                } else {
                    _records[_head] = record;
                    _head = (_head + 1) % _records.Length;
                }
            }
        }

        public void Clear() {
            lock (_lock) {
                Array.Clear(_records, 0, _records.Length);
                _head = 0;
                _count = 0;
            }
        }

        public bool TryGetBySequence(long sequence, out LogRecord record) {
            lock (_lock) {
                if (_count == 0) {
                    record = default;
                    return false;
                }
                long offset = sequence - _records[_head].Sequence;
                if (offset < 0 || offset >= _count) {
                    record = default;
                    return false;
                }
                record = _records[(_head + (int)offset) % _records.Length];
                return true;
            }
        }

        /// <summary>
        /// Copies retained records newer than <paramref name="sequence"/> into
        /// <paramref name="destination"/>, oldest first, and returns how many were written.
        /// Batching the scan under a single lock keeps the editor's polling cheap; if the
        /// destination fills up, the caller simply picks up the rest on its next call.
        /// </summary>
        public int CopyNewerThan(long sequence, LogRecord[] destination) {
            lock (_lock) {
                if (_count == 0) {
                    return 0;
                }
                long oldest = _records[_head].Sequence;
                long firstWanted = sequence + 1;
                int start = firstWanted <= oldest ? 0 : (int)(firstWanted - oldest);
                if (start >= _count) {
                    return 0;
                }
                int written = Math.Min(_count - start, destination.Length);
                for (int i = 0; i < written; i++) {
                    destination[i] = _records[(_head + start + i) % _records.Length];
                }
                return written;
            }
        }
    }
}
