using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace KenseiLog {
    /// <summary>
    /// Writes records to a rolling JSONL file under persistentDataPath/logs, numbered in the
    /// order they were written - log.0001.jsonl, log.0002.jsonl - with the highest being the
    /// one in hand.
    /// <para>
    /// This is what a shipped build has instead of a console: the tester sends the file and
    /// the log window opens it as a session, with the same tabs and filters as a live run.
    /// </para>
    /// </summary>
    public sealed class FileSink : ILogSink, IFlushableSink, IChannelFilteredSink, IDisposable {
        private const string DirectoryName = "logs";
        private const string FilePattern = "log.*.jsonl";

        /// <summary>
        /// Width the index is padded to, so that a file browser sorts the directory the way the
        /// files were written. Past it the names simply get longer, which takes ten thousand
        /// rotations - fifty gigabytes at the default size limit.
        /// </summary>
        private const string IndexFormat = "0000";

        private readonly object _lock = new object();

        // Thread-local so that building a line needs no lock. A shared builder was what forced
        // the lock to span the whole of Write - every thread that logged waited not only on the
        // disk but on another thread's JSON.
        [ThreadStatic] private static StringBuilder _lineBuilder;
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

        public FileSink(in LogConfig config) : this(in config, continueExistingFile: false) {
        }

        /// <summary>
        /// Opens the sink.
        /// <para>
        /// <paramref name="continueExistingFile"/> adds to the file that is already there
        /// rather than shifting it aside and starting a run. A build never wants that - each
        /// run gets its own file so that a crash report is never a blend of two - but the
        /// editor does: its sink is rebuilt on every domain reload, and starting a run per
        /// recompile would push a morning's logs out of the history by lunchtime.
        /// </para>
        /// <para>
        /// <paramref name="continueFilePath"/> names which file that is. Without it, or when the
        /// file it names has gone - pruned, or cleared by hand - the newest in the directory is
        /// taken, which is only the right answer while this sink is the one writing there.
        /// </para>
        /// </summary>
        public FileSink(in LogConfig config, bool continueExistingFile, string continueFilePath = null) {
            // Read on the main thread at construction. These reach into the engine, and Write
            // runs on whichever thread happened to log.
            _app = Application.productName + " " + Application.version;
            _unity = Application.unityVersion;
            _platform = Application.platform.ToString();
            _device = SystemInfo.deviceModel;

            SetDirectory(ResolveDirectory(in config));
            ApplySettings(in config);

            if (continueExistingFile) {
                ContinueSession(continueFilePath);
            } else {
                StartSession();
            }
        }

        public string LogDirectory { get; private set; }

        public string CurrentFilePath { get; private set; }

        /// <summary>False when the file could not be opened; the sink then does nothing.</summary>
        public bool IsWriting {
            get {
                lock (_lock) {
                    return _writer != null;
                }
            }
        }

        /// <summary>
        /// Whether a record on this channel would be written. Read without a lock on purpose:
        /// this is asked while the sink list is held, and a bool cannot be read half-written.
        /// </summary>
        public bool Accepts(LogChannel channel) =>
            _includeDev || channel != LogChannel.Dev;

        public void Write(in LogRecord record) {
            if (!_includeDev && record.Channel == LogChannel.Dev) {
                return;
            }

            // Built outside the lock: this is the expensive half of a write, and holding the
            // lock across it made every other logging thread wait for it as well as for the
            // disk. The byte count comes with it for the same reason.
            StringBuilder builder = LineBuilder();
            builder.Length = 0;
            LogJson.AppendRecord(builder, in record);
            string line = builder.ToString();
            long bytes = Encoding.UTF8.GetByteCount(line) + 1;

            lock (_lock) {
                if (_writer == null) {
                    return;
                }

                // Writing is where the disk actually gets touched, so it is where a full volume,
                // an ejected card or a revoked permission shows up. None of that may reach the
                // caller: this runs inside whatever code called Log, and a diagnostic tool that
                // can abort a frame of gameplay is worse than no diagnostic tool.
                try {
                    _writer.Write(line);
                    _writer.Write('\n');
                    _bytesWritten += bytes;

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
            // Outside the lock: this reads Application state, which is main thread only, and
            // Reconfigure is documented as such through LogCore.Configure.
            string directory = ResolveDirectory(in config);

            ReconfigureLocked(in config, directory);

            // Outside the lock as well, and never inside it: LogCore asks every sink what it
            // takes while holding the sink list, so calling it from under this lock would have
            // the two waiting on each other. What changed may be the dev channel, and nothing
            // is built for a channel no sink will take.
            LogCore.RefreshChannelInterest();
        }

        private void ReconfigureLocked(in LogConfig config, string directory) {
            lock (_lock) {
                ApplySettings(in config);
                if (string.Equals(directory, LogDirectory, StringComparison.Ordinal)) {
                    return;
                }

                // Somewhere else to write means a file there, and a session header of its own.
                // What was already written stays where it was: copying it across would mean
                // moving a file that something may already be reading.
                CloseWriter();
                SetDirectory(directory);
                StartSession();
            }
        }

        private void ApplySettings(in LogConfig config) {
            _sizeLimitBytes = Math.Max(64L, config.FileSizeLimitKb) * 1024L;
            _retainedFiles = Math.Max(1, config.RetainedFileCount);
            _flushInterval = Math.Max(0.5, config.FileFlushIntervalSeconds);
            _includeDev = config.FileIncludesDevChannel;
        }

        private void SetDirectory(string directory) {
            LogDirectory = directory;
            // Named when a file is opened: which one is current is whichever index is highest,
            // and that is not known until the directory has been looked at.
            CurrentFilePath = null;
        }

        private static string ResolveDirectory(in LogConfig config) =>
            string.IsNullOrEmpty(config.FileDirectory)
                ? Path.Combine(Application.persistentDataPath, DirectoryName)
                : config.FileDirectory;

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

        /// <summary>
        /// Opens the next file. Each run gets one of its own, so a crash report is never a
        /// blend of two runs.
        /// </summary>
        private void StartSession() {
            lock (_lock) {
                int next;
                try {
                    Directory.CreateDirectory(LogDirectory);
                    MigrateLegacyFile();
                    next = HighestIndex() + 1;
                } catch (Exception exception) {
                    // Without a directory there is nowhere to put a file, and without a listing
                    // there is no safe name to give one - picking blind would truncate a file
                    // belonging to a run somebody still wants.
                    Warn("file logging is off, could not prepare", exception);
                    return;
                }

                CurrentFilePath = IndexedPath(next);
                OpenWriter();
                // After the file exists, so that a directory it cannot tidy costs the run
                // nothing: what it failed to delete is tried again at the next rotation.
                PruneOldFiles();
            }
        }

        /// <summary>
        /// Opens the existing file for appending, with no second session header and the byte
        /// count carried over from what is already in it, so the size limit still means the
        /// size of the file. Falls back to starting a session when there is nothing to carry
        /// on from.
        /// </summary>
        private void ContinueSession(string preferredPath) {
            lock (_lock) {
                string target;
                long existing;
                try {
                    Directory.CreateDirectory(LogDirectory);
                    // The caller's own file when it named one. Another process writing into the
                    // same directory - an asset import worker, which shares the project path
                    // this directory is keyed by - leaves a newer file that is not ours, and
                    // appending to it would interleave two writers into one log.
                    target = preferredPath != null && File.Exists(preferredPath)
                        ? preferredPath
                        // By the name it actually has: a directory written by an older version,
                        // or by hand, may hold an index this version would pad differently.
                        : NewestFile(LogDirectory);
                    if (target == null) {
                        StartSession();
                        return;
                    }
                    existing = new FileInfo(target).Length;
                } catch (Exception exception) {
                    Warn("file logging is off, could not open the newest file in", exception);
                    return;
                }

                CurrentFilePath = target;
                OpenWriter(startSession: false);
                if (_writer != null) {
                    _bytesWritten = existing;
                }
            }
        }

        private void Rotate() {
            CloseWriter();

            int next;
            try {
                next = HighestIndex() + 1;
            } catch (Exception exception) {
                // Nowhere to go next, so carry on where we were. The file grows past its limit
                // and the next rotation tries again, which is a far better failure than the
                // silence that follows leaving the writer closed.
                Warn("could not open the next file, still writing to", exception);
                OpenWriter(startSession: false);
                return;
            }

            string previous = CurrentFilePath;
            CurrentFilePath = IndexedPath(next);
            OpenWriter();

            if (_writer == null) {
                // The next file would not open - something else has that name, or the disk has
                // filled. Going back to the one that was working beats going quiet.
                CurrentFilePath = previous;
                OpenWriter(startSession: false);
                return;
            }

            PruneOldFiles();
        }

        /// <summary>
        /// Gives a number to the file left by the scheme that called the current one
        /// current.jsonl.
        /// <para>
        /// That name matches nothing this version looks for, so without this it would sit in
        /// the directory for good - holding a size limit of disk, and holding the last run
        /// before the upgrade, which is exactly the run somebody may still want. One rename,
        /// once, on a name this version never creates.
        /// </para>
        /// </summary>
        private void MigrateLegacyFile() {
            string legacy = Path.Combine(LogDirectory, "current.jsonl");
            if (!File.Exists(legacy)) {
                return;
            }
            try {
                File.Move(legacy, IndexedPath(HighestIndex() + 1));
            } catch (Exception) {
                // Something is holding it. It keeps its old name and is tried again next run.
            }
        }

        /// <summary>The highest index in the directory, or 0 when there are no files yet.</summary>
        private int HighestIndex() {
            return IndexOf(NewestFile(LogDirectory));
        }

        /// <summary>
        /// The newest file in a log directory - the one with the highest index, by number
        /// rather than by name, since a name only sorts correctly while every index is the same
        /// width. Null when the directory holds none. Public because the editor's sink needs
        /// the same answer, and two answers to "which file is newest" is one too many.
        /// </summary>
        public static string NewestFile(string directory) {
            string[] existing = Directory.GetFiles(directory, FilePattern);
            string newest = null;
            int highest = 0;
            for (int i = 0; i < existing.Length; i++) {
                int index = IndexOfFile(existing[i]);
                if (index > highest) {
                    highest = index;
                    newest = existing[i];
                }
            }
            return newest;
        }

        private static int IndexOf(string path) =>
            path == null ? 0 : IndexOfFile(path);

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
                    StringBuilder builder = LineBuilder();
                    builder.Length = 0;
                    LogJson.AppendSessionHeader(
                        builder,
                        Guid.NewGuid().ToString("N"),
                        _app,
                        _unity,
                        _platform,
                        _device,
                        DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));

                    string header = builder.ToString();
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
                // Not "file logging is off": a rotation that cannot open the next file carries
                // on with the one it had, and says so itself.
                Warn("could not open", exception);
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
        /// Deletes everything past the newest <c>RetainedFileCount + 1</c> files - the one being
        /// written, and the retained ones behind it.
        /// <para>
        /// Deleting rather than shifting is the whole of the housekeeping now. Numbers rise with
        /// time and are never reused, so the file a tester mentions stays the file they meant,
        /// a file opened in the window cannot be renamed under it, and the sequence reads in the
        /// order it was written instead of backwards. It also removes every rename from the
        /// rotation, and a rename is what fails on Windows when anything else has the file open.
        /// </para>
        /// <para>
        /// A project that lowers RetainedFileCount tidies up at the next rotation rather than
        /// leaving the files above the new limit orphaned for good.
        /// </para>
        /// <para>
        /// One writer per directory is assumed, as it always was. Two processes sharing one -
        /// two clients of a multiplayer test on one machine - can each decide the other's file
        /// is old enough to delete.
        /// </para>
        /// </summary>
        private void PruneOldFiles() {
            string[] existing;
            try {
                existing = Directory.GetFiles(LogDirectory, FilePattern);
            } catch (Exception) {
                return;
            }

            int keep = _retainedFiles + 1;
            if (existing.Length <= keep) {
                return;
            }

            // Sorted by index, highest first, so the files kept are the newest. The one being
            // written holds the highest index of all - for this sink; a second process writing
            // into the same directory is outside what any of this can promise.
            Array.Sort(existing, CompareByIndexDescending);
            for (int i = keep; i < existing.Length; i++) {
                if (IndexOfFile(existing[i]) < 0) {
                    // Not a name this sink wrote. A file somebody kept by hand - log.crash.jsonl -
                    // matches the pattern without matching the scheme, and deleting it would be
                    // deleting the one log they meant to keep.
                    continue;
                }
                try {
                    File.Delete(existing[i]);
                } catch (Exception) {
                    // One stale file held open by a sync agent, a scanner or another instance
                    // must not cost the run its log; it is tried again at the next rotation.
                }
            }
        }

        private static int CompareByIndexDescending(string left, string right) =>
            IndexOfFile(right).CompareTo(IndexOfFile(left));

        /// <summary>
        /// The file written immediately before this one in the same directory, or null when
        /// there is none. Read when a rotation has only just happened and the file now current
        /// holds a handful of records - on its own it would be the whole of a session's history.
        /// </summary>
        public static string FileBefore(string path) {
            int index = IndexOfFile(path);
            if (index <= 0) {
                return null;
            }

            string directory = Path.GetDirectoryName(path);
            string[] existing = Directory.GetFiles(directory, FilePattern);
            string best = null;
            int bestIndex = 0;
            for (int i = 0; i < existing.Length; i++) {
                int candidate = IndexOfFile(existing[i]);
                if (candidate > 0 && candidate < index && candidate > bestIndex) {
                    bestIndex = candidate;
                    best = existing[i];
                }
            }
            return best;
        }

        /// <summary>
        /// Whether one file in a log directory was written before another. Both have to be
        /// names this sink wrote; anything else has no place in the order and answers false.
        /// <para>
        /// Public for the reason <see cref="FileBefore"/> is: the editor has to know how far
        /// back a session of its own reaches, and two answers to "which came first" is one
        /// too many.
        /// </para>
        /// </summary>
        public static bool WrittenBefore(string path, string other) {
            int index = IndexOfFile(path);
            int reference = IndexOfFile(other);
            return index > 0 && reference > 0 && index < reference;
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

        private static StringBuilder LineBuilder() =>
            _lineBuilder ?? (_lineBuilder = new StringBuilder(512));

        private string IndexedPath(int index) =>
            Path.Combine(LogDirectory, "log." + index.ToString(IndexFormat, CultureInfo.InvariantCulture) + ".jsonl");

        private void Warn(string what, Exception exception) {
            // The directory when there is no file yet, which is the case every message about
            // preparing one is raised in - and where the path would have been null.
            string where = CurrentFilePath ?? LogDirectory;
            // Rotate calls this from inside Write, which is inside Emit. Without the flag the
            // warning comes straight back through the foreign-log handler, takes the next
            // sequence, and reaches the later sinks ahead of the record we are still writing -
            // leaving the viewers' buffers out of order. UnityConsoleSink raises it for the
            // same reason; every Debug call made from inside a sink has to.
            bool suppressed = LogCore.SuppressForeignCapture;
            LogCore.SuppressForeignCapture = true;
            try {
                Debug.LogWarning("KenseiLog: " + what + ", " + where + " (" + exception.Message + ")");
            } finally {
                LogCore.SuppressForeignCapture = suppressed;
            }
        }
    }
}
