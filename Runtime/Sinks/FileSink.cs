using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace KenseiLog {
    /// <summary>
    /// Writes records to a rolling JSONL file under persistentDataPath/logs.
    /// <para>
    /// This is what a shipped build has instead of a console: the tester sends the file and
    /// the log window opens it as a session, with the same tabs and filters as a live run.
    /// </para>
    /// </summary>
    public sealed class FileSink : ILogSink, IFlushableSink, IDisposable {
        private const string CurrentFileName = "current.jsonl";
        private const string DirectoryName = "logs";

        private readonly object _lock = new object();
        private readonly StringBuilder _builder = new StringBuilder(512);
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _sizeLimitBytes;
        private int _retainedFiles;
        private double _flushInterval;
        private bool _includeDev;
        private readonly string _app;
        private readonly string _unity;
        private readonly string _platform;
        private readonly string _device;

        private StreamWriter _writer;
        private long _bytesWritten;
        private double _lastFlush;

        public FileSink(in LogConfig config) {
            LogDirectory = Path.Combine(Application.persistentDataPath, DirectoryName);
            CurrentFilePath = Path.Combine(LogDirectory, CurrentFileName);
            Reconfigure(in config);

            // Read on the main thread at construction. These reach into the engine, and Write
            // runs on whichever thread happened to log.
            _app = Application.productName + " " + Application.version;
            _unity = Application.unityVersion;
            _platform = Application.platform.ToString();
            _device = SystemInfo.deviceModel;

            StartSession();
        }

        public string LogDirectory { get; }

        public string CurrentFilePath { get; }

        /// <summary>False when the file could not be opened; the sink then does nothing.</summary>
        public bool IsWriting {
            get {
                lock (_lock) {
                    return _writer != null;
                }
            }
        }

        public void Write(in LogRecord record) {
            if (!_includeDev && record.Channel == LogChannel.Dev) {
                return;
            }

            lock (_lock) {
                if (_writer == null) {
                    return;
                }

                _builder.Length = 0;
                LogJson.AppendRecord(_builder, in record);
                string line = _builder.ToString();

                // Writing is where the disk actually gets touched, so it is where a full volume,
                // an ejected card or a revoked permission shows up. None of that may reach the
                // caller: this runs inside whatever code called Log, and a diagnostic tool that
                // can abort a frame of gameplay is worse than no diagnostic tool.
                try {
                    _writer.Write(line);
                    _writer.Write('\n');
                    _bytesWritten += Encoding.UTF8.GetByteCount(line) + 1;

                    double now = _clock.Elapsed.TotalSeconds;
                    // An error is usually the reason the file exists at all. If the app dies right
                    // after one, a buffered line is exactly the line you cannot afford to lose.
                    if (record.Level == LogLevel.Error || now - _lastFlush >= _flushInterval) {
                        _writer.Flush();
                        _lastFlush = now;
                    }
                } catch (Exception exception) {
                    StopWriting("file logging is off, could not write to", exception);
                    return;
                }

                if (_bytesWritten >= _sizeLimitBytes) {
                    Rotate();
                }
            }
        }

        /// <summary>
        /// Apply the settings that do not need the file reopened.
        /// <para>
        /// Configuration normally arrives after logging has already started: the sink is built
        /// during early initialisation so nothing is missed, while a project's own Configure
        /// call runs later. Rebuilding the sink instead would start a fresh session and push a
        /// file out of the history on every launch.
        /// </para>
        /// </summary>
        public void Reconfigure(in LogConfig config) {
            lock (_lock) {
                _sizeLimitBytes = Math.Max(64L, config.FileSizeLimitKb) * 1024L;
                _retainedFiles = Math.Max(1, config.RetainedFileCount);
                _flushInterval = Math.Max(0.5, config.FileFlushIntervalSeconds);
                _includeDev = config.FileIncludesDevChannel;
            }
        }

        public void Flush() {
            lock (_lock) {
                if (_writer == null) {
                    return;
                }
                try {
                    _writer.Flush();
                    _lastFlush = _clock.Elapsed.TotalSeconds;
                } catch (Exception exception) {
                    // The usual caller is the quit or pause hook. Throwing here would abort the
                    // teardown of everything that had not been flushed yet.
                    StopWriting("file logging is off, could not flush", exception);
                }
            }
        }

        /// <summary>
        /// Gives up on the file after an IO failure. The handle is released and the sink goes
        /// quiet rather than throwing once per record for the rest of the run. Caller holds the
        /// lock.
        /// </summary>
        private void StopWriting(string what, Exception exception) {
            try {
                _writer?.Dispose();
            } catch (Exception) {
                // Already failing; there is nothing useful left to do with it.
            }
            _writer = null;
            Warn(what, exception);
        }

        public void Dispose() {
            lock (_lock) {
                CloseWriter();
            }
        }

        private void StartSession() {
            lock (_lock) {
                try {
                    Directory.CreateDirectory(LogDirectory);
                    // Each run gets its own file, so a crash report is never a blend of two runs.
                    if (File.Exists(CurrentFilePath) && new FileInfo(CurrentFilePath).Length > 0) {
                        ShiftFiles();
                    }
                } catch (Exception exception) {
                    Warn("file logging is off, could not prepare", exception);
                    return;
                }
                OpenWriter();
            }
        }

        private void Rotate() {
            CloseWriter();
            try {
                ShiftFiles();
            } catch (Exception exception) {
                // Shifting can fail for reasons that pass: on Windows the log window holds the
                // current file open while it reads, and a rotation landing in that moment is
                // refused. Reopening anyway keeps logging alive - the file grows past its limit
                // and the next rotation tries again, which is a far better failure than the
                // silence that followed returning here, where the writer stayed closed and
                // every later record was dropped for the rest of the run.
                Warn("could not rotate, still writing to", exception);
                OpenWriter(startSession: false);
                return;
            }
            OpenWriter();
        }

        private void ShiftFiles() {
            DeleteBeyondRetained();
            for (int i = _retainedFiles - 1; i >= 1; i--) {
                string from = IndexedPath(i);
                if (File.Exists(from)) {
                    File.Move(from, IndexedPath(i + 1));
                }
            }
            if (File.Exists(CurrentFilePath)) {
                File.Move(CurrentFilePath, IndexedPath(1));
            }
        }

        /// <summary>
        /// Opens the current file. <paramref name="startSession"/> is false only when a rotation
        /// could not shift the files aside: the existing log is then still the one being written,
        /// so it is opened for append and left without a second header - truncating it would
        /// throw away the very records the rotation was trying to preserve.
        /// </summary>
        private void OpenWriter(bool startSession = true) {
            FileStream stream = null;
            try {
                stream = new FileStream(
                    CurrentFilePath,
                    startSession ? FileMode.Create : FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    4096);
                _writer = new StreamWriter(stream, new UTF8Encoding(false));
                stream = null;

                if (startSession) {
                    _builder.Length = 0;
                    LogJson.AppendSessionHeader(
                        _builder,
                        Guid.NewGuid().ToString("N"),
                        _app,
                        _unity,
                        _platform,
                        _device,
                        DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));

                    string header = _builder.ToString();
                    _writer.Write(header);
                    _writer.Write('\n');
                    _writer.Flush();
                    _bytesWritten = Encoding.UTF8.GetByteCount(header) + 1;
                } else {
                    // Counting from zero rather than from the size the file already has is what
                    // keeps a stuck rotation cheap. The file is past its limit by definition
                    // here, so carrying that figure over would make every single later record
                    // attempt a rotation, fail it, and warn about it. Starting again means one
                    // attempt per size limit of new logs until whatever held the file lets go.
                    _bytesWritten = 0;
                }
            } catch (Exception exception) {
                // The writer owns the stream once it is constructed; before that it is ours to
                // close, or the handle stays open with nothing referencing it.
                stream?.Dispose();
                _writer = null;
                Warn("file logging is off, could not open", exception);
                return;
            } finally {
                _lastFlush = _clock.Elapsed.TotalSeconds;
            }
        }

        private void CloseWriter() {
            if (_writer == null) {
                return;
            }
            try {
                _writer.Flush();
                _writer.Dispose();
            } catch (Exception exception) {
                Warn("could not close", exception);
            } finally {
                _writer = null;
            }
        }

        /// <summary>
        /// Clears the slot the shift is about to fill, and anything above it. Deleting only the
        /// file at the retained count is enough while that count never changes, but a project
        /// that lowers RetainedFileCount leaves the files above the new limit orphaned: the
        /// shift never touches them again, so they sit in the directory for good, holding disk
        /// the setting was lowered to release. Enumerating costs a directory listing once per
        /// rotation, which is once per size limit of logs.
        /// </summary>
        private void DeleteBeyondRetained() {
            string[] existing = Directory.GetFiles(LogDirectory, "log.*.jsonl");
            for (int i = 0; i < existing.Length; i++) {
                int index = IndexOfFile(existing[i]);
                if (index >= _retainedFiles) {
                    File.Delete(existing[i]);
                }
            }
        }

        /// <summary>The N in log.N.jsonl, or -1 for a name this sink did not write.</summary>
        private static int IndexOfFile(string path) {
            string name = Path.GetFileNameWithoutExtension(path);
            int dot = name.IndexOf('.');
            if (dot < 0 || !int.TryParse(name.Substring(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int index)) {
                return -1;
            }
            return index;
        }

        private string IndexedPath(int index) =>
            Path.Combine(LogDirectory, "log." + index.ToString(CultureInfo.InvariantCulture) + ".jsonl");

        private void Warn(string what, Exception exception) {
            // Rotate calls this from inside Write, which is inside Emit. Without the flag the
            // warning comes straight back through the foreign-log handler, takes the next
            // sequence, and reaches the later sinks ahead of the record we are still writing -
            // leaving the viewers' buffers out of order. UnityConsoleSink raises it for the
            // same reason; every Debug call made from inside a sink has to.
            bool suppressed = LogCore.SuppressForeignCapture;
            LogCore.SuppressForeignCapture = true;
            try {
                Debug.LogWarning("KenseiLog: " + what + ", " + CurrentFilePath + " (" + exception.Message + ")");
            } finally {
                LogCore.SuppressForeignCapture = suppressed;
            }
        }
    }
}
