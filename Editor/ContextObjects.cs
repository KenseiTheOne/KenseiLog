using System;
using UnityEditor;
#if UNITY_6000_3_OR_NEWER
using UnityEngine;
#endif

namespace KenseiLog.Editor {
    /// <summary>
    /// Turns a record's context id back into the object, on either side of the EntityId change.
    /// <para>
    /// Three shapes of the same call. Up to 6.2 there is only the int instance id. 6.3 brings
    /// EntityId and marks the int overloads obsolete, but its EntityId is still that same int and
    /// converts from one without complaint, so it goes the EntityId way with no warning to hide.
    /// From 6.4 EntityId goes through ulong, 64 bits wide by 6.5, and the int overloads are errors
    /// by 6.5.
    /// </para>
    /// </summary>
    public static class ContextObjects {
        public static UnityEngine.Object Resolve(long id) {
            if (id == 0) {
                return null;
            }
#if UNITY_6000_3_OR_NEWER
            return EditorUtility.EntityIdToObject(ToEntityId(id));
#else
            return EditorUtility.InstanceIDToObject((int)id);
#endif
        }

        public static bool Open(long id) {
            if (id == 0) {
                return false;
            }
#if UNITY_6000_3_OR_NEWER
            return AssetDatabase.OpenAsset(ToEntityId(id));
#else
            return AssetDatabase.OpenAsset((int)id);
#endif
        }

        /// <summary>
        /// Whether <see cref="TryRead"/> understands a field of this type - the id field of Unity's
        /// internal LogEntry, an int up to 6.3 and an EntityId on 6.5.
        /// </summary>
        internal static bool CanRead(Type type) {
            if (type == typeof(int)) {
                return true;
            }
#if UNITY_6000_3_OR_NEWER
            if (type == typeof(EntityId)) {
                return true;
            }
#endif
            return false;
        }

        /// <summary>The value of LogEntry's id field as a context id, whichever shape this editor gives it.</summary>
        internal static bool TryRead(object value, out long id) {
            switch (value) {
                case int instanceId:
                    id = instanceId;
                    return true;
#if UNITY_6000_3_OR_NEWER
                case EntityId entityId:
                    id = FromEntityId(entityId);
                    return true;
#endif
                default:
                    id = 0;
                    return false;
            }
        }

#if UNITY_6000_3_OR_NEWER
        private static EntityId ToEntityId(long id) {
#if UNITY_6000_4_OR_NEWER
            return EntityId.FromULong(unchecked((ulong)id));
#else
            return (int)id;
#endif
        }

        private static long FromEntityId(EntityId entityId) {
#if UNITY_6000_4_OR_NEWER
            return unchecked((long)EntityId.ToULong(entityId));
#else
            return (int)entityId;
#endif
        }
#endif
    }
}
