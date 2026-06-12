using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace REPOPathfinder
{
    /// <summary>
    /// Mirrors REPO's MapBacktrack target logic into world-space dots so the
    /// player can follow guidance without opening the map.
    ///
    /// Path model (v0.5): the NavMesh path is computed *once* (per route) and
    /// cached as a fixed list of world-space corners with prefix arc-distances.
    /// Each frame the player's current position is projected onto that cached
    /// polyline to produce a `_playerArc` scalar; dots are placed at world
    /// positions corresponding to (playerArc + slot*spacing + animOffset) along
    /// the cached corners. Recompute is on-demand only:
    ///   - target identity changed (extraction completed / new active extraction
    ///     / truck activated)
    ///   - player drifted more than OffPathThreshold meters from the polyline
    ///     (i.e., took a different route)
    /// This decouples dot world positions from per-frame player movement, so
    /// walking forward or backward along the path does not jitter the trail.
    ///
    /// Animation model (unchanged from v0.3): a single shared `_animOffset` in
    /// [0, spacing) plus a circular pool pointer `_frontPool` keeps spacing
    /// perfectly even and makes the per-cycle wrap (one sphere going front -> hidden
    /// -> back) invisible behind alpha fade.
    /// </summary>
    public class PathfinderManager : MonoBehaviour
    {
        private bool _trailEnabled;
        private NavMeshPath _path;

        // Cached path: corners and prefix arc distances. _totalLength = arc of last corner.
        private Vector3[] _cachedCorners;
        private float[] _cachedArcs;
        private float _totalLength;
        private object _cachedTargetIdentity;

        private readonly List<GameObject> _dots = new List<GameObject>();
        private readonly List<MeshRenderer> _renderers = new List<MeshRenderer>();
        private Vector3[] _smoothedPositions;
        private bool[] _wasVisible;

        private float _animOffset;
        private int _frontPool;
        private bool _animInitialized;

        private Material _sharedMaterial;
        private MaterialPropertyBlock _mpb;
        private Color _baseColor;
        private static readonly int ColorPropId = Shader.PropertyToID("_Color");

        private FieldInfo _fldExtractionPointCurrent;
        private FieldInfo _fldAllExtractionPointsCompleted;
        private FieldInfo _fldLastNavmeshPosition;

        private float _toastUntil;
        private string _toastText;
        private GUIStyle _toastStyle;

        private void Awake()
        {
            _path = new NavMeshPath();
            _mpb = new MaterialPropertyBlock();
            _trailEnabled = Plugin.Instance.EnabledOnStart.Value;
            _baseColor = ParseColor(Plugin.Instance.DotColorHex.Value);

            _fldExtractionPointCurrent = Reflect.Field(typeof(RoundDirector), "extractionPointCurrent");
            _fldAllExtractionPointsCompleted = Reflect.Field(typeof(RoundDirector), "allExtractionPointsCompleted");
            _fldLastNavmeshPosition = Reflect.Field(typeof(PlayerAvatar), "LastNavmeshPosition");

            if (_fldExtractionPointCurrent == null || _fldAllExtractionPointsCompleted == null || _fldLastNavmeshPosition == null)
            {
                Plugin.Log.LogWarning("Could not bind one or more REPO internal fields via reflection. Trail may be inactive.");
            }

            // Live-update the cached color whenever the customization UI (or any
            // other config consumer) writes a new value. ApplyDotAlpha reads
            // _baseColor every frame, so dots in the world refresh instantly.
            Plugin.Instance.DotColorHex.SettingChanged += OnDotColorChanged;
        }

        private void OnDestroy()
        {
            try
            {
                if (Plugin.Instance != null && Plugin.Instance.DotColorHex != null)
                    Plugin.Instance.DotColorHex.SettingChanged -= OnDotColorChanged;
            }
            catch { /* shutdown — safe to ignore */ }
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
            // Cached corners are world-space references; they don't survive a
            // scene change. Drop them so the next frame triggers a fresh compute.
            InvalidateCache();
            HideAllDots();
        }

        private void InvalidateCache()
        {
            _cachedCorners = null;
            _cachedArcs = null;
            _totalLength = 0f;
            _cachedTargetIdentity = null;
            if (_wasVisible != null) System.Array.Clear(_wasVisible, 0, _wasVisible.Length);
        }

        private void Update()
        {
            if (Input.GetKeyDown(Plugin.Instance.ToggleKey.Value))
            {
                _trailEnabled = !_trailEnabled;
                ShowToast(_trailEnabled ? "Pathfinder: ON" : "Pathfinder: OFF");
                if (!_trailEnabled) HideAllDots();
            }

            if (Input.GetKeyDown(Plugin.Instance.TargetToggleKey.Value))
            {
                var current = Plugin.Instance.TargetMode.Value;
                var next = current == PathfinderTargetMode.Auto
                    ? PathfinderTargetMode.Truck
                    : PathfinderTargetMode.Auto;
                Plugin.Instance.TargetMode.Value = next;
                ShowToast(next == PathfinderTargetMode.Truck
                    ? "Target: Truck"
                    : "Target: Auto");
                // The cached path was computed for the previous target; drop it
                // so the next frame produces a fresh path to the new destination.
                InvalidateCache();
            }

            if (!_trailEnabled) return;

            if (Map.Instance == null || PlayerController.instance == null ||
                LevelGenerator.Instance == null || !LevelGenerator.Instance.Generated ||
                RoundDirector.instance == null)
            {
                HideAllDots();
                return;
            }

            var avatar = PlayerController.instance.playerAvatarScript;
            if (avatar == null) { HideAllDots(); return; }

            Vector3 playerPos = Reflect.GetFieldValue(_fldLastNavmeshPosition, avatar, avatar.transform.position);

            if (NeedRecompute(playerPos))
            {
                if (!RecomputePath(playerPos))
                {
                    HideAllDots();
                    return;
                }
            }

            if (_cachedCorners == null || _cachedCorners.Length < 2 || _totalLength <= 0.5f)
            {
                HideAllDots();
                return;
            }

            ProjectOnCachedPath(playerPos, out float playerArc, out _);

            float availableLength = _totalLength - playerArc;
            if (availableLength < Plugin.Instance.MinDistanceToTarget.Value)
            {
                HideAllDots();
                return;
            }

            AdvanceAndPlace(Time.deltaTime, playerArc, availableLength);
        }

        private object ResolveTargetIdentity()
        {
            // Truck mode overrides the game state: always head back to the truck,
            // even if extractions are still pending (used to charge items mid-run).
            if (Plugin.Instance.TargetMode.Value == PathfinderTargetMode.Truck)
            {
                return LevelGenerator.Instance != null && LevelGenerator.Instance.LevelPathTruck != null
                    ? (object)"truck"
                    : null;
            }

            // Auto mode: mirrors REPO's own MapBacktrack target selection.
            bool allDone = Reflect.GetFieldValue(_fldAllExtractionPointsCompleted, RoundDirector.instance, false);
            if (allDone) return "truck";
            return Reflect.GetFieldValue<ExtractionPoint>(_fldExtractionPointCurrent, RoundDirector.instance, null);
        }

        private bool NeedRecompute(Vector3 playerPos)
        {
            var current = ResolveTargetIdentity();
            if (current == null)
            {
                // No active target - drop the cache so we don't render against stale corners.
                if (_cachedCorners != null) InvalidateCache();
                return false;
            }

            if (_cachedCorners == null || _cachedCorners.Length < 2) return true;
            if (!Equals(current, _cachedTargetIdentity)) return true;

            if (!ProjectOnCachedPath(playerPos, out _, out var dist)) return true;
            if (dist > Plugin.Instance.OffPathThreshold.Value) return true;

            return false;
        }

        private bool RecomputePath(Vector3 playerPos)
        {
            var target = ResolveTargetIdentity();
            if (target == null) return false;

            Vector3 targetPos;
            if (target as string == "truck")
            {
                var truck = LevelGenerator.Instance.LevelPathTruck;
                if (truck == null) return false;
                targetPos = truck.transform.position;
            }
            else if (target is ExtractionPoint ep)
            {
                targetPos = ep.transform.position;
            }
            else
            {
                return false;
            }

            if (Vector3.Distance(playerPos, targetPos) < Plugin.Instance.MinDistanceToTarget.Value)
                return false;

            if (!NavMesh.CalculatePath(playerPos, targetPos, NavMesh.AllAreas, _path) ||
                _path.status == NavMeshPathStatus.PathInvalid ||
                _path.corners == null || _path.corners.Length < 2)
            {
                return false;
            }

            CacheCurrentPath();
            _cachedTargetIdentity = target;
            return true;
        }

        private void CacheCurrentPath()
        {
            var c = _path.corners;
            if (_cachedCorners == null || _cachedCorners.Length != c.Length)
            {
                _cachedCorners = new Vector3[c.Length];
                _cachedArcs = new float[c.Length];
            }
            _cachedCorners[0] = c[0];
            _cachedArcs[0] = 0f;
            for (int i = 1; i < c.Length; i++)
            {
                _cachedCorners[i] = c[i];
                _cachedArcs[i] = _cachedArcs[i - 1] + Vector3.Distance(c[i - 1], c[i]);
            }
            _totalLength = _cachedArcs[c.Length - 1];

            // Reset smoothing flags so dots snap to their new positions on the
            // next frame instead of drawing a curved interpolation across the
            // path re-orientation.
            if (_wasVisible != null) System.Array.Clear(_wasVisible, 0, _wasVisible.Length);
        }

        // Projects p onto the cached polyline. Returns the arc-distance of the
        // closest point and that point's distance from p.
        private bool ProjectOnCachedPath(Vector3 p, out float arc, out float dist)
        {
            arc = 0f;
            dist = float.MaxValue;
            if (_cachedCorners == null || _cachedCorners.Length < 2) return false;

            for (int i = 1; i < _cachedCorners.Length; i++)
            {
                Vector3 a = _cachedCorners[i - 1];
                Vector3 b = _cachedCorners[i];
                Vector3 ab = b - a;
                float sqrLen = ab.sqrMagnitude;
                if (sqrLen < 1e-6f) continue;

                float t = Vector3.Dot(p - a, ab) / sqrLen;
                t = Mathf.Clamp01(t);
                Vector3 cp = a + ab * t;
                float d = Vector3.Distance(cp, p);
                if (d < dist)
                {
                    dist = d;
                    arc = _cachedArcs[i - 1] + Mathf.Sqrt(sqrLen) * t;
                }
            }
            return true;
        }

        private Vector3 CachedPointAtDistance(float distance)
        {
            if (_cachedCorners == null || _cachedCorners.Length == 0) return Vector3.zero;
            if (_cachedCorners.Length == 1 || distance <= 0f) return _cachedCorners[0];

            for (int i = 1; i < _cachedCorners.Length; i++)
            {
                if (distance <= _cachedArcs[i])
                {
                    float seg = _cachedArcs[i] - _cachedArcs[i - 1];
                    if (seg < 1e-6f) return _cachedCorners[i];
                    float t = (distance - _cachedArcs[i - 1]) / seg;
                    return Vector3.Lerp(_cachedCorners[i - 1], _cachedCorners[i], t);
                }
            }
            return _cachedCorners[_cachedCorners.Length - 1];
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
            EnsureSmoothing(maxDots);

            if (!_animInitialized)
            {
                _frontPool = 0;
                _animOffset = 0f;
                _animInitialized = true;
            }

            if (_frontPool < 0 || _frontPool >= maxDots)
                _frontPool = ((_frontPool % maxDots) + maxDots) % maxDots;

            _animOffset += speed * dt;
            while (_animOffset >= spacing)
            {
                _animOffset -= spacing;
                _frontPool = (_frontPool - 1 + maxDots) % maxDots;
                // After the decrement, the pool index sitting at _frontPool is
                // the sphere whose slot just wrapped from (maxDots-1) to 0. Its
                // world target jumped from the front of the trail back to the
                // player end - if we let the smoothing lerp handle that, the
                // sphere visibly flies across the room ("conveyor belt"). When
                // activeCount < maxDots this dot was hidden and its _wasVisible
                // was already false (no-op); when activeCount == maxDots it was
                // visible and we must clear the flag so the next render snaps.
                _wasVisible[_frontPool] = false;
            }

            int activeCount = Mathf.Min(maxDots, Mathf.Max(1, Mathf.CeilToInt(availableLength / spacing)));
            float trailExtent = activeCount * spacing;
            float endpoint = Mathf.Min(availableLength, trailExtent);
            float fadeZone = Mathf.Max(0.05f, Mathf.Min(spacing, endpoint * 0.5f) * fadeFrac);

            float smoothRate = Plugin.Instance.PositionSmoothRate.Value;
            float lerpT = (smoothRate > 0f) ? 1f - Mathf.Exp(-smoothRate * dt) : 1f;

            for (int k = 0; k < maxDots; k++)
            {
                int slot = ((k - _frontPool) % maxDots + maxDots) % maxDots;
                if (slot >= activeCount)
                {
                    if (_dots[k].activeSelf) _dots[k].SetActive(false);
                    _wasVisible[k] = false;
                    continue;
                }

                float d = slot * spacing + _animOffset;
                float dClamped = Mathf.Min(d, availableLength);

                Vector3 targetPos = CachedPointAtDistance(playerArc + dClamped);
                targetPos.y += yOffset;

                if (!_wasVisible[k])
                    _smoothedPositions[k] = targetPos;
                else
                    _smoothedPositions[k] = Vector3.Lerp(_smoothedPositions[k], targetPos, lerpT);

                _dots[k].transform.position = _smoothedPositions[k];
                _wasVisible[k] = true;

                if (_dots[k].transform.localScale.x != scale)
                    _dots[k].transform.localScale = new Vector3(scale, scale, scale);

                float fadeIn = Mathf.SmoothStep(0f, 1f, d / fadeZone);
                float fadeOut = Mathf.SmoothStep(0f, 1f, (endpoint - d) / fadeZone);
                ApplyDotAlpha(k, Mathf.Min(fadeIn, fadeOut));

                if (!_dots[k].activeSelf) _dots[k].SetActive(true);
            }
        }

        private void EnsureSmoothing(int count)
        {
            if (_smoothedPositions == null || _smoothedPositions.Length < count)
            {
                var freshPos = new Vector3[count];
                var freshVis = new bool[count];
                if (_smoothedPositions != null)
                {
                    System.Array.Copy(_smoothedPositions, freshPos, _smoothedPositions.Length);
                    System.Array.Copy(_wasVisible, freshVis, _wasVisible.Length);
                }
                _smoothedPositions = freshPos;
                _wasVisible = freshVis;
            }
        }

        private void EnsurePool(int count)
        {
            if (_sharedMaterial == null) _sharedMaterial = CreateSharedMaterial();
            while (_dots.Count < count)
            {
                var go = CreateDot(_dots.Count);
                _dots.Add(go);
                _renderers.Add(go.GetComponent<MeshRenderer>());
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
            renderer.sharedMaterial = _sharedMaterial;

            float scale = Mathf.Max(0.01f, Plugin.Instance.DotScale.Value);
            go.transform.localScale = new Vector3(scale, scale, scale);
            go.SetActive(false);
            return go;
        }

        private Material CreateSharedMaterial()
        {
            var shader = Shader.Find("Sprites/Default")
                         ?? Shader.Find("Unlit/Color")
                         ?? Shader.Find("Standard");
            var mat = new Material(shader);

            mat.color = _baseColor;
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", _baseColor);
            if (mat.HasProperty("_ZWrite")) mat.SetInt("_ZWrite", 0);
            if (mat.HasProperty("_SrcBlend")) mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            if (mat.HasProperty("_DstBlend")) mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            mat.renderQueue = 3500;
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
            for (int i = 0; i < _dots.Count; i++)
                if (_dots[i] != null && _dots[i].activeSelf) _dots[i].SetActive(false);
            if (_wasVisible != null)
                System.Array.Clear(_wasVisible, 0, _wasVisible.Length);
        }

        private static Color ParseColor(string hex)
        {
            if (!string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString(hex, out var c)) return c;
            return new Color(1f, 1f, 1f, 0.33f);
        }

        private void ShowToast(string text)
        {
            _toastText = text;
            _toastUntil = Time.unscaledTime + 1.5f;
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

            float w = 240f, h = 40f;
            var rect = new Rect((Screen.width - w) / 2f, Screen.height * 0.18f, w, h);
            var prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.Box(rect, GUIContent.none);
            GUI.color = prev;
            GUI.Label(rect, _toastText, _toastStyle);
        }
    }
}
