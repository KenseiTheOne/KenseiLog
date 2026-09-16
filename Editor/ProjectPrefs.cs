using UnityEngine;

namespace KenseiLog.Editor {
    /// <summary>
    /// Builds EditorPrefs keys that belong to one project.
    /// <para>
    /// EditorPrefs is stored per user and per Unity install, not per project, so a plain key
    /// is shared by every project the editor has ever opened. Column choices arriving from
    /// another project are a surprise; the folded-tag set is worse, because it accumulates the
    /// tags of all of them and nothing ever removes one.
    /// </para>
    /// <para>
    /// The project folder is what separates them. A project that moves starts again from the
    /// defaults, which is a better trade than one that never stops mixing.
    /// </para>
    /// </summary>
    internal static class ProjectPrefs {
        private static readonly string _prefix = "KenseiLog." + ProjectId() + ".";

        public static string Key(string name) =>
            _prefix + name;

        private static string ProjectId() {
            // FNV-1a rather than string.GetHashCode: the key has to mean the same thing on the
            // next editor launch, and nothing promises that of a runtime hash.
            string path = Application.dataPath.Replace('\\', '/').ToLowerInvariant();
            uint hash = 2166136261;
            for (int i = 0; i < path.Length; i++) {
                hash = (hash ^ path[i]) * 16777619;
            }
            return hash.ToString("x8");
        }
    }
}
