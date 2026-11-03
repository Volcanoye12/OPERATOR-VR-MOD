using System;
using HarmonyLib;
using UnityEngine;

namespace OperatorVR;

/// <summary>
/// The game finds what you can interact with (e.g. the shoot house's range target handle) from its camera's aim
/// (PlayerNetworking.ScanForInteractable / ScanForNearbyInteractable / ProcessInteractable). With hand aim that
/// camera points where the gun points, so the prompt vanished as soon as you looked at the thing. For just those
/// calls, the camera is put where your head looks, then put back. Controls.InteractWithHead.
/// </summary>
internal static class GazeInteraction
{
    private static int s_depth;
    private static Vector3 s_pos;
    private static Quaternion s_rot;
    private static bool s_applied;
    private static bool s_logged;

    // (Tried on with a gamepad too: the scans never found anything that way, so the pad keeps the game's own aim,
    // shown in VR by the GazeDot at the aim point instead)
    private static bool Active => Plugin.InteractWithHead.Value;

    internal static void Apply(Harmony harmony)
    {
        // Not even hooked when off, so turning it off also rules the hooks out if the game misbehaves
        if (!Active)
            return;
        foreach (var name in new[] { "ScanForInteractable", "ScanForNearbyInteractable", "ProcessInteractable" })
        {
            try
            {
                var m = AccessTools.Method(typeof(PlayerNetworking), name);
                if (m == null)
                {
                    Plugin.Logger.LogWarning($"[Gaze] PlayerNetworking.{name} not found");
                    continue;
                }
                // Postfix, not finalizer: finalizers aren't dependable on IL2CPP detours
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(GazeInteraction), nameof(Before)),
                                 postfix: new HarmonyMethod(typeof(GazeInteraction), nameof(After)));
                Plugin.Logger.LogInfo($"[Gaze] hooked PlayerNetworking.{name}");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[Gaze] hooking {name} failed: {e.GetType().Name}: {e.Message}");
            }
        }
    }

    private static int s_calls;

    private static void Before(System.Reflection.MethodBase __originalMethod)
    {
        // Nested calls (ProcessInteractable calling ScanForInteractable) keep the outermost swap
        if (s_depth++ > 0)
            return;
        s_applied = false;
        try
        {
            // No isLocalPlayer check: with it the swap never happened (only the local player scans anyway)
            if (!Active)
                return;
            s_applied = HeadTracking.TryApplyHeadPoseToGameCamera(out s_pos, out s_rot);
            if (s_calls < 5)
            {
                s_calls++;
                Plugin.Logger.LogInfo($"[Gaze] {__originalMethod.Name}: camera {(s_applied ? "moved to where you look" : "left alone (no head pose yet)")}");
            }
            if (s_applied && !s_logged)
            {
                s_logged = true;
                Plugin.Logger.LogInfo("[Gaze] Interactions now follow where you look");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Gaze] {e.GetType().Name}: {e.Message}");
        }
    }

    private static void After()
    {
        if (--s_depth > 0)
            return;
        s_depth = 0;
        if (s_applied)
        {
            s_applied = false;
            try { HeadTracking.RestoreGameCameraPose(s_pos, s_rot); } catch { }
        }
    }
}
