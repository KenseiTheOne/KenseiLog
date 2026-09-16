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
            }
        }

        /// <summary>
        /// The next record id. Public so a view can build records from a source of its own -
        /// the editor window seeds itself from Unity's console - without colliding with ours.
        /// </summary>
        public static long NextSequence() {
            return Interlocked.Increment(ref _sequence);
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

            bool suppressed = _suppressForeignCapture;
            _reportingSinkFailure = true;
            _suppressForeignCapture = true;
            try {
                UnityEngine.Debug.LogError(
                    "KenseiLog: sink " + sink.GetType().Name + " threw and will not be reported again this session (" +
                    exception.Message + ")");
            } catch (Exception) {
                // Nothing left to report through.
            } finally {
                // Restored rather than cleared: a sink that throws from inside its own Debug
                // call leaves the flag raised, and clearing it here would hand the rest of that
                // sink's work a flag it never set.
                _suppressForeignCapture = suppressed;
                _reportingSinkFailure = false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Write(string tag, string message, LogLevel level, LogChannel channel, Object context, string file, int line) {
            string stackTrace = _config.CaptureStackTraceOnError && level == LogLevel.Error
                ? new StackTrace(2, true).ToString()
                : null;

            LogRecord record = new LogRecord(
                Interlocked.Increment(ref _sequence),
                // Normalised here rather than at each call site. A null tag is easy to pass by
                // accident - Log.Prod(config?.NetTag, ...) is enough - and it survives all the
                // way to the viewers, where it throws out of a dictionary lookup or a palette
                // hash with a stack that never mentions the call that caused it.
                string.IsNullOrEmpty(tag) ? UntaggedTag : tag,
                message ?? string.Empty,
                level,
                channel,
                _clock.Elapsed.TotalMilliseconds,
                CurrentFrame(),
                file,
                line,
                stackTrace,
                ResolveContextId(context));

            Emit(in record);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void OnRuntimeInitialize() {
            Initialize();
            _clock.Restart();
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
                    // moves across: a capacity change that emptied the viewer would trade one
                    // silent surprise for another.
                    MemorySink resized = new MemorySink(capacity);
                    LogRecord[] carried = new LogRecord[_overlaySink.Buffer.Count];
                    int copied = _overlaySink.Buffer.CopyNewerThan(0, carried);
                    for (int i = 0; i < copied; i++) {
                        resized.Write(in carried[i]);
                    }
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
