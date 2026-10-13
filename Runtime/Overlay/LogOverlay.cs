using System;
using System.Collections.Generic;
using System.Globalization;
using Unity.Profiling;
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
    /// <para>
    /// The drawing lives on a second component, <see cref="LogOverlayView"/>, so that it can be
    /// switched off while this one keeps reading: a disabled behaviour receives no IMGUI events,
    /// and that is the only state in which an IMGUI overlay costs nothing on a frame. It is off
    /// while <see cref="Visible"/> is false, and in <see cref="OverlayBubbleMode.OnProblem"/>
    /// until the first warning or error. The view runs without a Layout pass - nothing here uses
    /// GUILayout - and the screen metrics are read in Update when they change rather than per
    /// event. Collapsed, it draws one texture the bubble is baked into in Update, and nothing
    /// else. Toggling this component's own <c>enabled</c> does not hide anything; use
    /// <see cref="Visible"/>. GUI.depth is not honoured.
    /// </para>
    /// </summary>
    [AddComponentMenu("")]
    public sealed class LogOverlay : MonoBehaviour {
        private const float RowHeight = 22f;
        private const float BarHeight = 28f;
        private const float NoticeHeight = 20f;
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

        // The bubble is baked into one texture and drawn with one call. A count that changes is
        // baked again at most this often; a change of colour, or of size, at once. Four times a
        // second is as fast as anyone reads a number that is changing anyway.
        private const float BakeInterval = 0.25f;
        // Any constant will do: it keeps the bubble's control id apart from the game's own.
        private const int BubbleHint = 0x4B4C4F47;

        // The package's own digits, drawn as strokes rather than taken from a font: the glyphs are
        // baked into the bubble's texture, and a font cannot be read back into pixels on every
        // platform - nor is a font's bitmap at this size anything the bubble could not draw itself.
        // Centre lines in points, y down; every stroke is a capsule GlyphRadius thick each side.
        private const float GlyphHeight = 7.8f;
        private const float GlyphRadius = 0.85f;
        private const float GlyphTracking = 1f;
        private const float DigitWidth = 4.2f;
        private const string GlyphChars = "0123456789.kMB+";

        private static readonly Color _metaColor = new Color(0.58f, 0.58f, 0.63f);
        private static readonly Color _iconOffColor = new Color(0.58f, 0.58f, 0.63f, 0.5f);
        private static readonly Color _invertedInk = new Color(0.07f, 0.07f, 0.09f);
        // Uneven on purpose: three lines of text, not a stack of equal bars, which would read as
        // a hamburger menu.
        private static readonly float[] _logBarWidths = { 1f, 0.62f, 0.84f };

        // Worst first, in the bubble and in the bar alike. They are the same three counts, and
        // with an order of their own each, opening the bubble laid them out the other way round
        // under the thumb that had just tapped it.
        private static readonly LogLevel[] _levelOrder = { LogLevel.Error, LogLevel.Warning, LogLevel.Log };
        private static readonly string[] _countShapes = { "999", "9.9k", "999k", "999M", "1B+" };
        private static readonly Comparison<TagRow> _byTagName = (left, right) => string.CompareOrdinal(left.Tag, right.Tag);
        private static readonly string[] _levelCaptions = { "Log", "Warn", "Err" };
        private static readonly Color _panelColor = new Color(0.09f, 0.09f, 0.11f, 0.94f);
        private static readonly Color _barColor = new Color(0.18f, 0.18f, 0.22f, 0.98f);
        private static readonly Glyph[] _glyphs = BuildGlyphs();
        // Measured over every shape Compact can produce, so the layout never moves as counts grow.
        private static readonly float _digitSlotWidth = WidestCount();
        private static readonly ProfilerMarker _bakeMarker = new ProfilerMarker("KenseiLog.BakeBubble");
        private static readonly byte[] _gammaToLinear = GammaToLinearTable();

        // How often the safe area is asked for when the screen size has not changed. It moves
        // on its own when a phone rotates between two orientations of the same size, or a
        // cutout setting changes, and half a second late is soon enough for a debug overlay.
        private const int SafeAreaPollFrames = 30;
        // After a change of size the safe area is read every frame for this many frames: on some
        // phones it reports the old orientation's insets for a frame or two after a rotation, and
        // read once on the resize it would stay wrong until the next poll.
        private const int SafeAreaSettleFrames = 10;

        private static LogOverlay _instance;
        // Volatile because the setter is callable from any thread: the next Update reads it.
        private static volatile bool _shown = true;

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
        private float _configuredScale = -1f;
        private LogOverlayView _view;
        private OverlayBubbleMode _bubbleMode = OverlayBubbleMode.Always;
        // The view's enabled state is worked out in Update and only when something it depends
        // on moved, so that a frame with nothing new does not touch the component at all.
        private bool _viewDirty = true;
        private bool _appliedShown = true;

        // Screen metrics, cached. Screen.dpi alone cost about thirteen microseconds per call on
        // a desktop player and was read twice per IMGUI pass; with safeArea and the size beside
        // it that was most of what the collapsed bubble spent outside drawing itself.
        private float _scale = 1f;
        private Rect _safe;
        private int _screenWidth = -1;
        private int _screenHeight = -1;
        private int _framesSinceSafeArea;
        private int _safeAreaSettling;
        private bool _scaleDirty = true;
        // Test hooks: how many times the scale was worked out, and how many of those asked for
        // Screen.dpi. SmokeRunner reads them to check nothing is recomputed per frame.
        internal int ScaleComputations;
        internal int DpiReads;
        internal int SafeAreaReads;

        private bool _open;
        private bool _listBuilt;
        private bool _showTags;
        private bool _followTail = true;
        private float _listHeight;
        private long _selected = -1;
        private long _detailSequence = -1;
        private string _detailBody;
        private readonly string[] _bubbleCounts = new string[3];
        // What each count was last shown as, as a number standing for the string Compact would
        // make of it - so that a count which moved without changing what it reads as is not
        // formatted, and the bubble not baked, for nothing.
        private readonly long[] _bubbleKeys = { -1L, -1L, -1L };
        // The level whose chip is inverted, or -1 when nothing is worth lighting up for.
        private int _bubbleAccent = -1;
        private bool _bubbleDirty = true;
        private readonly float _digitSlot = _digitSlotWidth;
        private readonly float _bubbleWidth = BubblePad * 2f + ChipGap * 2f + (IconSize + IconGap + _digitSlotWidth) * 3f;

        // The collapsed bubble, baked. _bubblePx is where it goes on the screen, in pixels, worked
        // out in Update and when it is dragged rather than per event.
        private Texture2D _bubbleTex;
        private Color32[] _bubblePixels;
        // The bubble without its digits, for the accent and set of empty counts in _baseKey.
        private Color32[] _bubbleBase;
        private int _baseKey = int.MinValue;
        private int _bubbleTexWidth;
        private int _bubbleTexHeight;
        private Rect _bubblePx;
        private bool _bubbleTexDirty = true;
        private int _bakedAccent = int.MinValue;
        private float _lastBake = float.NegativeInfinity;
        private bool _bubbleLive;
        private bool _viewOn;
        // Coverage masks for the marks and the digits, rasterised for one scale and kept.
        private float _maskScale = -1f;
        private readonly Mask[] _iconMasks = new Mask[3];
        private readonly Mask[] _glyphMasks = new Mask[GlyphChars.Length];
        private Vector2 _pressPixel;
        // The control id that holds hotControl for the bubble, or 0.
        private int _bubbleControl;
        // The mouse button whose press holds the bubble; only its release ends the press.
        private int _bubbleButton;
        // Test hook: how many times the bubble was baked.
        internal int BubbleBakes;
        // Checks only: bakes as for this colour space instead of the project's.
        internal bool? LinearForChecks;
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
        private GUIStyle _notice;
        private GUIStyle _counter;
        private Texture2D _panelTex;
        private Texture2D _paneTex;
        private Texture2D _rowTex;
        private Texture2D _barTex;
        private Texture2D _chipTex;

        private LogOverlay() {
            _index = new LogIndex(_filter);
        }

        /// <summary>Creates the overlay, or points the one there is at this sink and scale, with the bubble always shown.</summary>
        public static void Ensure(MemorySink sink, float scale) {
            Ensure(sink, scale, OverlayBubbleMode.Always);
        }

        /// <summary>Creates the overlay, or points the one there is at this sink, scale and bubble mode.</summary>
        public static void Ensure(MemorySink sink, float scale, OverlayBubbleMode bubble) {
            if (!Application.isPlaying) {
                return;
            }
            EnsureHost(sink, scale, bubble);
        }

        /// <summary>
        /// Ensure without the play-mode gate, which is what the headless checks drive. The host
        /// carries both components: this one reads, the view draws, and a second call adds
        /// neither again.
        /// </summary>
        internal static LogOverlay EnsureHost(MemorySink sink, float scale, OverlayBubbleMode bubble) {
            if (_instance == null) {
                GameObject host = new GameObject("KenseiLog Overlay") {
                    hideFlags = HideFlags.HideAndDontSave
                };
                if (Application.isPlaying) {
                    DontDestroyOnLoad(host);
                }
                _instance = host.AddComponent<LogOverlay>();
            }
            if (_instance._view == null) {
                LogOverlayView view = _instance.gameObject.AddComponent<LogOverlayView>();
                // Set here as well as in its Awake, which a component added outside play mode
                // never gets.
                view.useGUILayout = false;
                view.Owner = _instance;
                _instance._view = view;
            }
            _instance.Attach(sink, scale, bubble);
            return _instance;
        }

        private void Attach(MemorySink sink, float scale, OverlayBubbleMode bubble) {
            if (scale != _configuredScale) {
                _configuredScale = scale;
                _scaleDirty = true;
            }
            _bubbleMode = bubble;
            _viewDirty = true;
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
            // Now rather than at the next Update: the view was just added enabled, and a host made
            // after this frame's Update would otherwise draw once before its first look at them.
            RefreshScreenMetrics();
            ApplyViewState();
            PrepareBubble();
        }

        /// <summary>
        /// Open or close the viewer, for wiring into a debug menu of your own.
        /// <para>
        /// Opening it while <see cref="Visible"/> is false does nothing: the overlay is hidden,
        /// and stays closed until it is shown again.
        /// </para>
        /// </summary>
        public static bool IsOpen {
            get => _instance != null && _instance._open;
            set {
                if (_instance != null && (!value || _shown)) {
                    _instance.SetOpen(value);
                }
            }
        }

        /// <summary>
        /// Whether the overlay is drawn at all. True by default.
        /// <para>
        /// False closes the viewer and switches the drawing off - no IMGUI pass, nothing on the
        /// frame - while records are still taken in and counted, so the counts are right the
        /// moment it is shown again. Kept across <see cref="Ensure(MemorySink,float)"/> and
        /// <see cref="Remove"/>, so it can be set before the overlay exists, and back to true at
        /// the start of each run in the editor, domain reload or not.
        /// </para>
        /// <para>
        /// Callable from any thread. It takes effect at the overlay's next Update, or at once when
        /// set on the main thread with the overlay there.
        /// </para>
        /// </summary>
        public static bool Visible {
            get => _shown;
            set {
                _shown = value;
                if (!LogCore.IsMainThread()) {
                    return;
                }
                LogOverlay overlay = _instance;
                if (overlay != null) {
                    overlay.ApplyViewState();
                }
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() {
            // Enter Play Mode without a domain reload keeps statics, and a game that hid the
            // overlay in one run would otherwise start the next with it hidden.
            _shown = true;
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
                // Only a list that exists has to be taken again. While the viewer is closed there
                // is none, and the one built when it opens folds by the filter as it stands then.
                // Rebuilding here built it anyway and marked it built, so from then on every
                // record was formatted into a row nothing read, for the rest of the run - the cost
                // the closed viewer exists to avoid.
                if (_instance._listBuilt) {
                    _instance.RebuildVisible();
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
            DestroyAnywhere(_instance.gameObject);
            _instance = null;
        }

        private void SetOpen(bool value) {
            if (_open == value) {
                return;
            }
            _open = value;
            _viewDirty = true;
            // A press the bubble was holding ends with it: opened from code mid-press, the panel
            // would otherwise draw under a hotControl nothing is going to let go of.
            ReleaseBubblePointer();
            if (value) {
                if (!_listBuilt) {
                    RebuildVisible();
                }
            } else {
                _visible.Clear();
                _listBuilt = false;
                // Closed from the panel's own Close, the Repaint of this same frame is the
                // bubble's, before any Update: brought up to date here, it is not drawn for that
                // frame from where it was and what it counted when the panel opened.
                PrepareBubble();
            }
        }

        private void Update() {
            if (_sink != null && _sink.Version != _lastVersion) {
                _lastVersion = _sink.Version;
                Ingest();
            }

            RefreshScreenMetrics();

            // Visible may have been set from another thread since the last frame.
            if (_appliedShown != _shown) {
                _viewDirty = true;
            }
            if (_viewDirty) {
                ApplyViewState();
            }

            PrepareBubble();
        }

        /// <summary>
        /// Keeps the baked bubble and its place on the screen up to date while it is the thing on
        /// the screen, and does nothing otherwise: hidden, asleep or open, there is no bubble to
        /// bake. Coming back, it is baked at once if anything changed while it was away.
        /// </summary>
        private void PrepareBubble() {
            if (_open || !_viewOn || _sink == null) {
                _bubbleLive = false;
                return;
            }
            UpdateBubble(Time.unscaledTime, !_bubbleLive);
            _bubbleLive = true;
        }

        private void OnApplicationPause(bool paused) {
            // A finger on the bubble when the app goes to the background never sends its MouseUp.
            if (paused) {
                ReleaseBubblePointer();
            }
        }

        /// <summary>
        /// Switches the drawing on or off: on while the overlay is visible and there is something
        /// to show - the viewer open, the bubble set to always, or a warning or an error counted.
        /// The component is touched only when the answer changes.
        /// </summary>
        private void ApplyViewState() {
            _viewDirty = false;
            bool shown = _shown;
            _appliedShown = shown;
            if (!shown && _open) {
                SetOpen(false);
                _viewDirty = false;
            }

            bool want = shown && (_open || _bubbleMode == OverlayBubbleMode.Always || HasProblem());
            if (_view != null && _view.enabled != want) {
                _view.enabled = want;
            }
            _viewOn = want && _view != null;
            if (!_viewOn) {
                // A disabled view gets no MouseUp, and a hotControl left set blocks the game's own GUI.
                ReleaseBubblePointer();
            }
        }

        private bool HasProblem() {
            return _sink != null &&
                   _sink.LevelCount(LogLevel.Warning) + _sink.LevelCount(LogLevel.Error) > 0L;
        }

        /// <summary>
        /// Reads the screen once a frame instead of once per IMGUI event: its size every frame,
        /// the safe area when the size changes and every <see cref="SafeAreaPollFrames"/> frames
        /// otherwise, and the DPI only when the size or the configured scale changes - not at all
        /// when a scale is configured.
        /// </summary>
        private void RefreshScreenMetrics() {
            int width = Screen.width;
            int height = Screen.height;
            bool resized = width != _screenWidth || height != _screenHeight;
            if (resized) {
                _screenWidth = width;
                _screenHeight = height;
                _safeAreaSettling = SafeAreaSettleFrames;
            }

            if (_safeAreaSettling > 0 || ++_framesSinceSafeArea >= SafeAreaPollFrames) {
                _safe = Screen.safeArea;
                _framesSinceSafeArea = 0;
                SafeAreaReads++;
                if (_safeAreaSettling > 0) {
                    _safeAreaSettling--;
                }
            }

            if (resized || _scaleDirty) {
                _scaleDirty = false;
                ScaleComputations++;
                if (_configuredScale > 0f) {
                    _scale = _configuredScale;
                } else {
                    DpiReads++;
                    float dpi = Screen.dpi;
                    _scale = Mathf.Clamp(dpi > 1f ? dpi / 160f : height / 720f, 1f, 4f);
                }
            }
        }

        private void OnDestroy() {
            DestroyTexture(ref _panelTex);
            DestroyTexture(ref _paneTex);
            DestroyTexture(ref _rowTex);
            DestroyTexture(ref _barTex);
            DestroyTexture(ref _chipTex);
            DestroyTexture(ref _bubbleTex);
        }

        // =====================================================================
        // Data
        // =====================================================================

        private void Ingest() {
            LogRingBuffer buffer = _sink.Buffer;
            long oldest = buffer.OldestSequence;

            // Counts moved, so whether the bubble has something to show may have too.
            _viewDirty = true;

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
            _viewDirty = true;
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
            // The rebuild read the whole buffer, so the watermark moves up to what it took. A
            // record that landed after the last poll is in the list now, and left below the
            // watermark the next poll copied it again and listed it twice - on opening the viewer,
            // and on every filter change. The version is left alone on purpose: the poll still
            // has to see that something arrived, or the counts on the bar and the bubble would
            // not be redrawn for it.
            if (copied > 0) {
                _lastSequence = Math.Max(_lastSequence, _scratch[copied - 1].Sequence);
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

        /// <summary>
        /// Called by <see cref="LogOverlayView"/> for each IMGUI event. Not an OnGUI of this
        /// component's own, so that this one - which has to keep reading - never pays for one.
        /// </summary>
        internal void DrawGUI() {
            if (_sink == null) {
                return;
            }
#if UNITY_EDITOR
            // A paused editor runs no Update but still repaints the Game view, resized or not:
            // without this the viewer kept the old size, Close and Clear off the edge of a
            // smaller view until play resumed. Editor only - a player never repaints unpaused.
            if (UnityEditor.EditorApplication.isPaused && Event.current.type == EventType.Repaint) {
                RefreshScreenMetrics();
                PrepareBubble();
            }
#endif

            if (!_open) {
                DrawCollapsed();
                return;
            }

            EnsureStyles();

            float scale = _scale;

            // Laid out inside the safe area. Clear and Close sit in the corners, which on a
            // phone with a cutout or a home indicator is exactly where the system takes the
            // touches - a button under one cannot be pressed at all. safeArea is y-up from the
            // bottom of the screen and GUI is y-down from the top, hence the flip.
            Rect safe = _safe;
            Matrix4x4 previous = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(
                new Vector3(safe.xMin, _screenHeight - safe.yMax, 0f),
                Quaternion.identity,
                new Vector3(scale, scale, 1f));

            float width = safe.width / scale;
            float height = safe.height / scale;

            TrackDrag();

            DrawPanel(width, height);

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

        /// <summary>
        /// The collapsed bubble: one prebaked texture in screen pixels, and the pointer.
        /// <para>
        /// No style, no matrix, no label and no button. The bubble is what the overlay draws for
        /// almost the whole of a session, and drawn from IMGUI parts it was fourteen draw calls a
        /// frame - a box, a button, six quads for the marks and three lines of text through the
        /// text engine - for a picture that changes a few times a second at most. The picture is
        /// baked in Update when it changes (<see cref="BakeBubble"/>), and this draws it.
        /// </para>
        /// <para>
        /// The control id is asked for first and on every event, so it is the same id from one
        /// event to the next whatever else happens on the frame.
        /// </para>
        /// </summary>
        private void DrawCollapsed() {
            int id = GUIUtility.GetControlID(BubbleHint, FocusType.Passive);
            Event current = Event.current;
            EventType type = current.type;
            switch (type) {
                case EventType.Repaint:
                    if (_bubbleTex == null) {
                        return;
                    }
                    if (GUIUtility.hotControl == id) {
                        // The only feedback a press gets now that there is no button to light up.
                        Color was = GUI.color;
                        GUI.color = new Color(was.r * 0.85f, was.g * 0.85f, was.b * 0.85f, was.a);
                        GUI.DrawTexture(_bubblePx, _bubbleTex);
                        GUI.color = was;
                    } else {
                        GUI.DrawTexture(_bubblePx, _bubbleTex);
                    }
                    return;
                case EventType.MouseDown:
                case EventType.MouseDrag:
                case EventType.MouseUp:
                    if (BubblePointer(type, current.button, current.mousePosition, _bubblePx, _scale, id)) {
                        current.Use();
                    }
                    return;
                case EventType.MouseLeaveWindow:
                    ReleaseBubblePointer();
                    return;
                default:
                    return;
            }
        }

        /// <summary>
        /// One pointer event on the collapsed bubble, and whether it was the bubble's to use up: it
        /// was when the bubble held the pointer before it or took it with it. A press anywhere
        /// else belongs to the game, whose own GUI is drawn after this and never sees an event
        /// that was used.
        /// </summary>
        internal bool BubblePointer(EventType type, int button, Vector2 position, Rect pixelRect, float scale, int id) {
            bool held = GUIUtility.hotControl == id;
            HandleBubblePointer(type, button, position, pixelRect, scale, id);
            // A press is the bubble's only if it holds the pointer after it: one that missed let a
            // lost press go and belongs to the game.
            return (held && type != EventType.MouseDown) || GUIUtility.hotControl == id;
        }

        /// <summary>What a pointer event did to the bubble.</summary>
        internal enum BubbleAction {
            None,
            Moved,
            Opened
        }

        /// <summary>
        /// One pointer event on the collapsed bubble, in screen pixels, without Event.current - so
        /// that the checks can drive it.
        /// <para>
        /// A press on the bubble takes hotControl, so the drag and the release come back here
        /// wherever the finger goes; a press anywhere else is left alone. The meaning of the
        /// gesture is still <see cref="BubbleGesture"/>'s: a tap opens the viewer, a drag past the
        /// threshold moves the bubble and opens nothing, and a press that is let go off the bubble
        /// opens nothing either. The threshold is the rows' six points, in pixels at this scale.
        /// </para>
        /// <para>
        /// While the bubble holds the pointer, a second button going down on it is not a new press:
        /// it would forget the drag in progress, and the left release after it would open the
        /// viewer. A left press on it while held still starts over, as the press whose release was
        /// lost, and a press of any button off it lets that press go and is left to the game. Only
        /// the release of the button that pressed ends the press.
        /// </para>
        /// </summary>
        internal BubbleAction HandleBubblePointer(EventType type, int button, Vector2 position, Rect pixelRect, float scale, int id) {
            switch (type) {
                case EventType.MouseDown: {
                    bool hit = pixelRect.Contains(position);
                    if (id != 0 && GUIUtility.hotControl == id) {
                        if (!hit) {
                            // The release of the press that is held was lost; this press is the game's.
                            ReleaseBubblePointer();
                            return BubbleAction.None;
                        }
                        if (button != 0) {
                            return BubbleAction.None;
                        }
                    }
                    _bubbleGesture.Press(hit, _bubble);
                    if (hit) {
                        _pressPixel = position;
                        _bubbleButton = button;
                        GUIUtility.hotControl = id;
                        _bubbleControl = id;
                    }
                    return BubbleAction.None;
                }
                case EventType.MouseDrag: {
                    if (GUIUtility.hotControl != id || id == 0) {
                        return BubbleAction.None;
                    }
                    Vector2 travel = position - _pressPixel;
                    bool past = travel.sqrMagnitude > DragThresholdSquared * scale * scale;
                    if (!_bubbleGesture.TryDrag(past, travel / scale, out Vector2 moved)) {
                        return BubbleAction.None;
                    }
                    _bubble = moved;
                    PlaceBubble(scale);
                    return BubbleAction.Moved;
                }
                case EventType.MouseUp: {
                    if (GUIUtility.hotControl != id || id == 0 || button != _bubbleButton) {
                        return BubbleAction.None;
                    }
                    GUIUtility.hotControl = 0;
                    _bubbleControl = 0;
                    bool opens = _bubbleGesture.Opens(pixelRect.Contains(position));
                    _bubbleGesture.Release();
                    if (!opens) {
                        return BubbleAction.None;
                    }
                    // After the release rather than inside it, so the frame is drawn from one state.
                    SetOpen(true);
                    return BubbleAction.Opened;
                }
                default:
                    return BubbleAction.None;
            }
        }

        /// <summary>Ends a press on the bubble that will not see its MouseUp.</summary>
        private void ReleaseBubblePointer() {
            _bubbleGesture.Release();
            if (_bubbleControl != 0 && GUIUtility.hotControl == _bubbleControl) {
                GUIUtility.hotControl = 0;
            }
            _bubbleControl = 0;
        }

        /// <summary>
        /// Keeps the bubble inside the safe area and works out where that is in pixels. Whole
        /// pixels, and the texture's own size, so each texel lands on one pixel and the strokes
        /// stay as sharp as they were baked.
        /// </summary>
        private void PlaceBubble(float scale) {
            Rect safe = _safe;
            _bubble.x = Mathf.Clamp(_bubble.x, 0f, Mathf.Max(0f, safe.width / scale - _bubbleWidth));
            _bubble.y = Mathf.Clamp(_bubble.y, 0f, Mathf.Max(0f, safe.height / scale - BubbleHeight));
            float width = _bubbleTexWidth > 0 ? _bubbleTexWidth : Mathf.Ceil(_bubbleWidth * scale);
            float height = _bubbleTexHeight > 0 ? _bubbleTexHeight : Mathf.Ceil(BubbleHeight * scale);
            _bubblePx = new Rect(
                Mathf.Round(safe.xMin + _bubble.x * scale),
                Mathf.Round(_screenHeight - safe.yMax + _bubble.y * scale),
                width, height);
        }

        /// <summary>
        /// Brings the counts up to date and bakes the bubble when it has to be: at once when there
        /// is no texture yet, its size changed, the accent moved - the colour is the signal - or
        /// <paramref name="urgent"/> asks and something is waiting; otherwise a changed count waits
        /// until <see cref="BakeInterval"/> has passed since the last bake. Takes the time as an
        /// argument so that the checks can hand it one.
        /// </summary>
        internal void UpdateBubble(float now, bool urgent) {
            if (_bubbleDirty) {
                RebuildBubble();
            }

            float scale = _scale;
            int width = Mathf.CeilToInt(_bubbleWidth * scale);
            int height = Mathf.CeilToInt(BubbleHeight * scale);
            bool bakeNow = _bubbleTex == null || width != _bubbleTexWidth || height != _bubbleTexHeight ||
                        _bubbleAccent != _bakedAccent || (urgent && _bubbleTexDirty);
            if (bakeNow || (_bubbleTexDirty && (now - _lastBake >= BakeInterval || now < _lastBake))) {
                BakeBubble();
                _lastBake = now;
            }
            PlaceBubble(scale);
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

        /// <summary>
        /// A number that is the same for two counts exactly when <see cref="Compact"/> makes the
        /// same string of them, worked out without making one. The bubble asks it every time a
        /// record arrives, and formatting a string to find it unchanged was garbage on every frame
        /// a game logs.
        /// </summary>
        private static long CompactKey(long count) {
            if (count < 1000L) {
                return count;
            }
            if (count < 10000L) {
                return 1000000L + count / 100L;
            }
            if (count < 1000000L) {
                return 2000000L + count / 1000L;
            }
            if (count < 1000000000L) {
                return 3000000L + count / 1000000L;
            }
            return 4000000L;
        }

        /// <summary>
        /// Reads the counts into the bubble's strings. A string is made only when what it reads as
        /// changes, and only then is the bubble marked for baking: a count going from 1500 to 1501
        /// still reads 1.5k, and baking it again would change no pixel.
        /// </summary>
        private void RebuildBubble() {
            int accent = -1;
            for (int level = 2; level >= 0; level--) {
                long count = _sink.LevelCount((LogLevel)level);
                long key = count == 0L ? -1L : CompactKey(count);
                if (key != _bubbleKeys[level]) {
                    _bubbleKeys[level] = key;
                    _bubbleCounts[level] = count == 0L ? null : Compact(count);
                    _bubbleTexDirty = true;
                }

                // Only a warning or an error is worth lighting up for. A badge that brightens
                // because the game logged at all is the panel nobody asked for.
                if (accent < 0 && count > 0 && level > (int)LogLevel.Log) {
                    accent = level;
                }
            }
            _bubbleAccent = accent;
            _bubbleDirty = false;
        }

        /// <summary>
        /// Paints the bubble into its texture: the background it has always shown, then for each
        /// level its mark and count. A level with nothing to report keeps its place and loses its
        /// digits, so the layout never moves; the worst level present is drawn inverted - its own
        /// colour as a plate with the mark and digits knocked out of it - which is the part the
        /// corner of the eye picks up without reading anything.
        /// <para>
        /// The marks and digits come from coverage masks rasterised once per scale, so a bake is
        /// a fill and a few small blends, in Update, a few times a second at most. The texture is
        /// made again only when its size changes.
        /// </para>
        /// </summary>
        internal void BakeBubble() {
            using (_bakeMarker.Auto()) {
                float scale = _scale;
                EnsureMasks(scale);

                int width = Mathf.CeilToInt(_bubbleWidth * scale);
                int height = Mathf.CeilToInt(BubbleHeight * scale);
                if (_bubbleTex == null || width != _bubbleTexWidth || height != _bubbleTexHeight) {
                    DestroyTexture(ref _bubbleTex);
                    _bubbleTex = new Texture2D(width, height, TextureFormat.RGBA32, false, false) {
                        name = "KenseiLog Bubble",
                        hideFlags = HideFlags.HideAndDontSave,
                        wrapMode = TextureWrapMode.Clamp,
                        filterMode = FilterMode.Bilinear
                    };
                    _bubbleTexWidth = width;
                    _bubbleTexHeight = height;
                    _bubblePixels = new Color32[width * height];
                    _bubbleBase = new Color32[width * height];
                    _baseKey = int.MinValue;
                }

                // Everything but the digits changes only with the accent or a count coming or going
                // from zero, so it is painted once for each of those and copied in after that.
                int baseKey = (_bubbleAccent + 1) * 8 + (_bubbleCounts[0] == null ? 1 : 0) +
                              (_bubbleCounts[1] == null ? 2 : 0) + (_bubbleCounts[2] == null ? 4 : 0);
                Color32[] pixels = _bubblePixels;
                if (baseKey != _baseKey) {
                    // What the screen showed until 0.19: the button's face over the panel's box.
                    Color32 background = Over(_barColor, _panelColor);
                    for (int i = 0; i < pixels.Length; i++) {
                        pixels[i] = background;
                    }
                    float at = BubblePad;
                    for (int i = 0; i < _levelOrder.Length; i++) {
                        BakeChipBase(at, (int)_levelOrder[i], scale);
                        at += IconSize + IconGap + _digitSlot + ChipGap;
                    }
                    Array.Copy(pixels, _bubbleBase, pixels.Length);
                    _baseKey = baseKey;
                } else {
                    Array.Copy(_bubbleBase, pixels, pixels.Length);
                }

                float x = BubblePad;
                for (int i = 0; i < _levelOrder.Length; i++) {
                    BakeChipDigits(x, (int)_levelOrder[i], scale);
                    x += IconSize + IconGap + _digitSlot + ChipGap;
                }

                // IMGUI draws this texture as if it held linear values, and in a Linear project the
                // frame is encoded to sRGB on the way out: gamma colours written as they are came
                // out washed to grey, digits and plate both. TextCore converts its colours itself,
                // which is why the 0.18 digits did not show it. Baked here in gamma, as blended,
                // and converted at the end, so a Gamma project gets the same bytes as before.
                if (LinearForChecks ?? QualitySettings.activeColorSpace == ColorSpace.Linear) {
                    byte[] table = _gammaToLinear;
                    for (int i = 0; i < pixels.Length; i++) {
                        Color32 c = pixels[i];
                        pixels[i] = new Color32(table[c.r], table[c.g], table[c.b], c.a);
                    }
                }

                _bubbleTex.SetPixels32(pixels);
                _bubbleTex.Apply(false);
                _bubbleTexDirty = false;
                _bakedAccent = _bubbleAccent;
                BubbleBakes++;
            }
        }

        private Color ChipInk(int level) {
            if (_bubbleAccent == level) {
                return _invertedInk;
            }
            return _bubbleCounts[level] == null ? _iconOffColor : LevelColor((LogLevel)level);
        }

        /// <summary>A chip's plate, when it has one, and its mark.</summary>
        private void BakeChipBase(float x, int level, float scale) {
            if (_bubbleAccent == level) {
                // On whole pixels, so the plate has the hard edges it had when IMGUI drew it as a
                // box, rather than a column of half colour wherever a scale leaves it fractional.
                float chipWidth = IconSize + IconGap + _digitSlot;
                FillRect(Mathf.Round((x - 4f) * scale), Mathf.Round(4f * scale),
                    Mathf.Round((x + chipWidth + 4f) * scale), Mathf.Round((BubbleHeight - 4f) * scale),
                    LevelColor((LogLevel)level));
            }
            BlendMask(_iconMasks[level], Mathf.RoundToInt(x * scale),
                Mathf.RoundToInt((BubbleHeight - IconSize) * 0.5f * scale), ChipInk(level));
        }

        private void BakeChipDigits(float x, int level, float scale) {
            string count = _bubbleCounts[level];
            if (count == null) {
                return;
            }
            Color ink = ChipInk(level);
            // Each glyph starts on a whole pixel, rounded down so that the last one never reaches
            // past the slot; the masks carry a pixel of margin, hence the one taken off.
            float pen = x + IconSize + IconGap;
            int top = Mathf.RoundToInt((BubbleHeight * 0.5f - GlyphHeight * 0.5f - GlyphRadius) * scale) - 1;
            for (int i = 0; i < count.Length; i++) {
                int glyph = GlyphChars.IndexOf(count[i]);
                if (glyph < 0) {
                    continue;
                }
                if (_glyphMasks[glyph].Coverage == null) {
                    _glyphMasks[glyph] = GlyphMask(glyph, scale);
                }
                BlendMask(_glyphMasks[glyph], Mathf.FloorToInt(pen * scale) - 1, top, ink);
                pen += _glyphs[glyph].Advance;
            }
        }

        /// <summary>
        /// A solid rectangle, in pixels y down, with its edges covered by the fraction of each
        /// pixel they cross; the plate passes whole pixels, so it has none.
        /// </summary>
        private void FillRect(float left, float top, float right, float bottom, Color color) {
            int x0 = Mathf.Max(0, Mathf.FloorToInt(left));
            int x1 = Mathf.Min(_bubbleTexWidth, Mathf.CeilToInt(right));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(top));
            int y1 = Mathf.Min(_bubbleTexHeight, Mathf.CeilToInt(bottom));
            for (int y = y0; y < y1; y++) {
                float cy = Mathf.Min(y + 1f, bottom) - Mathf.Max(y, top);
                for (int x = x0; x < x1; x++) {
                    float cx = Mathf.Min(x + 1f, right) - Mathf.Max(x, left);
                    Blend(x, y, color, Mathf.Clamp01(cx) * Mathf.Clamp01(cy));
                }
            }
        }

        private void BlendMask(Mask mask, int left, int top, Color ink) {
            if (mask.Coverage == null) {
                return;
            }
            for (int y = 0; y < mask.Height; y++) {
                int py = top + y;
                if (py < 0 || py >= _bubbleTexHeight) {
                    continue;
                }
                for (int x = 0; x < mask.Width; x++) {
                    float coverage = mask.Coverage[y * mask.Width + x];
                    int px = left + x;
                    if (coverage > 0f && px >= 0 && px < _bubbleTexWidth) {
                        Blend(px, py, ink, coverage);
                    }
                }
            }
        }

        /// <summary>Lays a colour over one pixel, y counted down from the top as on the screen.</summary>
        private void Blend(int x, int y, Color color, float coverage) {
            int index = (_bubbleTexHeight - 1 - y) * _bubbleTexWidth + x;
            Color32 under = _bubblePixels[index];
            float a = coverage * color.a;
            if (a <= 0f) {
                return;
            }
            under.r = (byte)(under.r + (color.r * 255f - under.r) * a + 0.5f);
            under.g = (byte)(under.g + (color.g * 255f - under.g) * a + 0.5f);
            under.b = (byte)(under.b + (color.b * 255f - under.b) * a + 0.5f);
            _bubblePixels[index] = under;
        }

        private static byte[] GammaToLinearTable() {
            byte[] table = new byte[256];
            // The sRGB curve, worked out here rather than through Mathf.GammaToLinearSpace: this
            // runs whenever the type is first touched, and Visible may do that from any thread.
            for (int i = 0; i < 256; i++) {
                double c = i / 255.0;
                double linear = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
                table[i] = (byte)(linear * 255.0 + 0.5);
            }
            return table;
        }

        private static Color32 Over(Color top, Color bottom) {
            float alpha = top.a + bottom.a * (1f - top.a);
            if (alpha <= 0f) {
                return new Color32(0, 0, 0, 0);
            }
            float under = bottom.a * (1f - top.a);
            return new Color(
                (top.r * top.a + bottom.r * under) / alpha,
                (top.g * top.a + bottom.g * under) / alpha,
                (top.b * top.a + bottom.b * under) / alpha,
                alpha);
        }

        /// <summary>
        /// Rasterises the three marks for this scale and forgets the glyphs of the last one, once
        /// per scale: a phone's scale does not change, and a desktop's changes with the window.
        /// </summary>
        private void EnsureMasks(float scale) {
            if (_maskScale == scale) {
                return;
            }
            _maskScale = scale;
            _baseKey = int.MinValue;
            int icon = Mathf.Max(1, Mathf.RoundToInt(IconSize * scale));
            _iconMasks[(int)LogLevel.Log] = SupersampledMask(icon, InLogBars);
            _iconMasks[(int)LogLevel.Warning] = SupersampledMask(icon, InTriangle);
            _iconMasks[(int)LogLevel.Error] = SupersampledMask(icon, InErrorBadge);
            // The glyphs are rasterised when a count first needs them: most runs never show an M.
            for (int i = 0; i < GlyphChars.Length; i++) {
                _glyphMasks[i] = default;
            }
        }

        private void DrawPanel(float width, float height) {
            Rect panel = new Rect(0f, 0f, width, height);
            GUI.Box(panel, GUIContent.none, _panel);

            DrawBar(width);

            float top = BarHeight;
            string notice = EvictionNotice();
            if (notice != null) {
                GUI.Box(new Rect(0f, top, width, NoticeHeight), GUIContent.none, _pane);
                GUI.color = _metaColor;
                GUI.Label(new Rect(6f, top, width - 12f, NoticeHeight), notice, _notice);
                GUI.color = Color.white;
                top += NoticeHeight;
            }

            float detailHeight = _selected >= 0 ? Mathf.Min(height * 0.35f, 180f) : 0f;
            float tagWidth = _showTags ? Mathf.Min(200f, width * 0.5f) : 0f;
            float bodyHeight = height - top - detailHeight;

            // The pane takes its width out of the list instead of covering it. IMGUI gives a
            // press to the first control drawn under the pointer, so a pane drawn on top of the
            // list was visible but dead: the row button underneath took every tap first.
            if (tagWidth > 0f) {
                DrawTagPane(new Rect(0f, top, tagWidth, bodyHeight));
            }
            DrawList(new Rect(tagWidth, top, width - tagWidth, bodyHeight));

            if (detailHeight > 0f) {
                DrawDetail(new Rect(0f, height - detailHeight, width, detailHeight));
            }
        }

        private void DrawBar(float width) {
            GUI.Box(new Rect(0f, 0f, width, BarHeight), GUIContent.none, _bar);
            float x = 4f;

            // Same reason as the bubble's label, which was cached and these were not: a count
            // changes when a record arrives, and the view draws every frame.
            if (_levelLabelsDirty) {
                for (int i = 0; i < _levelLabels.Length; i++) {
                    _levelLabels[i] = _levelCaptions[i] + " " + Compact(_sink.LevelCount((LogLevel)i));
                }
                _levelLabelsDirty = false;
            }

            for (int i = 0; i < _levelOrder.Length; i++) {
                x += LevelButton(x, _levelOrder[i]);
            }

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
            // A view that is following the newest line stays following when the list gets
            // shorter under it. The notice at the top appearing is what found this: it takes its
            // height out of the list between one poll and the next, and the old offset then reads
            // as having scrolled away - so the view froze on a window that the ring then aged
            // out from under it, while looking as though it was at the bottom.
            if (_followTail && area.height < _listHeight) {
                _scroll.y = float.MaxValue;
            }
            _listHeight = area.height;

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

        /// <summary>
        /// One line for the top of the viewer once the ring has let something go, or null while
        /// it still holds everything.
        /// <para>
        /// Said once, there, rather than beside each tag or under an empty list: the tags show
        /// what is held and nothing else, and the reason fewer are held than were logged belongs
        /// to the whole viewer, not to any one of them.
        /// </para>
        /// <para>
        /// Worded from what the file sinks are actually doing, asked of them each pass rather than
        /// read off the configuration. A notice that sent the reader to a file for records that
        /// were never written to it would be the same kind of lie as the tag that led to an empty
        /// list - and the configuration says a file exists when its writer has failed, and says
        /// nothing about the editor's own session file, which keeps the dev channel regardless.
        /// </para>
        /// </summary>
        private string EvictionNotice() {
            if (!_sink.Buffer.HasEvicted) {
                return null;
            }
            if (!LogCore.AnyFileKeeps(LogChannel.Prod)) {
                return "Earlier messages are no longer kept.";
            }
            return LogCore.AnyFileKeeps(LogChannel.Dev)
                ? "Earlier messages are now only in the log file."
                : "Earlier messages: Prod in the log file, Dev not kept.";
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
                    // Nothing about where it went: that is the notice at the top, said once for
                    // the whole viewer rather than again for each thing that was pushed out.
                    return "  Nothing under '" + _filter.Tags[i] + "'.";
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

            _panelTex = SolidTexture(_panelColor);
            _paneTex = SolidTexture(new Color(0.12f, 0.12f, 0.15f, 1f));
            _barTex = SolidTexture(_barColor);
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
            _notice = new GUIStyle(_meta) { alignment = TextAnchor.MiddleLeft };

            _counter = new GUIStyle(_row) {
                fontSize = 12,
                alignment = TextAnchor.MiddleRight,
                padding = new RectOffset(0, 0, 0, 0)
            };
        }

        /// <summary>
        /// Rasterises a shape into a coverage mask of size by size pixels, sixteen samples a pixel.
        /// <para>
        /// Built here rather than shipped, because the package ships no assets - turning the flag
        /// on is the whole installation - and drawn rather than typed, because a character like a
        /// warning sign is not in every font a player build falls back to, and a glyph that is
        /// missing on the device is a box on the screen that nothing in the editor would show.
        /// Rasterised at the size it is shown at, so nothing resamples it afterwards.
        /// </para>
        /// </summary>
        private static Mask SupersampledMask(int size, Func<float, float, bool> inside) {
            float[] coverage = new float[size * size];
            for (int y = 0; y < size; y++) {
                for (int x = 0; x < size; x++) {
                    int hits = 0;
                    for (int sy = 0; sy < 4; sy++) {
                        for (int sx = 0; sx < 4; sx++) {
                            float u = (x + (sx + 0.5f) * 0.25f) / size;
                            float v = (y + (sy + 0.5f) * 0.25f) / size;
                            if (inside(u, v)) {
                                hits++;
                            }
                        }
                    }
                    coverage[y * size + x] = hits / 16f;
                }
            }
            return new Mask(size, size, coverage);
        }

        /// <summary>
        /// Three lines of text, uneven so they do not read as a hamburger menu. The same bars the
        /// mark was drawn as in quads before 0.19.
        /// </summary>
        private static bool InLogBars(float u, float v) {
            const float bar = 0.17f;
            const float step = 0.3f;
            const float top = (1f - (bar + step * 2f)) * 0.5f;
            for (int i = 0; i < _logBarWidths.Length; i++) {
                float from = top + step * i;
                if (v >= from && v < from + bar && u < _logBarWidths[i]) {
                    return true;
                }
            }
            return false;
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

        /// <summary>
        /// One glyph rasterised at this scale: each pixel covered by how far its centre is inside
        /// the nearest stroke, which smooths the edge by half a pixel at any size without
        /// supersampling. The mask has a pixel of margin all round, so the smoothing is never cut
        /// off; its pixel (1, 1) is the top left of the glyph's ink.
        /// </summary>
        internal static Mask GlyphMask(int glyph, float scale) {
            Glyph shape = _glyphs[glyph];
            int width = Mathf.CeilToInt((shape.Width + GlyphRadius * 2f) * scale) + 2;
            int height = Mathf.CeilToInt((GlyphHeight + GlyphRadius * 2f) * scale) + 2;
            float[] coverage = new float[width * height];
            float[] strokes = shape.Strokes;
            for (int y = 0; y < height; y++) {
                float py = (y - 0.5f) / scale - GlyphRadius;
                for (int x = 0; x < width; x++) {
                    float px = (x - 0.5f) / scale - GlyphRadius;
                    float nearest = float.MaxValue;
                    for (int i = 0; i < strokes.Length; i += 4) {
                        nearest = Mathf.Min(nearest, SegmentDistanceSquared(px, py,
                            strokes[i], strokes[i + 1], strokes[i + 2], strokes[i + 3]));
                    }
                    coverage[y * width + x] = Mathf.Clamp01((GlyphRadius - Mathf.Sqrt(nearest)) * scale + 0.5f);
                }
            }
            return new Mask(width, height, coverage);
        }

        private static float SegmentDistanceSquared(float px, float py, float ax, float ay, float bx, float by) {
            float dx = bx - ax;
            float dy = by - ay;
            float length = dx * dx + dy * dy;
            float t = length > 0f ? Mathf.Clamp01(((px - ax) * dx + (py - ay) * dy) / length) : 0f;
            float ex = px - (ax + dx * t);
            float ey = py - (ay + dy * t);
            return ex * ex + ey * ey;
        }

        /// <summary>The width a count takes in the bubble, in points.</summary>
        private static float CountWidth(string count) {
            float width = 0f;
            for (int i = 0; i < count.Length; i++) {
                int glyph = GlyphChars.IndexOf(count[i]);
                if (glyph >= 0) {
                    width += _glyphs[glyph].Advance;
                }
            }
            return Mathf.Max(0f, width - GlyphTracking);
        }

        private static float WidestCount() {
            float widest = 0f;
            for (int i = 0; i < _countShapes.Length; i++) {
                widest = Mathf.Max(widest, CountWidth(_countShapes[i]));
            }
            return widest;
        }

        /// <summary>
        /// The strokes of every character <see cref="Compact"/> can produce, in the order of
        /// <see cref="GlyphChars"/>. Coordinates are fractions of the glyph's width and height,
        /// y down; arcs are angles in degrees, 0 to the right and 90 down.
        /// <para>
        /// The digits share one width, as a font's figures do, so that a count ticking over does
        /// not shift the ones beside it.
        /// </para>
        /// </summary>
        private static Glyph[] BuildGlyphs() {
            return new[] {
                new GlyphPath(DigitWidth).Arc(0.5f, 0.5f, 0.5f, 0.5f, 0f, 360f).Build(),
                new GlyphPath(DigitWidth).Line(0.62f, 0f, 0.62f, 1f).Line(0.62f, 0f, 0.12f, 0.28f).Build(),
                new GlyphPath(DigitWidth).Arc(0.5f, 0.28f, 0.5f, 0.28f, 195f, 400f)
                    .Line(0.883f, 0.46f, 0f, 1f).Line(0f, 1f, 1f, 1f).Build(),
                new GlyphPath(DigitWidth).Arc(0.5f, 0.26f, 0.45f, 0.26f, 200f, 450f)
                    .Arc(0.5f, 0.74f, 0.5f, 0.26f, -90f, 160f).Line(0.3f, 0.5f, 0.5f, 0.5f).Build(),
                new GlyphPath(DigitWidth).Line(0.78f, 1f, 0.78f, 0f).Line(0.78f, 0f, 0f, 0.7f)
                    .Line(0f, 0.7f, 1f, 0.7f).Build(),
                new GlyphPath(DigitWidth).Line(0.92f, 0f, 0.12f, 0f).Line(0.12f, 0f, 0.06f, 0.45f)
                    .Line(0.06f, 0.45f, 0.213f, 0.418f).Arc(0.5f, 0.68f, 0.5f, 0.32f, -125f, 160f).Build(),
                // The top stops a pixel and a half short of the bowl at scale 1: run on to it, and at
                // that size the six closed into an eight.
                new GlyphPath(DigitWidth).Arc(0.5f, 0.72f, 0.5f, 0.28f, 0f, 360f).Line(0f, 0.72f, 0f, 0.38f)
                    .Arc(0.5f, 0.38f, 0.5f, 0.38f, 180f, 290f).Build(),
                new GlyphPath(DigitWidth).Line(0f, 0f, 1f, 0f).Line(1f, 0f, 0.35f, 1f).Build(),
                new GlyphPath(DigitWidth).Arc(0.5f, 0.25f, 0.42f, 0.25f, 0f, 360f)
                    .Arc(0.5f, 0.73f, 0.5f, 0.27f, 0f, 360f).Build(),
                // A six turned over.
                new GlyphPath(DigitWidth, true).Arc(0.5f, 0.72f, 0.5f, 0.28f, 0f, 360f).Line(0f, 0.72f, 0f, 0.38f)
                    .Arc(0.5f, 0.38f, 0.5f, 0.38f, 180f, 290f).Build(),
                new GlyphPath(0f).Line(0f, 1f, 0f, 1f).Build(),
                new GlyphPath(4f).Line(0f, 0f, 0f, 1f).Line(0.95f, 0.32f, 0f, 0.78f).Line(0.35f, 0.6f, 1f, 1f).Build(),
                new GlyphPath(6.4f).Line(0f, 1f, 0f, 0f).Line(0f, 0f, 0.5f, 0.7f).Line(0.5f, 0.7f, 1f, 0f)
                    .Line(1f, 0f, 1f, 1f).Build(),
                new GlyphPath(DigitWidth).Line(0f, 0f, 0f, 1f).Line(0f, 0f, 0.5f, 0f)
                    .Arc(0.5f, 0.24f, 0.45f, 0.24f, -90f, 90f).Line(0.5f, 0.48f, 0f, 0.48f)
                    .Arc(0.5f, 0.74f, 0.5f, 0.26f, -90f, 90f).Line(0.5f, 1f, 0f, 1f).Build(),
                new GlyphPath(4.6f).Line(0.5f, 0.22f, 0.5f, 0.86f).Line(0f, 0.54f, 1f, 0.54f).Build()
            };
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
            DestroyAnywhere(texture);
            texture = null;
        }

        // Destroy is refused outside play mode, which is where the headless checks run.
        private static void DestroyAnywhere(UnityEngine.Object target) {
            if (Application.isPlaying) {
                Destroy(target);
            } else {
                DestroyImmediate(target);
            }
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
        /// The view draws at least once per frame, so doing this per row per pass meant taking the
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

        /// <summary>Coverage, 0 to 1, of a small block of pixels, y down.</summary>
        internal readonly struct Mask {
            public readonly int Width;
            public readonly int Height;
            public readonly float[] Coverage;

            public Mask(int width, int height, float[] coverage) {
                Width = width;
                Height = height;
                Coverage = coverage;
            }
        }

        /// <summary>A glyph's strokes in points - x0, y0, x1, y1 per stroke - and how far it moves the pen.</summary>
        private readonly struct Glyph {
            public readonly float Width;
            public readonly float Advance;
            public readonly float[] Strokes;

            public Glyph(float width, float[] strokes) {
                Width = width;
                Advance = width + GlyphRadius * 2f + GlyphTracking;
                Strokes = strokes;
            }
        }

        /// <summary>Builds a glyph's strokes out of lines and elliptical arcs.</summary>
        private sealed class GlyphPath {
            private readonly List<float> _strokes = new List<float>();
            private readonly float _width;
            private readonly bool _turned;

            public GlyphPath(float width, bool turned = false) {
                _width = width;
                _turned = turned;
            }

            public GlyphPath Line(float u0, float v0, float u1, float v1) {
                if (_turned) {
                    u0 = 1f - u0;
                    v0 = 1f - v0;
                    u1 = 1f - u1;
                    v1 = 1f - v1;
                }
                _strokes.Add(u0 * _width);
                _strokes.Add(v0 * GlyphHeight);
                _strokes.Add(u1 * _width);
                _strokes.Add(v1 * GlyphHeight);
                return this;
            }

            public GlyphPath Arc(float cu, float cv, float ru, float rv, float from, float to) {
                int steps = Mathf.Max(4, Mathf.CeilToInt(Mathf.Abs(to - from) / 18f));
                float previousU = cu + ru * Mathf.Cos(from * Mathf.Deg2Rad);
                float previousV = cv + rv * Mathf.Sin(from * Mathf.Deg2Rad);
                for (int i = 1; i <= steps; i++) {
                    float angle = Mathf.Lerp(from, to, i / (float)steps) * Mathf.Deg2Rad;
                    float u = cu + ru * Mathf.Cos(angle);
                    float v = cv + rv * Mathf.Sin(angle);
                    Line(previousU, previousV, u, v);
                    previousU = u;
                    previousV = v;
                }
                return this;
            }

            public Glyph Build() {
                return new Glyph(_width, _strokes.ToArray());
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
