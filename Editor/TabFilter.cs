using System;
using System.Collections.Generic;

namespace KenseiLog.Editor {
    /// <summary>
    /// A saved view over the log stream: which tags, levels and channels a tab shows.
    /// Pure serialized data — the matching index lives in <see cref="TabView"/>, because
    /// Unity restores serialized objects without running field initialisers and would leave
    /// non-serialized collections null after a domain reload.
    /// </summary>
    [Serializable]
    public sealed class TabFilter {
        public string Name = "New tab";
        public List<string> Tags = new List<string>();
        public bool ShowLog = true;
        public bool ShowWarning = true;
        public bool ShowError = true;
        public bool ShowDev = true;
        public bool ShowProd = true;
        public string Search = string.Empty;
        public bool Collapse;

        /// <summary>Show only this frame, or -1 for every frame.</summary>
        public int IsolatedFrame = -1;

        public bool Matches(in LogRecord record) {
            if (!LevelAllowed(record.Level) || !ChannelAllowed(record.Channel)) {
                return false;
            }
            if (IsolatedFrame >= 0 && record.Frame != IsolatedFrame) {
                return false;
            }
            if (Tags.Count > 0 && !TagAllowed(record.Tag)) {
                return false;
            }
            // Search deliberately looks at the message only. Tags are a field, not a prefix in
            // the text, so searching for "combat" must never pull in the Combat tag by itself.
            if (!string.IsNullOrEmpty(Search)) {
                if (record.Message == null || record.Message.IndexOf(Search, StringComparison.OrdinalIgnoreCase) < 0) {
                    return false;
                }
            }
            return true;
        }

        private bool LevelAllowed(LogLevel level) {
            switch (level) {
                case LogLevel.Warning:
                    return ShowWarning;
                case LogLevel.Error:
                    return ShowError;
                default:
                    return ShowLog;
            }
        }

        private bool ChannelAllowed(LogChannel channel) =>
            channel == LogChannel.Dev ? ShowDev : ShowProd;

        private bool TagAllowed(string tag) {
            for (int i = 0; i < Tags.Count; i++) {
                if (TagMatches(tag, Tags[i])) {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Exact match, or a dot-separated descendant of the selected tag. The explicit dot
        /// check stops "Net" from also selecting an unrelated "Network" tag.
        /// </summary>
        public static bool TagMatches(string tag, string selected) {
            if (tag.Length < selected.Length) {
                return false;
            }
            if (!tag.StartsWith(selected, StringComparison.Ordinal)) {
                return false;
            }
            return tag.Length == selected.Length || tag[selected.Length] == '.';
        }
    }
}
