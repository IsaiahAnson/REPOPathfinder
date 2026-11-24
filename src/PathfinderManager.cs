using System;
using System.Collections.Generic;
using BepInEx;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace REPOPathfinder
{
    /// <summary>
    /// Draws the game's "where to go next" guidance as dots on the floor, so the player can follow
    /// it without opening the map. The target is chosen the way the map's backtrack dots choose it:
    /// the active extraction point, or the truck once every extraction is done.
    ///
    /// Path model: the NavMesh path is computed once per route and cached as world-space corners
    /// with prefix arc-distances. Each frame the player's position is projected onto that polyline
    /// to get a `playerArc`; dots sit at (playerArc + slot*spacing + animOffset) along the cached
    /// corners. A new path is computed only when the target changes or the player drifts more than
    /// OffPathThreshold meters from the polyline, so walking along the path never moves the dots.
    ///
    /// Animation model: one shared `_animOffset` in [0, spacing) plus a circular pool pointer
    /// `_frontPool` keeps spacing even and hides the per-cycle wrap behind the alpha fade.
    /// </summary>
    public class PathfinderManager : MonoBehaviour
    {
        // A path query that failed is not repeated before this much time has passed.
        private const float RetryAfterFailedPathSeconds = 0.75f;
        private const float MinSecondsBetweenPaths = 0.2f;
        // How far the target and a spectated player may be from the NavMesh and still be used.
        private const float TargetSnapMeters = 4f;
        private const float SpectatedSnapMeters = 3f;
        // The player's last NavMesh position is ignored when it is further than this from the player.
        private const float StaleStartMeters = 10f;
        private const int FailuresBeforeTurningOff = 5;

        private static readonly string[] ShaderNames =
        {
            "Sprites/Default", "Unlit/Color", "Hidden/Internal-Colored", "Standard",
        };

        private bool _trailEnabled;
        private NavMeshPath _path;
        private Vector3[] _cornerBuffer = new Vector3[128];

        // Cached path: the first _cornerCount entries are corners and prefix arc distances.
        private Vector3[] _cachedCorners = new Vector3[128];
        private float[] _cachedArcs = new float[128];
        private int _cornerCount;
        private float _totalLength;
        private Transform _cachedTarget;
        private Transform _reportedTarget;
        private float _nextPathAttempt;

        private readonly List<GameObject> _dots = new List<GameObject>();
        private readonly List<MeshRenderer> _renderers = new List<MeshRenderer>();
        private Vector3[] _smoothedPositions = new Vector3[0];
        private bool[] _wasVisible = new bool[0];
        private bool _anyDotVisible;

        private float _animOffset;
        private int _frontPool;

        private Material _sharedMaterial;
        private bool _materialCreated;
        private MaterialPropertyBlock _mpb;
        private Color _baseColor;
        private static readonly int ColorPropId = Shader.PropertyToID("_Color");

        private int _failures;
        private bool _failureLogged;

        private float _toastUntil;
        private string _toastText;
        private GUIStyle _toastStyle;

        private void Awake()
        {
            _path = new NavMeshPath();
            _mpb = new MaterialPropertyBlock();
            _trailEnabled = Plugin.Instance.EnabledOnStart.Value && GameAccess.Ready;
            _baseColor = ParseColor(Plugin.Instance.DotColorHex.Value);

            // ApplyDotAlpha reads _baseColor every frame, so the dots follow the customize panel live.
            Plugin.Instance.DotColorHex.SettingChanged += OnDotColorChanged;
        }

        private void OnDestroy()
        {
            if (Plugin.Instance != null && Plugin.Instance.DotColorHex != null)
            {
                Plugin.Instance.DotColorHex.SettingChanged -= OnDotColorChanged;
            }
        }

        private void OnDotColorChanged(object sender, EventArgs e)
        {
            _baseColor = ParseColor(Plugin.Instance.DotColorHex.Value);
        }

        private void OnEnable()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            DropTrail();
        }

        private void Update()
        {
            HandleHotkeys();
            if (!_trailEnabled) return;

            try
            {
                UpdateTrail();
                _failures = 0;
            }
            catch (Exception e)
            {
                // One broken frame must not become an error every frame for the rest of the session.
                DropTrail();
                if (!_failureLogged)
                {
                    _failureLogged = true;
                    Plugin.Log.LogError("The trail could not be updated: " + e);
                }
                if (++_failures >= FailuresBeforeTurningOff)
                {
                    _failures = 0;
                    _trailEnabled = false;
                    Plugin.Log.LogError($"The trail was turned off after {FailuresBeforeTurningOff} failed updates in a row. " +
                                        $"Press {Plugin.Instance.ToggleKey.Value} to turn it on again.");
                    ShowToast("Pathfinder: error, turned off");
                }
            }
        }

        private void HandleHotkeys()
        {
            var input = UnityInput.Current;

            if (input.GetKeyDown(Plugin.Instance.ToggleKey.Value))
            {
                if (!GameAccess.Ready)
                {
                    ShowToast("Pathfinder: needs an update for this game version");
                }
                else
                {
                    _trailEnabled = !_trailEnabled;
                    _failures = 0;
                    _failureLogged = false;
                    ShowToast(_trailEnabled ? "Pathfinder: ON" : "Pathfinder: OFF");
                    if (!_trailEnabled) DropTrail();
                }
            }

            if (input.GetKeyDown(Plugin.Instance.TargetToggleKey.Value))
            {
                var next = Plugin.Instance.TargetMode.Value == PathfinderTargetMode.Auto
                    ? PathfinderTargetMode.Truck
                    : PathfinderTargetMode.Auto;
                Plugin.Instance.TargetMode.Value = next;
                ShowToast(next == PathfinderTargetMode.Truck ? "Target: Truck" : "Target: Auto");
                // The cached path leads to the previous target.
                InvalidateCache();
            }
        }

        private void UpdateTrail()
        {
            if (Map.Instance == null || PlayerController.instance == null ||
                LevelGenerator.Instance == null || !LevelGenerator.Instance.Generated ||
                RoundDirector.instance == null)
            {
                // Between levels: the cached corners belong to a level that is gone.
                DropTrail();
                return;
            }

            Transform target = ResolveTarget();
            if (target == null)
            {
                DropTrail();
                return;
            }

            if (!TryGetStart(out Vector3 start))
            {
                HideAllDots();
                return;
            }

            float playerArc = 0f;
            bool onPath = _cornerCount >= 2 && target == _cachedTarget &&
                          ProjectOnCachedPath(start, out playerArc, out float offPath) &&
                          offPath <= Plugin.Instance.OffPathThreshold.Value;

            if (!onPath && Time.unscaledTime >= _nextPathAttempt)
            {
                bool computed = RecomputePath(start, target);
                _nextPathAttempt = Time.unscaledTime + (computed ? MinSecondsBetweenPaths : RetryAfterFailedPathSeconds);
                if (computed) ProjectOnCachedPath(start, out playerArc, out _);
            }

            // No usable path to this target (yet). An older path to the same target stays on screen
            // while a new one is waited for.
            if (_cornerCount < 2 || target != _cachedTarget || _totalLength <= 0.5f)
            {
                HideAllDots();
                return;
            }

            float availableLength = _totalLength - playerArc;
            if (availableLength < Plugin.Instance.MinDistanceToTarget.Value)
            {
                HideAllDots();
                return;
            }

            AdvanceAndPlace(Time.deltaTime, playerArc, availableLength);
        }

        // Truck mode: always the truck. Auto mode: what the map's backtrack dots point at.
        private static Transform ResolveTarget()
        {
            LevelPoint truck = LevelGenerator.Instance.LevelPathTruck;
            Transform truckTransform = truck != null ? truck.transform : null;

            if (Plugin.Instance.TargetMode.Value == PathfinderTargetMode.Truck) return truckTransform;

            RoundDirector director = RoundDirector.instance;
            if (GameAccess.AllExtractionsDone(director)) return truckTransform;

            ExtractionPoint point = GameAccess.CurrentExtraction(director);
            return point != null ? point.transform : null;
        }

        // Where the trail starts: the local player, or the teammate being spectated while dead.
        private static bool TryGetStart(out Vector3 start)
        {
            start = default;
            PlayerAvatar avatar = PlayerController.instance.playerAvatarScript;
            if (avatar == null) return false;

            if (!GameAccess.IsDead(avatar))
            {
                start = GameAccess.LastNavmeshPosition(avatar);
                Vector3 actual = avatar.transform.position;
                if ((start - actual).sqrMagnitude <= StaleStartMeters * StaleStartMeters) return true;
                // Not set yet on this level, or left behind by a teleport.
                return SnapToNavMesh(actual, SpectatedSnapMeters, out start);
            }

            PlayerAvatar spectated = GameAccess.SpectatedPlayer();
            if (spectated == null || spectated == avatar) return false;
            return SnapToNavMesh(spectated.transform.position, SpectatedSnapMeters, out start);
        }

        private static bool SnapToNavMesh(Vector3 position, float maxDistance, out Vector3 snapped)
        {
            if (NavMesh.SamplePosition(position, out NavMeshHit hit, maxDistance, NavMesh.AllAreas))
            {
                snapped = hit.position;
                return true;
            }
            snapped = position;
            return false;
        }

        private bool RecomputePath(Vector3 start, Transform target)
        {
            SnapToNavMesh(target.position, TargetSnapMeters, out Vector3 goal);

            if (!NavMesh.CalculatePath(start, goal, NavMesh.AllAreas, _path) ||
                _path.status == NavMeshPathStatus.PathInvalid)
            {
                return false;
            }

            int count = _path.GetCornersNonAlloc(_cornerBuffer);
            while (count == _cornerBuffer.Length && _cornerBuffer.Length < 4096)
            {
                _cornerBuffer = new Vector3[_cornerBuffer.Length * 2];
                count = _path.GetCornersNonAlloc(_cornerBuffer);
            }
            if (count < 2) return false;

            CachePath(count);
            _cachedTarget = target;

            if (target != _reportedTarget)
            {
                _reportedTarget = target;
                Plugin.Log.LogInfo($"Path to {target.name}: {_path.status}, {count} corners, {_totalLength:F0} m.");
                if (_path.status == NavMeshPathStatus.PathPartial)
                {
                    ShowToast("Pathfinder: no full route, showing the closest one");
                }
            }
            return true;
        }

        private void CachePath(int count)
        {
            if (_cachedCorners.Length < count)
            {
                _cachedCorners = new Vector3[_cornerBuffer.Length];
                _cachedArcs = new float[_cornerBuffer.Length];
            }

            _cachedCorners[0] = _cornerBuffer[0];
            _cachedArcs[0] = 0f;
            for (int i = 1; i < count; i++)
            {
                _cachedCorners[i] = _cornerBuffer[i];
                _cachedArcs[i] = _cachedArcs[i - 1] + Vector3.Distance(_cornerBuffer[i - 1], _cornerBuffer[i]);
            }
            _cornerCount = count;
            _totalLength = _cachedArcs[count - 1];

            // Dots snap to the new path instead of sliding across the room to it.
            Array.Clear(_wasVisible, 0, _wasVisible.Length);
        }

        private void InvalidateCache()
        {
            _cornerCount = 0;
            _totalLength = 0f;
            _cachedTarget = null;
            _nextPathAttempt = 0f;
        }

        private void DropTrail()
        {
            InvalidateCache();
            HideAllDots();
        }

        // Projects p onto the cached polyline. Returns the arc-distance of the closest point and
        // that point's distance from p.
        private bool ProjectOnCachedPath(Vector3 p, out float arc, out float dist)
        {
            arc = 0f;
            dist = float.MaxValue;
            if (_cornerCount < 2) return false;

            float bestSqr = float.MaxValue;
            for (int i = 1; i < _cornerCount; i++)
            {
                Vector3 a = _cachedCorners[i - 1];
                Vector3 ab = _cachedCorners[i] - a;
                float sqrLen = ab.sqrMagnitude;
                if (sqrLen < 1e-6f) continue;

                float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / sqrLen);
                float sqr = (a + ab * t - p).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    arc = _cachedArcs[i - 1] + Mathf.Sqrt(sqrLen) * t;
                }
            }
            if (bestSqr == float.MaxValue) return false;
            dist = Mathf.Sqrt(bestSqr);
            return true;
        }

        private Vector3 CachedPointAtDistance(float distance)
        {
            if (distance <= 0f) return _cachedCorners[0];

            for (int i = 1; i < _cornerCount; i++)
            {
                if (distance <= _cachedArcs[i])
                {
                    float seg = _cachedArcs[i] - _cachedArcs[i - 1];
                    if (seg < 1e-6f) return _cachedCorners[i];
                    float t = (distance - _cachedArcs[i - 1]) / seg;
                    return Vector3.Lerp(_cachedCorners[i - 1], _cachedCorners[i], t);
                }
            }
            return _cachedCorners[_cornerCount - 1];
        }

        private void AdvanceAndPlace(float dt, float playerArc, float availableLength)
        {
            int maxDots = Mathf.Max(1, Plugin.Instance.MaxDots.Value);
            float spacing = Mathf.Max(0.1f, Plugin.Instance.Spacing.Value);
            float yOffset = Plugin.Instance.GroundOffset.Value;
            float speed = Mathf.Max(0f, Plugin.Instance.FlowSpeed.Value);
            float fadeFrac = Mathf.Clamp(Plugin.Instance.FadeZoneFraction.Value, 0.05f, 0.5f);
            float scale = Mathf.Max(0.01f, Plugin.Instance.DotScale.Value);

            EnsurePool(maxDots);

            if (_frontPool < 0 || _frontPool >= maxDots)
                _frontPool = ((_frontPool % maxDots) + maxDots) % maxDots;

            _animOffset += speed * dt;
            while (_animOffset >= spacing)
            {
                _animOffset -= spacing;
                _frontPool = (_frontPool - 1 + maxDots) % maxDots;
                // The dot now at _frontPool just wrapped from the front of the trail to the player
                // end. Clearing its flag makes it snap there instead of being smoothed across the
                // whole trail.
                _wasVisible[_frontPool] = false;
            }

            int activeCount = Mathf.Min(maxDots, Mathf.Max(1, Mathf.CeilToInt(availableLength / spacing)));
            float endpoint = Mathf.Min(availableLength, activeCount * spacing);
            float fadeZone = Mathf.Max(0.05f, Mathf.Min(spacing, endpoint * 0.5f) * fadeFrac);

            float smoothRate = Plugin.Instance.PositionSmoothRate.Value;
            float lerpT = (smoothRate > 0f) ? 1f - Mathf.Exp(-smoothRate * dt) : 1f;
            var scaleVector = new Vector3(scale, scale, scale);

            // The pool never shrinks, so after MaxDots is lowered it holds more dots than are in
            // use: everything from maxDots up is switched off here.
            for (int k = 0; k < _dots.Count; k++)
            {
                int slot = k < maxDots ? ((k - _frontPool) % maxDots + maxDots) % maxDots : int.MaxValue;
                if (slot >= activeCount)
                {
                    if (_dots[k].activeSelf) _dots[k].SetActive(false);
                    _wasVisible[k] = false;
                    continue;
                }

                float d = slot * spacing + _animOffset;
                Vector3 targetPos = CachedPointAtDistance(playerArc + Mathf.Min(d, availableLength));
                targetPos.y += yOffset;

                _smoothedPositions[k] = _wasVisible[k]
                    ? Vector3.Lerp(_smoothedPositions[k], targetPos, lerpT)
                    : targetPos;
                _wasVisible[k] = true;

                Transform dot = _dots[k].transform;
                dot.position = _smoothedPositions[k];
                if (dot.localScale.x != scale) dot.localScale = scaleVector;

                float fadeIn = Mathf.SmoothStep(0f, 1f, d / fadeZone);
                float fadeOut = Mathf.SmoothStep(0f, 1f, (endpoint - d) / fadeZone);
                ApplyDotAlpha(k, Mathf.Min(fadeIn, fadeOut));

                if (!_dots[k].activeSelf) _dots[k].SetActive(true);
            }
            _anyDotVisible = true;
        }

        private void EnsurePool(int count)
        {
            if (!_materialCreated)
            {
                _materialCreated = true;
                _sharedMaterial = CreateSharedMaterial();
            }

            while (_dots.Count < count)
            {
                var go = CreateDot(_dots.Count);
                _dots.Add(go);
                _renderers.Add(go.GetComponent<MeshRenderer>());
            }

            if (_smoothedPositions.Length < _dots.Count)
            {
                Array.Resize(ref _smoothedPositions, _dots.Count);
                Array.Resize(ref _wasVisible, _dots.Count);
            }
        }

        private GameObject CreateDot(int idx)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = $"PathfinderDot_{idx}";
            go.transform.SetParent(transform, worldPositionStays: false);

            var collider = go.GetComponent<Collider>();
            if (collider != null) Destroy(collider);

            var renderer = go.GetComponent<MeshRenderer>();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            if (_sharedMaterial != null) renderer.sharedMaterial = _sharedMaterial;

            float scale = Mathf.Max(0.01f, Plugin.Instance.DotScale.Value);
            go.transform.localScale = new Vector3(scale, scale, scale);
            go.SetActive(false);
            return go;
        }

        private Material CreateSharedMaterial()
        {
            Shader shader = null;
            foreach (string shaderName in ShaderNames)
            {
                shader = Shader.Find(shaderName);
                if (shader != null) break;
            }
            if (shader == null)
            {
                Plugin.Log.LogWarning("None of these shaders is in this game build: " + string.Join(", ", ShaderNames) +
                                      ". The dots use Unity's default material, so they are not see-through.");
                return null;
            }

            var mat = new Material(shader) { color = _baseColor, renderQueue = 3500 };
            if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 0f);
            if (mat.HasProperty("_SrcBlend")) mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            if (mat.HasProperty("_DstBlend")) mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            return mat;
        }

        private void ApplyDotAlpha(int i, float fade)
        {
            var r = _renderers[i];
            var c = _baseColor;
            c.a = _baseColor.a * Mathf.Clamp01(fade);
            r.GetPropertyBlock(_mpb);
            _mpb.SetColor(ColorPropId, c);
            r.SetPropertyBlock(_mpb);
        }

        private void HideAllDots()
        {
            if (!_anyDotVisible) return;
            _anyDotVisible = false;
            for (int i = 0; i < _dots.Count; i++)
                if (_dots[i] != null && _dots[i].activeSelf) _dots[i].SetActive(false);
            Array.Clear(_wasVisible, 0, _wasVisible.Length);
        }

        private static Color ParseColor(string hex)
        {
            if (!string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString(hex, out var c)) return c;
            return new Color(1f, 1f, 1f, 0.33f);
        }

        private void ShowToast(string text)
        {
            _toastText = text;
            _toastUntil = Time.unscaledTime + 2f;
        }

        private void OnGUI()
        {
            if (Time.unscaledTime > _toastUntil || string.IsNullOrEmpty(_toastText)) return;

            if (_toastStyle == null)
            {
                _toastStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 18,
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold,
                };
                _toastStyle.normal.textColor = Color.white;
            }

            var content = new GUIContent(_toastText);
            float w = Mathf.Max(240f, _toastStyle.CalcSize(content).x + 32f), h = 40f;
            var rect = new Rect((Screen.width - w) / 2f, Screen.height * 0.18f, w, h);
            var prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.Box(rect, GUIContent.none);
            GUI.color = prev;
            GUI.Label(rect, content, _toastStyle);
        }
    }
}
