using System.Collections.Generic;
using KenseiLog;
using KenseiLog.Editor;
using UnityEditor;

/// <summary>
/// Enters play mode for real and reads the editor window afterwards. What the run logged before
/// its first scene loaded has to be there, under the line marking the entry, exactly once; what
/// was logged before Play was pressed has to be gone.
/// <para>
/// Apart from <c>SmokeRunner</c> because nothing it can call decides this. The clear, the
/// domain reload and <c>RuntimeInitializeOnLoadMethod</c> are put in order by the editor, and the
/// fault this guards lived entirely in that order: the clear ran on <c>EnteredPlayMode</c>, after
/// the run had begun, and took its first records - a startup error first among them - with it.
/// </para>
/// Run with: Unity -projectPath . -batchmode -nographics -executeMethod PlayEntryCheck.RunWithDomainReload
/// or <c>PlayEntryCheck.RunWithoutDomainReload</c>. No -quit: it leaves through Exit once the
/// entry has been read, and on its own after a minute if the entry never comes.
/// </summary>
public static class PlayEntryCheck {
    // The same strings as PlayEntryBoot, which cannot see this class from the runtime assembly.
    private const string Tag = "PlayEntryCheck";
    private const string BootMessage = "logged before the first scene loaded";
    private const string BeforeMessage = "logged before Play was pressed";
    private const string MarkerTag = "Editor";
    private const string Marker = "Entered play mode";

    // SessionState, because the entry with domain reload on lands in a domain that remembers
    // nothing else of this run.
    private const string RunningKey = "PlayEntryCheck.Running";
    private const string ModeKey = "PlayEntryCheck.Mode";
    private const string StartedAtKey = "PlayEntryCheck.StartedAt";
    private const double TimeoutSeconds = 60.0;

    // Set by EnteredPlayMode, which arrives in the domain the run lives in - a static is enough.
    private static bool _entered;

    public static void RunWithDomainReload() {
        Start(skipDomainReload: false);
    }

    public static void RunWithoutDomainReload() {
        Start(skipDomainReload: true);
    }

    private static void Start(bool skipDomainReload) {
        EditorSettings.enterPlayModeOptionsEnabled = skipDomainReload;
        EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload;
        EditorSink.ClearOnPlay = true;

        Log.Info(Tag, BeforeMessage);

        SessionState.SetBool(RunningKey, true);
        SessionState.SetString(ModeKey, skipDomainReload ? "without domain reload" : "with domain reload");
        SessionState.SetFloat(StartedAtKey, (float)EditorApplication.timeSinceStartup);
        Subscribe();
        EditorApplication.isPlaying = true;
    }

    [InitializeOnLoadMethod]
    private static void Resume() {
        if (SessionState.GetBool(RunningKey, false)) {
            Subscribe();
        }
    }

    private static void Subscribe() {
        EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
        EditorApplication.update -= Watchdog;
        EditorApplication.update += Watchdog;
    }

    private static void OnPlayModeChanged(PlayModeStateChange change) {
        if (change == PlayModeStateChange.EnteredPlayMode) {
            // Read on the next editor update, so that whatever the sink itself does on
            // EnteredPlayMode has been done: the order of two handlers of one event is nothing to
            // rest a check on. Not delayCall, which batchmode does not run during play mode.
            _entered = true;
        }
    }

    private static void Watchdog() {
        if (!SessionState.GetBool(RunningKey, false)) {
            return;
        }
        if (_entered) {
            Report();
        } else if (EditorApplication.timeSinceStartup - SessionState.GetFloat(StartedAtKey, 0f) > TimeoutSeconds) {
            Finish(new List<string> { "play mode was not entered within " + TimeoutSeconds + " s" });
        }
    }

    private static void Report() {
        LogRecord[] held = new LogRecord[4096];
        int count = EditorSink.Instance.Buffer.CopyNewerThan(-1, held);

        int markers = 0;
        int before = 0;
        long markerAt = -1;
        long bootAt = -1;
        for (int i = 0; i < count; i++) {
            LogRecord record = held[i];
            if (record.Tag == MarkerTag && record.Message != null && record.Message.StartsWith(Marker, System.StringComparison.Ordinal)) {
                markers++;
                markerAt = record.Sequence;
            } else if (record.Tag == Tag && record.Message == BootMessage) {
                bootAt = record.Sequence;
            } else if (record.Tag == Tag && record.Message == BeforeMessage) {
                before++;
            }
        }

        List<string> failures = new List<string>();
        if (bootAt < 0) {
            failures.Add("what the run logged before its first scene is not in the window");
        }
        if (markers != 1) {
            failures.Add("the window holds " + markers + " lines marking the entry, not one");
        }
        if (bootAt >= 0 && markerAt >= 0 && markerAt > bootAt) {
            failures.Add("the line marking the entry comes after what the run logged");
        }
        if (before > 0) {
            failures.Add("what was logged before Play was pressed survived Clear on Play");
        }
        Finish(failures);
    }

    private static void Finish(List<string> failures) {
        SessionState.EraseBool(RunningKey);
        string mode = SessionState.GetString(ModeKey, "?");
        System.Console.WriteLine(failures.Count == 0
            ? "PLAYENTRY RESULT: PASS (" + mode + ")"
            : "PLAYENTRY RESULT: FAIL (" + mode + "): " + string.Join("; ", failures));
        EditorApplication.Exit(failures.Count == 0 ? 0 : 1);
    }
}
