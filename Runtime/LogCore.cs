using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;
using Object = UnityEngine.Object;

namespace KenseiLog {
    /// <summary>
    /// Builds records and fans them out to the registered sinks.
    /// Most code should call <see cref="Log"/> instead of this type directly.
    /// </summary>
    public static class LogCore {
        /// <summary>Tag given to records captured from outside this facade.</summary>
        public const string ForeignTag = "Unity";

        /// <summary>Tag given to records written through the overloads that take no tag.</summary>
        public const string UntaggedTag = "Untagged";

        private static readonly object _sinkLock = new object();
        private static readonly Stopwatch _clock = new Stopwatch();
        private static readonly HashSet<ILogSink> _failedSinks = new HashSet<ILogSink>();

        private static ILogSink[] _sinks = Array.Empty<ILogSink>();
        private static UnityConsoleSink _consoleSink;
        private static FileSink _fileSink;
        private static MemorySink _overlaySink;
        private static LogConfig _config = LogConfig.Default();
        private static long _sequence;
        private static int _mainThreadId;
        private static int _lastKnownFrame;
        private static bool _sceneSystemsReady;
        private static bool _devChannelWanted = true;

        [ThreadStatic] private static bool _suppressForeignCapture;

        // Reporting a broken sink goes through Debug, which comes back through the foreign-log
        // handler and out to the sinks again. Thread-local for the same reason as the flag
        // above: a plain static would let one thread's report silence another thread's.
        [ThreadStatic] private static bool _reportingSinkFailure;

        public static LogConfig Config => _config;

        /// <summary>
        /// Set by sinks that call back into Unity logging, so the foreign-log handler can
        /// tell our own echo from a genuine engine message. Thread-local on purpose: the
        /// handler fires on arbitrary threads, and a plain static flag raised by one thread
        /// would swallow a real error arriving on another.
        /// </summary>
        internal static bool SuppressForeignCapture { get => _suppressForeignCapture; set => _suppressForeignCapture = value; }

        /// <summary>Safe to call repeatedly. Must be called from the main thread.</summary>
        public static void Initialize() {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            if (!_clock.IsRunning) {
                _clock.Start();
            }
            ApplyConfig();
        }

        /// <summary>
        /// Applies a configuration. Main thread only, and not safe against a second Configure
        /// running beside it: this reads Application state, creates the GameObject behind the
        /// overlay, and opens or closes the file sink. The intended caller is a
        /// RuntimeInitializeOnLoadMethod, which is where the defaults are decided once.
        /// </summary>
        public static void Configure(in LogConfig config) {
            _config = config;
            ApplyConfig();
        }

        public static void AddSink(ILogSink sink) {
            lock (_sinkLock) {
                ILogSink[] updated = new ILogSink[_sinks.Length + 1];
                Array.Copy(_sinks, updated, _sinks.Length);
                updated[_sinks.Length] = sink;
                _sinks = updated;
                RefreshChannelInterest();
            }
        }

        public static void RemoveSink(ILogSink sink) {
            lock (_sinkLock) {
                int index = Array.IndexOf(_sinks, sink);
                if (index < 0) {
                    return;
                }
                ILogSink[] updated = new ILogSink[_sinks.Length - 1];
                Array.Copy(_sinks, 0, updated, 0, index);
                Array.Copy(_sinks, index + 1, updated, index, _sinks.Length - index - 1);
                _sinks = updated;
                // A sink that is taken out and put back gets another chance to report, rather
                // than staying silently on the failed list for the rest of the app domain.
                _failedSinks.Remove(sink);
                RefreshChannelInterest();
            }
        }

        /// <summary>
        /// Works out whether anything registered would take a Dev record, by asking the sinks
        /// that answer for themselves. One that does not implement
        /// <see cref="IChannelFilteredSink"/> is taken to want everything, which is the safe
        /// answer for a sink this package knows nothing about.
        /// <para>
        /// The answers are cached, and asked for again only when the sink list changes. A sink
        /// that changes its mind while registered has to say so by calling this; until it does,
        /// it goes on being treated as it answered when it arrived, and nothing will point at
        /// why it stopped seeing a channel.
        /// </para>
        /// <para>
        /// The case worth catching is a development build with the defaults: the list there is
        /// exactly the file sink with the dev channel off, and every Log.DevInfo call was building
        /// a record - unwinding a stack trace, for an error - that was dropped on arrival. A
        /// development build is what gets profiled on a device, so it was the one build type
        /// that misreported what logging costs.
        /// </para>
        /// <para>
        /// Called by a sink whose answer has changed, as well as when the list does.
        /// </para>
        /// </summary>
        public static void RefreshChannelInterest() {
            lock (_sinkLock) {
                ILogSink[] sinks = _sinks;
                for (int i = 0; i < sinks.Length; i++) {
                    if (!(sinks[i] is IChannelFilteredSink filtered) || filtered.Accepts(LogChannel.Dev)) {
                        _devChannelWanted = true;
                        return;
                    }
                }
                _devChannelWanted = false;
            }
        }

        /// <summary>
        /// The next record id. Public so a view can build records from a source of its own -
        /// the editor window seeds itself from Unity's console - without colliding with ours.
        /// </summary>
        public static long NextSequence() {
            return Interlocked.Increment(ref _sequence);
        }

        /// <summary>
        /// The highest id issued so far. The editor records it when a domain ends and hands it
        /// back through <see cref="ReserveSequencesThrough"/> when the next one starts, so a
        /// session that carries on with its file carries on with its numbering too - whether or
        /// not the records themselves were read back.
        /// </summary>
        public static long CurrentSequence => Interlocked.Read(ref _sequence);

        /// <summary>
        /// Moves the counter past a record that already exists, so that what is written next
        /// cannot collide with it.
        /// <para>
        /// The counter starts again at zero with every app domain, which is fine while nothing
        /// outlives one. The editor's log file does: it is carried across a reload, and without
        /// this the records after the reload would repeat the numbers before it - leaving the
        /// file unsorted, and every lookup that binary-searches it wrong.
        /// </para>
        /// </summary>
        public static void ReserveSequencesThrough(long sequence) {
            while (true) {
                long current = Interlocked.Read(ref _sequence);
                if (current >= sequence) {
                    return;
                }
                if (Interlocked.CompareExchange(ref _sequence, sequence, current) == current) {
                    return;
                }
            }
        }

        public static void Emit(in LogRecord record) {
            ILogSink[] sinks = _sinks;
            for (int i = 0; i < sinks.Length; i++) {
                // Sinks are isolated from each other on purpose. They are visited in
                // registration order, so without this a sink that throws on some record would
                // take the file sink down with it - and the file is the only diagnostic a
                // shipped build has, which makes the records around a fault exactly the ones
                // that would go missing. Letting it through would also abort the caller's
                // frame: a logging call must not be able to break the code that logs.
                try {
                    sinks[i].Write(in record);
                } catch (Exception exception) {
                    ReportSinkFailure(sinks[i], exception);
                }
            }
        }

        /// <summary>
        /// Reports a throwing sink once and then stays quiet about it. A sink that fails on one
        /// record usually fails on all of them, and a report per record would be a second flood
        /// on top of the first - through the same machinery that is already misbehaving.
        /// </summary>
        private static void ReportSinkFailure(ILogSink sink, Exception exception) {
            if (_reportingSinkFailure) {
                return;
            }

            lock (_sinkLock) {
                if (!_failedSinks.Add(sink)) {
                    return;
                }
            }

            // Deliberately not suppressed. The report used to be kept out of the pipeline so it
            // could not come back through the foreign-log handler and take a sequence ahead of
            // the record still being written - but the flag above already ends the recursion,
            // and the ring buffer settles a late arrival back into place, so both hazards are
            // covered twice over. Suppressed, this reached only Player.log: the one channel this
            // package exists to replace, and never the file a tester sends back.
            _reportingSinkFailure = true;
            try {
                UnityEngine.Debug.LogError(
                    "KenseiLog: sink " + sink.GetType().Name + " threw and will not be reported again this session (" +
                    exception.Message + ")");
            } catch (Exception) {
                // Nothing left to report through.
            } finally {
                _reportingSinkFailure = false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Write(string tag, string message, LogLevel level, LogChannel channel, Object context, string file, int line) {
            // Before the stack trace, which is the expensive part. Nothing registered, or
            // nothing that would take this channel, means the record is built only to be
            // dropped by the first sink that looks at it. Returning here also leaves no gap in
            // the sequence, since the number is taken below.
            if (_sinks.Length == 0 || (channel == LogChannel.Dev && !_devChannelWanted)) {
                return;
            }

            string stackTrace = _config.CaptureStackTraceOnError && level == LogLevel.Error
                ? new StackTrace(2, true).ToString()
                : null;

            LogRecord record = new LogRecord(
                Interlocked.Increment(ref _sequence),
                // Normalised here rather than at each call site. A null tag is easy to pass by
                // accident - Log.Info(config?.NetTag, ...) is enough - and it survives all the
                // way to the viewers, where it throws out of a dictionary lookup or a palette
                // hash with a stack that never mentions the call that caused it.
                string.IsNullOrEmpty(tag) ? UntaggedTag : tag,
                message ?? string.Empty,
                level,
                channel,
                _clock.Elapsed.TotalMilliseconds,
                CurrentFrame(),
                NormalizeCallSite(file),
                line,
                stackTrace,
                ResolveContextId(context));

            Emit(in record);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void OnRuntimeInitialize() {
            // Statics outlive a play session when Enter Play Mode is set to skip the domain
            // reload, so everything that describes one run has to be put back by hand. Left
            // set, _sceneSystemsReady sends the second pass of ApplyConfig into this phase -
            // the one its own comment says has nowhere to put a GameObject - and the overlay
            // opens on the previous session's records with the previous session's frame
            // numbers. A sink muted after throwing last time stays muted for the same reason.
            _sceneSystemsReady = false;
            _lastKnownFrame = 0;
            _overlaySink?.Clear();
            lock (_sinkLock) {
                _failedSinks.Clear();
            }

            Initialize();
            _clock.Restart();
        }

        /// <summary>
        /// Trims a call site to a project-relative path outside the editor.
        /// <para>
        /// CallerFilePath is resolved by the compiler, so the Prod methods - which carry no
        /// Conditional attribute and therefore survive into a release build - hold the absolute
        /// path of the machine that built them. That path is written into the JSONL file, which
        /// is the file a tester sends back: it names the build agent, the account it ran under
        /// and the layout of the source tree. What the window needs in order to find the file
        /// is the part from Assets or Packages onwards, and that is all this keeps. In the
        /// editor the full path is left alone - it is the developer's own machine, and a source
        /// outside the project can only be opened by absolute path.
        /// </para>
        /// </summary>
        private static string NormalizeCallSite(string file) {
#if UNITY_EDITOR
            return file;
#else
            if (string.IsNullOrEmpty(file)) {
                return file;
            }

            // The compiler hands the same interned string to every call from a given line, so
            // the answer can be looked up by reference and the substring paid for once per call
            // site rather than once per record. Thread-local, so logging from several threads
            // needs no lock to read it; a miss only costs the work that used to happen anyway.
            Dictionary<string, string> cache = _callSites ?? (_callSites = new Dictionary<string, string>(ReferenceComparer.Instance));
            if (cache.TryGetValue(file, out string trimmed)) {
                return trimmed;
            }

            trimmed = ProjectRelativePath(file);
            cache[file] = trimmed;
            return trimmed;
#endif
        }

#if !UNITY_EDITOR
        [ThreadStatic] private static Dictionary<string, string> _callSites;

        private sealed class ReferenceComparer : IEqualityComparer<string> {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();

            public bool Equals(string left, string right) =>
                ReferenceEquals(left, right);

            public int GetHashCode(string value) =>
                RuntimeHelpers.GetHashCode(value);
        }
#endif

        /// <summary>
        /// The part of a path from Assets or Packages onwards, or the file name alone when it
        /// is under neither - a package in the global cache, say, which still identifies itself
        /// by name to whoever is reading the log.
        /// <para>
        /// Public, and compiled everywhere, because the trimming that uses it only runs in a
        /// build - where nothing can check it. The window's stack trace parsing is public for
        /// the same reason.
        /// </para>
        /// </summary>
        public static string ProjectRelativePath(string file) {
            if (string.IsNullOrEmpty(file)) {
                return file;
            }

            // Assets is preferred over Packages rather than the last anchor of either kind
            // simply winning. A project that installs NuGet packages keeps them in
            // Assets/Packages, where the last anchor is that inner Packages - which cuts the
            // Assets off the front and leaves a path the window can resolve to nothing. A build
            // path with a Packages folder above the project is the same shape in reverse.
            //
            // The last of a kind still wins, for a checkout nested inside another project. A
            // path that is already project-relative has no separator in front of its first
            // segment, so the start of the string counts as one.
            int assets = IsSegment(file, 0, "Assets") ? 0 : -1;
            int packages = IsSegment(file, 0, "Packages") ? 0 : -1;
            int lastSeparator = -1;
            for (int i = 0; i < file.Length; i++) {
                if (file[i] != '/' && file[i] != '\\') {
                    continue;
                }
                lastSeparator = i;
                if (IsSegment(file, i + 1, "Assets")) {
                    assets = i + 1;
                } else if (IsSegment(file, i + 1, "Packages")) {
                    packages = i + 1;
                }
            }

            int projectRelative = assets >= 0 ? assets : packages;
            if (projectRelative >= 0) {
                return file.Substring(projectRelative);
            }
            return lastSeparator < 0 ? file : file.Substring(lastSeparator + 1);
        }

        private static bool IsSegment(string path, int start, string segment) {
            if (start + segment.Length >= path.Length) {
                return false;
            }
            for (int i = 0; i < segment.Length; i++) {
                if (path[start + i] != segment[i]) {
                    return false;
                }
            }
            char next = path[start + segment.Length];
            return next == '/' || next == '\\';
        }

        /// <summary>
        /// Second pass, once the scene systems exist. Sinks are set up as early as possible so
        /// nothing is missed, but the pieces backed by a GameObject cannot be built at
        /// SubsystemRegistration - that runs before there is anywhere to put one.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void OnBeforeSceneLoad() {
            _sceneSystemsReady = true;
            ApplyConfig();
        }

        private static void ApplyConfig() {
            Application.logMessageReceivedThreaded -= OnForeignLogReceived;
            if (_config.CaptureForeignLogs) {
                Application.logMessageReceivedThreaded += OnForeignLogReceived;
            }

            if (_config.MirrorToUnityConsole) {
                if (_consoleSink == null) {
                    _consoleSink = new UnityConsoleSink();
                    AddSink(_consoleSink);
                }
            } else if (_consoleSink != null) {
                RemoveSink(_consoleSink);
                _consoleSink = null;
            }

            Application.quitting -= OnQuitting;
            Application.quitting += OnQuitting;

            // isPlaying gates the file sink because in the editor this runs again on every
            // domain reload, and each new sink starts a session by rotating the files. Without
            // the gate a few script recompiles would push every real log out of the history.
            // In a build isPlaying is always true, so devices are unaffected.
            if (_config.WriteToFile && Application.isPlaying) {
                if (_fileSink == null) {
                    _fileSink = new FileSink(in _config);
                    AddSink(_fileSink);
                } else {
                    // The sink exists from the early pass, built with defaults so that nothing
                    // logged during engine startup is lost. A project's own Configure arrives
                    // later and has to reach it, or its file settings would be silently ignored.
                    _fileSink.Reconfigure(in _config);
                }
                if (_sceneSystemsReady) {
                    LogLifecycleHooks.Ensure();
                }
            } else if (_fileSink != null) {
                RemoveSink(_fileSink);
                _fileSink.Dispose();
                _fileSink = null;
            }

            if (_config.ShowOverlay && Application.isPlaying) {
                int capacity = Math.Max(32, _config.OverlayRecordCapacity);
                if (_overlaySink == null) {
                    _overlaySink = new MemorySink(capacity);
                    AddSink(_overlaySink);
                } else if (_overlaySink.Buffer.Capacity != capacity) {
                    // A later Configure reaches the file sink through Reconfigure. The overlay's
                    // buffer is fixed at construction, so a changed capacity needs a new sink -
                    // without this the setting was accepted and ignored. What it already holds
                    // moves across, and so do its totals: a capacity change that emptied the
                    // viewer, or quietly reset the counts to whatever had survived the old ring,
                    // would trade one silent surprise for another.
                    MemorySink resized = new MemorySink(capacity, _overlaySink);
                    RemoveSink(_overlaySink);
                    _overlaySink = resized;
                    AddSink(_overlaySink);
                }
                if (_sceneSystemsReady) {
                    LogOverlay.Ensure(_overlaySink, _config.OverlayScale);
                }
            } else if (_overlaySink != null) {
                LogOverlay.Remove();
            }

            RefreshChannelInterest();
        }

        /// <summary>
        /// Takes the overlay's sink out of the pipeline. Called by the overlay as it goes away,
        /// so that removing the viewer directly leaves no sink filling a buffer nobody reads -
        /// and no stale sink here to make the next Configure think one is already in place.
        /// </summary>
        internal static void DetachOverlaySink() {
            if (_overlaySink == null) {
                return;
            }
            RemoveSink(_overlaySink);
            _overlaySink = null;
        }

        private static void OnQuitting() {
            FlushSinks();
            if (_fileSink == null) {
                return;
            }
            RemoveSink(_fileSink);
            _fileSink.Dispose();
            _fileSink = null;
        }

        /// <summary>The active file sink, or null when file logging is off.</summary>
        public static FileSink File => _fileSink;

        /// <summary>
        /// Whether some registered file sink is putting records of this channel on disk right
        /// now - the runtime's own, the editor's session file, or any other.
        /// <para>
        /// Asked of the sinks rather than read off the configuration, because the two disagree
        /// in exactly the cases that matter. A file sink whose writer has failed - a full disk, a
        /// path that cannot be written - stays registered and stays <see cref="File"/>, so a
        /// null check calls it a working file. And the editor registers a second file sink of its
        /// own that takes the dev channel whatever the runtime's is set to.
        /// </para>
        /// </summary>
        internal static bool AnyFileKeeps(LogChannel channel) =>
            AnyFileKeeps(_sinks, channel);

        private static bool AnyFileKeeps(ILogSink[] sinks, LogChannel channel) {
            for (int i = 0; i < sinks.Length; i++) {
                if (sinks[i] is FileSink file && file.IsWriting && file.Accepts(channel)) {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Pushes every buffering sink to its destination.</summary>
        public static void FlushSinks() {
            ILogSink[] sinks = _sinks;
            for (int i = 0; i < sinks.Length; i++) {
                if (sinks[i] is IFlushableSink flushable) {
                    // Isolated for the same reason as Emit, and it matters more here: the usual
                    // caller is the quit hook, so one sink throwing would skip the flush of
                    // every sink after it at the one moment there is no next chance.
                    try {
                        flushable.Flush();
                    } catch (Exception exception) {
                        ReportSinkFailure(sinks[i], exception);
                    }
                }
            }
        }

        private static void OnForeignLogReceived(string condition, string stackTrace, LogType type) {
            if (_suppressForeignCapture) {
                return;
            }

            LogRecord record = new LogRecord(
                Interlocked.Increment(ref _sequence),
                ForeignTag,
                condition,
                ToLevel(type),
                LogChannel.Prod,
                _clock.Elapsed.TotalMilliseconds,
                CurrentFrame(),
                null,
                0,
                string.IsNullOrEmpty(stackTrace) ? null : stackTrace,
                0,
                captured: true);

            Emit(in record);
        }

        private static LogLevel ToLevel(LogType type) {
            switch (type) {
                case LogType.Warning:
                    return LogLevel.Warning;
                case LogType.Error:
                case LogType.Exception:
                case LogType.Assert:
                    return LogLevel.Error;
                default:
                    return LogLevel.Log;
            }
        }

        private static int ResolveContextId(Object context) {
            // Both the == overload and GetInstanceID reach into native code, so they are only
            // valid on the main thread. A plain reference check keeps the off-thread path safe.
            if ((object)context == null || Thread.CurrentThread.ManagedThreadId != _mainThreadId) {
                return 0;
            }
            return context.GetInstanceID();
        }

        private static int CurrentFrame() {
            // Time.frameCount throws off the main thread, so background records inherit the
            // frame of the most recent main-thread log. Close enough to order them by.
            if (Thread.CurrentThread.ManagedThreadId == _mainThreadId) {
                _lastKnownFrame = Time.frameCount;
            }
            return _lastKnownFrame;
        }
    }
}
