using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Builds the demo scene from script so it can be regenerated without opening the editor.
/// Run with: Unity.exe -projectPath . -batchmode -quit -executeMethod DemoSceneBuilder.Build
/// </summary>
public static class DemoSceneBuilder {
    private const string ScenePath = "Assets/Demo/LogDemo.unity";

    public static void Build() {
        Directory.CreateDirectory("Assets/Demo");

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

        GameObject host = new GameObject("Log Demo");
        host.AddComponent<LogDemo>();

        Camera camera = Object.FindObjectOfType<Camera>();
        if (camera != null) {
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.16f, 0.17f, 0.20f);
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);

        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        AssetDatabase.SaveAssets();

        System.Console.WriteLine("DEMOSCENE: saved " + ScenePath +
                                 " (exists: " + File.Exists(ScenePath) +
                                 ", build scenes: " + EditorBuildSettings.scenes.Length + ")");
    }
}
