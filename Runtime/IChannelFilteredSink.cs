namespace KenseiLog {
    /// <summary>
    /// Implemented by a sink that turns a channel away, so that nothing is built for a channel
    /// no sink would take.
    /// <para>
    /// A record costs a stack trace on the error path, and in a development build the only sink
    /// is usually the file sink with the dev channel off - so every Log.Dev call there was
    /// building a record to be dropped by the first sink that looked at it. A sink that does
    /// not implement this is asked for everything, which is the safe answer for one this
    /// package knows nothing about.
    /// </para>
    /// <para>
    /// Answer without taking a lock: this is called while the sink list is held.
    /// </para>
    /// </summary>
    public interface IChannelFilteredSink {
        bool Accepts(LogChannel channel);
    }
}
