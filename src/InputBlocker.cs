using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace REPOPathfinder
{
    /// <summary>
    /// While <see cref="BlockInput"/> is true, zero out the legacy Input axes / mouse
    /// reads AND the new Unity InputSystem's <c>InputAction.ReadValue&lt;T&gt;()</c>
    /// path, so the game's player controller stops rotating the camera and firing
    /// weapons in response to mouse activity. IMGUI clicks still work because Unity
    /// routes them through <c>Event.current</c>, not through Input.
    ///
    /// Deliberately not patched: GetKey / GetKeyDown / GetKeyUp - so the F4/F5/F6
    /// hotkeys keep working while the customize menu is open.
    ///
    /// This is the same pattern used by REPOBlacklist's InputBlocker.
    /// </summary>
    public static class InputBlocker
    {
        public static bool BlockInput;

        // ---------- Legacy UnityEngine.Input ----------

        [HarmonyPatch(typeof(Input), nameof(Input.GetAxis))]
        public static class GetAxisPatch
        {
            static bool Prefix(ref float __result)
            {
                if (!BlockInput) return true;
                __result = 0f;
                return false;
            }
        }

        [HarmonyPatch(typeof(Input), nameof(Input.GetAxisRaw))]
        public static class GetAxisRawPatch
        {
            static bool Prefix(ref float __result)
            {
                if (!BlockInput) return true;
                __result = 0f;
                return false;
            }
        }

        [HarmonyPatch(typeof(Input), nameof(Input.GetMouseButton))]
        public static class GetMouseButtonPatch
        {
            static bool Prefix(ref bool __result)
            {
                if (!BlockInput) return true;
                __result = false;
                return false;
            }
        }

        [HarmonyPatch(typeof(Input), nameof(Input.GetMouseButtonDown))]
        public static class GetMouseButtonDownPatch
        {
            static bool Prefix(ref bool __result)
            {
                if (!BlockInput) return true;
                __result = false;
                return false;
            }
        }

        [HarmonyPatch(typeof(Input), nameof(Input.GetMouseButtonUp))]
        public static class GetMouseButtonUpPatch
        {
            static bool Prefix(ref bool __result)
            {
                if (!BlockInput) return true;
                __result = false;
                return false;
            }
        }

        // ---------- New UnityEngine.InputSystem.InputAction ----------
        // Bound dynamically because we don't reference the InputSystem assembly at
        // compile time. Called once from Plugin.Awake after PatchAll.

        public static void PatchInputSystem(Harmony harmony)
        {
            try
            {
                var actionType = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => a.GetName().Name == "Unity.InputSystem")
                    .Select(a => a.GetType("UnityEngine.InputSystem.InputAction"))
                    .FirstOrDefault(t => t != null);
                if (actionType == null)
                {
                    Plugin.Log?.LogInfo("New InputSystem not loaded; skipping its patches.");
                    return;
                }

                var openReadValue = actionType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "ReadValue"
                                          && m.IsGenericMethodDefinition
                                          && m.GetParameters().Length == 0);
                if (openReadValue == null)
                {
                    Plugin.Log?.LogWarning("Could not find InputAction.ReadValue<T>(); InputSystem patches not applied.");
                    return;
                }

                var openPrefix = typeof(InputBlocker).GetMethod(nameof(GenericReadValuePrefix),
                    BindingFlags.NonPublic | BindingFlags.Static);

                int patched = 0;
                foreach (var t in new[] { typeof(Vector2), typeof(float), typeof(Vector3) })
                {
                    try
                    {
                        var target = openReadValue.MakeGenericMethod(t);
                        var prefix = openPrefix.MakeGenericMethod(t);
                        harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                        patched++;
                    }
                    catch (Exception e)
                    {
                        Plugin.Log?.LogWarning($"Could not patch InputAction.ReadValue<{t.Name}>: {e.Message}");
                    }
                }
                Plugin.Log?.LogInfo($"Patched InputAction.ReadValue<T>() for {patched} closed generic(s).");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"PatchInputSystem failed: {e}");
            }
        }

        // Generic prefix bound per-T at registration time.
        // ReSharper disable once UnusedMember.Local
        private static bool GenericReadValuePrefix<T>(ref T __result)
        {
            if (!BlockInput) return true;
            __result = default;
            return false;
        }
    }
}
