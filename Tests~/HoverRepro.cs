using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// A bare ListView to hover over, with the pieces the log window puts on a row switchable one
/// at a time.
/// <code>
/// Window -> Kensei -> Hover repro
/// </code>
/// <para>
/// It exists to answer one question the profiler answers slowly: is the stutter something this
/// package does to a row, or something a Unity ListView does on its own? Nothing here belongs
/// to the package - no stylesheet, no records, no polling - so anything it reproduces with
/// every toggle off is Unity's, or the machine's.
/// </para>
/// <para>
/// **Row tooltip** is the positive control. A tooltip in UI Toolkit is a real window, and
/// building and tearing one down per row crossing is what froze the editor on Windows before
/// the tooltips came off the rows. If that toggle reproduces the symptom being chased and the
/// others do not, the symptom is window churn and the hunt is for whatever else makes a window.
/// </para>
/// </summary>
public sealed class HoverRepro : EditorWindow {
    private const int Rows = 8192;
    private const float RowHeight = 20f;
    private const double RebindInterval = 1.0 / 15.0;

    private readonly List<int> _items = new List<int>();

    // Assigned in CreateGUI, which is the only place they can be built. The suppressions keep
    // the file quiet in a project that compiles with -nullable:enable, and mean nothing in one
    // that does not.
    private VisualElement _host = null!;
    private ListView _list = null!;
    private Label _readout = null!;

    private bool _rowTooltip;
    private bool _sixCells = true;
    private bool _contextMenu;
    private bool _rebind;

    private double _lastUpdate;
    private double _lastRebind;
    private double _lastReport;
    private double _worstGap;

    [MenuItem("Window/Kensei/Hover repro")]
    private static void Open() {
        HoverRepro window = GetWindow<HoverRepro>();
        window.titleContent = new GUIContent("Hover repro");
        window.minSize = new Vector2(420f, 300f);
        window.Show();
    }

    private void OnEnable() {
        EditorApplication.update += OnEditorUpdate;
    }

    private void OnDisable() {
        EditorApplication.update -= OnEditorUpdate;
    }

    private void CreateGUI() {
        for (int i = 0; i < Rows; i++) {
            _items.Add(i);
        }

        VisualElement toolbar = new VisualElement();
        toolbar.style.flexDirection = FlexDirection.Row;
        toolbar.style.flexWrap = Wrap.Wrap;
        toolbar.style.flexShrink = 0f;
        rootVisualElement.Add(toolbar);

        toolbar.Add(Switch("Row tooltip", () => _rowTooltip, value => _rowTooltip = value));
        toolbar.Add(Switch("Six cells", () => _sixCells, value => _sixCells = value));
        toolbar.Add(Switch("Context menu", () => _contextMenu, value => _contextMenu = value));
        toolbar.Add(Switch("Rebind 15/s", () => _rebind, value => _rebind = value));

        _readout = new Label("worst stall: -");
        _readout.style.flexShrink = 0f;
        _readout.style.paddingLeft = 6f;
        _readout.style.paddingTop = 2f;
        _readout.style.paddingBottom = 2f;
        rootVisualElement.Add(_readout);

        _host = new VisualElement();
        _host.style.flexGrow = 1f;
        rootVisualElement.Add(_host);

        BuildList();
    }

    private Toggle Switch(string label, System.Func<bool> read, System.Action<bool> write) {
        Toggle toggle = new Toggle(label) { value = read() };
        toggle.style.marginLeft = 6f;
        toggle.RegisterValueChangedCallback(evt => {
            write(evt.newValue);
            BuildList();
        });
        return toggle;
    }

    private void BuildList() {
        _host.Clear();

        _list = new ListView {
            fixedItemHeight = RowHeight,
            virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
            selectionType = SelectionType.Single,
            makeItem = MakeRow,
            bindItem = BindRow,
            itemsSource = _items
        };
        _list.style.flexGrow = 1f;
        _host.Add(_list);
    }

    private VisualElement MakeRow() {
        VisualElement row = new VisualElement();
        row.style.flexDirection = FlexDirection.Row;
        row.style.alignItems = Align.Center;
        row.style.height = RowHeight;

        if (_rowTooltip) {
            row.tooltip = "The shape of the fault that froze this editor before.";
        }
        if (_contextMenu) {
            row.AddManipulator(new ContextualMenuManipulator(evt => evt.menu.AppendAction("Nothing", _ => { })));
        }

        int cells = _sixCells ? 6 : 1;
        for (int i = 0; i < cells; i++) {
            Label cell = new Label();
            cell.style.marginRight = 6f;
            if (i == cells - 1) {
                cell.style.flexGrow = 1f;
                cell.style.overflow = Overflow.Hidden;
                cell.style.whiteSpace = WhiteSpace.NoWrap;
            }
            row.Add(cell);
        }
        return row;
    }

    private void BindRow(VisualElement element, int index) {
        for (int i = 0; i < element.childCount; i++) {
            ((Label)element.ElementAt(i)).text = i == element.childCount - 1
                ? "row " + index + " - a message about as long as an engine warning tends to be"
                : index.ToString();
        }
    }

    private void OnEditorUpdate() {
        double now = EditorApplication.timeSinceStartup;

        // The gap between one editor tick and the next is what a stall actually is, and it
        // needs no profiler to read.
        if (_lastUpdate > 0.0) {
            double gap = now - _lastUpdate;
            if (gap > _worstGap) {
                _worstGap = gap;
            }
        }
        _lastUpdate = now;

        if (_rebind && _list != null && now - _lastRebind >= RebindInterval) {
            _lastRebind = now;
            _list.RefreshItems();
        }

        if (now - _lastReport >= 1.0) {
            _lastReport = now;
            if (_readout != null) {
                _readout.text = "worst stall in the last second: " + (_worstGap * 1000.0).ToString("0") + " ms";
            }
            _worstGap = 0.0;
        }
    }
}
