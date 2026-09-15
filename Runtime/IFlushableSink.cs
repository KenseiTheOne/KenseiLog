namespace KenseiLog {
    /// <summary>
    /// A sink that buffers and therefore has something to lose when the app stops.
    /// <see cref="LogCore.FlushSinks"/> reaches these at the moments that matter.
    /// </summary>
    public interface IFlushableSink {
        void Flush();
    }
}
