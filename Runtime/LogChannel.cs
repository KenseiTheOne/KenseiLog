namespace KenseiLog {
    /// <summary>
    /// Which audience a log record is written for.
    /// <para>
    /// Dev calls are erased by the compiler outside the editor and development builds,
    /// so they cost nothing in release. Prod calls are always compiled and are meant to
    /// survive into shipped builds, where they are the only diagnostics available.
    /// </para>
    /// </summary>
    public enum LogChannel : byte {
        Dev = 0,
        Prod = 1
    }
}
