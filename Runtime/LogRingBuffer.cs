using System;
using System.Collections.Generic;

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
        // How many records of each tag the buffer is holding right now. Kept here because this
        // is the only place that sees both ends: a record arrives on the logging thread and the
        // one it displaced leaves in the same breath, and nothing that polls afterwards can know
        // what went. A viewer that counted for itself could only count arrivals, which is how
        // the overlay's tag pane came to offer tags whose every record had already gone.
        private readonly Dictionary<string, int> _tags = new Dictionary<string, int>();
        private int _head;
        private int _count;

        public LogRingBuffer(int capacity) {
            if (capacity < 1) {
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Ring buffer capacity must be at least 1.");
            }
            _records = new LogRecord[capacity];
        }

        public int Capacity => _records.Length;

        /// <summary>One tag and how many of its records the buffer is holding.</summary>
        public readonly struct TagCount {
            public readonly string Tag;
            public readonly int Held;

            public TagCount(string tag, int held) {
                Tag = tag;
                Held = held;
            }
        }

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
                    // Read before it is written over, and counted out before the new one is
                    // counted in: the two can be the same tag, and the order keeps the tally
                    // from dipping through zero and dropping the key.
                    Forget(_records[_head].Tag);
                    _records[_head] = record;
                    _head = (_head + 1) % _records.Length;
                }
                Remember(record.Tag);
                SettleLast();
            }
        }

        /// <summary>
        /// The tags the buffer is holding, with how many records of each. Written into the
        /// caller's list so that a viewer polling every frame allocates nothing once its list
        /// has grown. A tag whose last record has left is not in it.
        /// </summary>
        public int CopyTagCounts(List<TagCount> into) {
            into.Clear();
            lock (_lock) {
                foreach (KeyValuePair<string, int> pair in _tags) {
                    into.Add(new TagCount(pair.Key, pair.Value));
                }
            }
            return into.Count;
        }

        private void Remember(string tag) {
            _tags.TryGetValue(tag, out int held);
            _tags[tag] = held + 1;
        }

        private void Forget(string tag) {
            if (!_tags.TryGetValue(tag, out int held)) {
                return;
            }
            if (held <= 1) {
                _tags.Remove(tag);
                return;
            }
            _tags[tag] = held - 1;
        }

        /// <summary>
        /// Puts the record just added back into sequence order. Everything that reads this
        /// buffer binary-searches on <see cref="LogRecord.Sequence"/>, but a record takes its
        /// sequence when it is built and reaches the sinks a moment later, so two threads
        /// logging at once can arrive the wrong way round - and one inversion is enough to make
        /// a search walk past records that are sitting right there. Records arrive in order
        /// nearly always, which makes this one comparison on the usual path and a short walk on
        /// the rare inverted one.
        /// <para>
        /// The walk is bounded by how far out of order a record is, not by the capacity: an
        /// in-order add and an add that swaps one place both measure about 0.05us at capacity
        /// 8192. Reaching the full walk - around 0.13ms there - would need a record older than
        /// every one already buffered, so a thread descheduled for the span of an entire buffer.
        /// That is the ceiling rather than a case worth designing around.
        /// </para>
        /// Caller holds the lock.
        /// </summary>
        private void SettleLast() {
            int length = _records.Length;
            for (int i = _count - 1; i > 0; i--) {
                int current = (_head + i) % length;
                int previous = (_head + i - 1) % length;
                if (_records[previous].Sequence <= _records[current].Sequence) {
                    return;
                }
                LogRecord swap = _records[previous];
                _records[previous] = _records[current];
                _records[current] = swap;
            }
        }

        public void Clear() {
            lock (_lock) {
                Array.Clear(_records, 0, _records.Length);
                _tags.Clear();
                _head = 0;
                _count = 0;
            }
        }

        public bool TryGetBySequence(long sequence, out LogRecord record) {
            lock (_lock) {
                int index = Find(sequence);
                if (index < 0) {
                    record = default;
                    return false;
                }
                record = At(index);
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
                int found = Find(sequence + 1);
                int start = found < 0 ? ~found : found;
                if (start >= _count) {
                    return 0;
                }
                int written = Math.Min(_count - start, destination.Length);
                for (int i = 0; i < written; i++) {
                    destination[i] = At(start + i);
                }
                return written;
            }
        }

        /// <summary>
        /// Logical index of the record with this sequence, or the bitwise complement of where
        /// it would go. Binary search rather than arithmetic on the oldest sequence, because
        /// sequences are only guaranteed to rise, not to be contiguous: a file sink that skips
        /// the dev channel produces gaps, and a session loaded from such a file has them too.
        /// Caller holds the lock.
        /// </summary>
        private int Find(long sequence) {
            int low = 0;
            int high = _count - 1;
            while (low <= high) {
                int mid = low + ((high - low) >> 1);
                long midSequence = At(mid).Sequence;
                if (midSequence == sequence) {
                    return mid;
                }
                if (midSequence < sequence) {
                    low = mid + 1;
                } else {
                    high = mid - 1;
                }
            }
            return ~low;
        }

        private LogRecord At(int logicalIndex) =>
            _records[(_head + logicalIndex) % _records.Length];
    }
}
