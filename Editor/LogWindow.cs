using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.UIElements;

namespace KenseiLog.Editor {
    /// <summary>
    /// Log viewer with one filter tab per saved view.
    /// <para>
    /// Open via menu: Window -> Kensei -> Logs.
    /// </para>
    /// </summary>
    public sealed class LogWindow : EditorWindow {
        private const double RefreshInterval = 1.0 / 15.0;
        private const float RowHeight = 20f;

        /// <summary>
        /// How long the search box waits before filtering. A keystroke rebuilds the tab from
        /// the whole buffer and reassembles the tag tree, and nobody types one character.
        /// </summary>
        private const long SearchDebounceMs = 150;

        /// <summary>Joins folded tag names in EditorPrefs. A tag can hold no vertical bar.</summary>
        private const string FoldSeparator = "|";

        private static readonly string _frameKey = ProjectPrefs.Key("Columns.Frame.v2");
        private static readonly string _timeKey = ProjectPrefs.Key("Columns.Time.v2");
        private static readonly string _tagKey = ProjectPrefs.Key("Columns.Tag.v2");
        private static readonly string _foldedTagsKey = ProjectPrefs.Key("FoldedTags");
        private static readonly string _treeKey = ProjectPrefs.Key("Tree.v2");
        private static readonly string _compactKey = ProjectPrefs.Key("Compact");

        [SerializeField] private List<LogFilter> _filters = new List<LogFilter>();
        [SerializeField] private int _activeTab;

        private readonly List<TabView> _views = new List<TabView>();
        private readonly Dictionary<string, int> _tagCounts = new Dictionary<string, int>();
        private readonly int[] _levelCounts = new int[3];
        private readonly int[] _renderedCounts = { -1, -1, -1 };
        private readonly HashSet<string> _foldedTags = new HashSet<string>();
        private readonly Dictionary<long, ConsoleContext> _consoleContexts = new Dictionary<long, ConsoleContext>();
        private readonly Dictionary<string, Label> _tagCountLabels = new Dictionary<string, Label>();

        private LogRecord[] _scratch;
        private long _lastSequence;
        private int _lastVersion = -1;
        private double _lastRefresh;
        private bool _paused;
        private bool _followTail = true;
        private bool _showFrame = true;
        private bool _showTime = true;
        private bool _showTag = true;
        private bool _showTree = true;
        private bool _compact;
        private int _lastRenderedRevision = -1;

        private VisualElement _tabBar;
        private ScrollView _tagPane;
        private ListView _listView;
        private Label _emptyHint;
        private Label _detailHeader;
        private ScrollView _detailScroll;
        private Label _detailBody;
        private Button _sourceButton;
        private Button _pingButton;
        private ToolbarToggle _logToggle;
        private ToolbarToggle _warningToggle;
        private ToolbarToggle _errorToggle;
        private ToolbarToggle _devToggle;
        private ToolbarToggle _prodToggle;
        private ToolbarToggle _collapseToggle;
        private ToolbarSearchField _searchField;
        private Label _frameIsolationLabel;
        private ToolbarButton _clearButton;
        private VisualElement _sessionBar;
        private Label _sessionLabel;
        private VisualElement _headerFrame;
        private VisualElement _headerTime;
        private VisualElement _headerTag;
        private ToolbarToggle _compactToggle;
        private VisualElement _treeHandle;
        private Label _treeHandleArrow;
        private Label _headerMenuButton;
        private IVisualElementScheduledItem _searchDebounce;

        private LogSession _session;

        /// <summary>
        /// The file the window is showing, kept so it can be reopened after a domain reload.
        /// LogSession itself is a plain object and neither the reference nor its contents
        /// survive one, so a recompile used to drop the window back to live logs without
        /// saying so.
        /// </summary>
        [SerializeField] private string _sessionPath;

        private LogFilter ActiveFilter => _filters[Mathf.Clamp(_activeTab, 0, _filters.Count - 1)];

        private TabView ActiveView => _views[Mathf.Clamp(_activeTab, 0, _views.Count - 1)];

        // Compact is a mode laid over the preferences, never a write into them: pressing it
        // must not cost you the column layout you chose, and leaving it must give that back
        // without having to remember anything.
        private bool ShowFrame => !_compact && _showFrame;

        private bool ShowTime => !_compact && _showTime;

        private bool ShowTag => !_compact && _showTag;

        private bool ShowTree => !_compact && _showTree;

        /// <summary>Live records, or the ones loaded from a session file.</summary>
        private LogRingBuffer Source => _session != null ? _session.Buffer : EditorSink.Instance.Buffer;

        [MenuItem("Window/Kensei/Logs")]
        private static void Open() {
            LogWindow window = GetWindow<LogWindow>();
            window.titleContent = new GUIContent("Logs");
            window.minSize = new Vector2(520f, 260f);
            window.Show();
        }

        private void OnEnable() {
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable() {
            EditorApplication.update -= OnEditorUpdate;
        }

        public void CreateGUI() {
            if (_filters.Count == 0) {
                _filters.Add(new LogFilter { Name = "All" });
            }
            RestoreSession();
            _showFrame = EditorPrefs.GetBool(_frameKey, true);
            _showTime = EditorPrefs.GetBool(_timeKey, true);
            _showTag = EditorPrefs.GetBool(_tagKey, true);
            _showTree = EditorPrefs.GetBool(_treeKey, true);
            _compact = EditorPrefs.GetBool(_compactKey, false);
            string[] folded = EditorPrefs.GetString(_foldedTagsKey, string.Empty)
                .Split(new[] { FoldSeparator }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < folded.Length; i++) {
                _foldedTags.Add(folded[i]);
            }

            EnsureScratch();
            RebuildViews();

            StyleSheet sheet = LoadStyleSheet();
            if (sheet != null) {
                rootVisualElement.styleSheets.Add(sheet);
            }
            rootVisualElement.AddToClassList("kl-root");

            BuildTabBar();
            BuildSessionBar();
            BuildBody();

            RefreshTabBar();
            RefreshSessionBar();
            ApplyColumnVisibility();
            ApplyTagPaneVisibility();
            SyncToolbarToFilter();
            Ingest(force: true);
        }

        private void BuildSessionBar() {
            _sessionBar = new VisualElement();
            _sessionBar.AddToClassList("kl-sessionbar");

            _sessionLabel = new Label();
            _sessionLabel.AddToClassList("kl-session-label");
            _sessionBar.Add(_sessionLabel);

            Button live = new Button(GoLive) { text = "Back to live" };
            live.AddToClassList("kl-session-live");
            _sessionBar.Add(live);

            rootVisualElement.Add(_sessionBar);
        }

        // =====================================================================
        // Layout
        // =====================================================================

        private void BuildTabBar() {
            _tabBar = new VisualElement();
            _tabBar.AddToClassList("kl-tabbar");
            rootVisualElement.Add(_tabBar);
        }

        private void BuildBody() {
            VisualElement body = new VisualElement();
            body.AddToClassList("kl-body");
            rootVisualElement.Add(body);

            _tagPane = new ScrollView();
            _tagPane.AddToClassList("kl-tagpane");
            body.Add(_tagPane);

            // The control that hides the tree lives against the tree, pointing the way it will
            // move. In the toolbar it was a word among fifteen others, and "Tree" next to a
            // "Tag" column told nobody which of the two it meant.
            _treeHandle = new VisualElement();
            _treeHandle.AddToClassList("kl-treehandle");
            _treeHandle.RegisterCallback<PointerDownEvent>(_ => {
                _showTree = !_showTree;
                EditorPrefs.SetBool(_treeKey, _showTree);
                ApplyTagPaneVisibility();
            });

            // The arrow carries the tooltip, and it is the size of a button. On the strip
            // itself the tooltip belonged to a 13px column running the full height of the
            // window, standing where the pointer crosses between the tree and the list: a
            // tooltip in UI Toolkit is a real OS window, and building and tearing one down per
            // crossing is what froze the editor on Windows. It is the fault the row tooltips
            // had, in a shape that outlived the fix for them.
            _treeHandleArrow = new Label();
            _treeHandleArrow.AddToClassList("kl-treehandle-arrow");
            _treeHandle.Add(_treeHandleArrow);
            body.Add(_treeHandle);

            VisualElement main = new VisualElement();
            main.AddToClassList("kl-main");
            body.Add(main);

            main.Add(BuildToolbar());

            main.Add(BuildColumnHeader());

            VisualElement listArea = new VisualElement();
            listArea.AddToClassList("kl-listarea");
            main.Add(listArea);

            _listView = new ListView {
                fixedItemHeight = RowHeight,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                selectionType = SelectionType.Single,
                makeItem = MakeRow,
                bindItem = BindRow,
                itemsSource = ActiveView.Sequences
            };
            _listView.AddToClassList("kl-list");
            _listView.selectedIndicesChanged += OnSelectionChanged;
            _listView.itemsChosen += _ => OpenSelectedSource();
            listArea.Add(_listView);

            _emptyHint = new Label();
            _emptyHint.AddToClassList("kl-empty");
            listArea.Add(_emptyHint);

            main.Add(BuildDetailPane());
        }

        private Toolbar BuildToolbar() {
            Toolbar toolbar = new Toolbar();
            toolbar.AddToClassList("kl-toolbar");

            // Three groups, divided: what the view is doing, what it lets through, and what
            // it does to the whole session. Everything used to sit in one undivided row.
            ToolbarToggle pause = new ToolbarToggle { text = "Pause", tooltip = "Stop updating the view. Recording continues." };
            pause.value = _paused;
            pause.RegisterValueChangedCallback(evt => {
                _paused = evt.newValue;
                if (!_paused) {
                    // Records kept arriving while the view was paused, and the poll marked them
                    // seen without taking them. Nothing was lost, but nothing was reachable
                    // either until the next log line happened to land.
                    Ingest(force: true);
                }
            });
            toolbar.Add(pause);

            ToolbarToggle follow = new ToolbarToggle { text = "Follow", tooltip = "Keep scrolling to the newest record." };
            follow.value = _followTail;
            follow.RegisterValueChangedCallback(evt => _followTail = evt.newValue);
            toolbar.Add(follow);

            toolbar.Add(Divider());

            _logToggle = FilterToggle("Log", value => ActiveFilter.ShowLog = value);
            _warningToggle = FilterToggle("Warn", value => ActiveFilter.ShowWarning = value);
            _errorToggle = FilterToggle("Error", value => ActiveFilter.ShowError = value);

            // The console's own three, so the severity reads before the word does.
            // The full-size icons, not the .sml ones: those are small source images, and
            // stretching them into place is what made them look smeared.
            AddIcon(_logToggle, "console.infoicon");
            AddIcon(_warningToggle, "console.warnicon");
            AddIcon(_errorToggle, "console.erroricon");
            toolbar.Add(_logToggle);
            toolbar.Add(_warningToggle);
            toolbar.Add(_errorToggle);

            toolbar.Add(Divider());

            _devToggle = FilterToggle("Dev", value => ActiveFilter.ShowDev = value);
            _prodToggle = FilterToggle("Prod", value => ActiveFilter.ShowProd = value);
            toolbar.Add(_devToggle);
            toolbar.Add(_prodToggle);

            toolbar.Add(Divider());

            _collapseToggle = FilterToggle("Collapse", value => ActiveFilter.Collapse = value);
            toolbar.Add(_collapseToggle);

            _searchField = new ToolbarSearchField();
            _searchField.AddToClassList("kl-search");
            _searchField.tooltip = "Searches the message text only. Tags are a separate field.";
            // Every keystroke used to refilter the whole buffer and reassemble the tag tree.
            // Typing a six-letter tag name did that six times, and only the last of them was
            // the search anyone asked for.
            _searchDebounce = _searchField.schedule.Execute(ApplySearch);
            _searchDebounce.Pause();
            _searchField.RegisterValueChangedCallback(_ => _searchDebounce.ExecuteLater(SearchDebounceMs));
            toolbar.Add(_searchField);

            _frameIsolationLabel = new Label();
            _frameIsolationLabel.AddToClassList("kl-frame-pill");
            _frameIsolationLabel.style.display = DisplayStyle.None;
            _frameIsolationLabel.RegisterCallback<PointerDownEvent>(_ => {
                ActiveFilter.IsolatedFrame = -1;
                RebuildActiveView();
                SyncToolbarToFilter();
            });
            toolbar.Add(_frameIsolationLabel);

            toolbar.Add(Spacer());

            ToolbarButton openFile = new ToolbarButton(OpenSessionFile) {
                text = "Open file",
                tooltip = "Open a log file written by a build and browse it like a live run."
            };
            AddIcon(openFile, "Project");
            toolbar.Add(openFile);

            _clearButton = new ToolbarButton(() => {
                EditorSink.Instance.Clear();
                ResetIngest();
            }) { text = "Clear" };
            AddIcon(_clearButton, "TreeEditor.Trash");
            toolbar.Add(_clearButton);

            ToolbarToggle clearOnPlay = new ToolbarToggle { text = "Clear on Play" };
            clearOnPlay.value = EditorSink.ClearOnPlay;
            clearOnPlay.RegisterValueChangedCallback(evt => EditorSink.ClearOnPlay = evt.newValue);
            toolbar.Add(clearOnPlay);

            return toolbar;
        }

        // =====================================================================
        // Session files
        // =====================================================================

        private void OpenSessionFile() {
            string startIn = LogCore.File != null ? LogCore.File.LogDirectory : Application.persistentDataPath;
            string path = EditorUtility.OpenFilePanel("Open log session", startIn, "jsonl");
            if (string.IsNullOrEmpty(path)) {
                return;
            }

            if (!LogSessionReader.TryRead(path, out LogSession session, out string error)) {
                EditorUtility.DisplayDialog("Kensei Log", "Could not read this file.\n\n" + error, "OK");
                return;
            }

            _session = session;
            _sessionPath = path;
            OnSourceChanged();
        }

        private void GoLive() {
            if (_session == null) {
                return;
            }
            _session = null;
            _sessionPath = null;
            OnSourceChanged();
        }

        /// <summary>
        /// Reopens the file the window was on before a domain reload. Only the path survives
        /// one - LogSession is a plain object Unity does not serialise - so the file is read
        /// again. A file that has since gone away drops the window back to live logs, which is
        /// what it did silently on every recompile before.
        /// </summary>
        private void RestoreSession() {
            if (string.IsNullOrEmpty(_sessionPath)) {
                return;
            }
            if (LogSessionReader.TryRead(_sessionPath, out LogSession session, out _)) {
                _session = session;
                return;
            }
            _sessionPath = null;
        }

        private void OnSourceChanged() {
            EnsureScratch();
            ResetIngest();
            RefreshSessionBar();
            Ingest(force: true);
        }

        private void RefreshSessionBar() {
            bool viewingFile = _session != null;
            _sessionBar.style.display = viewingFile ? DisplayStyle.Flex : DisplayStyle.None;
            _clearButton.SetEnabled(!viewingFile);

            if (!viewingFile) {
                return;
            }
            _sessionLabel.text = _session.Describe() +
                                 (_session.SkippedLines > 0 ? "   (" + _session.SkippedLines + " unreadable lines skipped)" : string.Empty);
        }

        /// <summary>
        /// Column visibility, opened from the header rather than the toolbar - a heading is
        /// where anyone looks to change the column under it, and the toolbar already runs the
        /// full width of a docked window.
        /// <para>
        /// These are a window setting, not a per-tab one. Which columns you want is a habit,
        /// and having it differ from tab to tab would be a surprise every time you switched.
        /// </para>
        /// </summary>
        private void ShowColumnMenu() {
            GenericMenu menu = new GenericMenu();
            AppendColumnItem(menu, "Frame", _showFrame, value => _showFrame = value, _frameKey);
            AppendColumnItem(menu, "Time", _showTime, value => _showTime = value, _timeKey);
            AppendColumnItem(menu, "Tag", _showTag, value => _showTag = value, _tagKey);
            menu.AddSeparator(string.Empty);
            if (_compact) {
                menu.AddDisabledItem(new GUIContent("Compact is hiding these"), true);
            } else {
                menu.AddItem(new GUIContent("Show all"), false, () => {
                    SetColumn(value => _showFrame = value, _frameKey, true);
                    SetColumn(value => _showTime = value, _timeKey, true);
                    SetColumn(value => _showTag = value, _tagKey, true);
                    ApplyColumnVisibility();
                });
            }
            menu.ShowAsContext();
        }

        private void AppendColumnItem(GenericMenu menu, string label, bool shown, Action<bool> write, string key) {
            if (_compact) {
                menu.AddDisabledItem(new GUIContent(label), false);
                return;
            }
            menu.AddItem(new GUIContent(label), shown, () => {
                SetColumn(write, key, !shown);
                ApplyColumnVisibility();
            });
        }

        private static void SetColumn(Action<bool> write, string key, bool value) {
            write(value);
            EditorPrefs.SetBool(key, value);
        }

        /// <summary>
        /// One switch for "just the messages": no tag tree, no column but the text.
        /// <para>
        /// A mode of its own rather than a preset. It leaves the per-column preferences
        /// untouched and merely overrides what is drawn, so leaving it restores exactly what
        /// you had - and while it is on, the controls it overrides are disabled, because a
        /// toggle that visibly does nothing is worse than one you cannot press.
        /// </para>
        /// </summary>
        private void SetCompact(bool compact) {
            _compact = compact;
            EditorPrefs.SetBool(_compactKey, compact);
            _compactToggle.SetValueWithoutNotify(compact);
            ApplyColumnVisibility();
            ApplyTagPaneVisibility();
        }

        /// <summary>
        /// A fixed header above the list. Its cells carry the same classes as a row's, so the
        /// two cannot drift apart: change a column width in the stylesheet and both follow.
        /// </summary>
        private VisualElement BuildColumnHeader() {
            VisualElement header = new VisualElement();
            header.AddToClassList("kl-header");

            // Stands in for the row's tag stripe so the columns line up under it.
            VisualElement stripeSpacer = new VisualElement();
            stripeSpacer.AddToClassList("kl-strip");
            header.Add(stripeSpacer);

            _headerFrame = HeaderCell("Frame", "kl-cell-frame");
            _headerTime = HeaderCell("Time", "kl-cell-time");
            _headerTag = HeaderCell("Tag", "kl-cell-tag");
            header.Add(_headerFrame);
            header.Add(_headerTime);
            header.Add(_headerTag);
            header.Add(HeaderCell("Message", "kl-cell-message"));
            header.Add(HeaderCell(string.Empty, "kl-cell-repeats"));

            _compactToggle = new ToolbarToggle {
                text = "Compact",
                tooltip = "Hide the tag tree and every column but the message."
            };
            _compactToggle.AddToClassList("kl-header-compact");
            _compactToggle.SetValueWithoutNotify(_compact);
            _compactToggle.RegisterValueChangedCallback(evt => SetCompact(evt.newValue));
            header.Add(_compactToggle);

            _headerMenuButton = new Label("\u22EE") {
                tooltip = "Choose which columns to show. Right-clicking the header does the same."
            };
            _headerMenuButton.AddToClassList("kl-header-menu");
            _headerMenuButton.RegisterCallback<PointerDownEvent>(evt => {
                evt.StopPropagation();
                ShowColumnMenu();
            });
            header.Add(_headerMenuButton);

            // The list reserves room for its vertical scroller; without the same gap here the
            // last column would sit a few pixels right of the values under it.
            VisualElement scrollerSpacer = new VisualElement();
            scrollerSpacer.AddToClassList("kl-header-scroller-gap");
            header.Add(scrollerSpacer);

            header.RegisterCallback<ContextClickEvent>(_ => ShowColumnMenu());
            return header;
        }

        private static Label HeaderCell(string text, string cellClass) {
            Label label = new Label(text);
            label.AddToClassList(cellClass);
            label.AddToClassList("kl-header-cell");
            return label;
        }

        private void ApplyTagPaneVisibility() {
            bool shown = ShowTree;
            _tagPane.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;

            _treeHandleArrow.text = shown ? "\u25C0" : "\u25B6";
            _treeHandle.SetEnabled(!_compact);
            _treeHandleArrow.tooltip = _compact
                ? "Compact is hiding the tree."
                : shown ? "Hide the tag tree" : "Show the tag tree";
        }

        private void ApplyColumnVisibility() {
            _headerFrame.style.display = ShowFrame ? DisplayStyle.Flex : DisplayStyle.None;
            _headerTime.style.display = ShowTime ? DisplayStyle.Flex : DisplayStyle.None;
            _headerTag.style.display = ShowTag ? DisplayStyle.Flex : DisplayStyle.None;
            _headerMenuButton.SetEnabled(!_compact);

            // Rebuild rather than refresh: a row's visibility is set while binding, and
            // recycled rows keep whatever the last bind gave them until bound again.
            _lastRenderedRevision = -1;
            _listView.Rebuild();
        }

        /// <summary>
        /// Put one of the editor's own icons on a control.
        /// <para>
        /// Icon names move between Unity versions and skins, so a miss is silent and leaves
        /// the control with its text - the label already says what the button does, and the
        /// icon only makes it quicker to find.
        /// </para>
        /// </summary>
        private static void AddIcon(VisualElement target, string iconName) {
            Texture2D icon = null;
            try {
                icon = EditorGUIUtility.IconContent(iconName)?.image as Texture2D;
            } catch (Exception) {
                return;
            }
            if (icon == null) {
                return;
            }

            VisualElement element = new VisualElement();
            element.AddToClassList("kl-icon");
            element.style.backgroundImage = new StyleBackground(icon);
            element.pickingMode = PickingMode.Ignore;

            // A Button draws its own text rather than holding a child label, so an icon
            // inserted beside it lands on top of the words. Moving the text into a label of
            // its own gives the two something to lay out against.
            if (target is Button button && !string.IsNullOrEmpty(button.text)) {
                string caption = button.text;
                button.text = string.Empty;
                button.AddToClassList("kl-iconbutton");
                button.Add(element);
                Label label = new Label(caption);
                label.pickingMode = PickingMode.Ignore;
                button.Add(label);
                return;
            }

            target.Insert(0, element);
        }

        private void ApplySearch() {
            if (string.Equals(ActiveFilter.Search, _searchField.value, StringComparison.Ordinal)) {
                return;
            }
            ActiveFilter.Search = _searchField.value;
            RebuildActiveView();
        }

        private ToolbarToggle FilterToggle(string label, Action<bool> apply) {
            ToolbarToggle toggle = new ToolbarToggle { text = label };
            toggle.AddToClassList("kl-leveltoggle");
            toggle.RegisterValueChangedCallback(evt => {
                apply(evt.newValue);
                RebuildActiveView();
            });
            return toggle;
        }

        private VisualElement BuildDetailPane() {
            VisualElement detail = new VisualElement();
            detail.AddToClassList("kl-detail");

            _detailHeader = new Label();
            _detailHeader.AddToClassList("kl-detail-header");
            detail.Add(_detailHeader);

            // The body scrolls inside the pane rather than growing it. A Label is as tall as
            // its text and a flex item does not shrink below its content by default, so a long
            // stack trace made it taller than a pane whose height is fixed - and pushed the
            // buttons that follow it off the bottom, out of the window.
            _detailScroll = new ScrollView(ScrollViewMode.Vertical);
            _detailScroll.AddToClassList("kl-detail-scroll");
            detail.Add(_detailScroll);

            // A Label, not a read-only TextField. A TextField carries the whole text editing
            // apparatus - an edit engine, a selection model, a caret that schedules its own
            // repaints - and an editor window pays for all of it on every frame it is focused,
            // whether or not anything is in it. A selectable Label still copies.
            _detailBody = new Label();
            _detailBody.AddToClassList("kl-detail-body");
            _detailBody.selection.isSelectable = true;
            _detailScroll.Add(_detailBody);

            VisualElement actions = new VisualElement();
            actions.AddToClassList("kl-detail-actions");
            detail.Add(actions);

            _sourceButton = new Button(OpenSelectedSource) {
                text = "Open",
                tooltip = "Open the call site, or the asset the log was raised against."
            };
            _pingButton = new Button(PingSelectedContext) { text = "Ping" };
            AddIcon(_sourceButton, "ScriptableObject Icon");
            AddIcon(_pingButton, "d_Search Icon");
            actions.Add(_sourceButton);
            actions.Add(_pingButton);

            return detail;
        }

        private static VisualElement Spacer() {
            VisualElement spacer = new VisualElement();
            spacer.AddToClassList("kl-spacer");
            return spacer;
        }

        private static VisualElement Divider() {
            VisualElement divider = new VisualElement();
            divider.AddToClassList("kl-divider");
            return divider;
        }

        // =====================================================================
        // Ingest
        // =====================================================================

        private void OnEditorUpdate() {
            // A loaded file never changes, so there is nothing to poll for.
            if (_listView == null || _session != null || EditorSink.Instance == null) {
                return;
            }
            int version = EditorSink.Instance.Version;
            if (version == _lastVersion) {
                return;
            }
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastRefresh < RefreshInterval) {
                return;
            }
            _lastRefresh = now;
            _lastVersion = version;
            Ingest(force: false);
        }

        /// <summary>
        /// Keeps the copy buffer as large as the source.
        /// <para>
        /// CopyNewerThan stops when the destination is full and returns the oldest of what it
        /// had, so a scratch smaller than the buffer silently loses the newest records - which
        /// is what raising EditorSink.Capacity used to do, since the array was sized once when
        /// the window was built.
        /// </para>
        /// </summary>
        private void EnsureScratch() {
            int capacity = Mathf.Max(64, Source.Capacity);
            if (_scratch == null || _scratch.Length < capacity) {
                _scratch = new LogRecord[capacity];
            }
        }

        private void Ingest(bool force) {
            if (_paused && !force) {
                return;
            }

            EnsureScratch();
            LogRingBuffer buffer = Source;

            // A cleared or resized buffer restarts below our watermark; start over rather than
            // waiting for the sequence counter to catch up.
            long oldest = buffer.OldestSequence;
            if (buffer.Count == 0 || oldest > _lastSequence + 1) {
                if (buffer.Count == 0) {
                    ResetIngest();
                    return;
                }
                _lastSequence = oldest - 1;
            }

            int copied = buffer.CopyNewerThan(_lastSequence, _scratch);
            bool tagsChanged = false;

            for (int i = 0; i < copied; i++) {
                ref LogRecord record = ref _scratch[i];
                _lastSequence = record.Sequence;
                _levelCounts[(int)record.Level]++;

                if (_tagCounts.TryGetValue(record.Tag, out int seen)) {
                    _tagCounts[record.Tag] = seen + 1;
                } else {
                    _tagCounts[record.Tag] = 1;
                    tagsChanged = true;
                }

                for (int t = 0; t < _views.Count; t++) {
                    _views[t].Append(in record);
                }
            }

            for (int t = 0; t < _views.Count; t++) {
                _views[t].PruneBelow(oldest);
            }

            if (tagsChanged) {
                RefreshTagPane();
            } else if (copied > 0) {
                // A known tag whose count went up used to change nothing on screen: the pane
                // was only ever rebuilt when a tag appeared for the first time, so every
                // number beside a tag froze at whatever it held when it was first seen.
                RefreshTagCounts();
            }
            RefreshList();
            RefreshLevelCounts();
        }

        private void ResetIngest() {
            _lastSequence = 0;
            Array.Clear(_levelCounts, 0, _levelCounts.Length);
            _tagCounts.Clear();
            _consoleContexts.Clear();
            for (int i = 0; i < _views.Count; i++) {
                _views[i].Clear();
            }
            RefreshTagPane();
            RefreshList();
            RefreshLevelCounts();
        }

        private void RebuildViews() {
            _views.Clear();
            for (int i = 0; i < _filters.Count; i++) {
                _views.Add(new TabView(_filters[i]));
            }
        }

        private void RebuildActiveView() {
            RebuildView(ActiveView);
            SyncToolbarToFilter();
            RefreshTagPane();
            RefreshList();
        }

        private void RebuildView(TabView view) {
            _lastRenderedRevision = -1;
            EnsureScratch();
            view.Clear();
            LogRingBuffer buffer = Source;
            int copied = buffer.CopyNewerThan(0, _scratch);
            for (int i = 0; i < copied; i++) {
                view.Append(in _scratch[i]);
            }
        }

        // =====================================================================
        // Tabs
        // =====================================================================

        private void RefreshTabBar() {
            _tabBar.Clear();
            for (int i = 0; i < _filters.Count; i++) {
                int index = i;
                Button tab = new Button(() => SelectTab(index)) { text = _filters[i].Name };
                tab.AddToClassList("kl-tab");
                if (i == _activeTab) {
                    tab.AddToClassList("kl-tab--active");
                }
                tab.AddManipulator(new ContextualMenuManipulator(evt => BuildTabMenu(evt, index)));
                _tabBar.Add(tab);
            }

            Button add = new Button(AddTab) { text = "+" };
            add.AddToClassList("kl-tab-add");
            add.tooltip = "New filter tab";
            _tabBar.Add(add);
        }

        private void BuildTabMenu(ContextualMenuPopulateEvent evt, int index) {
            evt.menu.AppendAction("Rename", _ => RenameTab(index));
            evt.menu.AppendAction("Duplicate", _ => DuplicateTab(index));
            evt.menu.AppendAction("Delete", _ => DeleteTab(index),
                _filters.Count > 1 ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
        }

        private void SelectTab(int index) {
            _activeTab = index;
            // Revisions count per view, so one tab's number says nothing about another's.
            _lastRenderedRevision = -1;
            _listView.itemsSource = ActiveView.Sequences;
            RefreshTabBar();
            SyncToolbarToFilter();
            RefreshTagPane();
            RefreshList();
        }

        /// <summary>
        /// Add a tab and make it current, for seeding a project's own set of views from an
        /// editor script instead of rebuilding them by hand on every machine.
        /// </summary>
        public void AddTab(LogFilter filter) {
            _filters.Add(filter);
            TabView view = new TabView(filter);
            RebuildView(view);
            _views.Add(view);
            SelectTab(_filters.Count - 1);
        }

        /// <summary>
        /// Select the most recent record at this level in the current tab and show it in the
        /// detail pane. Handy on a "jump to the last error" shortcut.
        /// </summary>
        public void SelectNewest(LogLevel level) {
            TabView view = ActiveView;
            for (int i = view.Sequences.Count - 1; i >= 0; i--) {
                if (Source.TryGetBySequence(view.Sequences[i], out LogRecord record) && record.Level == level) {
                    _listView.SetSelection(i);
                    _listView.ScrollToItem(i);
                    return;
                }
            }
        }

        private void AddTab() {
            AddTab(new LogFilter { Name = "Tab " + (_filters.Count + 1) });
        }

        private void DuplicateTab(int index) {
            LogFilter source = _filters[index];
            LogFilter copy = new LogFilter {
                Name = source.Name + " copy",
                Tags = new List<string>(source.Tags),
                ShowLog = source.ShowLog,
                ShowWarning = source.ShowWarning,
                ShowError = source.ShowError,
                ShowDev = source.ShowDev,
                ShowProd = source.ShowProd,
                Search = source.Search,
                Collapse = source.Collapse,
                IsolatedFrame = source.IsolatedFrame
            };
            _filters.Add(copy);
            TabView view = new TabView(copy);
            RebuildView(view);
            _views.Add(view);
            SelectTab(_filters.Count - 1);
        }

        private void DeleteTab(int index) {
            _filters.RemoveAt(index);
            _views.RemoveAt(index);
            // Everything after the deleted tab shifts down by one, so an active tab beyond it
            // has to move with them - clamping alone kept the index and landed on the tab that
            // slid into its place.
            if (index < _activeTab) {
                _activeTab--;
            }
            _activeTab = Mathf.Clamp(_activeTab, 0, _filters.Count - 1);
            SelectTab(_activeTab);
        }

        private void RenameTab(int index) {
            // Captures the tab itself. An index goes stale the moment another tab is deleted
            // while the popup is open, and the rename then lands on whichever tab took its
            // place.
            LogFilter filter = _filters[index];
            TabRenamePopup.Show(this, filter.Name, name => {
                filter.Name = name;
                RefreshTabBar();
            });
        }

        // =====================================================================
        // Tag pane
        // =====================================================================

        private void RefreshTagPane() {
            _tagPane.Clear();
            _tagCountLabels.Clear();
            List<TagNode> roots = TagTree.Build(_tagCounts);
            for (int i = 0; i < roots.Count; i++) {
                AddTagRow(roots[i], 0);
            }
        }

        /// <summary>
        /// Writes the current totals into the rows that are already there. Rebuilding the pane
        /// instead would throw away its scroll position several times a second while logs are
        /// arriving, which is exactly when someone is reading it.
        /// </summary>
        private void RefreshTagCounts() {
            List<TagNode> roots = TagTree.Build(_tagCounts);
            for (int i = 0; i < roots.Count; i++) {
                ApplyTagCount(roots[i]);
            }
        }

        private void ApplyTagCount(TagNode node) {
            // A folded branch has no rows for its children, so a miss here is ordinary.
            if (_tagCountLabels.TryGetValue(node.FullTag, out Label label)) {
                string text = node.Count.ToString(CultureInfo.InvariantCulture);
                if (label.text != text) {
                    label.text = text;
                }
            }
            for (int i = 0; i < node.Children.Count; i++) {
                ApplyTagCount(node.Children[i]);
            }
        }

        private void AddTagRow(TagNode node, int depth) {
            bool selected = ActiveFilter.Tags.Contains(node.FullTag);
            bool hasChildren = node.Children.Count > 0;
            bool folded = _foldedTags.Contains(node.FullTag);

            VisualElement row = new VisualElement();
            row.AddToClassList("kl-tagrow");
            if (selected) {
                row.AddToClassList("kl-tagrow--selected");
            }
            row.style.paddingLeft = 4f + depth * 12f;

            if (hasChildren) {
                // The arrow swallows the click so folding a branch does not also select it -
                // two different intentions land within a few pixels of each other.
                Label arrow = new Label(folded ? "▸" : "▾");
                arrow.AddToClassList("kl-fold");
                arrow.RegisterCallback<PointerDownEvent>(evt => {
                    evt.StopPropagation();
                    ToggleFold(node.FullTag);
                });
                row.Add(arrow);
            } else {
                // Keeps leaf names on the same left edge as their folding siblings.
                VisualElement spacer = new VisualElement();
                spacer.AddToClassList("kl-fold");
                row.Add(spacer);
            }

            row.RegisterCallback<PointerDownEvent>(_ => ToggleTag(node.FullTag));

            VisualElement swatch = new VisualElement();
            swatch.AddToClassList("kl-swatch");
            swatch.style.backgroundColor = TagColor.For(node.FullTag);
            row.Add(swatch);

            Label label = new Label(node.Segment);
            label.AddToClassList("kl-tagname");
            row.Add(label);

            Label count = new Label(node.Count.ToString(CultureInfo.InvariantCulture));
            count.AddToClassList("kl-tagcount");
            row.Add(count);
            _tagCountLabels[node.FullTag] = count;

            _tagPane.Add(row);

            if (folded) {
                return;
            }
            for (int i = 0; i < node.Children.Count; i++) {
                AddTagRow(node.Children[i], depth + 1);
            }
        }

        private void ToggleFold(string fullTag) {
            if (!_foldedTags.Remove(fullTag)) {
                _foldedTags.Add(fullTag);
            }
            EditorPrefs.SetString(_foldedTagsKey, string.Join(FoldSeparator, _foldedTags));
            RefreshTagPane();
        }

        private void ToggleTag(string fullTag) {
            LogFilter filter = ActiveFilter;
            int index = filter.Tags.IndexOf(fullTag);
            if (index >= 0) {
                filter.Tags.RemoveAt(index);
            } else {
                filter.Tags.Add(fullTag);
            }
            RebuildActiveView();
        }

        // =====================================================================
        // Rows
        // =====================================================================

        private VisualElement MakeRow() {
            VisualElement row = new VisualElement();
            row.AddToClassList("kl-row");

            VisualElement strip = new VisualElement();
            strip.AddToClassList("kl-strip");
            row.Add(strip);

            Label frame = new Label();
            frame.AddToClassList("kl-cell-frame");
            row.Add(frame);

            Label time = new Label();
            time.AddToClassList("kl-cell-time");
            row.Add(time);

            Label tag = new Label();
            tag.AddToClassList("kl-cell-tag");
            row.Add(tag);

            Label message = new Label();
            message.AddToClassList("kl-cell-message");
            row.Add(message);

            Label repeats = new Label();
            repeats.AddToClassList("kl-cell-repeats");
            row.Add(repeats);

            row.AddManipulator(new ContextualMenuManipulator(evt => BuildRowMenu(evt, row)));
            return row;
        }

        private void BindRow(VisualElement element, int index) {
            TabView view = ActiveView;
            if (index >= view.Sequences.Count) {
                return;
            }
            element.userData = index;

            if (!Source.TryGetBySequence(view.Sequences[index], out LogRecord record)) {
                // Rows are recycled, so everything the record that had this one set has to be
                // put back as well as the text: its tag colour on the stripe, its severity on
                // the message, and whichever columns were showing when it was bound.
                element.ElementAt(0).style.backgroundColor = Color.clear;
                element.ElementAt(1).style.display = ShowFrame ? DisplayStyle.Flex : DisplayStyle.None;
                element.ElementAt(2).style.display = ShowTime ? DisplayStyle.Flex : DisplayStyle.None;
                element.ElementAt(3).style.display = ShowTag ? DisplayStyle.Flex : DisplayStyle.None;
                ((Label)element.ElementAt(1)).text = string.Empty;
                ((Label)element.ElementAt(2)).text = string.Empty;
                ((Label)element.ElementAt(3)).text = string.Empty;

                Label expired = (Label)element.ElementAt(4);
                expired.text = "(record expired)";
                expired.EnableInClassList("kl-level-warning", false);
                expired.EnableInClassList("kl-level-error", false);

                ((Label)element.ElementAt(5)).text = string.Empty;
                return;
            }

            element.ElementAt(1).style.display = ShowFrame ? DisplayStyle.Flex : DisplayStyle.None;
            element.ElementAt(2).style.display = ShowTime ? DisplayStyle.Flex : DisplayStyle.None;
            element.ElementAt(3).style.display = ShowTag ? DisplayStyle.Flex : DisplayStyle.None;

            element.ElementAt(0).style.backgroundColor = TagColor.For(record.Tag);
            ((Label)element.ElementAt(1)).text = record.Frame.ToString();
            ((Label)element.ElementAt(2)).text = (record.TimeMs / 1000.0).ToString("0.00");

            Label tagLabel = (Label)element.ElementAt(3);
            tagLabel.text = ShortTag(record.Tag);

            Label messageLabel = (Label)element.ElementAt(4);
            messageLabel.text = Clip(FirstLine(record.Message));
            messageLabel.EnableInClassList("kl-level-warning", record.Level == LogLevel.Warning);
            messageLabel.EnableInClassList("kl-level-error", record.Level == LogLevel.Error);

            Label repeats = (Label)element.ElementAt(5);
            int count = view.Repeats[index];
            repeats.text = count > 1 ? "x" + count : string.Empty;
        }

        private void BuildRowMenu(ContextualMenuPopulateEvent evt, VisualElement row) {
            if (!(row.userData is int index)) {
                return;
            }
            TabView view = ActiveView;
            if (index >= view.Sequences.Count) {
                return;
            }
            if (!Source.TryGetBySequence(view.Sequences[index], out LogRecord record)) {
                return;
            }

            int frame = record.Frame;
            string tag = record.Tag;
            string message = record.Message;

            evt.menu.AppendAction("Show only frame " + frame, _ => {
                ActiveFilter.IsolatedFrame = frame;
                RebuildActiveView();
            });
            evt.menu.AppendAction("Filter by tag '" + tag + "'", _ => {
                if (!ActiveFilter.Tags.Contains(tag)) {
                    ActiveFilter.Tags.Add(tag);
                }
                RebuildActiveView();
            });
            evt.menu.AppendAction("Copy message", _ => EditorGUIUtility.systemCopyBuffer = message);
        }

        // =====================================================================
        // Selection and navigation
        // =====================================================================

        private void OnSelectionChanged(IEnumerable<int> indices) {
            _detailScroll.scrollOffset = Vector2.zero;

            if (!TryGetSelectedRecord(out LogRecord record)) {
                _detailHeader.text = string.Empty;
                _detailBody.text = string.Empty;
                _sourceButton.SetEnabled(false);
                _pingButton.SetEnabled(false);
                return;
            }

            _detailHeader.text = record.Tag + "  ·  " + record.Level + "  ·  " + record.Channel +
                                 "  ·  frame " + record.Frame + "  ·  " +
                                 (record.TimeMs / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + "s";

            string body = record.Message;
            if (!string.IsNullOrEmpty(record.StackTrace)) {
                body += "\n\n" + record.StackTrace;
            }
            if (!string.IsNullOrEmpty(record.File)) {
                body += "\n\n" + record.File + ":" + record.Line;
            }
            _detailBody.text = body;

            // Enabled for an asset too, not only a source file, so it matches what a
            // double-click will actually do.
            _sourceButton.SetEnabled(TryGetSourceLocation(in record, out _, out _) || ResolveContextId(in record) != 0);

            int contextId = ResolveContextId(in record);

            // Resolved here rather than on the click, so a button that cannot do anything looks
            // like one instead of reporting the bad news afterwards.
            bool canPing = contextId != 0 && EditorUtility.InstanceIDToObject(contextId) != null;
            _pingButton.SetEnabled(canPing);
            _pingButton.tooltip = contextId == 0
                ? "This log was written without a related object."
                : canPing
                    ? "Highlight the related object in the hierarchy."
                    : "The object this log referred to no longer exists.";
        }

        private bool TryGetSelectedRecord(out LogRecord record) {
            record = default;
            int index = _listView.selectedIndex;
            TabView view = ActiveView;
            if (index < 0 || index >= view.Sequences.Count) {
                return false;
            }
            return Source.TryGetBySequence(view.Sequences[index], out record);
        }

        private void OpenSelectedSource() {
            if (!TryGetSelectedRecord(out LogRecord record)) {
                return;
            }
            if (TryGetSourceLocation(in record, out string file, out int line)) {
                OpenSource(file, line);
                return;
            }

            // No source to go to. Unity's console opens the asset the engine logged against -
            // the StyleSheet named in a USS warning - so follow it there instead of stopping
            // at "this record has no file".
            int contextId = ResolveContextId(in record);
            if (contextId != 0) {
                if (AssetDatabase.OpenAsset(contextId)) {
                    return;
                }
                UnityEngine.Object target = EditorUtility.InstanceIDToObject(contextId);
                if (target != null) {
                    EditorGUIUtility.PingObject(target);
                    return;
                }
            }
            ShowNotification(new GUIContent("Nothing to open for this record"));
        }

        /// <summary>
        /// Where a record points in source: its own call site when it has one, otherwise the
        /// first project frame in its stack trace - which is all a captured record ever has.
        /// </summary>
        private bool TryGetSourceLocation(in LogRecord record, out string file, out int line) {
            if (!string.IsNullOrEmpty(record.File)) {
                file = record.File;
                line = record.Line;
                return true;
            }
            if (TryFindSourceInStackTrace(record.StackTrace, out file, out line)) {
                return true;
            }

            ConsoleContext context = LookUpConsoleContext(in record);
            file = context.File;
            line = context.Line;
            return !string.IsNullOrEmpty(file);
        }

        /// <summary>
        /// The context object for a record, asking Unity's console for captured ones.
        /// <para>
        /// Looked up when a row is selected rather than when a record arrives: the console
        /// store takes a lock and is walked end to end, which is fine once per click and
        /// absurd once per log line.
        /// </para>
        /// </summary>
        private int ResolveContextId(in LogRecord record) {
            if (record.ContextInstanceId != 0) {
                return record.ContextInstanceId;
            }
            return LookUpConsoleContext(in record).InstanceId;
        }

        private ConsoleContext LookUpConsoleContext(in LogRecord record) {
            if (!record.Captured) {
                return default;
            }
            if (_consoleContexts.TryGetValue(record.Sequence, out ConsoleContext cached)) {
                return cached;
            }

            ConsoleContext resolved = default;
            if (ConsoleEntryBridge.TryResolve(record.Message, out int instanceId, out string file, out int line)) {
                resolved = new ConsoleContext(instanceId, file, line);
            }
            _consoleContexts[record.Sequence] = resolved;
            return resolved;
        }

        private readonly struct ConsoleContext {
            public readonly int InstanceId;
            public readonly string File;
            public readonly int Line;

            public ConsoleContext(int instanceId, string file, int line) {
                InstanceId = instanceId;
                File = file;
                Line = line;
            }
        }

        private void PingSelectedContext() {
            if (!TryGetSelectedRecord(out LogRecord record)) {
                return;
            }
            int contextId = ResolveContextId(in record);
            if (contextId == 0) {
                return;
            }
            UnityEngine.Object target = EditorUtility.InstanceIDToObject(contextId);
            if (target == null) {
                // A notification, not text appended to the header: the header is structured
                // metadata, and appending there stacked up one copy of this per click.
                ShowNotification(new GUIContent("That object no longer exists"));
                _pingButton.SetEnabled(false);
                _pingButton.tooltip = "The object this log referred to no longer exists.";
                return;
            }
            EditorGUIUtility.PingObject(target);
        }

        /// <summary>
        /// CallerFilePath hands back an absolute path, while AssetDatabase wants one relative
        /// to the project root. Anything outside the project - a package referenced by path,
        /// for instance - falls back to opening in the external editor.
        /// </summary>
        private void OpenSource(string file, int line) {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName.Replace('\\', '/');

            if (TryResolveAssetPath(file, projectRoot, out string relative)) {
                UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(relative);
                if (asset != null) {
                    AssetDatabase.OpenAsset(asset, line);
                    return;
                }
            }

            string normalized = file.Replace('\\', '/');
            if (File.Exists(normalized)) {
                InternalEditorUtility.OpenFileAtLineExternal(normalized, line);
                return;
            }

            // Doing nothing would be the worst answer: the row plainly shows a file and a line,
            // so the only readings left are "the window is broken" or "the line is a lie".
            ShowNotification(new GUIContent("Cannot find " + Path.GetFileName(normalized)));
        }

        /// <summary>
        /// Find a source location inside a stack trace.
        /// <para>
        /// Records captured from Unity's own log stream carry no file or line - the callback
        /// hands over the message, the trace and the level, nothing more. Unity writes the
        /// location into the trace itself as <c>(at Assets/Foo.cs:42)</c>, which is the same
        /// thing its console navigates by, so that is where to look.
        /// </para>
        /// </summary>
        public static bool TryFindSourceInStackTrace(string stackTrace, out string path, out int line) {
            path = null;
            line = 0;
            if (string.IsNullOrEmpty(stackTrace)) {
                return false;
            }

            int search = 0;
            while (true) {
                int open = stackTrace.IndexOf("(at ", search, StringComparison.Ordinal);
                if (open < 0) {
                    return false;
                }
                int close = stackTrace.IndexOf(')', open);
                if (close < 0) {
                    return false;
                }

                string candidate = stackTrace.Substring(open + 4, close - open - 4);
                search = close + 1;

                int colon = candidate.LastIndexOf(':');
                if (colon <= 0 || !int.TryParse(candidate.Substring(colon + 1), out int parsed)) {
                    continue;
                }

                string file = candidate.Substring(0, colon);
                // Unity also writes frames from its own compiled sources, whose paths point at
                // a build agent's disk. Only project-relative ones can be opened here.
                if (file.IndexOf("/Assets/", StringComparison.OrdinalIgnoreCase) < 0 &&
                    file.IndexOf("/Packages/", StringComparison.OrdinalIgnoreCase) < 0 &&
                    !file.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                    !file.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                path = file;
                line = parsed;
                return true;
            }
        }

        /// <summary>
        /// Turn a recorded source path into one this project can open.
        /// <para>
        /// The path was captured wherever the code was compiled, which is not necessarily this
        /// machine - a session file from someone else's build carries their directories, and
        /// their drive letter or home folder means nothing here. Everything from the last
        /// <c>Assets/</c> or <c>Packages/</c> segment onwards still addresses the same file.
        /// </para>
        /// </summary>
        public static bool TryResolveAssetPath(string recordedPath, string projectRoot, out string relative) {
            relative = null;
            if (string.IsNullOrEmpty(recordedPath)) {
                return false;
            }

            string normalized = recordedPath.Replace('\\', '/');
            if (!string.IsNullOrEmpty(projectRoot)) {
                string root = projectRoot.Replace('\\', '/').TrimEnd('/');
                if (normalized.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)) {
                    relative = normalized.Substring(root.Length + 1);
                    return true;
                }
            }

            int cut = Mathf.Max(
                normalized.LastIndexOf("/Assets/", StringComparison.OrdinalIgnoreCase),
                normalized.LastIndexOf("/Packages/", StringComparison.OrdinalIgnoreCase));
            if (cut >= 0) {
                relative = normalized.Substring(cut + 1);
                return true;
            }

            if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)) {
                relative = normalized;
                return true;
            }
            return false;
        }

        // =====================================================================
        // Refresh helpers
        // =====================================================================

        private void RefreshList() {
            _listView.itemsSource = ActiveView.Sequences;

            // Rebinding every visible row fifteen times a second costs the same whether or not
            // the tab changed, and most ticks it does not. The view's revision is what says so:
            // the row count cannot, because a full ring buffer drops one record for every
            // record it takes - the count holds still while the contents move under it, and
            // the list stopped repainting exactly when the logs were busiest.
            int revision = ActiveView.Revision;
            bool changed = revision != _lastRenderedRevision;
            if (changed) {
                _lastRenderedRevision = revision;
                _listView.RefreshItems();
            }

            bool empty = ActiveView.Sequences.Count == 0;
            _emptyHint.style.display = empty ? DisplayStyle.Flex : DisplayStyle.None;
            if (empty) {
                _emptyHint.text = EmptyHint();
            } else if (_followTail && !_paused && changed) {
                _listView.ScrollToItem(-1);
            }
        }

        private void RefreshLevelCounts() {
            // Writing the same three captions back every tick builds three strings and dirties
            // the toolbar for nothing; most ticks change at most one of the counts.
            SetCount(_logToggle, LogLevel.Log, "Log ");
            SetCount(_warningToggle, LogLevel.Warning, "Warn ");
            SetCount(_errorToggle, LogLevel.Error, "Error ");
        }

        private void SetCount(ToolbarToggle toggle, LogLevel level, string caption) {
            int count = _levelCounts[(int)level];
            if (_renderedCounts[(int)level] == count) {
                return;
            }
            _renderedCounts[(int)level] = count;
            toggle.text = caption + count.ToString(CultureInfo.InvariantCulture);
        }

        private void SyncToolbarToFilter() {
            LogFilter filter = ActiveFilter;
            _logToggle.SetValueWithoutNotify(filter.ShowLog);
            _warningToggle.SetValueWithoutNotify(filter.ShowWarning);
            _errorToggle.SetValueWithoutNotify(filter.ShowError);
            _devToggle.SetValueWithoutNotify(filter.ShowDev);
            _prodToggle.SetValueWithoutNotify(filter.ShowProd);
            _collapseToggle.SetValueWithoutNotify(filter.Collapse);
            _searchField.SetValueWithoutNotify(filter.Search);

            bool isolating = filter.IsolatedFrame >= 0;
            _frameIsolationLabel.style.display = isolating ? DisplayStyle.Flex : DisplayStyle.None;
            if (isolating) {
                _frameIsolationLabel.text = "frame " + filter.IsolatedFrame + "  x";
            }
        }

        /// <summary>
        /// A tab that matches nothing says why. A tag selected here but never emitted is the
        /// usual cause, and naming it turns a silent typo into an obvious one.
        /// </summary>
        private string EmptyHint() {
            if (_tagCounts.Count == 0) {
                // A loaded file holding nothing is the run that died during startup, which is
                // the case the reader was taught to open. Telling whoever opened it to enter
                // play mode would be a plain lie.
                return _session != null
                    ? "This file holds a session header and no records.\nWhatever wrote it ended before logging anything."
                    : "No records yet.\nEnter play mode, or call Log.Dev / Log.Prod.";
            }

            LogFilter filter = ActiveFilter;
            for (int i = 0; i < filter.Tags.Count; i++) {
                if (!TagSeen(filter.Tags[i])) {
                    return "Tag '" + filter.Tags[i] + "' has not appeared in this session.";
                }
            }
            if (filter.IsolatedFrame >= 0) {
                return "Nothing was logged on frame " + filter.IsolatedFrame + ".";
            }
            return "No records match this tab's filter.";
        }

        private bool TagSeen(string tag) {
            foreach (string seen in _tagCounts.Keys) {
                if (LogFilter.TagMatches(seen, tag)) {
                    return true;
                }
            }
            return false;
        }

        private StyleSheet LoadStyleSheet() {
            // Resolved from this script's own location so the package works wherever it is
            // installed: a path reference outside the project, an embedded folder, or Library.
            MonoScript script = MonoScript.FromScriptableObject(this);
            string scriptPath = script != null ? AssetDatabase.GetAssetPath(script) : null;
            if (!string.IsNullOrEmpty(scriptPath)) {
                string directory = Path.GetDirectoryName(scriptPath)?.Replace('\\', '/');
                if (!string.IsNullOrEmpty(directory)) {
                    StyleSheet beside = AssetDatabase.LoadAssetAtPath<StyleSheet>(directory + "/LogWindow.uss");
                    if (beside != null) {
                        return beside;
                    }
                }
            }

            // A package compiled into a DLL has no script asset to find the sheet beside, and
            // the path was dereferenced without asking - which threw out of CreateGUI and left
            // a window with no contents at all rather than an unstyled one.
            string[] found = AssetDatabase.FindAssets("LogWindow t:StyleSheet");
            for (int i = 0; i < found.Length; i++) {
                StyleSheet sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(AssetDatabase.GUIDToAssetPath(found[i]));
                if (sheet != null) {
                    return sheet;
                }
            }
            return null;
        }

        private static string ShortTag(string tag) {
            int dot = tag.LastIndexOf('.');
            return dot < 0 ? tag : tag.Substring(dot + 1);
        }

        /// <summary>
        /// Cut a message down before it reaches a row.
        /// <para>
        /// The column clips what does not fit, but clipping happens after layout: UI Toolkit
        /// still measures and builds a mesh for every glyph handed to it. Engine warnings run
        /// past two hundred characters while about ninety are visible, and the cost is paid on
        /// every repaint of a focused window, for every visible row.
        /// </para>
        /// </summary>
        private static string Clip(string message) {
            const int limit = 120;
            if (message == null || message.Length <= limit) {
                return message;
            }
            return message.Substring(0, limit) + "\u2026";
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
