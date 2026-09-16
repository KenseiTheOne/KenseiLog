using KenseiLog;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Reproduces what happens when MirrorToUnityConsole is turned on.
/// Run with: Unity.exe -projectPath . -batchmode -quit -executeMethod MirrorCheck.Run
/// </summary>
public static class MirrorCheck {
    public static void Run() {
        LogConfig config = LogConfig.Default();
        config.MirrorToUnityConsole = true;
        LogCore.Configure(config);

        EditorSinkClear();

        Debug.Log("MARKER-PLAIN-DEBUG-LOG");
        Log.Info("Smoke", "MARKER-FACADE-PROD");
        Debug.LogWarning("MARKER-PLAIN-WARNING");

        LogRingBuffer buffer = KenseiLog.Editor.EditorSink.Instance.Buffer;
        LogRecord[] scratch = new LogRecord[64];
        int copied = buffer.CopyNewerThan(0, scratch);

        System.Console.WriteLine("MIRRORCHECK: buffer holds " + copied + " records");
        for (int i = 0; i < copied; i++) {
            System.Console.WriteLine("MIRRORCHECK:   [" + scratch[i].Channel + "/" + scratch[i].Level + "] " +
                                     scratch[i].Tag + " | " + scratch[i].Message);
        }
    }

    private static void EditorSinkClear() {
        KenseiLog.Editor.EditorSink.Instance.Clear();
    }
}
