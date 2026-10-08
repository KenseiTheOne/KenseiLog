using System;
using System.Collections.Generic;
using System.Reflection;
using KenseiLog;
using KenseiLog.Editor;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Reloads the domain for real with the console left uncleared, and reads the editor window
/// afterwards. Every line the package mirrored into the console has to be in the window exactly
/// once, as the record it mirrored; none of them may come back a second time under the Unity tag.
/// <para>
/// Apart from <c>SmokeRunner</c>, which checks the matching itself, because the fault lived in
/// what the seed is handed by a real reload: a console the recompile did not clear, read back
/// against the session file the old domain closed.
/// </para>
/// <para>
/// It turns Clear on Recompile off for the run. That flag is the console's, kept in EditorPrefs,
/// so it is shared by every project on the machine: it is put back at the end whatever the
/// result, and a run killed before then has it put back by the next editor that loads this file.
/// </para>
/// Run with: Unity -projectPath . -batchmode -nographics -executeMethod ReloadSeedCheck.Run
/// No -quit: it leaves through Exit once the reload has been read, and on its own after a minute
/// if the reload never comes.
/// </summary>
public static class ReloadSeedCheck {
    private const string Tag = "ReloadSeedCheck";
    private const int ClearOnRecompile = 4096;

    // SessionState for the run, which only has to outlive the reload; EditorPrefs for the flag
    // to put back, which has to outlive a killed editor, since the flag itself does.
    private const string RunningKey = "ReloadSeedCheck.Running";
    private const string NonceKey = "ReloadSeedCheck.Nonce";
    private const string StartedAtKey = "ReloadSeedCheck.StartedAt";
    private const string SavedFlagKey = "KenseiLog.ReloadSeedCheck.ClearOnRecompileWas";
    private const double TimeoutSeconds = 60.0;

    public static void Run() {
        RestoreConsoleFlag();
        try {
            EditorPrefs.SetBool(SavedFlagKey, HasConsoleFlag(ClearOnRecompile));
            SetConsoleFlag(ClearOnRecompile, false);

            LogConfig config = LogCore.Config;
            config.MirrorToUnityConsole = true;
            LogCore.Configure(config);

            // A nonce, so that nothing an earlier run left in the session can be counted.
            string nonce = DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Log.Info(Tag, One(nonce));
            Log.Warning(Tag, Two(nonce));
            Log.Info(Tag, Multi(nonce));
            Debug.Log(Foreign(nonce));

            SessionState.SetString(NonceKey, nonce);
            SessionState.SetBool(RunningKey, true);
            SessionState.SetFloat(StartedAtKey, (float)EditorApplication.timeSinceStartup);
            EditorApplication.update -= Watchdog;
            EditorApplication.update += Watchdog;
            EditorUtility.RequestScriptReload();
        } catch (Exception exception) {
            Finish(new List<string> { "could not start: " + exception.Message });
        }
    }

    private static string One(string nonce) => "reload-seed one " + nonce;
    private static string Two(string nonce) => "reload-seed two " + nonce;
    private static string Multi(string nonce) => "reload-seed three " + nonce + "\nsecond line";
    private static string Foreign(string nonce) => "reload-seed foreign " + nonce;

    [InitializeOnLoadMethod]
    private static void Resume() {
        if (SessionState.GetBool(RunningKey, false)) {
            // Read on the next editor update, once the editor sink has finished seeding the
            // window: the order of two InitializeOnLoadMethods is nothing to rest a check on.
            EditorApplication.update -= Report;
            EditorApplication.update += Report;
        } else {
            // A run that was killed before it could put the flag back.
            RestoreConsoleFlag();
        }
    }

    private static void Watchdog() {
        if (SessionState.GetBool(RunningKey, false)
            && EditorApplication.timeSinceStartup - SessionState.GetFloat(StartedAtKey, 0f) > TimeoutSeconds) {
            Finish(new List<string> { "the domain did not reload within " + TimeoutSeconds + " s" });
        }
    }

    private static void Report() {
        EditorApplication.update -= Report;
        List<string> failures = new List<string>();
        try {
            string nonce = SessionState.GetString(NonceKey, "?");

            // Without the mirrored lines still in the console there is nothing for the seed to
            // get wrong, and every count below would pass for the wrong reason.
            string mirroredOne = Tag + ": " + One(nonce);
            bool consoleKeptThem = false;
            if (ConsoleEntryBridge.Available) {
                List<ConsoleEntryBridge.ConsoleEntry> entries = new List<ConsoleEntryBridge.ConsoleEntry>();
                ConsoleEntryBridge.ReadAll(entries);
                for (int i = 0; i < entries.Count; i++) {
                    if (entries[i].Message != null && entries[i].Message.StartsWith(mirroredOne, StringComparison.Ordinal)) {
                        consoleKeptThem = true;
                        break;
                    }
                }
            }
            if (!consoleKeptThem) {
                failures.Add("the console does not hold the mirrored lines after the reload, so nothing was checked");
                Finish(failures);
                return;
            }

            LogRecord[] held = new LogRecord[EditorSink.Instance.Buffer.Count + 16];
            int count = EditorSink.Instance.Buffer.CopyNewerThan(-1, held);

            int one = 0, two = 0, multi = 0, foreign = 0, doubled = 0;
            for (int i = 0; i < count; i++) {
                LogRecord record = held[i];
                if (record.Message == null || record.Message.IndexOf(nonce, StringComparison.Ordinal) < 0) {
                    continue;
                }
                if (record.Tag == Tag) {
                    if (record.Message == One(nonce)) one++;
                    else if (record.Message == Two(nonce)) two++;
                    else if (record.Message == Multi(nonce)) multi++;
                } else if (record.Tag == LogCore.ForeignTag) {
                    if (record.Message.StartsWith(Tag + ": ", StringComparison.Ordinal)) doubled++;
                    else if (record.Message == Foreign(nonce)) foreign++;
                }
            }

            if (one != 1 || two != 1 || multi != 1) {
                failures.Add("the logged records are in the window " + one + ", " + two + " and " + multi + " times, not once each");
            }
            if (doubled != 0) {
                failures.Add(doubled + " mirrored lines came back a second time under the " + LogCore.ForeignTag + " tag");
            }
            if (foreign != 1) {
                failures.Add("the foreign line is in the window " + foreign + " times, not once");
            }
        } catch (Exception exception) {
            failures.Add("threw " + exception.GetType().Name + ": " + exception.Message);
        }
        Finish(failures);
    }

    private static void Finish(List<string> failures) {
        EditorApplication.update -= Watchdog;
        SessionState.EraseBool(RunningKey);
        try {
            RestoreConsoleFlag();
        } finally {
            System.Console.WriteLine(failures.Count == 0
                ? "RELOADSEED RESULT: PASS"
                : "RELOADSEED RESULT: FAIL: " + string.Join("; ", failures));
            EditorApplication.Exit(failures.Count == 0 ? 0 : 1);
        }
    }

    private static void RestoreConsoleFlag() {
        if (!EditorPrefs.HasKey(SavedFlagKey)) {
            return;
        }
        SetConsoleFlag(ClearOnRecompile, EditorPrefs.GetBool(SavedFlagKey, true));
        EditorPrefs.DeleteKey(SavedFlagKey);
    }

    // UnityEditor.LogEntries is internal; the console's own toggles go through it.
    private static Type LogEntries {
        get {
            Type type = typeof(EditorWindow).Assembly.GetType("UnityEditor.LogEntries");
            if (type != null) {
                return type;
            }
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()) {
                type = assembly.GetType("UnityEditor.LogEntries");
                if (type != null) {
                    return type;
                }
            }
            throw new InvalidOperationException("UnityEditor.LogEntries not found");
        }
    }

    private static bool HasConsoleFlag(int bit) {
        PropertyInfo flags = LogEntries.GetProperty("consoleFlags", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        return ((int)flags.GetValue(null) & bit) != 0;
    }

    private static void SetConsoleFlag(int bit, bool value) {
        MethodInfo set = LogEntries.GetMethod("SetConsoleFlag", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        set.Invoke(null, new object[] { bit, value });
    }
}
