namespace KenseiLog.Editor {
    /// <summary>
    /// What installing the editor sink should do about the session file, worked out from the
    /// state it is handed and nothing else.
    /// <para>
    /// Apart because these are the decisions that have gone wrong twice: a process that had no
    /// business opening a file of its own, and a path that was no longer the file being written.
    /// Neither could be reached from a check while the reasoning lived inside a method only a
    /// domain reload calls.
    /// </para>
    /// </summary>
    public static class SessionPlan {
        /// <summary>
        /// Whether this process owns the editor's session file. Only the editor itself does. An
        /// asset import worker and an out-of-process profiler reload the domain exactly as the
        /// editor does and resolve the same directory, having the same project path, so left to
        /// themselves they open files beside it that the editor later mistakes for its own.
        /// </summary>
        public static bool Installs(bool isAssetImportWorker, bool isSecondaryProcess) =>
            !isAssetImportWorker && !isSecondaryProcess;

        /// <summary>
        /// The file to read the window's history back from, or null to leave it to the console.
        /// </summary>
        public static string SeedFrom(bool continuing, bool writeSessionFile, bool worthSeeding, string rememberedPath) =>
            continuing && writeSessionFile && worthSeeding ? rememberedPath : null;

        /// <summary>
        /// The file to carry on writing, or null to start one. Carrying on makes sense only once
        /// the records have come back: after a read that found none, appending would put records
        /// numbered from one underneath records numbered in the hundreds, leaving the file
        /// unsorted and every lookup into it wrong.
        /// </summary>
        public static string ContinueFrom(bool continuing, bool seeded, string rememberedPath) =>
            continuing && seeded ? rememberedPath : null;
    }
}
