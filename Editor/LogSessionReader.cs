using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace KenseiLog.Editor {
    /// <summary>A session loaded from a JSONL file written by <see cref="FileSink"/>.</summary>
    public sealed class LogSession {
        public string FileName;
        public string App;
        public string Unity;
        public string Platform;
        public string Device;
        public string StartedUtc;
        public int SkippedLines;

        /// <summary>Schema of the file, or 0 for one written before the field existed.</summary>
        public int SchemaVersion;

        /// <summary>True when the first line was a session header this reader understood.</summary>
        public bool HasHeader;

        public LogRingBuffer Buffer;

        public string Describe() {
            if (string.IsNullOrEmpty(App)) {
                return FileName;
            }
            return FileName + "  ·  " + App + "  ·  " + Platform + "  ·  " + Device + "  ·  " + StartedUtc;
        }
    }

    /// <summary>
    /// Reads a log file back into the same shape a live run produces, so the window can show
    /// a tester's file with the same tabs, tags and filters.
    /// <para>
    /// A record line is read by a parser for the one shape <see cref="LogJson"/> writes, and
    /// anything else goes to JsonUtility. See <see cref="WrittenLine"/> for why there are two.
    /// </para>
    /// </summary>
    public static class LogSessionReader {
        public static bool TryRead(string path, out LogSession session, out string error) {
            session = null;

            List<LogRecord> records = new List<LogRecord>();
            LogSession loaded = new LogSession { FileName = Path.GetFileName(path) };
            SharedStrings shared = new SharedStrings();

            try {
                // FileShare.ReadWrite is the point: the file is very often the one the running
                // app still has open for writing, and anything stricter is refused outright.
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader reader = new StreamReader(stream)) {
                    bool first = true;
                    string line;
                    while ((line = reader.ReadLine()) != null) {
                        if (line.Length == 0) {
                            continue;
                        }
                        if (first) {
                            first = false;
                            if (ReadHeader(line, loaded)) {
                                continue;
                            }
                        }
                        // A torn final line is expected when the writer is still going; it is
                        // counted and skipped rather than failing the whole file.
                        if (TryReadRecord(line, fromThisSession: false, shared, out LogRecord record)) {
                            records.Add(record);
                        } else {
                            loaded.SkippedLines++;
                        }
                    }
                }
            } catch (Exception exception) {
                error = exception.Message;
                return false;
            }

            if (loaded.SchemaVersion > LogJson.SchemaVersion) {
                error = "Written by a newer KenseiLog (file schema " + loaded.SchemaVersion +
                        ", this one reads " + LogJson.SchemaVersion + "). Update the package to open it.";
                return false;
            }

            // A file holding nothing but its header still opens. That is the build that died
            // during startup - the case this package exists for - and refusing it left the only
            // evidence of the death unreadable, while its header names the device it died on.
            if (records.Count == 0 && !loaded.HasHeader) {
                error = "No readable records in the file" +
                        (loaded.SkippedLines > 0 ? " (" + loaded.SkippedLines + " unparsable lines)." : ".");
                return false;
            }

            // Records arrive in order, so appending them leaves the buffer sorted by sequence,
            // which is what lookups rely on.
            loaded.Buffer = new LogRingBuffer(Math.Max(1, records.Count));
            for (int i = 0; i < records.Count; i++) {
                loaded.Buffer.Add(records[i]);
            }

            session = loaded;
            error = null;
            return true;
        }

        /// <summary>
        /// Reads every record of a file, for seeding rather than for viewing, and returns how
        /// many it added. The header and a torn final line simply fail to parse and are passed
        /// over; a file that cannot be opened adds nothing.
        /// <para>
        /// The one path that keeps a record's related object, since the file is the editor's
        /// own: an instance id means something only inside the session that issued it.
        /// </para>
        /// </summary>
        public static int ReadForSeeding(string path, List<LogRecord> into) {
            int before = into.Count;
            SharedStrings shared = new SharedStrings();
            try {
                // ReadWrite for the same reason as above: this file usually has a writer.
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader reader = new StreamReader(stream)) {
                    string line;
                    while ((line = reader.ReadLine()) != null) {
                        if (line.Length > 0 && TryReadRecord(line, fromThisSession: true, shared, out LogRecord record)) {
                            into.Add(record);
                        }
                    }
                }
            } catch (Exception) {
                // What was read before the failure stands: a file that went away part way is
                // still worth the records it gave.
            }
            return into.Count - before;
        }

        private static bool ReadHeader(string line, LogSession session) {
            if (line.IndexOf("\"" + LogJson.SessionKey + "\"", StringComparison.Ordinal) < 0) {
                return false;
            }
            try {
                HeaderDto dto = JsonUtility.FromJson<HeaderDto>(line);
                session.App = dto.app;
                session.Unity = dto.unity;
                session.Platform = dto.platform;
                session.Device = dto.device;
                session.StartedUtc = dto.started;
                session.SchemaVersion = dto.v;
                session.HasHeader = true;
                return true;
            } catch (Exception) {
                // A file truncated mid-header is still worth showing for its records.
                return false;
            }
        }

        /// <summary>
        /// The related object is kept only for this editor's own file. An instance id means
        /// something inside the session that issued it and nowhere else: from another machine's
        /// build it would resolve here to whatever happens to hold that number, and Ping would
        /// jump to an unrelated object - worse than a button that does nothing.
        /// <para>
        /// The tag, the call site's path and the stack trace go through <paramref name="shared"/>;
        /// see <see cref="SharedStrings"/>.
        /// </para>
        /// </summary>
        private static bool TryReadRecord(string line, bool fromThisSession, SharedStrings shared,
                                          out LogRecord record) =>
            WrittenLine.TryRead(line, fromThisSession, shared, out record) ||
            TryReadAnyJson(line, fromThisSession, shared, out record);

        /// <summary>
        /// Any line JSON can express, through JsonUtility: a file from a schema this build does
        /// not know, one edited by hand, or a torn last line, which it refuses like anything
        /// else that does not parse.
        /// </summary>
        private static bool TryReadAnyJson(string line, bool fromThisSession, SharedStrings shared,
                                           out LogRecord record) {
            record = default;
            try {
                RecordDto dto = JsonUtility.FromJson<RecordDto>(line);
                if (dto == null || dto.tag == null) {
                    return false;
                }
                record = new LogRecord(
                    dto.sq,
                    shared.Share(dto.tag),
                    dto.msg,
                    (LogLevel)dto.lv,
                    (LogChannel)dto.ch,
                    dto.t,
                    dto.f,
                    string.IsNullOrEmpty(dto.file) ? null : shared.Share(dto.file),
                    dto.ln,
                    string.IsNullOrEmpty(dto.st) ? null : shared.Share(dto.st),
                    fromThisSession ? dto.ctx : 0,
                    dto.cap);
                return true;
            } catch (Exception) {
                return false;
            }
        }

        /// <summary>
        /// One string for each distinct tag, call site and stack trace in a file. A record logged
        /// live shares the string its call site compiled in; one parsed from a line brought a copy
        /// of its own, and across a session of them that was half of what the records cost.
        /// Messages are left alone - they are mostly unique, and a table of them would cost more
        /// than it saved.
        /// <para>
        /// Looked up by the text of the line as it stands, escapes and all, so that a value seen
        /// before costs neither an allocation nor an unescaping: a Windows call site is all
        /// backslashes, each written as two, on every record that has one. Hashed and compared by
        /// loops of its own, for the reason <see cref="WrittenLine"/> gives - the class library
        /// looks at characters ten times slower in the editor's Debug code mode.
        /// </para>
        /// </summary>
        private sealed class SharedStrings {
            private int[] _hashes = new int[256];
            private string[] _raw = new string[256];
            private string[] _values = new string[256];
            private int _count;

            // For what JsonUtility has already decoded, where there is no raw text to go by.
            private readonly Dictionary<string, string> _decoded = new Dictionary<string, string>(StringComparer.Ordinal);

            public string Share(string value) {
                if (_decoded.TryGetValue(value, out string existing)) {
                    return existing;
                }
                _decoded.Add(value, value);
                return value;
            }

            /// <summary>The value read before from this raw text, or null when there is none.</summary>
            public string Find(string line, int start, int end, int hash) {
                int mask = _raw.Length - 1;
                for (int slot = hash & mask; _raw[slot] != null; slot = (slot + 1) & mask) {
                    if (_hashes[slot] == hash && Same(_raw[slot], line, start, end)) {
                        return _values[slot];
                    }
                }
                return null;
            }

            public void Add(string raw, int hash, string value) {
                if ((_count + 1) * 2 > _raw.Length) {
                    Grow();
                }
                Place(hash, raw, value);
                _count++;
            }

            private void Place(int hash, string raw, string value) {
                int mask = _raw.Length - 1;
                int slot = hash & mask;
                while (_raw[slot] != null) {
                    slot = (slot + 1) & mask;
                }
                _hashes[slot] = hash;
                _raw[slot] = raw;
                _values[slot] = value;
            }

            private void Grow() {
                int[] hashes = _hashes;
                string[] raw = _raw;
                string[] values = _values;
                _hashes = new int[hashes.Length * 2];
                _raw = new string[raw.Length * 2];
                _values = new string[values.Length * 2];
                for (int i = 0; i < raw.Length; i++) {
                    if (raw[i] != null) {
                        Place(hashes[i], raw[i], values[i]);
                    }
                }
            }

            private static bool Same(string raw, string line, int start, int end) {
                if (raw.Length != end - start) {
                    return false;
                }
                for (int i = 0; i < raw.Length; i++) {
                    if (raw[i] != line[start + i]) {
                        return false;
                    }
                }
                return true;
            }
        }

        /// <summary>
        /// Reads a record line made of the keys and the value forms <see cref="LogJson.AppendRecord"/>
        /// writes, and answers false for anything else - which then goes to JsonUtility, so a line
        /// this does not recognise comes back as it always did.
        /// <para>
        /// For speed first. A recompile reads the whole editor session back, and JsonUtility was
        /// nearly all of what that cost. Written for the editor's default Debug code mode, and
        /// measured there, because what is fast in it is not what is fast anywhere else: the
        /// class library's own scans slow down by ten times - IndexOf over two hundred characters
        /// took 12.6us against 1.2us in Release - and so does every call that looks at characters,
        /// CompareOrdinal among them, while a plain loop over the string's indexer stays cheap.
        /// Every character is looked at by a loop here and nothing else; the library is used only
        /// to copy, which is a block move in either mode. Crossing strings with IndexOf made this
        /// slower than JsonUtility, and comparing keys with CompareOrdinal did before that.
        /// </para>
        /// <para>
        /// And for the escapes this package writes on purpose, which JsonUtility gets wrong. A
        /// lone surrogate - half an emoji, from a message cut mid-character - is written as
        /// \uXXXX so that the reader gets the code unit back rather than the U+FFFD the encoder
        /// would have left, and JsonUtility threw on a high one, losing the record, and returned
        /// the whole message empty for a low one. An escaped NUL cut the message off where it
        /// stood. Each escape here is the code unit it names, whatever that is.
        /// </para>
        /// <para>
        /// Strict on purpose: no whitespace, no key it does not know, no key twice, no raw control
        /// character, no exponent, no leading zero, no fraction in an integer. Each of those is a
        /// line from somewhere else, and somewhere else is what JsonUtility is for. The order of
        /// the keys is not checked; nothing is read differently for it.
        /// </para>
        /// </summary>
        private static class WrittenLine {
            private const int Time = 1 << 0;
            private const int Sequence = 1 << 1;
            private const int Frame = 1 << 2;
            private const int Level = 1 << 3;
            private const int Channel = 1 << 4;
            private const int Captured = 1 << 5;
            private const int Tag = 1 << 6;
            private const int Message = 1 << 7;
            private const int File = 1 << 8;
            private const int Line = 1 << 9;
            private const int Context = 1 << 10;
            private const int StackTrace = 1 << 11;

            /// <summary>What the writer puts on every record; a line without one is not its.</summary>
            private const int Always = Time | Sequence | Frame | Level | Channel | Tag | Message;

            /// <summary>
            /// Past fifteen digits a mantissa is no longer exact in a double, and the division
            /// below stops being the correctly rounded answer that JsonUtility gives.
            /// </summary>
            private const int MaxTimeDigits = 15;

            private static readonly double[] _powersOfTen = {
                1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11, 1e12, 1e13, 1e14, 1e15
            };

            [ThreadStatic] private static StringBuilder _unescaped;

            public static bool TryRead(string line, bool fromThisSession, SharedStrings shared,
                                       out LogRecord record) {
                record = default;
                int length = line.Length;
                // Nearly every torn last line fails here rather than half way through.
                if (length < 2 || line[0] != '{' || line[length - 1] != '}') {
                    return false;
                }

                double time = 0.0;
                long sequence = 0;
                long frame = 0;
                long level = 0;
                long channel = 0;
                long lineNumber = 0;
                long context = 0;
                bool captured = false;
                string tag = null;
                string message = null;
                string file = null;
                string stackTrace = null;
                int seen = 0;
                int at = 1;

                while (true) {
                    if (at >= length || line[at] != '"') {
                        return false;
                    }
                    int keyStart = at + 1;
                    int keyEnd = keyStart;
                    while (keyEnd < length && keyEnd - keyStart <= 4 && line[keyEnd] != '"') {
                        keyEnd++;
                    }
                    if (keyEnd + 1 >= length || line[keyEnd] != '"' || line[keyEnd + 1] != ':') {
                        return false;
                    }
                    int field = FieldOf(line, keyStart, keyEnd - keyStart);
                    if (field == 0 || (seen & field) != 0) {
                        return false;
                    }
                    seen |= field;
                    at = keyEnd + 2;

                    bool read;
                    switch (field) {
                        case Time:
                            read = ReadTime(line, ref at, out time);
                            break;
                        case Sequence:
                            read = ReadInteger(line, ref at, out sequence);
                            break;
                        case Frame:
                            read = ReadInteger(line, ref at, out frame);
                            break;
                        case Level:
                            read = ReadInteger(line, ref at, out level);
                            break;
                        case Channel:
                            read = ReadInteger(line, ref at, out channel);
                            break;
                        case Captured:
                            read = ReadBool(line, ref at, out captured);
                            break;
                        case Tag:
                            read = ReadSharedString(line, ref at, shared, out tag);
                            break;
                        case Message:
                            read = ReadString(line, ref at, out message);
                            break;
                        case File:
                            read = ReadSharedString(line, ref at, shared, out file);
                            break;
                        case Line:
                            read = ReadInteger(line, ref at, out lineNumber);
                            break;
                        case Context:
                            read = ReadInteger(line, ref at, out context);
                            break;
                        default:
                            read = ReadSharedString(line, ref at, shared, out stackTrace);
                            break;
                    }
                    if (!read || at >= length) {
                        return false;
                    }

                    char next = line[at++];
                    if (next == '}') {
                        break;
                    }
                    if (next != ',') {
                        return false;
                    }
                }

                // The writer's int fields, as ints: JsonUtility wraps a value past the range
                // rather than refusing it, and a line holding one is not the writer's anyway.
                if (at != length || (seen & Always) != Always ||
                    !FitsInt(frame) || !FitsInt(level) || !FitsInt(channel) || !FitsInt(lineNumber) || !FitsInt(context)) {
                    return false;
                }

                record = new LogRecord(
                    sequence,
                    tag,
                    message,
                    (LogLevel)level,
                    (LogChannel)channel,
                    time,
                    (int)frame,
                    string.IsNullOrEmpty(file) ? null : file,
                    (int)lineNumber,
                    string.IsNullOrEmpty(stackTrace) ? null : stackTrace,
                    fromThisSession ? (int)context : 0,
                    captured);
                return true;
            }

            private static bool FitsInt(long value) =>
                value >= int.MinValue && value <= int.MaxValue;

            /// <summary>The field a key names, or 0 for any other.</summary>
            private static int FieldOf(string line, int start, int length) {
                if (length < 1 || length > 4) {
                    return 0;
                }
                char first = line[start];
                if (length == 1) {
                    return first == 't' ? Time : first == 'f' ? Frame : 0;
                }
                char second = line[start + 1];
                if (length == 2) {
                    switch (first) {
                        case 's':
                            return second == 'q' ? Sequence : second == 't' ? StackTrace : 0;
                        case 'l':
                            return second == 'v' ? Level : second == 'n' ? Line : 0;
                        case 'c':
                            return second == 'h' ? Channel : 0;
                        default:
                            return 0;
                    }
                }
                char third = line[start + 2];
                if (length == 3) {
                    if (first == 't' && second == 'a' && third == 'g') {
                        return Tag;
                    }
                    if (first == 'm' && second == 's' && third == 'g') {
                        return Message;
                    }
                    if (first == 'c' && second == 'a' && third == 'p') {
                        return Captured;
                    }
                    if (first == 'c' && second == 't' && third == 'x') {
                        return Context;
                    }
                    return 0;
                }
                return first == 'f' && second == 'i' && third == 'l' && line[start + 3] == 'e' ? File : 0;
            }

            /// <summary>
            /// An integer as the writer writes one: an optional minus, then digits with no
            /// leading zero. What follows it is the caller's to check - a fraction or an exponent
            /// leaves a character there that is neither a comma nor a brace.
            /// </summary>
            private static bool ReadInteger(string line, ref int at, out long value) {
                value = 0;
                int length = line.Length;
                bool negative = at < length && line[at] == '-';
                if (negative) {
                    at++;
                }
                int start = at;
                while (at < length) {
                    int digit = line[at] - '0';
                    if (digit < 0 || digit > 9) {
                        break;
                    }
                    // A magnitude past long.MaxValue is past any number the logger issues.
                    if (value > (long.MaxValue - digit) / 10) {
                        return false;
                    }
                    value = value * 10 + digit;
                    at++;
                }
                int digits = at - start;
                if (digits == 0 || (digits > 1 && line[start] == '0')) {
                    return false;
                }
                if (negative) {
                    value = -value;
                }
                return true;
            }

            /// <summary>
            /// A time as the writer writes one, with "0.###": digits, and up to three more after a
            /// point - read here with any number up to fifteen in all. The digits make an exact
            /// integer and the point a power of ten, so the one division is correctly rounded,
            /// which is what JsonUtility's own reading is.
            /// </summary>
            private static bool ReadTime(string line, ref int at, out double value) {
                value = 0.0;
                int length = line.Length;
                bool negative = at < length && line[at] == '-';
                if (negative) {
                    at++;
                }

                long mantissa = 0;
                int start = at;
                while (at < length) {
                    int digit = line[at] - '0';
                    if (digit < 0 || digit > 9) {
                        break;
                    }
                    mantissa = mantissa * 10 + digit;
                    at++;
                }
                int whole = at - start;
                if (whole == 0 || whole > MaxTimeDigits || (whole > 1 && line[start] == '0')) {
                    return false;
                }

                int decimals = 0;
                if (at < length && line[at] == '.') {
                    at++;
                    while (at < length) {
                        int digit = line[at] - '0';
                        if (digit < 0 || digit > 9) {
                            break;
                        }
                        if (whole + decimals == MaxTimeDigits) {
                            return false;
                        }
                        mantissa = mantissa * 10 + digit;
                        decimals++;
                        at++;
                    }
                    if (decimals == 0) {
                        return false;
                    }
                }

                value = mantissa / _powersOfTen[decimals];
                if (negative) {
                    value = -value;
                }
                return true;
            }

            private static bool ReadBool(string line, ref int at, out bool value) {
                value = false;
                if (at + 4 <= line.Length && line[at] == 't' && line[at + 1] == 'r' && line[at + 2] == 'u' && line[at + 3] == 'e') {
                    value = true;
                    at += 4;
                    return true;
                }
                if (at + 5 <= line.Length && line[at] == 'f' && line[at + 1] == 'a' && line[at + 2] == 'l' &&
                    line[at + 3] == 's' && line[at + 4] == 'e') {
                    at += 5;
                    return true;
                }
                return false;
            }

            /// <summary>
            /// A quoted string, unescaped. Cut straight out of the line when it holds no escape,
            /// which is most of them; built up a run at a time only when it does.
            /// </summary>
            private static bool ReadString(string line, ref int at, out string value) {
                value = null;
                int length = line.Length;
                if (ReadNull(line, ref at, out value)) {
                    return true;
                }
                if (at >= length || line[at] != '"') {
                    return false;
                }
                int start = at + 1;
                for (int i = start; i < length; i++) {
                    char c = line[i];
                    if (c == '"') {
                        value = line.Substring(start, i - start);
                        at = i + 1;
                        return true;
                    }
                    if (c == '\\') {
                        return ReadEscapedString(line, start, i, ref at, out value);
                    }
                    if (c < ' ') {
                        return false;
                    }
                }
                return false;
            }

            /// <summary>
            /// A quoted string that recurs from record to record, found in <paramref name="shared"/>
            /// by its raw text and read properly only the first time. The scan steps over whatever
            /// follows a backslash, so an escaped quote does not end it; what it steps over is
            /// checked only when the string is first read.
            /// </summary>
            private static bool ReadSharedString(string line, ref int at, SharedStrings shared, out string value) {
                value = null;
                int length = line.Length;
                if (ReadNull(line, ref at, out value)) {
                    return true;
                }
                if (at >= length || line[at] != '"') {
                    return false;
                }
                int start = at + 1;
                int hash = 0;
                bool escaped = false;
                int i = start;
                while (true) {
                    if (i >= length) {
                        return false;
                    }
                    char c = line[i];
                    if (c == '"') {
                        break;
                    }
                    if (c < ' ') {
                        return false;
                    }
                    if (c == '\\') {
                        escaped = true;
                        hash = hash * 31 + c;
                        i++;
                        if (i >= length) {
                            return false;
                        }
                        c = line[i];
                    }
                    hash = hash * 31 + c;
                    i++;
                }

                value = shared.Find(line, start, i, hash);
                if (value == null) {
                    string raw = line.Substring(start, i - start);
                    if (escaped) {
                        int opening = start - 1;
                        if (!ReadString(line, ref opening, out value)) {
                            return false;
                        }
                    } else {
                        value = raw;
                    }
                    // Through the table of decoded values as well, so that one value spelled two
                    // ways - an escape the writer did not need - or read once by JsonUtility is
                    // still one string. Asked once per spelling, never per record.
                    value = shared.Share(value);
                    shared.Add(raw, hash, value);
                }
                at = i + 1;
                return true;
            }

            /// <summary>
            /// The same, for a string holding at least one escape: each run between escapes is
            /// copied across whole, and each escape is the one character it stands for.
            /// </summary>
            private static bool ReadEscapedString(string line, int start, int firstEscape, ref int at, out string value) {
                value = null;
                StringBuilder builder = _unescaped ?? (_unescaped = new StringBuilder(256));
                builder.Length = 0;
                builder.Append(line, start, firstEscape - start);
                int length = line.Length;

                int i = firstEscape;
                while (i < length) {
                    int run = i;
                    while (i < length) {
                        char c = line[i];
                        if (c == '"' || c == '\\') {
                            break;
                        }
                        if (c < ' ') {
                            return false;
                        }
                        i++;
                    }
                    if (i > run) {
                        builder.Append(line, run, i - run);
                    }
                    if (i >= length) {
                        return false;
                    }
                    if (line[i] == '"') {
                        value = builder.ToString();
                        at = i + 1;
                        return true;
                    }

                    if (i + 1 >= length) {
                        return false;
                    }
                    char escape = line[i + 1];
                    i += 2;
                    switch (escape) {
                        case '"':
                            builder.Append('"');
                            break;
                        case '\\':
                            builder.Append('\\');
                            break;
                        case '/':
                            builder.Append('/');
                            break;
                        case 'n':
                            builder.Append('\n');
                            break;
                        case 'r':
                            builder.Append('\r');
                            break;
                        case 't':
                            builder.Append('\t');
                            break;
                        case 'b':
                            builder.Append('\b');
                            break;
                        case 'f':
                            builder.Append('\f');
                            break;
                        case 'u':
                            if (!ReadHex(line, i, out char unit)) {
                                return false;
                            }
                            builder.Append(unit);
                            i += 4;
                            break;
                        default:
                            return false;
                    }
                }
                return false;
            }

            /// <summary>
            /// The writer's null, which it writes for a null string. Read as JsonUtility has always
            /// read it, empty: a record logged through Log never has a null message or tag, since
            /// LogCore fills both in, so only one built by hand can carry it - and a viewer given a
            /// null would throw where an empty string reads as nothing.
            /// </summary>
            private static bool ReadNull(string line, ref int at, out string value) {
                value = null;
                if (at + 4 > line.Length || line[at] != 'n' || line[at + 1] != 'u' || line[at + 2] != 'l' || line[at + 3] != 'l') {
                    return false;
                }
                value = string.Empty;
                at += 4;
                return true;
            }

            private static bool ReadHex(string line, int at, out char unit) {
                unit = '\0';
                if (at + 4 > line.Length) {
                    return false;
                }
                int code = 0;
                for (int i = at; i < at + 4; i++) {
                    char c = line[i];
                    int digit = c >= '0' && c <= '9' ? c - '0'
                              : c >= 'a' && c <= 'f' ? c - 'a' + 10
                              : c >= 'A' && c <= 'F' ? c - 'A' + 10
                              : -1;
                    if (digit < 0) {
                        return false;
                    }
                    code = code * 16 + digit;
                }
                unit = (char)code;
                return true;
            }
        }

        [Serializable]
        private sealed class RecordDto {
            public double t;
            public long sq;
            public int f;
            public int lv;
            public int ch;
            public bool cap;
            public string tag;
            public string msg;
            public string file;
            public int ln;
            public int ctx;
            public string st;
        }

        [Serializable]
        private sealed class HeaderDto {
            public string session;
            public int v;
            public string app;
            public string unity;
            public string platform;
            public string device;
            public string started;
        }
    }
}
