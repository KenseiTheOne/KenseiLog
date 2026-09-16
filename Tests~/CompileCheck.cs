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
    /// <summary>
    /// The one assembly whose absence means the package did not compile. Anything else that
    /// came out says nothing about it.
    /// </summary>
    private const string PackageAssembly = "KenseiLog.Runtime.dll";

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

        // Both configurations, because they are not the same code. A development build keeps
        // the Dev methods and a release build has the compiler remove them along with every
        // argument expression - so the release configuration is the only one that ever
        // compiles what a shipped game compiles.
        CompileOne(target, ScriptCompilationOptions.None, "release");
        CompileOne(target, ScriptCompilationOptions.DevelopmentBuild, "development");
    }

    private static void CompileOne(BuildTarget target, ScriptCompilationOptions options, string configuration) {
        string output = Path.Combine(Path.GetTempPath(), "kenseilog-compile", target + "-" + configuration);
        Directory.CreateDirectory(output);

        try {
            ScriptCompilationSettings settings = new ScriptCompilationSettings {
                group = BuildPipeline.GetBuildTargetGroup(target),
                target = target,
                options = options
            };

            ScriptCompilationResult result = PlayerBuildInterface.CompilePlayerScripts(settings, output);
            bool built = Produced(result, PackageAssembly);
            Console.WriteLine((built ? "  ok   " : "  FAIL ") + target + " " + configuration + ": " +
                              (built ? PackageAssembly + " built" : PackageAssembly + " was not produced"));
            if (!built) {
                _failures++;
            }
        } catch (Exception exception) {
            Console.WriteLine("  FAIL " + target + " " + configuration + ": " +
                              exception.GetType().Name + ": " + exception.Message);
            _failures++;
        } finally {
            try {
                Directory.Delete(output, true);
            } catch (Exception) {
                // Leftovers in the temp directory are not worth failing a run over.
            }
        }
    }

    /// <summary>
    /// Whether that assembly is among what came out.
    /// <para>
    /// CompilePlayerScripts does not throw on a compiler error - it returns whichever
    /// assemblies were produced. So "something came out" is not the question and never was:
    /// any project holding a second assembly that does not reference this package would answer
    /// it yes with the package thoroughly broken.
    /// </para>
    /// </summary>
    private static bool Produced(ScriptCompilationResult result, string assembly) {
        if (result.assemblies == null) {
            return false;
        }
        for (int i = 0; i < result.assemblies.Count; i++) {
            if (string.Equals(Path.GetFileName(result.assemblies[i]), assembly, StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
        }
        return false;
    }
}
