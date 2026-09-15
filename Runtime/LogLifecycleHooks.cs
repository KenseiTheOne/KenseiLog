using UnityEngine;

namespace KenseiLog {
    /// <summary>
    /// Carries the one lifecycle signal that has no static equivalent.
    /// <para>
    /// On mobile an app is usually paused and then killed without ever reaching
    /// OnApplicationQuit, so OnApplicationPause is the last moment a buffered sink gets to
    /// reach the disk. That callback only exists on a MonoBehaviour, hence this object.
    /// </para>
    /// </summary>
    [AddComponentMenu("")]
    internal sealed class LogLifecycleHooks : MonoBehaviour {
        private static LogLifecycleHooks _instance;

        public static void Ensure() {
            if (_instance != null || !Application.isPlaying) {
                return;
            }

            GameObject host = new GameObject("KenseiLog Hooks") {
                hideFlags = HideFlags.HideAndDontSave
            };
            DontDestroyOnLoad(host);
            _instance = host.AddComponent<LogLifecycleHooks>();
        }

        private void OnApplicationPause(bool paused) {
            if (paused) {
                LogCore.FlushSinks();
            }
        }

        private void OnApplicationQuit() {
            LogCore.FlushSinks();
        }
    }
}
