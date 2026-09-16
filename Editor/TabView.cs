using System;
using System.Collections.Generic;

namespace KenseiLog.Editor {
    /// <summary>
    /// Runtime index for one tab: the sequences that passed its filter, in arrival order.
    /// <para>
    /// Grows by appending — every incoming record is tested against the filter exactly once.
    /// A full rebuild happens only when the filter itself changes.
    /// </para>
    /// </summary>
    public sealed class TabView {
        public readonly LogFilter Filter;
        public readonly List<long> Sequences = new List<long>();
        public readonly List<int> Repeats = new List<int>();

        private readonly List<CollapseKey> _keys = new List<CollapseKey>();
        private readonly Dictionary<CollapseKey, int> _collapsed = new Dictionary<CollapseKey, int>();

        public TabView(LogFilter filter) {
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

        public void Append(in LogRecord record) {
            if (!Filter.Matches(in record)) {
                return;
            }

            if (!Filter.Collapse) {
                Sequences.Add(record.Sequence);
                Repeats.Add(1);
                Revision++;
                return;
            }

            CollapseKey key = new CollapseKey(record.Tag, record.Message, record.Level);
            if (_collapsed.TryGetValue(key, out int position)) {
                Repeats[position]++;
                // Point the row at the newest occurrence so it stays reachable as the ring
                // rolls, and so jumping to it lands on what just happened.
                Sequences[position] = record.Sequence;
                Revision++;
                return;
            }

            _collapsed[key] = Sequences.Count;
            _keys.Add(key);
            Sequences.Add(record.Sequence);
            Repeats.Add(1);
            Revision++;
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
            if (Filter.Collapse) {
                PruneCollapsed(oldestSequence);
                return;
            }

            int drop = 0;
            while (drop < Sequences.Count && Sequences[drop] < oldestSequence) {
                drop++;
            }
            if (drop == 0) {
                return;
            }

            Sequences.RemoveRange(0, drop);
            Repeats.RemoveRange(0, drop);
            Revision++;
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
        private void PruneCollapsed(long oldestSequence) {
            int kept = 0;
            for (int i = 0; i < Sequences.Count; i++) {
                if (Sequences[i] < oldestSequence) {
                    continue;
                }
                Sequences[kept] = Sequences[i];
                Repeats[kept] = Repeats[i];
                _keys[kept] = _keys[i];
                kept++;
            }
            if (kept == Sequences.Count) {
                return;
            }

            Sequences.RemoveRange(kept, Sequences.Count - kept);
            Repeats.RemoveRange(kept, Repeats.Count - kept);
            _keys.RemoveRange(kept, _keys.Count - kept);

            _collapsed.Clear();
            for (int i = 0; i < _keys.Count; i++) {
                _collapsed[_keys[i]] = i;
            }
            Revision++;
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
