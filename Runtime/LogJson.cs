using System.Globalization;
using System.Text;

namespace KenseiLog {
    /// <summary>
    /// Writes records as one JSON object per line.
    /// <para>
    /// A delimited format would be shorter, but stack traces are multi-line and any parser
    /// reading them back would lose the plot on the first one. JSON handles the escaping.
    /// </para>
    /// <para>
    /// Written by hand rather than through JsonUtility: that would need a serializable object
    /// per record, which is an allocation on a path that runs whenever anything is logged.
    /// Reading back is editor-only, and is by hand too for the lines this writes -
    /// LogSessionReader.WrittenLine knows its keys and value forms, and leaves anything else to
    /// JsonUtility. A key added here reads correctly without it, and slowly until it learns it.
    /// </para>
    /// </summary>
    public static class LogJson {
        public const string SessionKey = "session";

        /// <summary>
        /// Layout of the lines in a log file, written into every session header. A reader that
        /// meets a number it does not know can say so; without one an older reader would parse
        /// a newer file into plausible nonsense and report nothing.
        /// <para>
        /// Adding a key does not move it: JsonUtility ignores what it does not know, so an older
        /// reader takes such a file as it always did. Only a change it would misread does.
        /// </para>
        /// </summary>
        public const int SchemaVersion = 1;

        public static void AppendRecord(StringBuilder builder, in LogRecord record) {
            builder.Append('{');

            AppendKey(builder, "t");
            // Invariant culture is not optional here: under a locale that uses a decimal comma
            // the number would be written as 128,44 and the line would stop being valid JSON.
            builder.Append(record.TimeMs.ToString("0.###", CultureInfo.InvariantCulture));

            builder.Append(",");
            AppendKey(builder, "sq");
            builder.Append(record.Sequence.ToString(CultureInfo.InvariantCulture));

            builder.Append(",");
            AppendKey(builder, "f");
            builder.Append(record.Frame.ToString(CultureInfo.InvariantCulture));

            builder.Append(",");
            AppendKey(builder, "lv");
            builder.Append(((int)record.Level).ToString(CultureInfo.InvariantCulture));

            builder.Append(",");
            AppendKey(builder, "ch");
            builder.Append(((int)record.Channel).ToString(CultureInfo.InvariantCulture));

            if (record.Captured) {
                builder.Append(",");
                AppendKey(builder, "cap");
                builder.Append("true");
            }

            builder.Append(",");
            AppendKey(builder, "tag");
            AppendString(builder, record.Tag);

            builder.Append(",");
            AppendKey(builder, "msg");
            AppendString(builder, record.Message);

            if (!string.IsNullOrEmpty(record.File)) {
                builder.Append(",");
                AppendKey(builder, "file");
                AppendString(builder, record.File);

                builder.Append(",");
                AppendKey(builder, "ln");
                builder.Append(record.Line.ToString(CultureInfo.InvariantCulture));
            }

            if (record.ContextInstanceId != 0) {
                // An instance id means something only inside the editor session that issued it.
                // Written all the same: the reader knows whether the file is that session's own.
                builder.Append(",");
                AppendKey(builder, "ctx");
                builder.Append(record.ContextInstanceId.ToString(CultureInfo.InvariantCulture));
            }

            if (!string.IsNullOrEmpty(record.StackTrace)) {
                builder.Append(",");
                AppendKey(builder, "st");
                AppendString(builder, record.StackTrace);
            }

            builder.Append('}');
        }

        public static void AppendSessionHeader(StringBuilder builder, string sessionId, string app,
                                               string unity, string platform, string device, string startedUtc) {
            builder.Append('{');
            AppendKey(builder, SessionKey);
            AppendString(builder, sessionId);
            builder.Append(",");
            AppendKey(builder, "v");
            builder.Append(SchemaVersion.ToString(CultureInfo.InvariantCulture));
            builder.Append(",");
            AppendKey(builder, "app");
            AppendString(builder, app);
            builder.Append(",");
            AppendKey(builder, "unity");
            AppendString(builder, unity);
            builder.Append(",");
            AppendKey(builder, "platform");
            AppendString(builder, platform);
            builder.Append(",");
            AppendKey(builder, "device");
            AppendString(builder, device);
            builder.Append(",");
            AppendKey(builder, "started");
            AppendString(builder, startedUtc);
            builder.Append('}');
        }

        private static void AppendKey(StringBuilder builder, string key) {
            builder.Append('"').Append(key).Append("\":");
        }

        private static void AppendString(StringBuilder builder, string value) {
            if (value == null) {
                builder.Append("null");
                return;
            }

            builder.Append('"');
            for (int i = 0; i < value.Length; i++) {
                char c = value[i];
                switch (c) {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (c < ' ') {
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        } else if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) {
                            // A complete pair goes through as it is - one character above the
                            // basic plane, which is what an emoji in a log message is.
                            builder.Append(c).Append(value[i + 1]);
                            i++;
                        } else if (char.IsSurrogate(c)) {
                            // Half a pair, from a message cut mid-character. Written as an
                            // escape because the UTF-8 encoder turns a lone surrogate into
                            // U+FFFD - the byte that says "something was here" and loses what.
                            // JSON permits the escape, so the reader gets the original code
                            // unit back and can decide for itself.
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        } else {
                            builder.Append(c);
                        }
                        break;
                }
            }
            builder.Append('"');
        }
    }
}
