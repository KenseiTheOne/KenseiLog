using System;
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

        private static readonly object _sinkLock = new object();
        private static readonly Stopwatch _clock = new Stopwatch();

        private static ILogSink[] _sinks = Array.Empty<ILogSink>();
        private static UnityConsoleSink _consoleSink;
        private static LogConfig _config = LogConfig.Default();
        private static long _sequence;
        private static int _mainThreadId;
        private static int _lastKnownFrame;

        [ThreadStatic] private static bool _suppressForeignCapture;

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
            }
        }

        public static void Emit(in LogRecord record) {
            ILogSink[] sinks = _sinks;
            for (int i = 0; i < sinks.Length; i++) {
                sinks[i].Write(in record);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Write(string tag, string message, LogLevel level, LogChannel channel, Object context, string file, int line) {
            string stackTrace = _config.CaptureStackTraceOnError && level == LogLevel.Error
                ? new StackTrace(2, true).ToString()
                : null;

            LogRecord record = new LogRecord(
                Interlocked.Increment(ref _sequence),
                tag,
                message,
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
                0);

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
