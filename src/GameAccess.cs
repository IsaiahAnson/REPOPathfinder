using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace REPOPathfinder
{
    /// <summary>
    /// Typed, non-boxing access to the game's internal fields. Bound once at startup; a field the
    /// game no longer has is reported by name in one log line instead of failing every frame.
    /// </summary>
    internal static class GameAccess
    {
        private static AccessTools.FieldRef<RoundDirector, ExtractionPoint> _extractionPointCurrent;
        private static AccessTools.FieldRef<RoundDirector, bool> _allExtractionPointsCompleted;
        private static AccessTools.FieldRef<PlayerAvatar, Vector3> _lastNavmeshPosition;
        private static AccessTools.FieldRef<PlayerAvatar, bool> _isDisabled;
        private static AccessTools.FieldRef<PlayerAvatar, bool> _deadSet;
        private static AccessTools.FieldRef<SpectateCamera, PlayerAvatar> _spectatedPlayer;

        /// <summary>True when the fields the trail cannot work without were found.</summary>
        public static bool Ready { get; private set; }

        public static void Bind(ManualLogSource log)
        {
            var missing = new List<string>();
            _extractionPointCurrent = Bind<RoundDirector, ExtractionPoint>("extractionPointCurrent", missing);
            _allExtractionPointsCompleted = Bind<RoundDirector, bool>("allExtractionPointsCompleted", missing);
            _lastNavmeshPosition = Bind<PlayerAvatar, Vector3>("LastNavmeshPosition", missing);
            Ready = missing.Count == 0;
            if (!Ready)
            {
                log.LogError("This game version no longer has: " + string.Join(", ", missing) +
                             ". The trail is turned off until REPOPathfinder is updated.");
            }

            // Only used to follow a teammate while dead. Without them the trail just hides on death.
            var optional = new List<string>();
            _isDisabled = Bind<PlayerAvatar, bool>("isDisabled", optional);
            _deadSet = Bind<PlayerAvatar, bool>("deadSet", optional);
            _spectatedPlayer = Bind<SpectateCamera, PlayerAvatar>("player", optional);
            if (optional.Count > 0)
            {
                log.LogWarning("Not found in this game version: " + string.Join(", ", optional) +
                               ". The trail will not follow the player you spectate.");
            }
        }

        private static AccessTools.FieldRef<T, F> Bind<T, F>(string name, List<string> missing) where T : class
        {
            try
            {
                var field = typeof(T).GetField(name, AccessTools.all);
                if (field != null && field.FieldType == typeof(F))
                {
                    return AccessTools.FieldRefAccess<T, F>(field);
                }
            }
            catch (Exception)
            {
                // Reported below, by name.
            }
            missing.Add(typeof(T).Name + "." + name);
            return null;
        }

        public static ExtractionPoint CurrentExtraction(RoundDirector director) => _extractionPointCurrent(director);

        public static bool AllExtractionsDone(RoundDirector director) => _allExtractionPointsCompleted(director);

        public static Vector3 LastNavmeshPosition(PlayerAvatar avatar) => _lastNavmeshPosition(avatar);

        public static bool IsDead(PlayerAvatar avatar)
        {
            return (_isDisabled != null && _isDisabled(avatar)) || (_deadSet != null && _deadSet(avatar));
        }

        /// <summary>The player the spectate camera is on, or null.</summary>
        public static PlayerAvatar SpectatedPlayer()
        {
            var camera = SpectateCamera.instance;
            return camera != null && _spectatedPlayer != null ? _spectatedPlayer(camera) : null;
        }
    }

    /// <summary>
    /// Holds the game's controls still while the customize panel is open, using the game's own
    /// switches (the ones its menus use). Nothing in Unity or the game is patched for this.
    /// </summary>
    internal static class GameInput
    {
        // Each call holds for this long, so the controls come back on their own shortly after the
        // panel closes, and a click on Close does not also reach the game.
        private const float HoldSeconds = 0.25f;

        // The only game controls left working while the panel is open: voice chat.
        private static readonly List<InputKey> AllowedKeys = new List<InputKey> { InputKey.PushToTalk };

        /// <summary>Call every frame while the panel is open.</summary>
        public static void HoldThisFrame()
        {
            var input = InputManager.instance;
            if (input != null)
            {
                input.DisableMovement(HoldSeconds);
                input.DisableAiming(HoldSeconds);
                input.DisableControlsExcept(HoldSeconds, AllowedKeys);
            }

            var cursor = CursorManager.instance;
            if (cursor != null)
            {
                cursor.Unlock(HoldSeconds);
            }
        }
    }
}
