using UnityEngine;

namespace KenseiLog {
    /// <summary>
    /// What a run of IMGUI events means for the bubble: a drag, a tap, or neither.
    /// <para>
    /// Apart from the drawing because both faults it guards were invisible from the editor and
    /// only a finger on a device could find them. A tap that did not open the viewer looks
    /// exactly like a tap that did nothing, and the button still lit up under it.
    /// </para>
    /// <para>
    /// The first fault was a flag that never cleared. The reset was keyed on the event type read
    /// after <c>GUI.Button</c>, and the button consumes the MouseUp it answers - by then the type
    /// is Used, the reset never ran, and one drag left the bubble unopenable until the app was
    /// restarted. The type is read before the button now, which is why this takes it as an
    /// argument rather than reading <c>Event.current</c> itself.
    /// </para>
    /// <para>
    /// The second was a missing threshold. Any movement at all started a drag, and a finger never
    /// lands without a pixel or two of travel, so an ordinary tap became a drag and was swallowed.
    /// The rows had a threshold already; the bubble did not.
    /// </para>
    /// </summary>
    public sealed class BubbleGesture {
        private Vector2 _bubbleAtPress;
        private bool _pressedOnBubble;

        /// <summary>Whether this press has turned into a drag.</summary>
        public bool Dragging { get; private set; }

        /// <summary>Records where the bubble was when the finger went down.</summary>
        public void Press(bool onBubble, Vector2 bubble) {
            _pressedOnBubble = onBubble;
            _bubbleAtPress = bubble;
            Dragging = false;
        }

        /// <summary>
        /// Where the bubble goes while the finger moves, or false when this movement is not a
        /// drag at all.
        /// <para>
        /// Measured from where the press began rather than accumulated from each event, so that
        /// crossing the threshold does not leave the bubble behind by however far the finger
        /// travelled to get there.
        /// </para>
        /// </summary>
        public bool TryDrag(bool pastThreshold, Vector2 travelSincePress, out Vector2 bubble) {
            if (!_pressedOnBubble || !pastThreshold) {
                bubble = _bubbleAtPress;
                return false;
            }

            Dragging = true;
            bubble = _bubbleAtPress + travelSincePress;
            return true;
        }

        public void Release() {
            Dragging = false;
            _pressedOnBubble = false;
        }

        /// <summary>Whether a press the button reported should open the viewer.</summary>
        public bool Opens(bool buttonPressed) =>
            buttonPressed && !Dragging;
    }
}
