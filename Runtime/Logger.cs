using System.Diagnostics;
using System.Runtime.CompilerServices;
using Object = UnityEngine.Object;

namespace KenseiLog {
    /// <summary>
    /// A logger bound to one tag, so a file states its tag once instead of at every call.
    /// <para>
    /// Usage:
    /// <code>
    /// private static readonly Logger Log = Logger.For(Tags.Combat);
    ///
    /// Log.Dev("hit " + target.name + " for " + damage);
    /// Log.ProdError("no hitbox on " + target.name);
    /// </code>
    /// </para>
    /// <para>
    /// A struct, so the field costs a string reference and nothing else. The Dev methods carry
    /// ConditionalAttribute exactly as the static ones do - it applies to instance methods too -
    /// so they are removed from release builds along with their arguments. The field
    /// initialiser is not: it survives as one assignment per type, which is the whole price.
    /// </para>
    /// <para>
    /// Naming the field <c>Log</c> shadows the static <see cref="Log"/> class inside that type,
    /// which is the point: every unqualified call in the file then carries the tag. Reach a
    /// different tag from the same file with the full <c>KenseiLog.Log.Dev(tag, message)</c>.
    /// </para>
    /// </summary>
    public readonly struct Logger {
        private readonly string _tag;

        private Logger(string tag) {
            _tag = tag;
        }

        /// <summary>The bound tag, or <see cref="LogCore.UntaggedTag"/> for a default instance.</summary>
        public string Tag => _tag ?? LogCore.UntaggedTag;

        public static Logger For(string tag) {
            return new Logger(tag);
        }

        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public void Dev(string message, Object context = null,
                        [CallerFilePath] string file = null, [CallerLineNumber] int line = 0) =>
            LogCore.Write(Tag, message, LogLevel.Log, LogChannel.Dev, context, file, line);

        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public void DevWarning(string message, Object context = null,
                               [CallerFilePath] string file = null, [CallerLineNumber] int line = 0) =>
            LogCore.Write(Tag, message, LogLevel.Warning, LogChannel.Dev, context, file, line);

        // NoInlining keeps the frame count to the StackTrace constructor fixed, so a captured
        // trace starts at the call site rather than inside here.
        [MethodImpl(MethodImplOptions.NoInlining)]
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public void DevError(string message, Object context = null,
                             [CallerFilePath] string file = null, [CallerLineNumber] int line = 0) =>
            LogCore.Write(Tag, message, LogLevel.Error, LogChannel.Dev, context, file, line);

        public void Prod(string message, Object context = null,
                         [CallerFilePath] string file = null, [CallerLineNumber] int line = 0) =>
            LogCore.Write(Tag, message, LogLevel.Log, LogChannel.Prod, context, file, line);

        public void ProdWarning(string message, Object context = null,
                                [CallerFilePath] string file = null, [CallerLineNumber] int line = 0) =>
            LogCore.Write(Tag, message, LogLevel.Warning, LogChannel.Prod, context, file, line);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ProdError(string message, Object context = null,
                              [CallerFilePath] string file = null, [CallerLineNumber] int line = 0) =>
            LogCore.Write(Tag, message, LogLevel.Error, LogChannel.Prod, context, file, line);

        /// <summary>A logger for a tag nested under this one: <c>Combat</c> gives <c>Combat.AI</c>.</summary>
        public Logger Child(string segment) {
            return new Logger(Tag + "." + segment);
        }
    }
}
