namespace KenseiLog {
    /// <summary>
    /// Runtime tunables. Apply with <see cref="LogCore.Configure"/> from a
    /// RuntimeInitializeOnLoadMethod of your own if the defaults do not fit.
    /// <para>
    /// Editor-only settings such as how many records the window keeps live on the editor
    /// side instead, so a shipped build carries no knobs it cannot use.
    /// </para>
    /// </summary>
    public struct LogConfig {
        /// <summary>
        /// Capture a managed stack trace for Error records. Unwinding the stack is by far the
        /// most expensive part of logging, which is why the other levels never do it and rely
        /// on the caller file and line instead.
        /// </summary>
        public bool CaptureStackTraceOnError;

        /// <summary>Mirror every record into the Unity console as a regular Debug.Log.</summary>
        public bool MirrorToUnityConsole;

        /// <summary>
        /// Fold logs that bypass this facade into the pipeline: engine exceptions, errors from
        /// third-party packages, anything calling Debug.Log directly. Without this a shipped
        /// log file is missing exactly the unhandled exception you needed to see.
        /// </summary>
        public bool CaptureForeignLogs;

        public static LogConfig Default() {
            return new LogConfig {
                CaptureStackTraceOnError = true,
                MirrorToUnityConsole = false,
                CaptureForeignLogs = true
            };
        }
    }
}
