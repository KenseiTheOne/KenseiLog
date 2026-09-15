using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace KenseiLog.Editor {
    /// <summary>
    /// Editor-side tag colours. The hue comes from <see cref="TagPalette"/> so the window and
    /// the in-game overlay agree on which tag is which; only saturation and brightness differ,
    /// pinned per editor skin to keep text on top of the colour readable.
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

            Color color = _cachedProSkin
                ? TagPalette.For(tag, 0.50f, 0.88f, -0.08f)
                : TagPalette.For(tag, 0.62f, 0.68f, 0.08f);
            _cache[tag] = color;
            return color;
        }
    }
}
