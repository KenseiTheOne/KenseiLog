using UnityEngine;

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

            string line = record.Tag + ": " + record.Message;

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
    }
}
