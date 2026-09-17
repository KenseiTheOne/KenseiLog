using System;

namespace KenseiLog.Editor {
    /// <summary>What a reload settled on for the editor's session file.</summary>
    public readonly struct SessionDecision {
        /// <summary>Whether the window's history came back out of the file.</summary>
        public readonly bool Seeded;

        /// <summary>The file to carry on writing, or null to start one.</summary>
        public readonly string ContinuePath;

        public SessionDecision(bool seeded, string continuePath) {
            Seeded = seeded;
            ContinuePath = continuePath;
        }
    }

    /// <summary>
    /// What installing the editor sink should do about the session file, worked out from the
    /// state it is handed and nothing else.
    /// <para>
    /// Apart because these are the decisions that have gone wrong three times: a process that
    /// had no business opening a file of its own, a path that was no longer the file being
    /// written, and carrying on with a file made conditional on reading it back. None could be
    /// reached from a check while the reasoning lived inside a method only a domain reload calls.
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
        /// Whether to read the history back, and which file to carry on writing.
        /// <para>
        /// The two are decided together but are not the same question, and tying them together
        /// is what went wrong: carrying on used to require the records coming back, because
        /// without them the numbering restarted and the file would end up unsorted. The numbering
        /// is carried across on its own now - see <see cref="LogCore.CurrentSequence"/> - so a
        /// reload that does not read anything back still keeps to the file. Two reloads did not
        /// read anything back: entering play mode with Clear on Play set, which is the reload
        /// people do dozens of times a day, and a rotation on the last record before the reload,
        /// which leaves a file holding a header and nothing else.
        /// </para>
        /// <para>
        /// <paramref name="seedFrom"/> is the read itself, handed in so that what decides can be
        /// checked without a file, and what is decided can be checked with one.
        /// </para>
        /// </summary>
        public static SessionDecision Resolve(bool continuing, bool writeSessionFile, bool worthSeeding,
                                              string rememberedPath, Func<string, bool> fileExists,
                                              Func<string, bool> seedFrom) {
            if (!continuing || !writeSessionFile || rememberedPath == null || !fileExists(rememberedPath)) {
                return new SessionDecision(false, null);
            }

            return new SessionDecision(worthSeeding && seedFrom(rememberedPath), rememberedPath);
        }
    }
}
