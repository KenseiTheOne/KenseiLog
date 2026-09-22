using System;
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
        // Measured from where the finger went down, not from one event to the next. A single
        // frame's movement is a flick detector: at a phone's scale six units in one event is
        // most of a thousand pixels a second, so a slow deliberate scroll never crossed it and
        // the release selected a row the reader meant only to pass. Displacement rather than
        // distance travelled, so that a long press with a tremor in it stays a press.
        //
        // Squared once here because it is compared against a squared length.
        private const float DragThresholdSquared = 6f * 6f;

        // The bubble is three chips - error, warning, log, in that order and always all three,
        // so the eye learns a position instead of re-reading a sentence that changes shape as
        // events arrive. Its width is fixed for the same reason: one thing moving on the screen
        // is the game, and a badge that grows as it counts is a second.
        private const float BubbleHeight = 34f;
        private const float BubblePad = 7f;
        private const float ChipGap = 7f;
        private const float IconSize = 14f;
        // Deliberately smaller than ChipGap, so each mark groups with its own number rather than
        // with the chip beside it.
        private const float IconGap = 3f;
        // The marks are rasterised at this and then resampled to whatever the matrix scale makes
        // of a fourteen point icon - smaller than the mask on a desktop, larger on a phone.
        // Sixteen samples a texel and a mip chain, so the edge is already smooth either way.
        private const int IconTexSize = 32;

        private static readonly Color _metaColor = new Color(0.58f, 0.58f, 0.63f);
        private static readonly Color _iconOffColor = new Color(0.58f, 0.58f, 0.63f, 0.5f);
        private static readonly Color _invertedInk = new Color(0.07f, 0.07f, 0.09f);
        // Uneven on purpose: three lines of text, not a stack of equal bars, which would read as
        // a hamburger menu.
        private static readonly float[] _logBarWidths = { 1f, 0.62f, 0.84f };
        private static readonly string[] _countShapes = { "999", "9.9k", "999k", "999M", "1B+" };
        private static readonly Comparison<TagRow> _byTagName = (left, right) => string.CompareOrdinal(left.Tag, right.Tag);
        private static readonly string[] _levelCaptions = { "Log", "Warn", "Err" };

        private static LogOverlay _instance;

        private readonly LogFilter _filter = new LogFilter { Name = "Overlay" };
        // The same index the editor window keeps, so that collapsing repeats is written once.
        // The rows beside it are this viewer's own: it redraws every frame where the window
        // polls fifteen times a second, so it cannot format a row per pass.
        private readonly LogIndex _index;
        private readonly List<Row> _visible = new List<Row>();
        private readonly List<int> _keptSlots = new List<int>();
        private readonly List<LogRingBuffer.TagCount> _tagCensus = new List<LogRingBuffer.TagCount>();
        private readonly List<TagRow> _tagRows = new List<TagRow>();

        private MemorySink _sink;
        private LogRecord[] _scratch;
        private long _lastSequence;
        private int _lastVersion = -1;
        private float _configuredScale;

        private bool _open;
        private bool _listBuilt;
        private bool _showTags;
        private bool _followTail = true;
        private long _selected = -1;
        private long _detailSequence = -1;
        private string _detailBody;
        private readonly string[] _bubbleCounts = new string[3];
        // The level whose chip is inverted, or -1 when nothing is worth lighting up for.
        private int _bubbleAccent = -1;
        private bool _bubbleDirty = true;
        private float _digitSlot;
        private float _bubbleWidth;
        private readonly string[] _levelLabels = new string[3];
        private bool _levelLabelsDirty = true;
        private bool _tagRowsDirty;
        private Vector2 _scroll;
        private Vector2 _tagScroll;
        private Vector2 _bubble = new Vector2(12f, 12f);
        private readonly BubbleGesture _bubbleGesture = new BubbleGesture();
        private bool _didDrag;
        private Vector2 _pressPosition;

        private GUIStyle _panel;
        private GUIStyle _pane;
        private GUIStyle _row;
        private GUIStyle _bar;
        private GUIStyle _button;
        private GUIStyle _detail;
        private GUIStyle _meta;
        private GUIStyle _count;
        private GUIStyle _counter;
        private Texture2D _panelTex;
        private Texture2D _paneTex;
        private Texture2D _rowTex;
        private Texture2D _barTex;
        private Texture2D _chipTex;
        private Texture2D _warningIconTex;
        private Texture2D _errorIconTex;

        private LogOverlay() {
            _index = new LogIndex(_filter);
        }

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
            _instance.Attach(sink, scale);
        }

        private void Attach(MemorySink sink, float scale) {
            _configuredScale = scale;
            if (!ReferenceEquals(_sink, sink)) {
                // A different sink means a different session: its sequences start again from the
                // bottom, so the watermark and everything counted from the old one have to go
                // with it or the viewer shows a mixture of the two and ingests nothing new.
                _sink = sink;
                _lastVersion = -1;
                Reset();
            }
            if (_scratch == null || _scratch.Length < sink.Buffer.Capacity) {
                _scratch = new LogRecord[sink.Buffer.Capacity];
            }
        }

        /// <summary>Open or close the viewer, for wiring into a debug menu of your own.</summary>
        public static bool IsOpen {
            get => _instance != null && _instance._open;
            set {
                if (_instance != null) {
                    _instance.SetOpen(value);
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
        /// Whether repeats are folded into one row with a count, as the editor window's Collapse
        /// does. Changing it rebuilds the list, which is why it is not a plain field like the
        /// one above: the fold happens as records are taken in, so the ones already taken have
        /// to be taken again.
        /// </summary>
        public static bool Collapsed {
            get => _instance != null && _instance._filter.Collapse;
            set {
                if (_instance == null || _instance._filter.Collapse == value) {
                    return;
                }
                _instance._filter.Collapse = value;
                _instance.RebuildVisible();
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
            bool wasBuilt = overlay._listBuilt;
            if (!wasBuilt) {
                overlay.RebuildVisible();
            }

            for (int i = overlay._visible.Count - 1; i >= 0; i--) {
                if (overlay._visible[i].Level == level) {
                    overlay._selected = overlay._visible[i].Sequence;
                    overlay._scroll.y = Mathf.Max(0f, i * RowHeight - RowHeight * 4f);
                    // The caller asked for this record specifically; following the tail would
                    // scroll it back off the screen on the next log line.
                    overlay._followTail = false;
                    break;
                }
            }

            if (!wasBuilt && !overlay._open) {
                // Built only to find the record, and nothing reads a row while the bubble is
                // collapsed. Keeping them would reinstate the per-record cost that the
                // collapsed path exists to avoid, for the rest of the run. The selection is a
                // sequence rather than an index, so it survives - and the rebuild on opening
                // scrolls to it.
                overlay._visible.Clear();
                overlay._listBuilt = false;
            }
        }

        public static void Remove() {
            // The sink goes with the viewer. Left registered it would keep filling a buffer
            // nothing displays, and LogCore would then see an overlay sink already in place and
            // skip building one the next time the overlay is switched back on.
            LogCore.DetachOverlaySink();
            if (_instance == null) {
                return;
            }
            Destroy(_instance.gameObject);
            _instance = null;
        }

        private void SetOpen(bool value) {
            if (_open == value) {
                return;
            }
            _open = value;
            if (value) {
                if (!_listBuilt) {
                    RebuildVisible();
                }
            } else {
                _visible.Clear();
                _listBuilt = false;
            }
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
            DestroyTexture(ref _warningIconTex);
            DestroyTexture(ref _errorIconTex);
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
                // Never let the watermark walk backwards. The buffer keeps itself in order, so
                // this should not come up - but if anything ever did hand back an out-of-order
                // batch, assigning blindly would re-copy it on the next poll and keep doing so,
                // growing the list without end. Clamping turns that into a duplicated row.
                _lastSequence = Math.Max(_lastSequence, record.Sequence);

                // A row costs a colour conversion, two substrings and a concatenation, and
                // while the bubble is collapsed nothing reads one. The list is built from the
                // buffer when the viewer opens instead, so a build shipped with the overlay
                // enabled pays for the counts and nothing else.
                if (_listBuilt) {
                    TakeRow(in record);
                }
            }
            // Not gated on what was copied. This runs only when the sink's version moved, which
            // means records arrived - and in a burst longer than the ring most of them are
            // already gone by now, counted by the sink and never seen here.
            _bubbleDirty = true;
            _levelLabelsDirty = true;
            _tagRowsDirty = true;

            int drop = 0;
            if (_listBuilt && _index.PruneBelow(oldest, _keptSlots, out int droppedFromFront)) {
                if (droppedFromFront >= 0) {
                    drop = droppedFromFront;
                    _visible.RemoveRange(0, drop);
                } else {
                    // A collapsed view loses rows from anywhere, not off the front, so the
                    // survivors are moved down over the gaps in the order the index moved them.
                    for (int i = 0; i < _keptSlots.Count; i++) {
                        _visible[i] = _visible[_keptSlots[i]];
                    }
                    _visible.RemoveRange(_keptSlots.Count, _visible.Count - _keptSlots.Count);
                }
            }
            if (drop > 0) {
                // Rows left the top of the list, so the same offset now points further down it.
                // Without this the content slides under the finger every time the ring wraps,
                // which on a busy scene is continuous.
                _scroll.y = Mathf.Max(0f, _scroll.y - drop * RowHeight);
            }

            if (_followTail) {
                _scroll.y = float.MaxValue;
            }
        }

        private void Reset() {
            _index.Clear();
            _visible.Clear();
            _tagCensus.Clear();
            _tagRows.Clear();
            _lastSequence = 0;
            _selected = -1;
            _detailSequence = -1;
            _detailBody = null;
            _bubbleDirty = true;
            _levelLabelsDirty = true;
            _tagRowsDirty = true;
            _followTail = true;
            _scroll = Vector2.zero;
        }

        /// <summary>
        /// Rebuilds the visible list from the buffer: after a filter change, and when the viewer
        /// opens, since nothing is kept up to date while it is collapsed.
        /// </summary>
        private void RebuildVisible() {
            _index.Clear();
            _visible.Clear();
            int copied = _sink.Buffer.CopyNewerThan(0, _scratch);
            for (int i = 0; i < copied; i++) {
                TakeRow(in _scratch[i]);
            }
            _listBuilt = true;
            ScrollToSelectionOrTail();
        }

        /// <summary>
        /// Offers a record to the index and keeps this viewer's formatted row beside whichever
        /// slot came back: a new slot appends one, and a slot a repeat folded into replaces the
        /// row there, because the index points a folded row at the newest occurrence.
        /// </summary>
        private void TakeRow(in LogRecord record) {
            int slot = _index.Append(in record);
            if (slot < 0) {
                return;
            }
            if (slot == _visible.Count) {
                _visible.Add(new Row(in record));
                return;
            }
            _visible[slot] = new Row(in record);
        }

        /// <summary>
        /// Puts the view where the reader would want it after a rebuild: on the selected
        /// record when there is one - SelectNewest picks one while the viewer is still
        /// collapsed - and on the newest line otherwise.
        /// </summary>
        private void ScrollToSelectionOrTail() {
            if (_selected >= 0) {
                for (int i = _visible.Count - 1; i >= 0; i--) {
                    if (_visible[i].Sequence == _selected) {
                        _scroll.y = Mathf.Max(0f, i * RowHeight - RowHeight * 4f);
                        _followTail = false;
                        return;
                    }
                }
            }
            _followTail = true;
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

            // Laid out inside the safe area. Clear and Close sit in the corners, which on a
            // phone with a cutout or a home indicator is exactly where the system takes the
            // touches - a button under one cannot be pressed at all. safeArea is y-up from the
            // bottom of the screen and GUI is y-down from the top, hence the flip.
            Rect safe = Screen.safeArea;
            Matrix4x4 previous = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(
                new Vector3(safe.xMin, Screen.height - safe.yMax, 0f),
                Quaternion.identity,
                new Vector3(scale, scale, 1f));

            float width = safe.width / scale;
            float height = safe.height / scale;

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
                _pressPosition = current.mousePosition;
            } else if (current.type == EventType.MouseDrag &&
                       (current.mousePosition - _pressPosition).sqrMagnitude > DragThresholdSquared) {
                _didDrag = true;
            }
        }

        private void DrawBubble(float width, float height) {
            // Rebuilt when a record arrives rather than per pass: this is the state the overlay
            // is in for almost all of a session, and OnGUI runs at least twice a frame.
            if (_bubbleDirty) {
                RebuildBubble();
            }

            _bubble.x = Mathf.Clamp(_bubble.x, 0f, Mathf.Max(0f, width - _bubbleWidth));
            _bubble.y = Mathf.Clamp(_bubble.y, 0f, Mathf.Max(0f, height - BubbleHeight));

            Rect rect = new Rect(_bubble.x, _bubble.y, _bubbleWidth, BubbleHeight);
            Event current = Event.current;
            // Read before GUI.Button, which consumes the MouseUp it answers: a reset keyed on
            // the type afterwards never ran, and one drag left the bubble unopenable for good.
            EventType type = current.type;

            if (type == EventType.MouseDown) {
                _bubbleGesture.Press(rect.Contains(current.mousePosition), _bubble);
            }

            // _didDrag carries the same threshold the rows use. Without it any movement at all
            // began a drag, and a finger never lands without a pixel or two of travel.
            if (type == EventType.MouseDrag &&
                _bubbleGesture.TryDrag(_didDrag, current.mousePosition - _pressPosition, out Vector2 dragged)) {
                _bubble = dragged;
                current.Use();
                return;
            }

            GUI.Box(rect, GUIContent.none, _panel);

            // The whole bubble is one control, drawn before the chips so that they sit on top of
            // it, and its answer acted on after they are drawn rather than inside the call:
            // SetOpen swaps the overlay to its open state, and doing that half way through
            // drawing the collapsed one would leave a frame drawn from two different states.
            bool tapped = _bubbleGesture.Opens(GUI.Button(rect, GUIContent.none, _button));

            float x = rect.x + BubblePad;
            for (int level = 2; level >= 0; level--) {
                DrawChip(x, rect.y, level);
                x += IconSize + IconGap + _digitSlot + ChipGap;
            }

            if (tapped) {
                SetOpen(true);
            }
            if (type == EventType.MouseUp) {
                _bubbleGesture.Release();
            }
        }

        /// <summary>
        /// A count narrow enough for a chip: exact to three digits, then thousands to one
        /// decimal, then whole thousands, then whole millions, and pinned at a billion.
        /// <para>
        /// The ladder has to terminate, and the top has to be a shape the slot was measured
        /// from. Left open it returned "1000M" at a billion - five characters into a label
        /// measured for four and clipped rather than overflowed, so a billion records read as a
        /// thousand. Understating at a ceiling the reader can see is one thing; a number a
        /// million times too small, in the digits this counting exists to make trustworthy, is
        /// another. Every branch here returns one of the shapes in <see cref="_countShapes"/>.
        /// </para>
        /// </summary>
        private static string Compact(long count) {
            if (count < 1000L) {
                return count.ToString(CultureInfo.InvariantCulture);
            }
            if (count < 10000L) {
                return (count / 1000L).ToString(CultureInfo.InvariantCulture) + "." +
                       (count % 1000L / 100L).ToString(CultureInfo.InvariantCulture) + "k";
            }
            if (count < 1000000L) {
                return (count / 1000L).ToString(CultureInfo.InvariantCulture) + "k";
            }
            if (count < 1000000000L) {
                return (count / 1000000L).ToString(CultureInfo.InvariantCulture) + "M";
            }
            return "1B+";
        }

        private void RebuildBubble() {
            _bubbleAccent = -1;
            for (int level = 2; level >= 0; level--) {
                long count = _sink.LevelCount((LogLevel)level);
                _bubbleCounts[level] = count == 0L ? null : Compact(count);

                // Only a warning or an error is worth lighting up for. A badge that brightens
                // because the game logged at all is the panel nobody asked for.
                if (_bubbleAccent < 0 && count > 0 && level > (int)LogLevel.Log) {
                    _bubbleAccent = level;
                }
            }
            _bubbleDirty = false;
        }

        /// <summary>
        /// One level's mark and count. A level with nothing to report keeps its place and loses
        /// its digits, so the layout never moves; the worst level present is drawn inverted - its
        /// own colour as a plate with the mark and digits knocked out of it - which is the part
        /// the corner of the eye picks up without reading anything.
        /// </summary>
        private void DrawChip(float x, float y, int level) {
            bool accent = _bubbleAccent == level;
            string count = _bubbleCounts[level];
            float chipWidth = IconSize + IconGap + _digitSlot;

            if (accent) {
                GUI.color = LevelColor((LogLevel)level);
                GUI.DrawTexture(new Rect(x - 4f, y + 4f, chipWidth + 8f, BubbleHeight - 8f), _chipTex);
            }

            GUI.color = accent ? _invertedInk : count == null ? _iconOffColor : LevelColor((LogLevel)level);
            DrawIcon(x, y + (BubbleHeight - IconSize) * 0.5f, level);

            if (count != null) {
                GUI.Label(new Rect(x + IconSize + IconGap, y, _digitSlot, BubbleHeight), count, _count);
            }
            GUI.color = Color.white;
        }

        /// <summary>
        /// The three marks, told apart by silhouette before colour: a round badge holding an
        /// exclamation for an error, a solid triangle for a warning, horizontal lines for a log.
        /// Round against pointed against level is a distinction that survives a colour-blind
        /// reader, a screen in sunlight, and a screenshot pasted into a report in greyscale -
        /// which is the state the two coloured chips arrive in most often.
        /// </summary>
        private void DrawIcon(float x, float y, int level) {
            if (level == (int)LogLevel.Error) {
                GUI.DrawTexture(new Rect(x, y, IconSize, IconSize), _errorIconTex);
                return;
            }
            if (level == (int)LogLevel.Warning) {
                GUI.DrawTexture(new Rect(x, y, IconSize, IconSize), _warningIconTex);
                return;
            }

            // Drawn as quads rather than rasterised like the other two: a two point line baked
            // into a mask resamples into a grey smear at this size, while a stretched quad keeps
            // its edge at every scale.
            float bar = IconSize * 0.17f;
            float step = IconSize * 0.3f;
            float top = y + (IconSize - (bar + step * 2f)) * 0.5f;
            for (int i = 0; i < _logBarWidths.Length; i++) {
                GUI.DrawTexture(new Rect(x, top + step * i, IconSize * _logBarWidths[i], bar), _chipTex);
            }
        }

        private void DrawPanel(float width, float height) {
            Rect panel = new Rect(0f, 0f, width, height);
            GUI.Box(panel, GUIContent.none, _panel);

            DrawBar(width);

            float detailHeight = _selected >= 0 ? Mathf.Min(height * 0.35f, 180f) : 0f;
            float tagWidth = _showTags ? Mathf.Min(200f, width * 0.5f) : 0f;
            float bodyHeight = height - BarHeight - detailHeight;

            // The pane takes its width out of the list instead of covering it. IMGUI gives a
            // press to the first control drawn under the pointer, so a pane drawn on top of the
            // list was visible but dead: the row button underneath took every tap first.
            if (tagWidth > 0f) {
                DrawTagPane(new Rect(0f, BarHeight, tagWidth, bodyHeight));
            }
            DrawList(new Rect(tagWidth, BarHeight, width - tagWidth, bodyHeight));

            if (detailHeight > 0f) {
                DrawDetail(new Rect(0f, height - detailHeight, width, detailHeight));
            }
        }

        private void DrawBar(float width) {
            GUI.Box(new Rect(0f, 0f, width, BarHeight), GUIContent.none, _bar);
            float x = 4f;

            // Same reason as the bubble's label, which was cached and these were not: a count
            // changes when a record arrives, and OnGUI runs at least twice a frame.
            if (_levelLabelsDirty) {
                for (int i = 0; i < _levelLabels.Length; i++) {
                    _levelLabels[i] = _levelCaptions[i] + " " + Compact(_sink.LevelCount((LogLevel)i));
                }
                _levelLabelsDirty = false;
            }

            x += LevelButton(x, LogLevel.Log);
            x += LevelButton(x, LogLevel.Warning);
            x += LevelButton(x, LogLevel.Error);

            GUI.color = _showTags ? new Color(0.5f, 0.9f, 1f) : Color.white;
            if (GUI.Button(new Rect(x, 3f, 54f, BarHeight - 6f), "Tags", _button)) {
                _showTags = !_showTags;
            }
            GUI.color = Color.white;
            x += 58f;

            if (_filter.Tags.Count > 0) {
                if (GUI.Button(new Rect(x, 3f, 54f, BarHeight - 6f), "All tags", _button)) {
                    _filter.Tags.Clear();
                    RebuildVisible();
                }
                x += 58f;
            }

            // Short caption when the bar is tight, which on a phone held upright it is: the
            // level counts, Tags, Clear and Close were already most of the width. Drawn at
            // whichever size fits rather than dropped, because a control that silently is not
            // there is the failure this package keeps having.
            float room = width - 122f - x;
            if (room > 30f) {
                float span = room > 72f ? 68f : 44f;
                GUI.color = _filter.Collapse ? new Color(0.5f, 0.9f, 1f) : Color.white;
                if (GUI.Button(new Rect(x, 3f, span, BarHeight - 6f), span > 50f ? "Collapse" : "Fold", _button)) {
                    _filter.Collapse = !_filter.Collapse;
                    RebuildVisible();
                }
                GUI.color = Color.white;
            }

            if (GUI.Button(new Rect(width - 118f, 3f, 54f, BarHeight - 6f), "Clear", _button)) {
                _sink.Clear();
                Reset();
            }
            if (GUI.Button(new Rect(width - 60f, 3f, 54f, BarHeight - 6f), "Close", _button)) {
                SetOpen(false);
            }
        }

        private float LevelButton(float x, LogLevel level) {
            bool shown = _filter.LevelAllowed(level);
            GUI.color = shown ? LevelColor(level) : new Color(0.45f, 0.45f, 0.45f);
            if (GUI.Button(new Rect(x, 3f, 58f, BarHeight - 6f), _levelLabels[(int)level], _button)) {
                _filter.SetLevel(level, !shown);
                RebuildVisible();
            }
            GUI.color = Color.white;
            return 60f;
        }

        private void DrawList(Rect area) {
            float content = _visible.Count * RowHeight;
            _scroll = GUI.BeginScrollView(area, _scroll, new Rect(0f, 0f, area.width - 16f, content));

            // Sticking to the newest line is the point of a log viewer on a device, but only
            // while the reader has not scrolled away from it to look at something.
            _followTail = content <= area.height || _scroll.y >= content - area.height - 1f;

            int first = Mathf.Max(0, Mathf.FloorToInt(_scroll.y / RowHeight));
            int last = Mathf.Min(_visible.Count, first + Mathf.CeilToInt(area.height / RowHeight) + 1);

            bool showFrame = area.width > 520f;
            for (int i = first; i < last; i++) {
                DrawRow(new Rect(0f, i * RowHeight, area.width - 16f, RowHeight), _visible[i],
                    _index.Repeats[i], showFrame);
            }

            GUI.EndScrollView();

            if (_visible.Count == 0) {
                GUI.Label(new Rect(area.x, area.y + 12f, area.width, 40f), EmptyHint(), _detail);
            }
        }

        private void DrawRow(Rect rect, Row row, int repeats, bool showFrame) {
            bool selected = row.Sequence == _selected;
            if (selected) {
                GUI.Box(rect, GUIContent.none, _bar);
            }

            GUI.color = row.Stripe;
            GUI.DrawTexture(new Rect(rect.x + 2f, rect.y + 4f, 3f, rect.height - 8f), _chipTex);

            float x = rect.x + 8f;
            GUI.color = _metaColor;
            // Frame only when there is room for it. On a phone the message needs the width more
            // than the frame number does, and the detail pane carries it anyway.
            if (showFrame) {
                GUI.Label(new Rect(x, rect.y, 48f, rect.height), row.Frame, _meta);
                x += 52f;
            }
            GUI.Label(new Rect(x, rect.y, 44f, rect.height), row.Time, _meta);
            x += 50f;

            // The counter sits at the right end and takes its width out of the message, which
            // is the only thing on the row that can give any up.
            float counter = repeats > 1 ? 46f : 0f;

            GUI.color = LevelColor(row.Level);
            if (GUI.Button(new Rect(x, rect.y, rect.xMax - x - counter, rect.height), row.Text, _row) && !_didDrag) {
                _selected = selected ? -1 : row.Sequence;
            }

            if (repeats > 1) {
                // Plain text rather than a chip: a filled block at the end of a row reads as a
                // control to press, and this one does nothing. The count carries itself.
                //
                // Held within reach of the message rather than pinned to the right edge. On a
                // phone the two are the same place; on a desktop window the edge is half a
                // screen away from the text it belongs to, and a number that far off reads as
                // belonging to whatever the game happens to be drawing under it.
                float at = Mathf.Min(rect.xMax - counter, x + 330f);
                GUI.color = _metaColor;
                GUI.Label(new Rect(at, rect.y, counter - 6f, rect.height), "x" + Compact(repeats), _counter);
            }
            GUI.color = Color.white;
        }

        private void DrawDetail(Rect area) {
            GUI.Box(area, GUIContent.none, _bar);
            if (!_sink.Buffer.TryGetBySequence(_selected, out LogRecord record)) {
                _selected = -1;
                _detailSequence = -1;
                _detailBody = null;
                return;
            }

            // Built once per selection. Reassembling it per pass meant two or more copies of
            // the record and its stack trace every frame - on a 4KB trace, about a megabyte of
            // garbage a second for as long as the record stayed open.
            if (_detailSequence != _selected) {
                _detailBody = DetailText(in record);
                _detailSequence = _selected;
            }

            GUI.Label(new Rect(area.x + 6f, area.y + 4f, area.width - 70f, area.height - 8f), _detailBody, _detail);

            if (GUI.Button(new Rect(area.xMax - 60f, area.y + 4f, 54f, 24f), "Copy", _button)) {
                GUIUtility.systemCopyBuffer = _detailBody;
            }
        }

        private static string DetailText(in LogRecord record) {
            string body = record.Tag + "  ·  " + record.Level + "  ·  " + record.Channel +
                          "  ·  frame " + record.Frame + "  ·  " + Seconds(record.TimeMs) + "s" +
                          "\n" + record.Message;
            if (!string.IsNullOrEmpty(record.File)) {
                body += "\n" + record.File + ":" + record.Line;
            }
            if (!string.IsNullOrEmpty(record.StackTrace)) {
                body += "\n" + record.StackTrace;
            }
            return body;
        }

        private void DrawTagPane(Rect area) {
            // Opaque, not the translucent panel background: this sits on top of the list, and
            // at 94% the rows underneath still showed through enough to make it unreadable.
            GUI.Box(area, GUIContent.none, _pane);
            GUI.color = new Color(0f, 0f, 0f, 0.5f);
            GUI.DrawTexture(new Rect(area.xMax - 1f, area.y, 1f, area.height), _chipTex);
            GUI.color = Color.white;
            EnsureTagRows();

            float content = _tagRows.Count * RowHeight;
            _tagScroll = GUI.BeginScrollView(area, _tagScroll, new Rect(0f, 0f, area.width - 16f, content));

            float y = 0f;
            for (int i = 0; i < _tagRows.Count; i++) {
                TagRow tag = _tagRows[i];
                bool active = _filter.Tags.Contains(tag.Tag);
                Rect row = new Rect(0f, y, area.width - 16f, RowHeight);
                if (active) {
                    GUI.Box(row, GUIContent.none, _bar);
                }

                // The colour is the tag's identity, so it stays on whether the tag is selected
                // or not; selection is carried by the highlight and the text brightness.
                GUI.color = tag.Chip;
                GUI.DrawTexture(new Rect(row.x + 5f, row.y + 7f, 8f, 8f), _chipTex);

                GUI.color = active ? Color.white : new Color(0.62f, 0.62f, 0.66f);
                if (GUI.Button(new Rect(row.x + 18f, row.y, row.width - 18f, row.height), tag.Label, _row) && !_didDrag) {
                    _filter.ToggleTag(tag.Tag);
                    RebuildVisible();
                }
                GUI.color = Color.white;
                y += RowHeight;
            }

            GUI.EndScrollView();
        }

        /// <summary>
        /// Rebuilds the census and the rows drawn from it. Same reason the list rows are cached:
        /// the label is a concatenation and the chip is an HSV conversion, and neither changes
        /// between records - only between passes, of which there are hundreds a second.
        /// </summary>
        private void EnsureTagRows() {
            if (!_tagRowsDirty) {
                return;
            }

            _sink.Buffer.CopyTagCounts(_tagCensus);
            _tagRows.Clear();
            for (int i = 0; i < _tagCensus.Count; i++) {
                // A tag's number has to be what tapping it produces, and tapping Combat brings
                // in Combat.Damage as well. Counting the key alone would put a number on the row
                // that the list it opens does not match - a smaller lie than the one this
                // replaced, but the same kind. Quadratic over distinct tags, which are tens.
                int held = 0;
                for (int other = 0; other < _tagCensus.Count; other++) {
                    if (LogFilter.TagMatches(_tagCensus[other].Tag, _tagCensus[i].Tag)) {
                        held += _tagCensus[other].Held;
                    }
                }
                _tagRows.Add(new TagRow(_tagCensus[i].Tag, held));
            }
            _tagRows.Sort(_byTagName);
            _tagRowsDirty = false;
        }

        private string EmptyHint() {
            EnsureTagRows();
            if (_tagCensus.Count == 0) {
                return "  No records yet.";
            }
            for (int i = 0; i < _filter.Tags.Count; i++) {
                bool held = false;
                for (int other = 0; other < _tagCensus.Count; other++) {
                    if (LogFilter.TagMatches(_tagCensus[other].Tag, _filter.Tags[i])) {
                        held = true;
                        break;
                    }
                }
                if (!held) {
                    // Not "has not appeared": it may well have, and been pushed out since. The
                    // pane no longer lists it, so say which of the two happened rather than
                    // leaving the reader to guess at an empty list.
                    return "  Nothing under '" + _filter.Tags[i] + "' is still in the buffer.";
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

            _counter = new GUIStyle(_row) {
                fontSize = 12,
                alignment = TextAnchor.MiddleRight,
                padding = new RectOffset(0, 0, 0, 0)
            };

            _count = new GUIStyle(_row) {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                // _row pads four points at each side, which would inflate every width measured
                // from this style and push the digits away from the mark they belong to.
                padding = new RectOffset(0, 0, 0, 0)
            };

            _warningIconTex = MaskTexture(InTriangle);
            _errorIconTex = MaskTexture(InErrorBadge);

            // Measured over every shape Compact can produce rather than guessed: the widest of
            // them is not the same string on every platform, because the font is not either.
            _digitSlot = 0f;
            for (int i = 0; i < _countShapes.Length; i++) {
                _digitSlot = Mathf.Max(_digitSlot, _count.CalcSize(new GUIContent(_countShapes[i])).x);
            }
            _bubbleWidth = BubblePad * 2f + ChipGap * 2f + (IconSize + IconGap + _digitSlot) * 3f;
        }

        /// <summary>
        /// Rasterises a shape into a mask: white throughout, with coverage in the alpha.
        /// <para>
        /// Built here rather than shipped, because the package ships no assets - turning the flag
        /// on is the whole installation - and drawn rather than typed, because a character like a
        /// warning sign is not in every font a player build falls back to, and a glyph that is
        /// missing on the device is a box on the screen that nothing in the editor would show.
        /// </para>
        /// </summary>
        private static Texture2D MaskTexture(Func<float, float, bool> inside) {
            Texture2D texture = new Texture2D(IconTexSize, IconTexSize, TextureFormat.RGBA32, true) {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp
            };

            Color[] pixels = new Color[IconTexSize * IconTexSize];
            for (int y = 0; y < IconTexSize; y++) {
                for (int x = 0; x < IconTexSize; x++) {
                    int hits = 0;
                    for (int sy = 0; sy < 4; sy++) {
                        for (int sx = 0; sx < 4; sx++) {
                            float u = (x + (sx + 0.5f) * 0.25f) / IconTexSize;
                            float v = (y + (sy + 0.5f) * 0.25f) / IconTexSize;
                            if (inside(u, v)) {
                                hits++;
                            }
                        }
                    }
                    // Texture rows run bottom-up; the shapes below are described top-down.
                    pixels[(IconTexSize - 1 - y) * IconTexSize + x] = new Color(1f, 1f, 1f, hits / 16f);
                }
            }

            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        private static bool InTriangle(float u, float v) {
            if (v < 0.08f || v > 0.9f) {
                return false;
            }
            return Mathf.Abs(u - 0.5f) <= 0.46f * (v - 0.08f) / 0.82f;
        }

        /// <summary>
        /// A round badge with an exclamation cut out of it.
        /// <para>
        /// Not a cross, which is what this was first drawn as and is the worse answer twice
        /// over. A bare saltire is the universal close affordance, and this is a small tappable
        /// thing in the corner of a screen - the one place it would be read as a button that
        /// dismisses it. And a round badge carrying an exclamation is already what an error
        /// looks like in the console beside it, so the reader has the meaning before the legend.
        /// </para>
        /// <para>
        /// The cut-out is what keeps it from being a blob. A filled shape and the warning's
        /// filled triangle differ only by having a point, which is not enough once the two
        /// colours have collapsed together for a reader who cannot tell red from amber.
        /// </para>
        /// </summary>
        private static bool InErrorBadge(float u, float v) {
            return InOctagon(u, v, 1f) && !InBang(u, v, 0.62f);
        }

        private static bool InOctagon(float u, float v, float size) {
            float cu = Mathf.Abs(u - 0.5f) / size;
            float cv = Mathf.Abs(v - 0.5f) / size;
            return cu <= 0.45f && cv <= 0.45f && cu + cv <= 0.64f;
        }

        private static bool InBang(float u, float v, float size) {
            float cu = (u - 0.5f) / size;
            float cv = (v - 0.5f) / size;
            if (Mathf.Abs(cu) <= 0.13f && cv >= -0.4f && cv <= 0.1f) {
                return true;
            }

            float dv = cv - 0.29f;
            return cu * cu + dv * dv <= 0.145f * 0.145f;
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

        /// <summary>
        /// Everything a row needs to draw itself, worked out once when the record arrives.
        /// <para>
        /// OnGUI runs at least twice per frame, so doing this per row per pass meant taking the
        /// buffer's lock, re-deriving the tag colour and cutting two substrings thousands of
        /// times a second - all to produce the same characters, because none of it changes
        /// after the record is written.
        /// </para>
        /// </summary>
        private readonly struct Row {
            public readonly long Sequence;
            public readonly LogLevel Level;
            public readonly Color Stripe;
            public readonly string Frame;
            public readonly string Time;
            public readonly string Text;

            public Row(in LogRecord record) {
                Sequence = record.Sequence;
                Level = record.Level;
                Stripe = TagPalette.For(record.Tag, 0.55f, 0.85f, -0.08f);
                Frame = record.Frame.ToString(CultureInfo.InvariantCulture);
                Time = Seconds(record.TimeMs);
                Text = ShortTag(record.Tag) + "  " + FirstLine(record.Message);
            }
        }

        /// <summary>A tag as the pane draws it, worked out when the counts change.</summary>
        private readonly struct TagRow {
            public readonly string Tag;
            public readonly string Label;
            public readonly Color Chip;

            public TagRow(string tag, int count) {
                Tag = tag;
                Label = tag + "  " + count;
                Chip = TagPalette.For(tag, 0.6f, 0.9f, -0.08f);
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
