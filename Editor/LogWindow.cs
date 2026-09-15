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
        private const string FrameKey = "KenseiLog.Columns.Frame";
        private const string TimeKey = "KenseiLog.Columns.Time";
        private const string TagKey = "KenseiLog.Columns.Tag";

        [SerializeField] private List<LogFilter> _filters = new List<LogFilter>();
        [SerializeField] private int _activeTab;

        private readonly List<TabView> _views = new List<TabView>();
        private readonly Dictionary<string, int> _tagCounts = new Dictionary<string, int>();
        private readonly int[] _levelCounts = new int[3];

        private LogRecord[] _scratch;
        private long _lastSequence;
        private int _lastVersion = -1;
        private double _lastRefresh;
        private bool _paused;
        private bool _followTail = true;
        private bool _showFrame = true;
        private bool _showTime = true;
        private bool _showTag = true;

        private VisualElement _tabBar;
        private ScrollView _tagPane;
        private ListView _listView;
        private Label _emptyHint;
        private Label _detailHeader;
        private TextField _detailBody;
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

        private LogSession _session;

        private LogFilter ActiveFilter => _filters[Mathf.Clamp(_activeTab, 0, _filters.Count - 1)];

        private TabView ActiveView => _views[Mathf.Clamp(_activeTab, 0, _views.Count - 1)];

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
            _showFrame = EditorPrefs.GetBool(FrameKey, true);
            _showTime = EditorPrefs.GetBool(TimeKey, true);
            _showTag = EditorPrefs.GetBool(TagKey, true);

            _scratch = new LogRecord[Source.Capacity];
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

            VisualElement main = new VisualElement();
            main.AddToClassList("kl-main");
            body.Add(main);

            main.Add(BuildToolbar());

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

            ToolbarToggle pause = new ToolbarToggle { text = "Pause", tooltip = "Stop updating the view. Recording continues." };
            pause.value = _paused;
            pause.RegisterValueChangedCallback(evt => _paused = evt.newValue);
            toolbar.Add(pause);

            ToolbarToggle follow = new ToolbarToggle { text = "Follow", tooltip = "Keep scrolling to the newest record." };
            follow.value = _followTail;
            follow.RegisterValueChangedCallback(evt => _followTail = evt.newValue);
            toolbar.Add(follow);

            _searchField = new ToolbarSearchField();
            _searchField.AddToClassList("kl-search");
            _searchField.tooltip = "Searches the message text only. Tags are a separate field.";
            _searchField.RegisterValueChangedCallback(evt => {
                ActiveFilter.Search = evt.newValue;
                RebuildActiveView();
            });
            toolbar.Add(_searchField);

            toolbar.Add(Spacer());

            _logToggle = FilterToggle("Log", value => ActiveFilter.ShowLog = value);
            _warningToggle = FilterToggle("Warn", value => ActiveFilter.ShowWarning = value);
            _errorToggle = FilterToggle("Error", value => ActiveFilter.ShowError = value);
            toolbar.Add(_logToggle);
            toolbar.Add(_warningToggle);
            toolbar.Add(_errorToggle);

            _devToggle = FilterToggle("Dev", value => ActiveFilter.ShowDev = value);
            _prodToggle = FilterToggle("Prod", value => ActiveFilter.ShowProd = value);
            toolbar.Add(_devToggle);
            toolbar.Add(_prodToggle);

            _collapseToggle = FilterToggle("Collapse", value => ActiveFilter.Collapse = value);
            toolbar.Add(_collapseToggle);

            toolbar.Add(BuildColumnsMenu());

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
            toolbar.Add(openFile);

            _clearButton = new ToolbarButton(() => {
                EditorSink.Instance.Clear();
                ResetIngest();
            }) { text = "Clear" };
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
            OnSourceChanged();
        }

        private void GoLive() {
            if (_session == null) {
                return;
            }
            _session = null;
            OnSourceChanged();
        }

        private void OnSourceChanged() {
            _scratch = new LogRecord[Mathf.Max(64, Source.Capacity)];
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
        /// Column visibility, as checked items rather than three more toolbar toggles: the bar
        /// already runs the full width of a docked window, and columns are a view preference
        /// worth less permanent space than the filters beside them.
        /// <para>
        /// These are a window setting, not a per-tab one. Which columns you want is a habit,
        /// and having it differ from tab to tab would be a surprise every time you switched.
        /// </para>
        /// </summary>
        private ToolbarMenu BuildColumnsMenu() {
            ToolbarMenu menu = new ToolbarMenu { text = "Columns" };
            menu.tooltip = "Show or hide the frame, time and tag columns.";

            AppendColumnItem(menu, "Frame", () => _showFrame, value => _showFrame = value, FrameKey);
            AppendColumnItem(menu, "Time", () => _showTime, value => _showTime = value, TimeKey);
            AppendColumnItem(menu, "Tag", () => _showTag, value => _showTag = value, TagKey);

            return menu;
        }

        private void AppendColumnItem(ToolbarMenu menu, string label, Func<bool> read, Action<bool> write, string key) {
            menu.menu.AppendAction(
                label,
                _ => {
                    bool value = !read();
                    write(value);
                    EditorPrefs.SetBool(key, value);
                    // Rebuild rather than refresh: display is set while binding, and recycled
                    // rows keep whatever the last bind gave them until they are bound again.
                    _listView.Rebuild();
                },
                _ => read() ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
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

            _detailBody = new TextField { multiline = true, isReadOnly = true };
            _detailBody.AddToClassList("kl-detail-body");
            detail.Add(_detailBody);

            VisualElement actions = new VisualElement();
            actions.AddToClassList("kl-detail-actions");
            detail.Add(actions);

            _sourceButton = new Button(OpenSelectedSource) { text = "Open source" };
            _pingButton = new Button(PingSelectedContext) { text = "Ping" };
            actions.Add(_sourceButton);
            actions.Add(_pingButton);

            return detail;
        }

        private static VisualElement Spacer() {
            VisualElement spacer = new VisualElement();
            spacer.AddToClassList("kl-spacer");
            return spacer;
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

        private void Ingest(bool force) {
            if (_paused && !force) {
                return;
            }

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
            }
            RefreshList();
            RefreshLevelCounts();
        }

        private void ResetIngest() {
            _lastSequence = 0;
            Array.Clear(_levelCounts, 0, _levelCounts.Length);
            _tagCounts.Clear();
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
            _activeTab = Mathf.Clamp(_activeTab, 0, _filters.Count - 1);
            SelectTab(_activeTab);
        }

        private void RenameTab(int index) {
            TabRenamePopup.Show(this, _filters[index].Name, name => {
                _filters[index].Name = name;
                RefreshTabBar();
            });
        }

        // =====================================================================
        // Tag pane
        // =====================================================================

        private void RefreshTagPane() {
            _tagPane.Clear();
            List<TagNode> roots = TagTree.Build(_tagCounts);
            for (int i = 0; i < roots.Count; i++) {
                AddTagRow(roots[i], 0);
            }
        }

        private void AddTagRow(TagNode node, int depth) {
            LogFilter filter = ActiveFilter;
            bool selected = filter.Tags.Contains(node.FullTag);

            VisualElement row = new VisualElement();
            row.AddToClassList("kl-tagrow");
            if (selected) {
                row.AddToClassList("kl-tagrow--selected");
            }
            row.style.paddingLeft = 4f + depth * 12f;
            row.RegisterCallback<PointerDownEvent>(_ => ToggleTag(node.FullTag));

            VisualElement swatch = new VisualElement();
            swatch.AddToClassList("kl-swatch");
            swatch.style.backgroundColor = TagColor.For(node.FullTag);
            row.Add(swatch);

            Label label = new Label(node.Segment);
            label.AddToClassList("kl-tagname");
            label.tooltip = node.FullTag;
            row.Add(label);

            Label count = new Label(node.Count.ToString());
            count.AddToClassList("kl-tagcount");
            row.Add(count);

            _tagPane.Add(row);

            for (int i = 0; i < node.Children.Count; i++) {
                AddTagRow(node.Children[i], depth + 1);
            }
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
                ((Label)element.ElementAt(1)).text = string.Empty;
                ((Label)element.ElementAt(2)).text = string.Empty;
                ((Label)element.ElementAt(3)).text = string.Empty;
                ((Label)element.ElementAt(4)).text = "(record expired)";
                ((Label)element.ElementAt(5)).text = string.Empty;
                return;
            }

            element.ElementAt(1).style.display = _showFrame ? DisplayStyle.Flex : DisplayStyle.None;
            element.ElementAt(2).style.display = _showTime ? DisplayStyle.Flex : DisplayStyle.None;
            element.ElementAt(3).style.display = _showTag ? DisplayStyle.Flex : DisplayStyle.None;

            element.ElementAt(0).style.backgroundColor = TagColor.For(record.Tag);
            ((Label)element.ElementAt(1)).text = record.Frame.ToString();
            ((Label)element.ElementAt(2)).text = (record.TimeMs / 1000.0).ToString("0.00");

            Label tagLabel = (Label)element.ElementAt(3);
            tagLabel.text = ShortTag(record.Tag);
            tagLabel.tooltip = record.Tag;

            Label messageLabel = (Label)element.ElementAt(4);
            messageLabel.text = FirstLine(record.Message);
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
            if (!TryGetSelectedRecord(out LogRecord record)) {
                _detailHeader.text = string.Empty;
                _detailBody.value = string.Empty;
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
            _detailBody.value = body;

            _sourceButton.SetEnabled(TryGetSourceLocation(in record, out _, out _));

            // Resolved here rather than on the click, so a button that cannot do anything looks
            // like one instead of reporting the bad news afterwards.
            bool canPing = record.ContextInstanceId != 0 &&
                           EditorUtility.InstanceIDToObject(record.ContextInstanceId) != null;
            _pingButton.SetEnabled(canPing);
            _pingButton.tooltip = record.ContextInstanceId == 0
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
            }
        }

        /// <summary>
        /// Where a record points in source: its own call site when it has one, otherwise the
        /// first project frame in its stack trace - which is all a captured record ever has.
        /// </summary>
        private static bool TryGetSourceLocation(in LogRecord record, out string file, out int line) {
            if (!string.IsNullOrEmpty(record.File)) {
                file = record.File;
                line = record.Line;
                return true;
            }
            return TryFindSourceInStackTrace(record.StackTrace, out file, out line);
        }

        private void PingSelectedContext() {
            if (!TryGetSelectedRecord(out LogRecord record) || record.ContextInstanceId == 0) {
                return;
            }
            UnityEngine.Object target = EditorUtility.InstanceIDToObject(record.ContextInstanceId);
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
            _listView.RefreshItems();

            bool empty = ActiveView.Sequences.Count == 0;
            _emptyHint.style.display = empty ? DisplayStyle.Flex : DisplayStyle.None;
            if (empty) {
                _emptyHint.text = EmptyHint();
            } else if (_followTail && !_paused) {
                _listView.ScrollToItem(-1);
            }
        }

        private void RefreshLevelCounts() {
            _logToggle.text = "Log " + _levelCounts[(int)LogLevel.Log];
            _warningToggle.text = "Warn " + _levelCounts[(int)LogLevel.Warning];
            _errorToggle.text = "Error " + _levelCounts[(int)LogLevel.Error];
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
                return "No records yet.\nEnter play mode, or call Log.Dev / Log.Prod.";
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
            string scriptPath = AssetDatabase.GetAssetPath(script);
            string directory = Path.GetDirectoryName(scriptPath).Replace('\\', '/');
            return AssetDatabase.LoadAssetAtPath<StyleSheet>(directory + "/LogWindow.uss");
        }

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
