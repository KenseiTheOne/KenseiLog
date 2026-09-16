# Overlay Demo

Open `LogDemo.unity` and press Play.

A bubble appears in the corner. It reads the error count if there are errors, the warning
count if there are warnings but none, and the total otherwise. Drag it anywhere, tap it to
open the viewer. The buttons on the right produce records to look at:

- **Prod log / Warning / Error** — records written through this API.
- **Unity exception** — thrown through `Debug.LogException`, so it arrives under the `Unity`
  tag. This is the case a logger that only sees its own calls would miss entirely.
- **Spam** — a burst of identical messages. Turn on collapse to watch them fold into one row.

The scene also logs on a timer under `Combat.Damage` and `Combat.AI`, so the tag hierarchy has
something in it. Select `Combat` and both arrive; `Net` never does.

One message reads "combat handshake rejected by relay" and is tagged `Net`. Searching for
`combat` finds it, but selecting the `Combat` tag does not — the tag is a field, not a prefix
in the text.

File logging is on, so a file under `persistentDataPath/logs` fills up as you play — the
highest-numbered one is the running session. Open it
afterwards: open **Window → Kensei → Logs** and press **Open file** in the toolbar.
