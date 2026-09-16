using System.Diagnostics;
using System.Runtime.CompilerServices;
using Object = UnityEngine.Object;

namespace KenseiLog {
    /// <summary>
    /// Entry point for logging. Six flat methods: three levels across two channels.
    /// <para>
    /// Usage:
    /// <code>
    /// Log.DevInfo(Tags.Combat, "hit " + target.name + " for " + damage);
    /// Log.DevWarning(Tags.Combat, "no hitbox on " + target.name, this);
    /// Log.Error(Tags.Net, "desync at tick " + tick);
    /// </code>
    /// </para>
    /// <para>
    /// The unprefixed names - <see cref="Info"/>, <see cref="Warning"/>, <see cref="Error"/> -
    /// are the prod channel, because a log that survives into a shipped build is the one you
    /// reach for without thinking. The dev channel spells itself out: DevInfo, DevWarning,
    /// DevError.
    /// </para>
    /// <para>
    /// The Dev methods carry ConditionalAttribute, so outside the editor and development
    /// builds the compiler removes the call along with every argument expression. Building
    /// the message costs nothing in release because it never runs.
    /// </para>
    /// <para>
    /// Prefer concatenation over interpolation on hot paths: Unity compiles as C# 9, where
    /// an interpolated hole holding a value type boxes it and allocates a params array,
    /// while "text " + value calls ToString directly.
    /// </para>
    /// </summary>
    public static class Log {
        private const string EditorSymbol = "UNITY_EDITOR";
        private const string DevelopmentBuildSymbol = "DEVELOPMENT_BUILD";

        [Conditional(EditorSymbol), Conditional(DevelopmentBuildSymbol)]
        public static void DevInfo(string tag, string message, Object context = null,
                                   [CallerFilePath] string file = null, [CallerLineNumber] int line = 0) =>
            LogCore.Write(tag, message, LogLevel.Log, LogChannel.Dev, context, file, line);

        [Conditional(EditorSymbol), Conditional(DevelopmentBuildSymbol)]
        public static void DevWarning(string tag, string message, Object context = null,
                                      [CallerFilePath] string file = null, [CallerLineNumber] int line = 0) =>
            LogCore.Write(tag, message, LogLevel.Warning, LogChannel.Dev, context, file, line);

        // NoInlining keeps the frame count between the caller and the StackTrace constructor
        // fixed, so the captured trace starts at the call site rather than inside the facade.
        [MethodImpl(MethodImplOptions.NoInlining)]
        [Conditional(EditorSymbol), Conditional(DevelopmentBuildSymbol)]
        public static void DevError(string tag, string message, Object context = null,
                                    [CallerFilePath] string file = null, [CallerLineNumber] int line = 0) =>
            LogCore.Write(tag, message, LogLevel.Error, LogChannel.Dev, context, file, line);

        public static void Info(string tag, string message, Object context = null,
                                [CallerFilePath] string file = null, [CallerLineNumber] int line = 0) =>
            LogCore.Write(tag, message, LogLevel.Log, LogChannel.Prod, context, file, line);

        public static void Warning(string tag, string message, Object context = null,
                                   [CallerFilePath] string file = null, [CallerLineNumber] int line = 0) =>
            LogCore.Write(tag, message, LogLevel.Warning, LogChannel.Prod, context, file, line);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Error(string tag, string message, Object context = null,
                                 [CallerFilePath] string file = null, [CallerLineNumber] int line = 0) =>
            LogCore.Write(tag, message, LogLevel.Error, LogChannel.Prod, context, file, line);

        // =====================================================================
        // Without a tag
        //
        // For the log you are about to delete anyway. It lands under "Untagged" rather than
        // nowhere, which keeps it inside the window and apart from the engine's own chatter -
        // reaching for Debug.Log instead would bury it under the Unity tag. A branch of the
        // tag tree filling up with these is also a fair hint about where to put a real one.
        //
        // No ambiguity with the six above: their second parameter is a string, these take a
        // UnityEngine.Object, and there is no conversion between the two.
        // =====================================================================

        [Conditional(EditorSymbol), Conditional(DevelopmentBuildSymbol)]
        public static void DevInfo(string message, Object context = null,
                                   [CallerFilePath] string file = null, [CallerLineNumber] int line = 0) =>
            LogCore.Write(LogCore.UntaggedTag, message, LogLevel.Log, LogChannel.Dev, context, file, line);

        [Conditional(EditorSymbol), Conditional(DevelopmentBuildSymbol)]
        public static void DevWarning(string message, Object context = null,
                                      [CallerFilePath] string file = null, [CallerLineNumber] int line = 0) =>
            LogCore.Write(LogCore.UntaggedTag, message, LogLevel.Warning, LogChannel.Dev, context, file, line);

        [MethodImpl(MethodImplOptions.NoInlining)]
        [Conditional(EditorSymbol), Conditional(DevelopmentBuildSymbol)]
        public static void DevError(string message, Object context = null,
                                    [CallerFilePath] string file = null, [CallerLineNumber] int line = 0) =>
            LogCore.Write(LogCore.UntaggedTag, message, LogLevel.Error, LogChannel.Dev, context, file, line);

        public static void Info(string message, Object context = null,
                                [CallerFilePath] string file = null, [CallerLineNumber] int line = 0) =>
            LogCore.Write(LogCore.UntaggedTag, message, LogLevel.Log, LogChannel.Prod, context, file, line);

        public static void Warning(string message, Object context = null,
                                   [CallerFilePath] string file = null, [CallerLineNumber] int line = 0) =>
            LogCore.Write(LogCore.UntaggedTag, message, LogLevel.Warning, LogChannel.Prod, context, file, line);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Error(string message, Object context = null,
                                 [CallerFilePath] string file = null, [CallerLineNumber] int line = 0) =>
            LogCore.Write(LogCore.UntaggedTag, message, LogLevel.Error, LogChannel.Prod, context, file, line);
    }
}
