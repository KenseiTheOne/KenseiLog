using System;
using System.Collections.Generic;
using System.IO;
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
    /// Parsing goes through JsonUtility rather than the hand-written path used for writing:
    /// this runs once per file, where correctness matters more than allocations.
    /// </para>
    /// </summary>
    public static class LogSessionReader {
        public static bool TryRead(string path, out LogSession session, out string error) {
            session = null;

            List<LogRecord> records = new List<LogRecord>();
            LogSession loaded = new LogSession { FileName = Path.GetFileName(path) };
            Dictionary<string, string> shared = new Dictionary<string, string>(StringComparer.Ordinal);

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
            Dictionary<string, string> shared = new Dictionary<string, string>(StringComparer.Ordinal);
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
        /// The tag, the call site's path and the stack trace go through <paramref name="shared"/>,
        /// so that a file's records hold one string for each distinct value. A record logged live
        /// shares the string its call site compiled in; one parsed from a line brings a copy of
        /// its own, and across a session of them that was tens of megabytes of the same few
        /// hundred strings. Messages are left alone - they are mostly unique, and a table of them
        /// would cost more than it saved.
        /// </para>
        /// </summary>
        private static bool TryReadRecord(string line, bool fromThisSession, Dictionary<string, string> shared,
                                          out LogRecord record) {
            record = default;
            try {
                RecordDto dto = JsonUtility.FromJson<RecordDto>(line);
                if (dto == null || dto.tag == null) {
                    return false;
                }
                record = new LogRecord(
                    dto.sq,
                    Share(dto.tag, shared),
                    dto.msg,
                    (LogLevel)dto.lv,
                    (LogChannel)dto.ch,
                    dto.t,
                    dto.f,
                    string.IsNullOrEmpty(dto.file) ? null : Share(dto.file, shared),
                    dto.ln,
                    string.IsNullOrEmpty(dto.st) ? null : Share(dto.st, shared),
                    fromThisSession ? dto.ctx : 0,
                    dto.cap);
                return true;
            } catch (Exception) {
                return false;
            }
        }

        private static string Share(string value, Dictionary<string, string> shared) {
            if (shared.TryGetValue(value, out string existing)) {
                return existing;
            }
            shared.Add(value, value);
            return value;
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
