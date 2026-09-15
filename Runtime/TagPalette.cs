using UnityEngine;

namespace KenseiLog {
    /// <summary>
    /// Derives a stable colour from a tag name, so a tag looks the same across sessions,
    /// machines and views without anyone configuring anything.
    /// <para>
    /// The hue comes from the root segment, so Combat.Damage and Combat.AI read as relatives;
    /// depth only shifts brightness. Saturation and brightness are passed in, because the
    /// editor window and the in-game overlay sit on very different backgrounds.
    /// </para>
    /// </summary>
    public static class TagPalette {
        public static Color For(string tag, float saturation, float brightness, float depthStep) {
            int dot = tag.IndexOf('.');
            string root = dot < 0 ? tag : tag.Substring(0, dot);
            float hue = Hash(root) % 3600 / 3600f;
            float shifted = Mathf.Clamp(brightness + depthStep * Mathf.Min(Depth(tag), 3), 0.30f, 0.97f);
            return Color.HSVToRGB(hue, saturation, shifted);
        }

        /// <summary>
        /// FNV-1a rather than string.GetHashCode: the runtime is free to randomise its string
        /// hash per process, which would repaint every tag on each restart.
        /// </summary>
        public static uint Hash(string value) {
            unchecked {
                uint hash = 2166136261;
                for (int i = 0; i < value.Length; i++) {
                    hash ^= value[i];
                    hash *= 16777619;
                }
                return hash;
            }
        }

        private static int Depth(string tag) {
            int depth = 0;
            for (int i = 0; i < tag.Length; i++) {
                if (tag[i] == '.') {
                    depth++;
                }
            }
            return depth;
        }
    }
}
