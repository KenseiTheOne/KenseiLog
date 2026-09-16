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

        /// <summary>
        /// Write records to a rolling JSONL file under persistentDataPath/logs. This is the
        /// only diagnostics a shipped build has, so it is on by default - except on WebGL,
        /// where there is no file to fetch afterwards.
        /// </summary>
        public bool WriteToFile;

        /// <summary>Rotate the current file once it passes this size.</summary>
        public int FileSizeLimitKb;

        /// <summary>How many rotated files to keep alongside the current one.</summary>
        public int RetainedFileCount;

        /// <summary>
        /// How long buffered lines may sit unwritten. Errors bypass this and flush at once.
        /// </summary>
        public float FileFlushIntervalSeconds;

        /// <summary>
        /// Also write dev records to the file. Off by default: in a release build there are no
        /// dev records at all, and in the editor they would bury the prod events worth keeping.
        /// </summary>
        public bool FileIncludesDevChannel;

        /// <summary>
        /// Draw the in-game overlay: a small bubble that expands into a log viewer on the
        /// device. Off by default, because a debug panel appearing in someone's game
        /// uninvited is worse than having to ask for it.
        /// </summary>
        public bool ShowOverlay;

        /// <summary>How many records the overlay keeps. Kept small; phones are not desktops.</summary>
        public int OverlayRecordCapacity;

        /// <summary>UI scale for the overlay, or 0 to derive one from screen DPI.</summary>
        public float OverlayScale;

        public static LogConfig Default() {
            return new LogConfig {
                CaptureStackTraceOnError = true,
                MirrorToUnityConsole = false,
                CaptureForeignLogs = true,
#if UNITY_WEBGL && !UNITY_EDITOR
                // persistentDataPath on WebGL is a browser-backed virtual filesystem inside the
                // page. Nothing can fetch the file afterwards, and at the default limits the
                // rolling history would hold about twenty megabytes of the heap for a build that
                // can never read it back. Turn it on deliberately if you have a way to.
                WriteToFile = false,
#else
                WriteToFile = true,
#endif
                FileSizeLimitKb = 5 * 1024,
                RetainedFileCount = 3,
                FileFlushIntervalSeconds = 5f,
                FileIncludesDevChannel = false,
                ShowOverlay = false,
                OverlayRecordCapacity = 512,
                OverlayScale = 0f
            };
        }
    }
}
