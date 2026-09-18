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
                        if (TryReadRecord(line, fromThisSession: false, out LogRecord record)) {
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
        /// Reads the last records of a file, for seeding rather than for viewing.
        /// <para>
        /// Bounded by bytes as well as by count, because this runs on every domain reload and a
        /// file at the default size limit would otherwise cost seconds of every recompile. The
        /// read starts inside the file, so the line it lands in is discarded - and the header,
        /// which is the first line of all, is simply not there to find.
        /// </para>
        /// <para>
        /// The one path that keeps a record's related object, since the file is the editor's
        /// own: an instance id means something only inside the session that issued it.
        /// </para>
        /// </summary>
        public static int ReadTail(string path, int maxRecords, long maxBytes, List<LogRecord> into) =>
            ReadTail(path, maxRecords, maxBytes, into, out _);

        /// <summary>
        /// The same read, reporting what it cost.
        /// <para>
        /// <paramref name="bytesScanned"/> is how much of the budget went on this file, so a
        /// seed spanning two files can spend one budget between them rather than one each.
        /// A read that found nothing costs nothing, which is what leaves the whole budget to
        /// the file behind a rotation.
        /// </para>
        /// </summary>
        public static int ReadTail(string path, int maxRecords, long maxBytes, List<LogRecord> into,
                                   out long bytesScanned) =>
            ReadTail(path, maxRecords, maxBytes, into, out bytesScanned, out _);

        /// <summary>
        /// The same read again, saying as well whether the file came back entire.
        /// <para>
        /// <paramref name="fromTheStart"/> is false when the tail was cut at the front, by
        /// either of the two bounds: the budget, which makes the read begin part way into the
        /// file, or the record count, which fills and then overwrites the ring. Both leave
        /// records missing between what came back and anything older, so neither can be followed
        /// by the file behind this one - that would put an older stretch of the log in front of a
        /// gap with nothing in the window to say it was there.
        /// </para>
        /// <para>
        /// The two have to be asked about together. The byte bound alone looks sufficient, and
        /// is not: a line the parser rejects - a torn write, a header, a record from a schema
        /// this build does not know - leaves the ring full of lines but the list one record short
        /// of the buffer, which reads as room to spare while the front has already gone.
        /// </para>
        /// </summary>
        public static int ReadTail(string path, int maxRecords, long maxBytes, List<LogRecord> into,
                                   out long bytesScanned, out bool fromTheStart) {
            bytesScanned = 0;
            fromTheStart = false;

            // Lines first, records second. Parsing is what costs - a JsonUtility call and an
            // object per line - and a tail of two megabytes holds more lines than the buffer can
            // keep, so parsing them all and then dropping the front would be paying for records
            // nothing will ever see. Held in a ring: reading is forwards, keeping is the end.
            string[] kept = new string[maxRecords];
            int count = 0;
            int next = 0;
            bool droppedALine = false;

            try {
                // ReadWrite for the same reason as above: this file usually has a writer.
                bool startedMidFile;
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                    startedMidFile = stream.Length > maxBytes;
                    if (startedMidFile) {
                        stream.Seek(stream.Length - maxBytes, SeekOrigin.Begin);
                    }
                    bytesScanned = startedMidFile ? maxBytes : stream.Length;

                    using (StreamReader reader = new StreamReader(stream)) {
                        bool skipPartialLine = startedMidFile;
                        string line;
                        while ((line = reader.ReadLine()) != null) {
                            if (skipPartialLine) {
                                skipPartialLine = false;
                                continue;
                            }
                            if (line.Length == 0) {
                                continue;
                            }
                            kept[next] = line;
                            next = (next + 1) % kept.Length;
                            if (count < kept.Length) {
                                count++;
                            } else {
                                droppedALine = true;
                            }
                        }
                    }
                }

                fromTheStart = !startedMidFile && !droppedALine;
            } catch (Exception) {
                bytesScanned = 0;
                fromTheStart = false;
                return 0;
            }

            int added = 0;
            int first = (next - count + kept.Length) % kept.Length;
            for (int i = 0; i < count; i++) {
                // The header and a torn line both simply fail to parse.
                if (TryReadRecord(kept[(first + i) % kept.Length], fromThisSession: true, out LogRecord record)) {
                    into.Add(record);
                    added++;
                }
            }
            return added;
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
        /// </summary>
        private static bool TryReadRecord(string line, bool fromThisSession, out LogRecord record) {
            record = default;
            try {
                RecordDto dto = JsonUtility.FromJson<RecordDto>(line);
                if (dto == null || dto.tag == null) {
                    return false;
                }
                record = new LogRecord(
                    dto.sq,
                    dto.tag,
                    dto.msg,
                    (LogLevel)dto.lv,
                    (LogChannel)dto.ch,
                    dto.t,
                    dto.f,
                    string.IsNullOrEmpty(dto.file) ? null : dto.file,
                    dto.ln,
                    string.IsNullOrEmpty(dto.st) ? null : dto.st,
                    fromThisSession ? dto.ctx : 0,
                    dto.cap);
                return true;
            } catch (Exception) {
                return false;
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
