using System.Runtime.CompilerServices;
using UnityEngine;

// For ConsoleLine below: the editor reads the console back after a domain reload and has to
// know which of its lines this sink wrote. Internal rather than public so the format stays
// free to change without it being API.
[assembly: InternalsVisibleTo("KenseiLog.Editor")]

namespace KenseiLog {
    /// <summary>
    /// Mirrors records into the Unity console. Off by default; turn it on through
    /// <see cref="LogConfig.MirrorToUnityConsole"/> when you want errors to keep colouring
    /// the console and pausing play mode, or when a build should write to the player log.
    /// <para>
    /// The related object is not forwarded: turning a stored instance id back into an object
    /// is an editor-only operation, and the log window already offers Ping for that.
    /// </para>
    /// </summary>
    public sealed class UnityConsoleSink : ILogSink {
        public void Write(in LogRecord record) {
            // A captured record came out of the console to begin with. Writing it back would
            // print every foreign message a second time under the Unity tag.
            if (record.Captured) {
                return;
            }

            string line = ConsoleLine(record.Tag, record.Message);

            // Our own Debug.Log call comes straight back through the foreign-log handler.
            // The flag breaks that loop without hiding genuine engine messages.
            LogCore.SuppressForeignCapture = true;
            try {
                switch (record.Level) {
                    case LogLevel.Warning:
                        Debug.LogWarning(line);
                        break;
                    case LogLevel.Error:
                        Debug.LogError(line);
                        break;
                    default:
                        Debug.Log(line);
                        break;
                }
            } finally {
                LogCore.SuppressForeignCapture = false;
            }
        }

        /// <summary>
        /// The line this sink writes to the console for a record. The one place the format
        /// lives: after a domain reload the editor matches the console's lines against the
        /// session file, and a mirrored line it fails to recognise as this comes back into the
        /// window a second time, under the Unity tag.
        /// </summary>
        internal static string ConsoleLine(string tag, string message) => tag + ": " + message;
    }
}
