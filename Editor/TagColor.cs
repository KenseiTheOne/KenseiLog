using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace KenseiLog.Editor {
    /// <summary>
    /// Assigns every tag a stable colour derived from its name, so a tag looks the same
    /// across sessions and machines without anyone configuring anything.
    /// <para>
    /// The hue comes from the root segment, so Combat.Damage and Combat.AI read as relatives;
    /// depth only shifts brightness. Saturation and brightness are pinned per editor skin,
    /// which keeps text on top of the colour readable in both themes.
    /// </para>
    /// </summary>
    public static class TagColor {
        private static readonly Dictionary<string, Color> _cache = new Dictionary<string, Color>();
        private static bool _cachedProSkin;

        public static Color For(string tag) {
            if (_cachedProSkin != EditorGUIUtility.isProSkin) {
                _cache.Clear();
                _cachedProSkin = EditorGUIUtility.isProSkin;
            }
            if (_cache.TryGetValue(tag, out Color cached)) {
                return cached;
            }

            Color color = Build(tag, _cachedProSkin);
            _cache[tag] = color;
            return color;
        }

        private static Color Build(string tag, bool proSkin) {
            int rootLength = tag.IndexOf('.');
            string root = rootLength < 0 ? tag : tag.Substring(0, rootLength);

            float hue = Hash(root) % 3600 / 3600f;
            int depth = Depth(tag);
            float saturation = proSkin ? 0.50f : 0.62f;
            float brightness = proSkin ? 0.88f : 0.68f;
            float step = 0.08f * Mathf.Min(depth, 3);

            brightness = Mathf.Clamp(proSkin ? brightness - step : brightness + step, 0.35f, 0.95f);
            return Color.HSVToRGB(hue, saturation, brightness);
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

        /// <summary>
        /// FNV-1a rather than string.GetHashCode: the runtime is free to randomise its hash
        /// per process, which would repaint every tag on each editor restart.
        /// </summary>
        private static uint Hash(string value) {
            unchecked {
                uint hash = 2166136261;
                for (int i = 0; i < value.Length; i++) {
                    hash ^= value[i];
                    hash *= 16777619;
                }
                return hash;
            }
        }
    }
}
