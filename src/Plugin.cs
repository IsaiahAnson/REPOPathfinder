using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace REPOPathfinder
{
    public enum PathfinderTargetMode
    {
        // Game-driven: paths to the current ExtractionPoint, falling back to the
        // truck once RoundDirector reports allExtractionPointsCompleted. This is
        // the default and matches the in-game map dot behavior.
        Auto,
        // Forces the truck regardless of extraction state - useful for going back
        // to charge items mid-run.
        Truck,
    }

    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "isaia.repopathfinder";
        public const string NAME = "REPOPathfinder";
        public const string VERSION = "0.7.2";

        // Bumped whenever a default value changes. The migration step below
        // overwrites stale values that match a previous default with the new
        // default. User-customized values (anything that doesn't match a known
        // previous default) are left untouched.
        private const int CurrentConfigVersion = 3;

        public static Plugin Instance { get; private set; }
        public static ManualLogSource Log { get; private set; }

        public ConfigEntry<KeyCode> ToggleKey;
        public ConfigEntry<KeyCode> TargetToggleKey;
        public ConfigEntry<KeyCode> CustomizeMenuKey;
        public ConfigEntry<bool> EnabledOnStart;
        public ConfigEntry<PathfinderTargetMode> TargetMode;

        public ConfigEntry<float> Spacing;
        public ConfigEntry<int> MaxDots;
        public ConfigEntry<float> GroundOffset;
        public ConfigEntry<float> DotScale;
        public ConfigEntry<string> DotColorHex;
        public ConfigEntry<float> RecalcInterval;
        public ConfigEntry<float> MinDistanceToTarget;
        public ConfigEntry<float> FlowSpeed;
        public ConfigEntry<float> FadeZoneFraction;
        public ConfigEntry<float> PositionSmoothRate;
        public ConfigEntry<float> OffPathThreshold;
        public ConfigEntry<int> ConfigVersion;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            ToggleKey = Config.Bind(
                "Input", "ToggleKey", KeyCode.F4,
                "Key that toggles the on-the-ground guidance trail.");
            TargetToggleKey = Config.Bind(
                "Input", "TargetToggleKey", KeyCode.F5,
                "Key that switches the trail's destination between Auto (game-driven: current extraction, or truck when all are complete) and Truck (forces the truck so you can run back to charge items mid-run).");
            CustomizeMenuKey = Config.Bind(
                "Input", "CustomizeMenuKey", KeyCode.F6,
                "Key that opens/closes the in-game customization panel for changing dot color, size, flow speed, and spacing with live preview.");
            EnabledOnStart = Config.Bind(
                "Input", "EnabledOnStart", false,
                "If true, the trail is visible immediately on every level. If false, you must press the toggle key.");
            TargetMode = Config.Bind(
                "Input", "TargetMode", PathfinderTargetMode.Auto,
                "Current target mode. Auto follows the game's own pathing logic. Truck always points back to the truck regardless of extraction state. Persisted across sessions; toggle at runtime with TargetToggleKey.");

            Spacing = Config.Bind(
                "Trail", "SpacingMeters", 1.5f,
                "Distance between dots in meters along the path.");
            MaxDots = Config.Bind(
                "Trail", "MaxDots", 30,
                "Maximum number of dots placed at once. Higher values reach further but cost more.");
            GroundOffset = Config.Bind(
                "Trail", "GroundOffsetMeters", 0.1f,
                "Small vertical offset above the NavMesh surface so dots don't z-fight with the floor.");
            DotScale = Config.Bind(
                "Trail", "DotScale", 0.07f,
                "Diameter of each dot in meters.");
            DotColorHex = Config.Bind(
                "Trail", "DotColorHex", "#FFFFFF55",
                "Dot color as #RRGGBBAA. Default is faint translucent white (~33% alpha).");
            RecalcInterval = Config.Bind(
                "Trail", "RecalcIntervalSeconds", 0.25f,
                "[Deprecated as of 0.5.0 - has no effect.] Path is now cached and only recomputed when the player wanders off-path or the target changes; see OffPathRecomputeThresholdMeters.");
            MinDistanceToTarget = Config.Bind(
                "Trail", "MinDistanceToTargetMeters", 1.5f,
                "If the player is within this distance of the target, dots are hidden.");
            FlowSpeed = Config.Bind(
                "Trail", "FlowSpeedMetersPerSec", 2.0f,
                "How fast dots flow forward along the path, in meters per second. The trail recycles continuously so the path is always populated from the player.");
            FadeZoneFraction = Config.Bind(
                "Trail", "FadeZoneFraction", 0.4f,
                "Fraction of one spacing over which dots fade in (near the player end of the recycle) and fade out (near the front). Smaller = sharper edges; larger = softer flow. Range 0.05-0.5.");
            PositionSmoothRate = Config.Bind(
                "Trail", "PositionSmoothRate", 25f,
                "Damping rate for dot world positions (1/seconds). With the cached-path model the trail no longer translates with player movement, but smoothing still helps soften the rare jumps when the path is re-oriented after a turn. Higher = snappier. Lower = softer. Set to 0 to disable.");
            OffPathThreshold = Config.Bind(
                "Trail", "OffPathRecomputeThresholdMeters", 3.0f,
                "When the player drifts further than this many meters from the cached path's polyline, the path is recomputed. Lower values make the trail re-orient faster after a turn but can cause spurious recomputes when hugging walls in wide rooms; higher values are steadier but lag longer after a deliberate turn into a different corridor.");

            ConfigVersion = Config.Bind(
                "Internal", "ConfigVersion", 0,
                "Internal: do not edit. Tracks which set of defaults this config was created with so newer mod versions can migrate stale values.");

            if (ConfigVersion.Value < CurrentConfigVersion)
            {
                MigrateConfig(ConfigVersion.Value);
                ConfigVersion.Value = CurrentConfigVersion;
            }

            var managerObj = new GameObject("REPOPathfinderManager");
            UnityEngine.Object.DontDestroyOnLoad(managerObj);
            managerObj.hideFlags = HideFlags.HideAndDontSave;
            managerObj.AddComponent<PathfinderManager>();
            managerObj.AddComponent<PathfinderCustomizeUI>();

            // Harmony patches: InputBlocker zeros out legacy Input.* axes/buttons
            // and the new InputAction.ReadValue<T>() while the customize menu is
            // open, so dragging sliders no longer rotates the camera or fires
            // weapons. PatchAll picks up the legacy attribute-decorated patches;
            // the InputSystem patches are bound dynamically because we don't
            // reference Unity.InputSystem at compile time.
            var harmony = new Harmony(GUID);
            try { harmony.PatchAll(Assembly.GetExecutingAssembly()); }
            catch (Exception e) { Log.LogWarning($"Harmony PatchAll issues (non-fatal): {e.Message}"); }
            InputBlocker.PatchInputSystem(harmony);

            Log.LogInfo($"{NAME} {VERSION} loaded. Toggle key: {ToggleKey.Value}");
        }

        private void MigrateConfig(int fromVersion)
        {
            // Catch values that exactly match a prior default. If the user
            // customized a setting, the value won't match and we leave it.
            if (DotColorHex.Value == "#00E5FFFF" || DotColorHex.Value == "#FFFFFFFF" || DotColorHex.Value == "#FFFFFF80")
            {
                DotColorHex.Value = (string)DotColorHex.DefaultValue;
            }
            if (Mathf.Approximately(DotScale.Value, 0.25f) || Mathf.Approximately(DotScale.Value, 0.125f))
            {
                DotScale.Value = (float)DotScale.DefaultValue;
            }
            Log.LogInfo($"Migrated config from version {fromVersion} -> {CurrentConfigVersion}.");
        }
    }
}
