namespace KenseiLog {
    /// <summary>
    /// Runtime tunables. Apply with <see cref="LogCore.Configure"/> from a
    /// RuntimeInitializeOnLoadMethod of your own if the defaults do not fit.
    /// <para>
    /// Editor-only settings such as whether the editor writes a session file of its own live
    /// on the editor side instead, so a shipped build carries no knobs it cannot use.
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

        /// <summary>
        /// Where the files go. Empty means persistentDataPath/logs, which is the only place a
        /// build can be relied on to write.
        /// <para>
        /// Changing it in a later <see cref="LogCore.Configure"/> starts a file in the new
        /// place; what was written before the change stays where it was written.
        /// </para>
        /// </summary>
        public string FileDirectory;

        /// <summary>Rotate the current file once it passes this size.</summary>
        public int FileSizeLimitKb;

        /// <summary>How many rotated files to keep alongside the current one.</summary>
        public int RetainedFileCount;

        /// <summary>
        /// How long buffered lines may sit unwritten. Errors bypass this and flush at once.
        /// </summary>
        public float FileFlushIntervalSeconds;

        /// <summary>
        /// Also write dev records to the file. On by default in a development build and off
        /// everywhere else - which is the same thing as on wherever it changes anything. A
        /// release build has no dev records to write, and the editor writes them to a session
        /// file of its own, so taking them here too would put every one on disk twice in play
        /// mode. A development build is the one place they exist and go nowhere else: without
        /// this they lived in the overlay's ring alone and were gone once it turned over, in the
        /// build handed to testers precisely so that somebody could read them.
        /// </summary>
        public bool FileIncludesDevChannel;

        /// <summary>
        /// Draw the in-game overlay: a small bubble that expands into a log viewer on the
        /// device. Off by default, because a debug panel appearing in someone's game
        /// uninvited is worse than having to ask for it.
        /// </summary>
        public bool ShowOverlay;

        /// <summary>
        /// How many records the overlay keeps. The ring is shared by every tag, both channels
        /// and the logs captured from outside this package, so one tag's share of it is a
        /// fraction - at five hundred it was a few dozen, and a tag would leave the pane while
        /// the session was still young.
        /// <para>
        /// A record is sixty four bytes and holds on to its message, around two hundred more.
        /// Four thousand of them is a megabyte and a half on a phone, counting the equal-sized
        /// scratch array the viewer keeps beside it.
        /// </para>
        /// </summary>
        public int OverlayRecordCapacity;

        /// <summary>UI scale for the overlay, or 0 to derive one from screen DPI.</summary>
        public float OverlayScale;

        public static LogConfig Default() {
            return new LogConfig {
                CaptureStackTraceOnError = true,
                MirrorToUnityConsole = false,
                FileDirectory = null,
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
#if DEVELOPMENT_BUILD && !UNITY_EDITOR
                FileIncludesDevChannel = true,
#else
                FileIncludesDevChannel = false,
#endif
                ShowOverlay = false,
                OverlayRecordCapacity = 4096,
                OverlayScale = 0f
            };
        }
    }
}
