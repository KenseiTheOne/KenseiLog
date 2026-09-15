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
        private readonly long _sizeLimitBytes;
        private readonly int _retainedFiles;
        private readonly double _flushInterval;
        private readonly bool _includeDev;
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
            _sizeLimitBytes = Math.Max(64L, config.FileSizeLimitKb) * 1024L;
            _retainedFiles = Math.Max(1, config.RetainedFileCount);
            _flushInterval = Math.Max(0.5, config.FileFlushIntervalSeconds);
            _includeDev = config.FileIncludesDevChannel;

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

                if (_bytesWritten >= _sizeLimitBytes) {
                    Rotate();
                }
            }
        }

        public void Flush() {
            lock (_lock) {
                if (_writer == null) {
                    return;
                }
                _writer.Flush();
                _lastFlush = _clock.Elapsed.TotalSeconds;
            }
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
                    Warn(exception);
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
                Warn(exception);
                return;
            }
            OpenWriter();
        }

        private void ShiftFiles() {
            string oldest = IndexedPath(_retainedFiles);
            if (File.Exists(oldest)) {
                File.Delete(oldest);
            }
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

        private void OpenWriter() {
            try {
                FileStream stream = new FileStream(CurrentFilePath, FileMode.Create, FileAccess.Write, FileShare.Read, 4096);
                _writer = new StreamWriter(stream, new UTF8Encoding(false));
            } catch (Exception exception) {
                _writer = null;
                Warn(exception);
                return;
            }

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
            _lastFlush = _clock.Elapsed.TotalSeconds;
        }

        private void CloseWriter() {
            if (_writer == null) {
                return;
            }
            _writer.Flush();
            _writer.Dispose();
            _writer = null;
        }

        private string IndexedPath(int index) =>
            Path.Combine(LogDirectory, "log." + index.ToString(CultureInfo.InvariantCulture) + ".jsonl");

        private void Warn(Exception exception) {
            Debug.LogWarning("KenseiLog: file logging is off, " + CurrentFilePath + " could not be opened (" + exception.Message + ")");
        }
    }
}
