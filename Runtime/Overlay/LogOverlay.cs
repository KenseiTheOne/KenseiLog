using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace KenseiLog {
    /// <summary>
    /// On-device log viewer drawn straight over the game.
    /// <para>
    /// Open via menu: none - it appears as a small bubble once
    /// <see cref="LogConfig.ShowOverlay"/> is on, and expands when tapped.
    /// </para>
    /// <para>
    /// Drawn with IMGUI on purpose. A debug overlay that needs a Canvas, a prefab, a
    /// PanelSettings asset or a package dependency is a debug overlay nobody installs, and
    /// IMGUI reads touches through Event.current, so projects on the new Input System are
    /// unaffected. It also cannot collide with the game's own EventSystem.
    /// </para>
    /// </summary>
    [AddComponentMenu("")]
    public sealed class LogOverlay : MonoBehaviour {
        private const float RowHeight = 22f;
        private const float BarHeight = 28f;
        private const float DragThreshold = 6f;

        private static LogOverlay _instance;

        private readonly LogFilter _filter = new LogFilter { Name = "Overlay" };
        private readonly List<long> _visible = new List<long>();
        private readonly Dictionary<string, int> _tagCounts = new Dictionary<string, int>();
        private readonly int[] _levelCounts = new int[3];

        private MemorySink _sink;
        private LogRecord[] _scratch;
        private long _lastSequence;
        private int _lastVersion = -1;
        private float _configuredScale;

        private bool _open;
        private bool _showTags;
        private long _selected = -1;
        private Vector2 _scroll;
        private Vector2 _tagScroll;
        private Vector2 _bubble = new Vector2(12f, 12f);
        private bool _draggingBubble;
        private bool _didDrag;

        private GUIStyle _panel;
        private GUIStyle _pane;
        private GUIStyle _row;
        private GUIStyle _bar;
        private GUIStyle _button;
        private GUIStyle _detail;
        private GUIStyle _meta;
        private Texture2D _panelTex;
        private Texture2D _paneTex;
        private Texture2D _rowTex;
        private Texture2D _barTex;
        private Texture2D _chipTex;

        public static void Ensure(MemorySink sink, float scale) {
            if (!Application.isPlaying) {
                return;
            }
            if (_instance == null) {
                GameObject host = new GameObject("KenseiLog Overlay") {
                    hideFlags = HideFlags.HideAndDontSave
                };
                DontDestroyOnLoad(host);
                _instance = host.AddComponent<LogOverlay>();
            }
            _instance._sink = sink;
            _instance._configuredScale = scale;
            _instance._scratch = new LogRecord[sink.Buffer.Capacity];
        }

        /// <summary>Open or close the viewer, for wiring into a debug menu of your own.</summary>
        public static bool IsOpen {
            get => _instance != null && _instance._open;
            set {
                if (_instance != null) {
                    _instance._open = value;
                }
            }
        }

        /// <summary>Show or hide the tag list inside the viewer.</summary>
        public static bool TagPaneVisible {
            get => _instance != null && _instance._showTags;
            set {
                if (_instance != null) {
                    _instance._showTags = value;
                }
            }
        }

        /// <summary>
        /// Jump to the most recent record at this level and expand it. Handy on a "something
        /// broke, show me" button.
        /// </summary>
        public static void SelectNewest(LogLevel level) {
            if (_instance == null) {
                return;
            }
            LogOverlay overlay = _instance;
            for (int i = overlay._visible.Count - 1; i >= 0; i--) {
                if (overlay._sink.Buffer.TryGetBySequence(overlay._visible[i], out LogRecord record) && record.Level == level) {
                    overlay._selected = record.Sequence;
                    overlay._scroll.y = Mathf.Max(0f, i * RowHeight - RowHeight * 4f);
                    return;
                }
            }
        }

        public static void Remove() {
            if (_instance == null) {
                return;
            }
            Destroy(_instance.gameObject);
            _instance = null;
        }

        private void Update() {
            if (_sink == null || _sink.Version == _lastVersion) {
                return;
            }
            _lastVersion = _sink.Version;
            Ingest();
        }

        private void OnDestroy() {
            DestroyTexture(ref _panelTex);
            DestroyTexture(ref _paneTex);
            DestroyTexture(ref _rowTex);
            DestroyTexture(ref _barTex);
            DestroyTexture(ref _chipTex);
        }

        // =====================================================================
        // Data
        // =====================================================================

        private void Ingest() {
            LogRingBuffer buffer = _sink.Buffer;
            long oldest = buffer.OldestSequence;

            if (buffer.Count == 0) {
                Reset();
                return;
            }
            if (oldest > _lastSequence + 1) {
                _lastSequence = oldest - 1;
            }

            int copied = buffer.CopyNewerThan(_lastSequence, _scratch);
            for (int i = 0; i < copied; i++) {
                LogRecord record = _scratch[i];
                _lastSequence = record.Sequence;
                _levelCounts[(int)record.Level]++;
                _tagCounts.TryGetValue(record.Tag, out int seen);
                _tagCounts[record.Tag] = seen + 1;

                if (_filter.Matches(in record)) {
                    _visible.Add(record.Sequence);
                }
            }

            int drop = 0;
            while (drop < _visible.Count && _visible[drop] < oldest) {
                drop++;
            }
            if (drop > 0) {
                _visible.RemoveRange(0, drop);
            }
        }

        private void Reset() {
            _visible.Clear();
            _tagCounts.Clear();
            _lastSequence = 0;
            _selected = -1;
            for (int i = 0; i < _levelCounts.Length; i++) {
                _levelCounts[i] = 0;
            }
        }

        private void Refilter() {
            _visible.Clear();
            int copied = _sink.Buffer.CopyNewerThan(0, _scratch);
            for (int i = 0; i < copied; i++) {
                if (_filter.Matches(in _scratch[i])) {
                    _visible.Add(_scratch[i].Sequence);
                }
            }
            _scroll.y = float.MaxValue;
        }

        // =====================================================================
        // Drawing
        // =====================================================================

        private void OnGUI() {
            if (_sink == null) {
                return;
            }

            EnsureStyles();

            float scale = _configuredScale > 0f
                ? _configuredScale
                : Mathf.Clamp(Screen.dpi > 1f ? Screen.dpi / 160f : Screen.height / 720f, 1f, 4f);

            Matrix4x4 previous = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));

            float width = Screen.width / scale;
            float height = Screen.height / scale;

            TrackDrag();

            if (_open) {
                DrawPanel(width, height);
            } else {
                DrawBubble(width, height);
            }

            GUI.matrix = previous;
        }

        /// <summary>
        /// A tap that moved is a scroll, not a press. Tracked here so row buttons can ignore
        /// the release that ends a drag.
        /// </summary>
        private void TrackDrag() {
            Event current = Event.current;
            if (current.type == EventType.MouseDown) {
                _didDrag = false;
            } else if (current.type == EventType.MouseDrag && current.delta.sqrMagnitude > DragThreshold) {
                _didDrag = true;
            }
        }

        private void DrawBubble(float width, float height) {
            const float size = 34f;
            _bubble.x = Mathf.Clamp(_bubble.x, 0f, Mathf.Max(0f, width - size * 3f));
            _bubble.y = Mathf.Clamp(_bubble.y, 0f, Mathf.Max(0f, height - size));

            Rect rect = new Rect(_bubble.x, _bubble.y, size * 3f, size);
            Event current = Event.current;

            if (current.type == EventType.MouseDrag && rect.Contains(current.mousePosition)) {
                _bubble += current.delta;
                _draggingBubble = true;
                current.Use();
                return;
            }

            GUI.Box(rect, GUIContent.none, _panel);

            int errors = _levelCounts[(int)LogLevel.Error];
            int warnings = _levelCounts[(int)LogLevel.Warning];
            int total = _levelCounts[0] + _levelCounts[1] + _levelCounts[2];
            string label = errors > 0
                ? errors + " error" + (errors == 1 ? string.Empty : "s")
                : warnings > 0
                    ? warnings + " warning" + (warnings == 1 ? string.Empty : "s")
                    : total + " logs";
            GUI.color = errors > 0 ? new Color(1f, 0.45f, 0.4f) : warnings > 0 ? new Color(1f, 0.8f, 0.3f) : Color.white;

            if (GUI.Button(rect, label, _button) && !_draggingBubble) {
                _open = true;
            }
            GUI.color = Color.white;

            if (current.type == EventType.MouseUp) {
                _draggingBubble = false;
            }
        }

        private void DrawPanel(float width, float height) {
            Rect panel = new Rect(0f, 0f, width, height);
            GUI.Box(panel, GUIContent.none, _panel);

            DrawBar(width);

            float detailHeight = _selected >= 0 ? Mathf.Min(height * 0.35f, 180f) : 0f;
            Rect list = new Rect(0f, BarHeight, width, height - BarHeight - detailHeight);
            DrawList(list);

            if (detailHeight > 0f) {
                DrawDetail(new Rect(0f, height - detailHeight, width, detailHeight));
            }
            if (_showTags) {
                DrawTagPane(new Rect(0f, BarHeight, Mathf.Min(200f, width * 0.5f), height - BarHeight));
            }
        }

        private void DrawBar(float width) {
            GUI.Box(new Rect(0f, 0f, width, BarHeight), GUIContent.none, _bar);
            float x = 4f;

            x += LevelButton(x, LogLevel.Log, "Log");
            x += LevelButton(x, LogLevel.Warning, "Warn");
            x += LevelButton(x, LogLevel.Error, "Err");

            GUI.color = _showTags ? new Color(0.5f, 0.9f, 1f) : Color.white;
            if (GUI.Button(new Rect(x, 3f, 54f, BarHeight - 6f), "Tags", _button)) {
                _showTags = !_showTags;
            }
            GUI.color = Color.white;
            x += 58f;

            if (_filter.Tags.Count > 0 && GUI.Button(new Rect(x, 3f, 54f, BarHeight - 6f), "All tags", _button)) {
                _filter.Tags.Clear();
                Refilter();
            }

            if (GUI.Button(new Rect(width - 118f, 3f, 54f, BarHeight - 6f), "Clear", _button)) {
                _sink.Clear();
                Reset();
            }
            if (GUI.Button(new Rect(width - 60f, 3f, 54f, BarHeight - 6f), "Close", _button)) {
                _open = false;
            }
        }

        private float LevelButton(float x, LogLevel level, string label) {
            bool shown = _filter.LevelAllowed(level);
            GUI.color = shown ? LevelColor(level) : new Color(0.45f, 0.45f, 0.45f);
            if (GUI.Button(new Rect(x, 3f, 58f, BarHeight - 6f), label + " " + _levelCounts[(int)level], _button)) {
                _filter.SetLevel(level, !shown);
                Refilter();
            }
            GUI.color = Color.white;
            return 60f;
        }

        private void DrawList(Rect area) {
            float content = _visible.Count * RowHeight;
            _scroll = GUI.BeginScrollView(area, _scroll, new Rect(0f, 0f, area.width - 16f, content));

            int first = Mathf.Max(0, Mathf.FloorToInt(_scroll.y / RowHeight));
            int last = Mathf.Min(_visible.Count, first + Mathf.CeilToInt(area.height / RowHeight) + 1);

            bool showFrame = area.width > 520f;
            for (int i = first; i < last; i++) {
                if (!_sink.Buffer.TryGetBySequence(_visible[i], out LogRecord record)) {
                    continue;
                }
                DrawRow(new Rect(0f, i * RowHeight, area.width - 16f, RowHeight), in record, showFrame);
            }

            GUI.EndScrollView();

            if (_visible.Count == 0) {
                GUI.Label(new Rect(area.x, area.y + 12f, area.width, 40f), EmptyHint(), _detail);
            }
        }

        private void DrawRow(Rect rect, in LogRecord record, bool showFrame) {
            bool selected = record.Sequence == _selected;
            if (selected) {
                GUI.Box(rect, GUIContent.none, _bar);
            }

            GUI.color = TagPalette.For(record.Tag, 0.55f, 0.85f, -0.08f);
            GUI.DrawTexture(new Rect(rect.x + 2f, rect.y + 4f, 3f, rect.height - 8f), _chipTex);

            float x = rect.x + 8f;
            GUI.color = new Color(0.58f, 0.58f, 0.63f);
            // Frame only when there is room for it. On a phone the message needs the width more
            // than the frame number does, and the detail pane carries it anyway.
            if (showFrame) {
                GUI.Label(new Rect(x, rect.y, 48f, rect.height), record.Frame.ToString(CultureInfo.InvariantCulture), _meta);
                x += 52f;
            }
            GUI.Label(new Rect(x, rect.y, 44f, rect.height), Seconds(record.TimeMs), _meta);
            x += 50f;

            GUI.color = LevelColor(record.Level);
            long sequence = record.Sequence;
            string text = ShortTag(record.Tag) + "  " + FirstLine(record.Message);
            if (GUI.Button(new Rect(x, rect.y, rect.xMax - x, rect.height), text, _row) && !_didDrag) {
                _selected = selected ? -1 : sequence;
            }
            GUI.color = Color.white;
        }

        private void DrawDetail(Rect area) {
            GUI.Box(area, GUIContent.none, _bar);
            if (!_sink.Buffer.TryGetBySequence(_selected, out LogRecord record)) {
                _selected = -1;
                return;
            }

            string body = record.Tag + "  ·  " + record.Level + "  ·  " + record.Channel +
                          "  ·  frame " + record.Frame + "  ·  " + Seconds(record.TimeMs) + "s" +
                          "\n" + record.Message;
            if (!string.IsNullOrEmpty(record.File)) {
                body += "\n" + record.File + ":" + record.Line;
            }
            if (!string.IsNullOrEmpty(record.StackTrace)) {
                body += "\n" + record.StackTrace;
            }

            GUI.Label(new Rect(area.x + 6f, area.y + 4f, area.width - 70f, area.height - 8f), body, _detail);

            if (GUI.Button(new Rect(area.xMax - 60f, area.y + 4f, 54f, 24f), "Copy", _button)) {
                GUIUtility.systemCopyBuffer = body;
            }
        }

        private void DrawTagPane(Rect area) {
            // Opaque, not the translucent panel background: this sits on top of the list, and
            // at 94% the rows underneath still showed through enough to make it unreadable.
            GUI.Box(area, GUIContent.none, _pane);
            GUI.color = new Color(0f, 0f, 0f, 0.5f);
            GUI.DrawTexture(new Rect(area.xMax - 1f, area.y, 1f, area.height), _chipTex);
            GUI.color = Color.white;
            float content = _tagCounts.Count * RowHeight;
            _tagScroll = GUI.BeginScrollView(area, _tagScroll, new Rect(0f, 0f, area.width - 16f, content));

            float y = 0f;
            foreach (KeyValuePair<string, int> pair in _tagCounts) {
                bool active = _filter.Tags.Contains(pair.Key);
                Rect row = new Rect(0f, y, area.width - 16f, RowHeight);
                if (active) {
                    GUI.Box(row, GUIContent.none, _bar);
                }

                // The colour is the tag's identity, so it stays on whether the tag is selected
                // or not; selection is carried by the highlight and the text brightness.
                GUI.color = TagPalette.For(pair.Key, 0.6f, 0.9f, -0.08f);
                GUI.DrawTexture(new Rect(row.x + 5f, row.y + 7f, 8f, 8f), _chipTex);

                GUI.color = active ? Color.white : new Color(0.62f, 0.62f, 0.66f);
                if (GUI.Button(new Rect(row.x + 18f, row.y, row.width - 18f, row.height), pair.Key + "  " + pair.Value, _row) && !_didDrag) {
                    _filter.ToggleTag(pair.Key);
                    Refilter();
                }
                GUI.color = Color.white;
                y += RowHeight;
            }

            GUI.EndScrollView();
        }

        private string EmptyHint() {
            if (_tagCounts.Count == 0) {
                return "  No records yet.";
            }
            for (int i = 0; i < _filter.Tags.Count; i++) {
                bool seen = false;
                foreach (string tag in _tagCounts.Keys) {
                    if (LogFilter.TagMatches(tag, _filter.Tags[i])) {
                        seen = true;
                        break;
                    }
                }
                if (!seen) {
                    return "  Tag '" + _filter.Tags[i] + "' has not appeared in this session.";
                }
            }
            return "  Nothing matches the current filter.";
        }

        // =====================================================================
        // Styles
        // =====================================================================

        private void EnsureStyles() {
            if (_panel != null) {
                return;
            }

            _panelTex = SolidTexture(new Color(0.09f, 0.09f, 0.11f, 0.94f));
            _paneTex = SolidTexture(new Color(0.12f, 0.12f, 0.15f, 1f));
            _barTex = SolidTexture(new Color(0.18f, 0.18f, 0.22f, 0.98f));
            _rowTex = SolidTexture(new Color(0f, 0f, 0f, 0f));
            _chipTex = SolidTexture(Color.white);

            _panel = new GUIStyle { normal = { background = _panelTex } };
            _pane = new GUIStyle { normal = { background = _paneTex } };
            _bar = new GUIStyle { normal = { background = _barTex } };

            _row = new GUIStyle {
                normal = { background = _rowTex, textColor = Color.white },
                hover = { background = _rowTex, textColor = Color.white },
                active = { background = _barTex, textColor = Color.white },
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip,
                fontSize = 12,
                padding = new RectOffset(4, 4, 0, 0)
            };

            _button = new GUIStyle(_row) {
                normal = { background = _barTex, textColor = Color.white },
                alignment = TextAnchor.MiddleCenter
            };

            _detail = new GUIStyle(_row) {
                alignment = TextAnchor.UpperLeft,
                wordWrap = true,
                clipping = TextClipping.Clip
            };

            _meta = new GUIStyle(_row) { alignment = TextAnchor.MiddleRight, fontSize = 11 };
        }

        private static Texture2D SolidTexture(Color color) {
            Texture2D texture = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }

        private static void DestroyTexture(ref Texture2D texture) {
            if (texture == null) {
                return;
            }
            Destroy(texture);
            texture = null;
        }

        private static Color LevelColor(LogLevel level) {
            switch (level) {
                case LogLevel.Warning:
                    return new Color(1f, 0.82f, 0.35f);
                case LogLevel.Error:
                    return new Color(1f, 0.45f, 0.4f);
                default:
                    return new Color(0.88f, 0.88f, 0.9f);
            }
        }

        private static string Seconds(double milliseconds) =>
            (milliseconds / 1000.0).ToString("0.00", CultureInfo.InvariantCulture);

        private static string ShortTag(string tag) {
            int dot = tag.LastIndexOf('.');
            return dot < 0 ? tag : tag.Substring(dot + 1);
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
