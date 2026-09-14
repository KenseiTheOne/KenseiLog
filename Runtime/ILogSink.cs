namespace KenseiLog {
    /// <summary>
    /// Receives every record that passes through <see cref="LogCore"/>.
    /// Implementations must tolerate calls from any thread: Unity raises captured log
    /// callbacks off the main thread, and jobs log too.
    /// </summary>
    public interface ILogSink {
        void Write(in LogRecord record);
    }
}
