using System;
using System.Collections.Generic;

namespace KenseiLog.Editor {
    public sealed class TagNode {
        public string Segment;
        public string FullTag;
        public int Count;
        public readonly List<TagNode> Children = new List<TagNode>();
    }

    /// <summary>
    /// Turns the flat set of tags seen this session into a tree along dot boundaries,
    /// so Combat.Damage and Combat.AI group under Combat without anyone declaring it.
    /// A parent's count includes its descendants.
    /// </summary>
    public static class TagTree {
        public static List<TagNode> Build(Dictionary<string, int> tagCounts) {
            List<TagNode> roots = new List<TagNode>();

            foreach (KeyValuePair<string, int> pair in tagCounts) {
                string tag = pair.Key;
                List<TagNode> level = roots;
                int start = 0;

                while (true) {
                    int dot = tag.IndexOf('.', start);
                    int end = dot < 0 ? tag.Length : dot;
                    string segment = tag.Substring(start, end - start);

                    TagNode node = Find(level, segment);
                    if (node == null) {
                        node = new TagNode {
                            Segment = segment,
                            FullTag = tag.Substring(0, end)
                        };
                        level.Add(node);
                    }
                    node.Count += pair.Value;

                    if (dot < 0) {
                        break;
                    }
                    level = node.Children;
                    start = dot + 1;
                }
            }

            Sort(roots);
            return roots;
        }

        private static TagNode Find(List<TagNode> nodes, string segment) {
            for (int i = 0; i < nodes.Count; i++) {
                if (string.Equals(nodes[i].Segment, segment, StringComparison.Ordinal)) {
                    return nodes[i];
                }
            }
            return null;
        }

        private static void Sort(List<TagNode> nodes) {
            nodes.Sort((a, b) => string.CompareOrdinal(a.Segment, b.Segment));
            for (int i = 0; i < nodes.Count; i++) {
                Sort(nodes[i].Children);
            }
        }
    }
}
