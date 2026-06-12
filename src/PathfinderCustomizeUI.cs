using BepInEx.Configuration;
using UnityEngine;

namespace REPOPathfinder
{
    /// <summary>
    /// IMGUI overlay for tweaking dot color and a handful of trail-shape values
    /// at runtime. Toggled with Plugin.CustomizeMenuKey (default F6). All edits
    /// write back to the bound BepInEx ConfigEntries, so changes persist across
    /// sessions and the world dots update live (PathfinderManager subscribes to
    /// DotColorHex.SettingChanged for color, and reads scale/flow/spacing every
    /// frame so they pick up automatically).
    /// </summary>
    public class PathfinderCustomizeUI : MonoBehaviour
    {
        private bool _visible;
        private Rect _windowRect = new Rect(20f, 60f, 360f, 0f);
        private int _windowId;

        // Working color state. Sliders edit this; we serialize back to the hex
        // ConfigEntry whenever it changes. Resyncs from config when the window
        // is opened so external edits (e.g., manually edited cfg) show up.
        private Color _editColor;
        private string _hexBuffer;
        private bool _hexBufferDirty;
        private bool _needsSyncFromConfig = true;

        private static readonly (string label, string hex)[] Presets =
        {
            ("White",   "#FFFFFF80"),
            ("Cyan",    "#00E5FF80"),
            ("Yellow",  "#FFEB3B80"),
            ("Red",     "#FF525280"),
            ("Green",   "#00E67680"),
            ("Blue",    "#448AFF80"),
            ("Magenta", "#E040FB80"),
            ("Orange",  "#FF9100AA"),
        };

        private Texture2D _whiteTex;
        private GUIStyle _boldStyle;

        private CursorLockMode _savedLockState;
        private bool _savedCursorVisible;
        private bool _cursorOverridden;

        private void Awake()
        {
            _windowId = GetInstanceID();
        }

        private void Update()
        {
            if (Input.GetKeyDown(Plugin.Instance.CustomizeMenuKey.Value))
            {
                _visible = !_visible;
                if (_visible)
                {
                    _needsSyncFromConfig = true;
                    AcquireCursor();
                }
                else
                {
                    ReleaseCursor();
                }
                // Block the player controller's mouse-look / movement / fire while
                // the menu is open. IMGUI clicks come through Event.current and
                // hotkeys (GetKey*) are deliberately not patched, so closing the
                // menu still works.
                InputBlocker.BlockInput = _visible;
            }
        }

        private void OnDisable()
        {
            // If the component is disabled (scene unload, mod unload, etc) while
            // the menu is up, don't leave the player frozen.
            if (_visible)
            {
                _visible = false;
                ReleaseCursor();
                InputBlocker.BlockInput = false;
            }
        }

        private void OnGUI()
        {
            if (!_visible) return;

            if (_needsSyncFromConfig)
            {
                SyncFromConfig();
                _needsSyncFromConfig = false;
            }

            // REPO normally locks/hides the cursor during gameplay. We override
            // this every frame the menu is open so sliders are clickable; on
            // close we restore whatever state the game was using.
            if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
            if (!Cursor.visible) Cursor.visible = true;

            _windowRect = GUILayout.Window(
                _windowId,
                _windowRect,
                DrawWindow,
                "REPOPathfinder — Customize",
                GUILayout.Width(360f));
        }

        private void AcquireCursor()
        {
            if (_cursorOverridden) return;
            _savedLockState = Cursor.lockState;
            _savedCursorVisible = Cursor.visible;
            _cursorOverridden = true;
        }

        private void ReleaseCursor()
        {
            if (!_cursorOverridden) return;
            Cursor.lockState = _savedLockState;
            Cursor.visible = _savedCursorVisible;
            _cursorOverridden = false;
        }

        private void SyncFromConfig()
        {
            if (!ColorUtility.TryParseHtmlString(Plugin.Instance.DotColorHex.Value, out _editColor))
                _editColor = new Color(1f, 1f, 1f, 0.5f);
            _hexBuffer = ColorToHex(_editColor);
            _hexBufferDirty = false;
        }

        private void DrawWindow(int id)
        {
            GUILayout.Space(4f);

            GUILayout.Label("Dot Color", BoldLabel);
            DrawColorChannel("R", ref _editColor.r);
            DrawColorChannel("G", ref _editColor.g);
            DrawColorChannel("B", ref _editColor.b);
            DrawColorChannel("A", ref _editColor.a);

            GUILayout.Space(2f);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Hex:", GUILayout.Width(30f));
            string newHex = GUILayout.TextField(_hexBuffer ?? "", 9, GUILayout.Width(110f));
            if (newHex != _hexBuffer) { _hexBuffer = newHex; _hexBufferDirty = true; }
            GUI.enabled = _hexBufferDirty;
            if (GUILayout.Button("Apply", GUILayout.Width(58f)))
            {
                if (ColorUtility.TryParseHtmlString(_hexBuffer, out var parsed))
                {
                    _editColor = parsed;
                    _hexBufferDirty = false;
                    PushColorToConfig();
                }
            }
            GUI.enabled = true;
            GUILayout.Label("Preview:", GUILayout.Width(60f));
            DrawSwatch(_editColor, 60f, 18f);
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);
            GUILayout.Label("Presets", BoldLabel);
            const int perRow = 4;
            for (int i = 0; i < Presets.Length; i++)
            {
                if (i % perRow == 0) GUILayout.BeginHorizontal();
                if (GUILayout.Button(Presets[i].label, GUILayout.Width(78f)))
                {
                    if (ColorUtility.TryParseHtmlString(Presets[i].hex, out var parsed))
                    {
                        _editColor = parsed;
                        _hexBuffer = ColorToHex(_editColor);
                        _hexBufferDirty = false;
                        PushColorToConfig();
                    }
                }
                if (i % perRow == perRow - 1 || i == Presets.Length - 1) GUILayout.EndHorizontal();
            }

            GUILayout.Space(8f);
            GUILayout.Label("Trail Shape", BoldLabel);
            DrawFloatSlider("Dot size",   Plugin.Instance.DotScale,   0.02f, 0.5f, "F2");
            DrawFloatSlider("Flow speed", Plugin.Instance.FlowSpeed,  0f,    6f,   "F1");
            DrawFloatSlider("Spacing",    Plugin.Instance.Spacing,    0.5f,  4f,   "F1");

            GUILayout.Space(8f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Reset Visuals", GUILayout.Width(110f)))
                ResetVisualDefaults();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close", GUILayout.Width(60f)))
            {
                _visible = false;
                ReleaseCursor();
                InputBlocker.BlockInput = false;
            }
            GUILayout.EndHorizontal();

            // Drag by the title bar.
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
        }

        private GUIStyle BoldLabel
        {
            get
            {
                if (_boldStyle == null)
                    _boldStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
                return _boldStyle;
            }
        }

        private void DrawColorChannel(string label, ref float value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(20f));
            float prev = value;
            value = GUILayout.HorizontalSlider(value, 0f, 1f, GUILayout.Width(220f));
            int byteVal = Mathf.RoundToInt(Mathf.Clamp01(value) * 255f);
            GUILayout.Label(byteVal.ToString("D3"), GUILayout.Width(36f));
            GUILayout.EndHorizontal();
            if (Mathf.Abs(value - prev) > 1e-4f)
            {
                _hexBuffer = ColorToHex(_editColor);
                _hexBufferDirty = false;
                PushColorToConfig();
            }
        }

        private static void DrawFloatSlider(string label, ConfigEntry<float> entry, float min, float max, string format)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(80f));
            float prev = entry.Value;
            float v = GUILayout.HorizontalSlider(entry.Value, min, max, GUILayout.Width(180f));
            GUILayout.Label(v.ToString(format), GUILayout.Width(50f));
            GUILayout.EndHorizontal();
            if (Mathf.Abs(v - prev) > 1e-4f)
                entry.Value = v;
        }

        private void DrawSwatch(Color c, float width, float height)
        {
            EnsureWhiteTex();
            var rect = GUILayoutUtility.GetRect(width, height, GUILayout.Width(width), GUILayout.Height(height));
            var prev = GUI.color;
            // White underlay so transparency is visually obvious.
            GUI.color = Color.white;
            GUI.DrawTexture(rect, _whiteTex);
            GUI.color = c;
            GUI.DrawTexture(rect, _whiteTex);
            GUI.color = prev;
        }

        private void EnsureWhiteTex()
        {
            if (_whiteTex != null) return;
            _whiteTex = new Texture2D(1, 1);
            _whiteTex.SetPixel(0, 0, Color.white);
            _whiteTex.Apply();
            _whiteTex.hideFlags = HideFlags.HideAndDontSave;
        }

        private void ResetVisualDefaults()
        {
            Plugin.Instance.DotColorHex.Value = (string)Plugin.Instance.DotColorHex.DefaultValue;
            Plugin.Instance.DotScale.Value    = (float)Plugin.Instance.DotScale.DefaultValue;
            Plugin.Instance.FlowSpeed.Value   = (float)Plugin.Instance.FlowSpeed.DefaultValue;
            Plugin.Instance.Spacing.Value     = (float)Plugin.Instance.Spacing.DefaultValue;
            _needsSyncFromConfig = true;
        }

        private void PushColorToConfig()
        {
            string newHex = ColorToHex(_editColor);
            if (Plugin.Instance.DotColorHex.Value != newHex)
                Plugin.Instance.DotColorHex.Value = newHex;
        }

        private static string ColorToHex(Color c)
        {
            int r = Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255);
            int g = Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255);
            int b = Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255);
            int a = Mathf.Clamp(Mathf.RoundToInt(c.a * 255f), 0, 255);
            return $"#{r:X2}{g:X2}{b:X2}{a:X2}";
        }
    }
}
