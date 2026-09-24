using System;
using System.Collections.Generic;

namespace KenseiLog {
    /// <summary>
    /// Store of log records, safe to use from any thread: a fixed-capacity ring, or with
    /// <see cref="Unbounded"/> one that keeps everything until it is cleared.
    /// <para>
    /// Reads are addressed by <see cref="LogRecord.Sequence"/> rather than by position:
    /// positions shift as the ring overwrites itself, so a consumer that remembered an
    /// index would silently start reading a different record.
    /// </para>
    /// </summary>
    public sealed class LogRingBuffer {
        // An unbounded buffer grows a block at a time rather than one array by doubling. It
        // holds a session, which is tens of megabytes of records, and doubling copies all of it
        // while the logging threads wait on the lock - and holds both copies while it does.
        private const int BlockShift = 12;
        private const int BlockSize = 1 << BlockShift;
        private const int BlockMask = BlockSize - 1;

        private readonly object _lock = new object();
        // Exactly one of these two is set: the ring, or the blocks of a buffer with no end.
        private readonly LogRecord[] _records;
        private readonly List<LogRecord[]> _blocks;
        // How many records of each tag the buffer is holding right now. Kept here because this
        // is the only place that sees both ends: a record arrives on the logging thread and the
        // one it displaced leaves in the same breath, and nothing that polls afterwards can know
        // what went. A viewer that counted for itself could only count arrivals, which is how
        // the overlay's tag pane came to offer tags whose every record had already gone.
        private readonly Dictionary<string, int> _tags = new Dictionary<string, int>();
        // Written under the lock, read without it: it only ever turns true until a Clear, so a
        // reader that sees it a frame late has missed nothing it could act on.
        private volatile bool _evicted;
        private int _head;
        private int _count;

        public LogRingBuffer(int capacity) {
            if (capacity < 1) {
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Ring buffer capacity must be at least 1.");
            }
            _records = new LogRecord[capacity];
        }

        private LogRingBuffer() {
            _blocks = new List<LogRecord[]>();
        }

        /// <summary>
        /// A buffer that never lets a record go: it grows for as long as records arrive, and
        /// only <see cref="Clear"/> empties it. What the editor window keeps, as Unity's console
        /// does - a limit there is one more place a record goes missing without anybody asking.
        /// </summary>
        public static LogRingBuffer Unbounded() =>
            new LogRingBuffer();

        /// <summary>
        /// How many records it holds before the oldest start to go; int.MaxValue for one made
        /// by <see cref="Unbounded"/>, which never lets one go.
        /// </summary>
        public int Capacity => _records != null ? _records.Length : int.MaxValue;

        /// <summary>
        /// Whether a record has been pushed out since the buffer was made or last cleared - so
        /// whether what it holds is still everything it was given.
        /// </summary>
        public bool HasEvicted => _evicted;

        /// <summary>
        /// For a buffer built to carry on from one that had already let records go: those records
        /// are gone whether or not this one ever evicts, and a new buffer with room to spare would
        /// otherwise report a whole history it does not have.
        /// </summary>
        internal void MarkEvicted() {
            lock (_lock) {
                _evicted = true;
            }
        }

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
                    return _count == 0 ? 0L : Slot(0).Sequence;
                }
            }
        }

        public void Add(in LogRecord record) {
            lock (_lock) {
                if (_blocks != null) {
                    if (_count == _blocks.Count << BlockShift) {
                        _blocks.Add(new LogRecord[BlockSize]);
                    }
                    Slot(_count) = record;
                    _count++;
                } else if (_count < _records.Length) {
                    Slot(_count) = record;
                    _count++;
                } else {
                    // Read before it is written over, and counted out before the new one is
                    // counted in: the two can be the same tag, and the order keeps the tally
                    // from dipping through zero and dropping the key.
                    Forget(_records[_head].Tag);
                    _evicted = true;
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
            for (int i = _count - 1; i > 0; i--) {
                ref LogRecord current = ref Slot(i);
                ref LogRecord previous = ref Slot(i - 1);
                if (previous.Sequence <= current.Sequence) {
                    return;
                }
                LogRecord swap = previous;
                previous = current;
                current = swap;
            }
        }

        public void Clear() {
            lock (_lock) {
                if (_blocks != null) {
                    // Dropped rather than wiped: a Clear is how somebody gives the memory of a
                    // long session back, and emptied blocks would keep every byte of it.
                    _blocks.Clear();
                } else {
                    Array.Clear(_records, 0, _records.Length);
                }
                _tags.Clear();
                _evicted = false;
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
            Slot(logicalIndex);

        /// <summary>Where the record at this logical index lives. Caller holds the lock.</summary>
        private ref LogRecord Slot(int logicalIndex) {
            if (_blocks != null) {
                return ref _blocks[logicalIndex >> BlockShift][logicalIndex & BlockMask];
            }
            return ref _records[(_head + logicalIndex) % _records.Length];
        }
    }
}
