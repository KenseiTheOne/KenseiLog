using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Player;

/// <summary>
/// Compiles the package's scripts for the player targets.
/// <code>
/// Unity.exe -projectPath . -batchmode -quit -nographics -executeMethod CompileCheck.Run
/// </code>
/// <para>
/// The editor compiles with UNITY_EDITOR defined and for no target in particular, so the
/// branches that only exist in a build are never seen: the call site trimmed outside the
/// editor, the file sink turned off on WebGL. A typo in one of those is a broken build for
/// whoever builds first, found long after it was written.
/// </para>
/// <para>
/// This compiles rather than builds - seconds against minutes, and a build would need a scene,
/// an icon and a signing setup to say the same thing about a compiler error. Targets whose
/// module is not installed are reported and skipped, since that is a missing download rather
/// than a broken package.
/// </para>
/// </summary>
public static class CompileCheck {
    private static int _failures;

    public static void Run() {
        Compile(BuildTarget.StandaloneWindows64);
        Compile(BuildTarget.WebGL);
        Compile(BuildTarget.Android);

        Console.WriteLine(_failures == 0
            ? "COMPILE RESULT: PASS"
            : "COMPILE RESULT: FAIL (" + _failures + ")");

        if (_failures > 0) {
            EditorApplication.Exit(1);
        }
    }

    private static void Compile(BuildTarget target) {
        if (!BuildPipeline.IsBuildTargetSupported(BuildPipeline.GetBuildTargetGroup(target), target)) {
            Console.WriteLine("  skip " + target + ": build support is not installed");
            return;
        }

        string output = Path.Combine(Path.GetTempPath(), "kenseilog-compile", target.ToString());
        Directory.CreateDirectory(output);

        try {
            ScriptCompilationSettings settings = new ScriptCompilationSettings {
                group = BuildPipeline.GetBuildTargetGroup(target),
                target = target,
                options = ScriptCompilationOptions.DevelopmentBuild
            };

            ScriptCompilationResult result = PlayerBuildInterface.CompilePlayerScripts(settings, output);
            bool built = result.assemblies != null && result.assemblies.Count > 0;
            Console.WriteLine((built ? "  ok   " : "  FAIL ") + target + ": " +
                              (built ? result.assemblies.Count + " assemblies" : "nothing was produced"));
            if (!built) {
                _failures++;
            }
        } catch (Exception exception) {
            Console.WriteLine("  FAIL " + target + ": " + exception.GetType().Name + ": " + exception.Message);
            _failures++;
        } finally {
            try {
                Directory.Delete(output, true);
            } catch (Exception) {
                // Leftovers in the temp directory are not worth failing a run over.
            }
        }
    }
}
