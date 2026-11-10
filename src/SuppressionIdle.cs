using System;
using HarmonyLib;
using UnityEngine;

namespace OperatorVR;

/// <summary>
/// Rendering.IdleSuppressionOff (2026-11-10, speed): the game's "under fire" screen effect (SuppressionPostFX: blur,
/// vignette, colour) is a full-screen pass per eye that ran every frame even with nobody shooting at you. The GPU
/// speed test measured it at ~0.8-1 ms of the 17 ms frame in Georgia. While VR runs it now reports itself inactive
/// when its strength (now and where it's heading) is zero, so HDRP skips it; once you're shot at it draws as before.
/// </summary>
internal static class SuppressionIdle
{
    private const float Idle = 0.001f;
    private static int s_skipped, s_drawn;
    private static float s_logAt;

    internal static void Apply(Harmony harmony)
    {
        try
        {
            var target = AccessTools.Method(typeof(SuppressionPostFX), "IsActive");
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(SuppressionIdle), nameof(AfterIsActive)));
            Plugin.Logger.LogInfo("[Suppression] hooked SuppressionPostFX.IsActive (skipped while you're not under fire)");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Suppression] patching failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void AfterIsActive(SuppressionPostFX __instance, ref bool __result)
    {
        try
        {
            if (!__result || !Plugin.IdleSuppressionOff.Value || !OpenXRBootstrap.IsRunning)
                return;
            var busy = (__instance.MakeFullySuppressed != null && __instance.MakeFullySuppressed.value)
                       || Mathf.Abs(SuppressionPostFX.IntensityMultiplier) > Idle
                       || Mathf.Abs(SuppressionPostFX.IntensityMultiplierTarget) > Idle;
            if (busy)
                s_drawn++;
            else
            {
                s_skipped++;
                __result = false;
            }
            var now = Time.unscaledTime;
            if (now >= s_logAt)
            {
                if (s_logAt > 0f)
                    Plugin.Logger.LogInfo($"[Suppression] under-fire effect: drawn {s_drawn}x, skipped {s_skipped}x (idle) in the last 30 s");
                s_logAt = now + 30f;
                s_drawn = s_skipped = 0;
            }
        }
        catch
        {
            // never break the render pipeline's check
        }
    }
}
