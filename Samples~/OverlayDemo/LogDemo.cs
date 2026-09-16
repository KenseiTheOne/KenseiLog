using KenseiLog;
using UnityEngine;

/// <summary>
/// Drives the logger so the in-game overlay has something to show.
/// Press Play; the overlay bubble appears in the corner, and the buttons on the right
/// produce records to look at.
/// </summary>
public sealed class LogDemo : MonoBehaviour {
    private static class Tags {
        public const string Boot = "Boot";
        public const string Combat = "Combat";
        public const string Damage = "Combat.Damage";
        public const string Ai = "Combat.AI";
        public const string Net = "Net";
        public const string Economy = "Economy";
    }

    private static readonly string[] _enemies = { "Orc", "Wolf", "Bandit", "Golem" };

    [SerializeField] private bool _enableOverlay = true;
    [SerializeField] private bool _writeFile = true;
    [SerializeField] private int _spamCount = 2000;
    [SerializeField] private float _chatterSeconds = 0.75f;

    private float _nextChatter;
    private int _tick;
    private GUIStyle _button;
    private GUIStyle _wrapped;

    private void Awake() {
        LogConfig config = LogConfig.Default();
        config.ShowOverlay = _enableOverlay;
        config.WriteToFile = _writeFile;
        LogCore.Configure(config);
    }

    private void Start() {
        Log.Info(Tags.Boot, "demo started on " + Application.platform, this);
        Log.DevInfo(Tags.Boot, "dev channel is compiled in this build", this);
        Log.Info(Tags.Economy, "wallet loaded: 1240 coins");
        Log.Warning(Tags.Net, "relay latency 180ms, above budget");

        // The word "combat" sits in this message on purpose: the Combat tab must not pick it
        // up, because the tag is a field and the search only looks at the text.
        Log.Info(Tags.Net, "combat handshake rejected by relay");
    }

    private void Update() {
        if (Time.unscaledTime < _nextChatter) {
            return;
        }
        _nextChatter = Time.unscaledTime + _chatterSeconds;
        _tick++;

        string enemy = _enemies[_tick % _enemies.Length];
        Log.DevInfo(Tags.Damage, "hit " + enemy + " for " + Random.Range(4, 40));
        if (_tick % 3 == 0) {
            Log.DevInfo(Tags.Ai, enemy + " lost its target");
        }
        if (_tick % 7 == 0) {
            Log.DevWarning(Tags.Combat, "no hitbox on " + enemy);
        }
    }

    private void OnGUI() {
        float scale = Mathf.Clamp(Screen.dpi > 1f ? Screen.dpi / 160f : Screen.height / 720f, 1f, 4f);
        Matrix4x4 previous = GUI.matrix;
        GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));

        if (_button == null) {
            _button = new GUIStyle(GUI.skin.button) { fontSize = 12 };
        }

        float width = Screen.width / scale;
        float x = width - 136f;
        // Starts below the overlay's own toolbar so the two never sit on top of each other.
        float y = 36f;

        GUI.Label(new Rect(x, y, 128f, 20f), "Kensei Log demo");
        y += 22f;

        if (GUI.Button(new Rect(x, y, 128f, 26f), "Prod log", _button)) {
            Log.Info(Tags.Economy, "purchase completed: starter pack");
        }
        y += 28f;

        if (GUI.Button(new Rect(x, y, 128f, 26f), "Warning", _button)) {
            Log.Warning(Tags.Net, "packet dropped at tick " + Time.frameCount);
        }
        y += 28f;

        if (GUI.Button(new Rect(x, y, 128f, 26f), "Error", _button)) {
            Log.Error(Tags.Net, "desync at tick " + Time.frameCount, this);
        }
        y += 28f;

        if (GUI.Button(new Rect(x, y, 128f, 26f), "Unity exception", _button)) {
            // Goes through Debug, not this API - it should still turn up under the Unity tag.
            Debug.LogException(new System.InvalidOperationException("deliberate demo exception"), this);
        }
        y += 28f;

        if (GUI.Button(new Rect(x, y, 128f, 26f), "Spam " + _spamCount, _button)) {
            for (int i = 0; i < _spamCount; i++) {
                Log.DevInfo(Tags.Damage, "repeated damage tick");
            }
        }
        y += 34f;

        if (LogCore.File != null) {
            if (_wrapped == null) {
                _wrapped = new GUIStyle(GUI.skin.label) { fontSize = 10, wordWrap = true };
            }
            GUI.Label(new Rect(x - 130f, y, 258f, 60f), "writing to\n" + LogCore.File.CurrentFilePath, _wrapped);
        }

        GUI.matrix = previous;
    }
}
