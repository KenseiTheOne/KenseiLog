using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Builds a windowed development player of the demo scene.
/// <code>
/// Unity.exe -projectPath . -batchmode -quit -executeMethod DemoBuilder.BuildWindows
///           [-buildout &lt;path to exe&gt;]
/// </code>
/// </summary>
public static class DemoBuilder {
    private const string OutputArg = "-buildout";
    private const string Scene = "Assets/Demo/LogDemo.unity";

    public static void BuildWindows() {
        string output = ResolveOutput();

        PlayerSettings.companyName = "Kensei";
        PlayerSettings.productName = "KenseiLog Demo";
        PlayerSettings.defaultScreenWidth = 1280;
        PlayerSettings.defaultScreenHeight = 720;
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.resizableWindow = true;
        // Without this the player stops rendering the moment it loses focus, which is exactly
        // what happens while an automated capture run is going.
        PlayerSettings.runInBackground = true;

        BuildPlayerOptions options = new BuildPlayerOptions {
            scenes = new[] { Scene },
            locationPathName = output,
            target = BuildTarget.StandaloneWindows64,
            targetGroup = BuildTargetGroup.Standalone,
            options = BuildOptions.Development
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        System.Console.WriteLine("DEMOBUILD: " + report.summary.result +
                                 ", errors: " + report.summary.totalErrors +
                                 ", output: " + output);

        if (report.summary.result != BuildResult.Succeeded) {
            EditorApplication.Exit(1);
        }
    }

    private static string ResolveOutput() {
        string[] args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++) {
            if (args[i] == OutputArg) {
                return args[i + 1];
            }
        }

        // Beside the project, never inside it: a player written under Assets would be
        // imported on the next refresh.
        string projectRoot = Directory.GetParent(Application.dataPath).FullName;
        string beside = Directory.GetParent(projectRoot).FullName;
        return Path.Combine(beside, "KenseiLogBuild", "KenseiLogDemo.exe");
    }
}
