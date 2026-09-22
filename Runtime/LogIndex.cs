using System;
using System.Collections.Generic;

namespace KenseiLog {
    /// <summary>
    /// An index over the records a filter accepts: their sequences, in arrival order, with how
    /// many times each row has repeated when the filter collapses them.
    /// <para>
    /// Grows by appending — every incoming record is tested against the filter exactly once.
    /// A full rebuild happens only when the filter itself changes.
    /// </para>
    /// <para>
    /// In Runtime rather than beside the editor window that first needed it, because the in-game
    /// viewer collapses repeats too, and two implementations of the same fold is one more than
    /// this package wants to keep right.
    /// </para>
    /// </summary>
    public sealed class LogIndex {
        public readonly LogFilter Filter;
        public readonly List<long> Sequences = new List<long>();
        public readonly List<int> Repeats = new List<int>();

        private readonly List<CollapseKey> _keys = new List<CollapseKey>();
        private readonly Dictionary<CollapseKey, int> _collapsed = new Dictionary<CollapseKey, int>();

        public LogIndex(LogFilter filter) {
            Filter = filter;
        }

        public int Count => Sequences.Count;

        /// <summary>
        /// Bumped whenever anything a row draws from changes.
        /// <para>
        /// The count cannot stand in for this. Once the ring buffer is full it drops a record
        /// for every record it takes, so the count holds still while the contents move under
        /// it - and a view that repaints on a changed count stops repainting exactly when the
        /// logs are busiest. Collapse has the same problem from the other side: a repeat
        /// changes a row's tally and its record without changing how many rows there are.
        /// </para>
        /// </summary>
        public int Revision { get; private set; }

        /// <summary>
        /// Takes a record and answers which slot now stands for it: the slot appended at the
        /// end, the earlier slot a repeat folded into, or -1 when the filter refused it.
        /// <para>
        /// Reported rather than void because a viewer may keep something of its own beside each
        /// slot - the in-game one caches a formatted row, since it redraws per frame where the
        /// editor window polls fifteen times a second - and it has to know whether to add one or
        /// replace one. Without that it would have to rebuild the lot on every record.
        /// </para>
        /// </summary>
        public int Append(in LogRecord record) {
            if (!Filter.Matches(in record)) {
                return -1;
            }

            if (!Filter.Collapse) {
                Sequences.Add(record.Sequence);
                Repeats.Add(1);
                Revision++;
                return Sequences.Count - 1;
            }

            CollapseKey key = new CollapseKey(record.Tag, record.Message, record.Level);
            if (_collapsed.TryGetValue(key, out int position)) {
                Repeats[position]++;
                // Point the row at the newest occurrence so it stays reachable as the ring
                // rolls, and so jumping to it lands on what just happened.
                Sequences[position] = record.Sequence;
                Revision++;
                return position;
            }

            _collapsed[key] = Sequences.Count;
            _keys.Add(key);
            Sequences.Add(record.Sequence);
            Repeats.Add(1);
            Revision++;
            return Sequences.Count - 1;
        }

        public void Clear() {
            Sequences.Clear();
            Repeats.Clear();
            _keys.Clear();
            _collapsed.Clear();
            Revision++;
        }

        /// <summary>Drops entries whose record the ring buffer has already overwritten.</summary>
        public void PruneBelow(long oldestSequence) {
            PruneBelow(oldestSequence, null, out int _);
        }

        /// <summary>
        /// The same, reporting what went so a viewer can bring anything it keeps beside the
        /// slots along with it.
        /// <para>
        /// Two reports rather than one, because the two branches drop in different shapes and
        /// only one of them is cheap to describe as a list. An uncollapsed view loses a leading
        /// run, so a count says all of it; a collapsed one loses rows from anywhere, so the
        /// survivors are named. Gathering survivors in both cases would turn the common branch
        /// from a walk of what leaves into a walk of what stays.
        /// </para>
        /// <para>
        /// <paramref name="droppedFromFront"/> is -1 when the survivors were named instead.
        /// </para>
        /// </summary>
        public bool PruneBelow(long oldestSequence, List<int> keptSlots, out int droppedFromFront) {
            keptSlots?.Clear();
            if (Filter.Collapse) {
                droppedFromFront = -1;
                return PruneCollapsed(oldestSequence, keptSlots);
            }

            int drop = 0;
            while (drop < Sequences.Count && Sequences[drop] < oldestSequence) {
                drop++;
            }
            droppedFromFront = drop;
            if (drop == 0) {
                return false;
            }

            Sequences.RemoveRange(0, drop);
            Repeats.RemoveRange(0, drop);
            Revision++;
            return true;
        }

        /// <summary>
        /// The same, for a collapsed view, where the list is not in sequence order.
        /// <para>
        /// A repeat points its row at the newest occurrence, which writes a late sequence into
        /// an early slot - so a scan of the leading run stops at that row and leaves everything
        /// expired behind it. The rows stay, their records do not, and the tab fills with a
        /// band of "(record expired)" that nothing ever clears. Every row is tested instead;
        /// this runs once per poll, against a list bounded by the ring buffer.
        /// </para>
        /// </summary>
        private bool PruneCollapsed(long oldestSequence, List<int> keptSlots) {
            int kept = 0;
            for (int i = 0; i < Sequences.Count; i++) {
                if (Sequences[i] < oldestSequence) {
                    continue;
                }
                keptSlots?.Add(i);
                Sequences[kept] = Sequences[i];
                Repeats[kept] = Repeats[i];
                _keys[kept] = _keys[i];
                kept++;
            }
            if (kept == Sequences.Count) {
                keptSlots?.Clear();
                return false;
            }

            Sequences.RemoveRange(kept, Sequences.Count - kept);
            Repeats.RemoveRange(kept, Repeats.Count - kept);
            _keys.RemoveRange(kept, _keys.Count - kept);

            _collapsed.Clear();
            for (int i = 0; i < _keys.Count; i++) {
                _collapsed[_keys[i]] = i;
            }
            Revision++;
            return true;
        }

        private readonly struct CollapseKey : IEquatable<CollapseKey> {
            private readonly string _tag;
            private readonly string _message;
            private readonly LogLevel _level;

            public CollapseKey(string tag, string message, LogLevel level) {
                _tag = tag;
                _message = message;
                _level = level;
            }

            public bool Equals(CollapseKey other) =>
                _level == other._level && _tag == other._tag && _message == other._message;

            public override bool Equals(object obj) =>
                obj is CollapseKey other && Equals(other);

            public override int GetHashCode() {
                unchecked {
                    int hash = _tag.GetHashCode();
                    hash = (hash * 397) ^ (_message?.GetHashCode() ?? 0);
                    return (hash * 397) ^ (int)_level;
                }
            }
        }
    }
}
