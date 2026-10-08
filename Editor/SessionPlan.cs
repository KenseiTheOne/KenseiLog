using System;
using System.Collections.Generic;

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
    /// What installing the editor sink should do about the session file, and with what the
    /// console still holds, worked out from the state it is handed and nothing else.
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

        /// <summary>
        /// Whether entering play mode reloads the domain, which decides which side of the reload
        /// writes the line marking the entry: one written before it goes to a window that is
        /// about to be rebuilt without it. No reload only when Enter Play Mode Options are on and
        /// domain reload is among what they skip.
        /// <para>
        /// A forecast, and wrong in one direction only: with domain reload skipped, a script the
        /// editor has not compiled yet still reloads on the way in. The line is then written on
        /// both sides - twice in the file, once in the window - which is the cheaper way to be
        /// wrong than a window with no line at all.
        /// </para>
        /// </summary>
        public static bool EnteringPlayModeReloads(bool enterPlayModeOptionsEnabled, bool domainReloadSkipped) =>
            !enterPlayModeOptionsEnabled || !domainReloadSkipped;

        /// <summary>
        /// Which of the console's entries the session file does not already account for, by
        /// index into <paramref name="consoleMessages"/> - the message of each entry with its
        /// stack trace taken off. Matched by first line and by count: a line read back three
        /// times accounts for three entries, and a fourth is one the file genuinely missed.
        /// <para>
        /// A record of the file accounts for the line it put in the console. A captured one came
        /// out of the console to begin with, so that is its own message. One of the package's
        /// own is in the console only if <see cref="UnityConsoleSink"/> mirrored it, and then as
        /// <see cref="UnityConsoleSink.ConsoleLine"/> writes it, tag in front. Leaving those out
        /// brought every mirrored line back after a reload as a second record under the Unity
        /// tag.
        /// </para>
        /// <para>
        /// Not conditional on mirroring being on: this runs before the project has configured
        /// anything, and the file does not say whether a record was mirrored when it was
        /// written. The cost of assuming it was is a foreign line that reads exactly
        /// <c>Tag: message</c> of a record from the same session being taken for the mirror.
        /// </para>
        /// </summary>
        public static List<int> Unaccounted(IReadOnlyList<LogRecord> fromFile, IReadOnlyList<string> consoleMessages) {
            Dictionary<string, int> accounted = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < fromFile.Count; i++) {
                LogRecord record = fromFile[i];
                string line = record.Captured
                    ? record.Message
                    : UnityConsoleSink.ConsoleLine(record.Tag, record.Message);
                string key = FirstLine(line);
                accounted.TryGetValue(key, out int seen);
                accounted[key] = seen + 1;
            }

            List<int> unaccounted = new List<int>();
            for (int i = 0; i < consoleMessages.Count; i++) {
                string key = FirstLine(consoleMessages[i]);
                if (accounted.TryGetValue(key, out int left) && left > 0) {
                    accounted[key] = left - 1;
                } else {
                    unaccounted.Add(i);
                }
            }
            return unaccounted;
        }

        private static string FirstLine(string message) {
            if (string.IsNullOrEmpty(message)) {
                return string.Empty;
            }
            int newline = message.IndexOf('\n');
            return newline < 0 ? message : message.Substring(0, newline);
        }
    }
}
