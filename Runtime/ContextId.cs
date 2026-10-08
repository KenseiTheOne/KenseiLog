using UnityEngine;

namespace KenseiLog {
    /// <summary>
    /// The id a record keeps for its related object - what <see cref="LogRecord.ContextInstanceId"/>
    /// holds. Up to Unity 6.3 that is the 32-bit instance id. From 6.4 it is the EntityId, which
    /// is 64 bits by 6.5 and no longer fits an int, kept bit for bit in a long: cut down to 32 bits it
    /// resolves to nothing.
    /// </summary>
    public static class ContextId {
        /// <summary>The id of a live object. Main thread only, like everything that asks native code.</summary>
        public static long Of(Object context) {
#if UNITY_6000_4_OR_NEWER
            return unchecked((long)EntityId.ToULong(context.GetEntityId()));
#else
            return context.GetInstanceID();
#endif
        }
    }
}
