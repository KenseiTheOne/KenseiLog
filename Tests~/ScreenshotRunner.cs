using System.Collections;
using System.IO;
using KenseiLog;
using UnityEngine;

/// <summary>
/// Drives the overlay through a few states and captures the screen at each one.
/// Only wakes up when the player is launched with -screenshots, so it never interferes with
/// normal play. Sandbox-only; not part of the package sample.
/// </summary>
public sealed class ScreenshotRunner : MonoBehaviour {
    private const string Flag = "-screenshots";
    private const string OutputArg = "-shotdir";

    private string _directory;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap() {
        string[] args = System.Environment.GetCommandLineArgs();
        string directory = null;
        bool wanted = false;

        for (int i = 0; i < args.Length; i++) {
            if (args[i] == Flag) {
                wanted = true;
            } else if (args[i] == OutputArg && i + 1 < args.Length) {
                directory = args[i + 1];
            }
        }
        if (!wanted) {
            return;
        }

        GameObject host = new GameObject("Screenshot Runner");
        DontDestroyOnLoad(host);
        host.AddComponent<ScreenshotRunner>()._directory = directory ?? Application.persistentDataPath;
    }

    private IEnumerator Start() {
        Directory.CreateDirectory(_directory);

        // Let the demo's timed logs build up a few tags first, then put one of each level in:
        // the bubble's whole job is showing all three at once, and a shot taken before anything
        // has gone wrong is the one state that demonstrates none of it.
        yield return new WaitForSecondsRealtime(3f);
        Log.Error("Net", "desync at tick 4417, client ahead by 3 frames");
        yield return new WaitForSecondsRealtime(0.4f);
        yield return Capture("01-bubble");

        LogOverlay.IsOpen = true;
        yield return Capture("02-open");

        LogOverlay.TagPaneVisible = true;
        yield return Capture("03-tags");

        LogOverlay.TagPaneVisible = false;
        Log.Error("Net", "desync at tick 4417, client ahead by 3 frames");
        yield return new WaitForSecondsRealtime(0.4f);
        LogOverlay.SelectNewest(LogLevel.Error);
        yield return Capture("04-detail");

        Debug.LogException(new System.InvalidOperationException("deliberate demo exception"));
        yield return new WaitForSecondsRealtime(0.4f);
        LogOverlay.SelectNewest(LogLevel.Error);
        yield return Capture("05-unity-exception");

        LogCore.FlushSinks();
        yield return new WaitForSecondsRealtime(0.3f);
        Application.Quit();
    }

    private IEnumerator Capture(string name) {
        string path = Path.Combine(_directory, name + ".png");
        yield return new WaitForEndOfFrame();
        ScreenCapture.CaptureScreenshot(path);

        // CaptureScreenshot finishes on a later frame; wait for the file to actually land.
        float deadline = Time.realtimeSinceStartup + 5f;
        while (!File.Exists(path) && Time.realtimeSinceStartup < deadline) {
            yield return null;
        }
        yield return new WaitForSecondsRealtime(0.3f);
    }
}
