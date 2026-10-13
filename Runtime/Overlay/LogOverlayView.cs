using UnityEngine;

namespace KenseiLog {
    /// <summary>
    /// The part of the overlay that receives IMGUI events, and nothing else.
    /// <para>
    /// Kept apart from <see cref="LogOverlay"/> so that the drawing can be switched off without
    /// switching off the reading: a disabled behaviour gets no OnGUI at all, which is the only
    /// way an IMGUI overlay costs nothing on a frame, while the overlay's Update has to keep
    /// taking records in for the counts to be right when it comes back.
    /// </para>
    /// <para>
    /// No layout pass. Nothing the overlay draws uses GUILayout, and with useGUILayout on Unity
    /// runs OnGUI once more per frame for the Layout event, which is most of what the collapsed
    /// bubble used to cost.
    /// </para>
    /// </summary>
    [AddComponentMenu("")]
    internal sealed class LogOverlayView : MonoBehaviour {
        internal LogOverlay Owner;

        private void Awake() {
            useGUILayout = false;
        }

        private void OnGUI() {
            if (Owner != null) {
                Owner.DrawGUI();
            }
        }
    }
}
