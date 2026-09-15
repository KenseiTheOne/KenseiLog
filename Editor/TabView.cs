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

        public void Append(in LogRecord record) {
            if (!Filter.Matches(in record)) {
                return;
            }

            if (!Filter.Collapse) {
                Sequences.Add(record.Sequence);
                Repeats.Add(1);
                return;
            }

            CollapseKey key = new CollapseKey(record.Tag, record.Message, record.Level);
            if (_collapsed.TryGetValue(key, out int position)) {
                Repeats[position]++;
                // Point the row at the newest occurrence so it stays reachable as the ring
                // rolls, and so jumping to it lands on what just happened.
                Sequences[position] = record.Sequence;
                return;
            }

            _collapsed[key] = Sequences.Count;
            _keys.Add(key);
            Sequences.Add(record.Sequence);
            Repeats.Add(1);
        }

        public void Clear() {
            Sequences.Clear();
            Repeats.Clear();
            _keys.Clear();
            _collapsed.Clear();
        }

        /// <summary>Drops entries whose record the ring buffer has already overwritten.</summary>
        public void PruneBelow(long oldestSequence) {
            int drop = 0;
            while (drop < Sequences.Count && Sequences[drop] < oldestSequence) {
                drop++;
            }
            if (drop == 0) {
                return;
            }

            Sequences.RemoveRange(0, drop);
            Repeats.RemoveRange(0, drop);
            if (!Filter.Collapse) {
                return;
            }

            _keys.RemoveRange(0, drop);
            _collapsed.Clear();
            for (int i = 0; i < _keys.Count; i++) {
                _collapsed[_keys[i]] = i;
            }
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
