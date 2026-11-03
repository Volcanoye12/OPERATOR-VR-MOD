using System;
using HarmonyLib;
#if MELON
using Il2CppMichsky.DreamOS;
#else
using Michsky.DreamOS;
#endif
using UnityEngine.EventSystems;

namespace OperatorVR;

/// <summary>
/// Temporary: logs how far a laptop click gets inside the game (pointer click on a DreamOS button,
/// the button actually firing, double-click checks, entering/leaving the laptop), so we can see
/// where clicks stop in VR.
/// </summary>
[HarmonyPatch]
internal static class LaptopClickLog
{
    private static int _logged;

    internal static void Apply(Harmony harmony)
    {
        try
        {
            harmony.PatchAll(typeof(LaptopClickLog));
            Plugin.Logger.LogInfo("[LaptopClick] patches applied");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[LaptopClick] patching failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void Log(string msg)
    {
        if (_logged++ < 300)
            Plugin.Logger.LogInfo($"[LaptopClick] {msg}");
    }

    private static string Name(ButtonManager b) => b == null ? "-" : LaptopDiagnostics.Path(b.transform);

    [HarmonyPatch(typeof(ButtonManager), nameof(ButtonManager.OnPointerClick))]
    [HarmonyPrefix]
    private static void ButtonClick(ButtonManager __instance, PointerEventData eventData)
    {
        try
        {
            Log($"pointer click on '{Name(__instance)}' button={eventData?.button} clickCount={eventData?.clickCount} " +
                $"interactable={__instance.isInteractable} doubleClick={__instance.checkForDoubleClick} period={__instance.doubleClickPeriod}");
        }
        catch (Exception e) { Log($"click log error {e.Message}"); }
    }

    [HarmonyPatch(typeof(ButtonManager), nameof(ButtonManager.InvokeOnClick))]
    [HarmonyPrefix]
    private static void ButtonFired(ButtonManager __instance)
    {
        try { Log($"button FIRED '{Name(__instance)}'"); }
        catch (Exception e) { Log($"fire log error {e.Message}"); }
    }

    [HarmonyPatch(typeof(ButtonManager), nameof(ButtonManager.CheckForDoubleClick))]
    [HarmonyPrefix]
    private static void DoubleClickCheck(ButtonManager __instance)
    {
        try { Log($"double-click check on '{Name(__instance)}'"); }
        catch (Exception e) { Log($"double-click log error {e.Message}"); }
    }

    [HarmonyPatch(typeof(UnityEngine.UI.Button), nameof(UnityEngine.UI.Button.OnPointerClick))]
    [HarmonyPrefix]
    private static void UnityButtonClick(UnityEngine.UI.Button __instance)
    {
        try { Log($"unity button click '{LaptopDiagnostics.Path(__instance.transform)}' interactable={__instance.IsInteractable()}"); }
        catch (Exception e) { Log($"unity click log error {e.Message}"); }
    }

    // Standing only in VR (the user's choice, 2026-11-03): Interact at a laptop doesn't sit you down; the pad goes to
    // our mission screen standing over it instead (MissionScreen)
    private static bool s_accessSkipped;

    [HarmonyPatch(typeof(MissionLaptop), nameof(MissionLaptop.AccessLaptop))]
    [HarmonyPrefix]
    private static bool Access(MissionLaptop __instance, PlayerNetworking __0)
    {
        try
        {
            Log($"AccessLaptop '{__instance.name}'");
            if (MissionScreen.StandingMode)
            {
                s_accessSkipped = true;
                // Missions are chosen at the window's screen where there is one (the user's choice), else here
                if (MissionScreen.UseLaptopInstead(__instance))
                    MissionScreen.TakeControl(__instance, __0);
                else
                    Log("AccessLaptop skipped: missions are chosen at the window screen");
                return false;
            }
        }
        catch (Exception e) { Log($"access log error {e.Message}"); }
        return true;
    }

    /// <summary>The laptop the player is sitting at (between AccessLaptop and ExitLaptop), or null.</summary>
    internal static MissionLaptop CurrentLaptop { get; private set; }

    // Sitting down moves the view; start the pointer in the middle of the screen again
    [HarmonyPatch(typeof(MissionLaptop), nameof(MissionLaptop.AccessLaptop))]
    [HarmonyPostfix]
    private static void AccessDone(MissionLaptop __instance)
    {
        try
        {
            if (s_accessSkipped)
            {
                s_accessSkipped = false;
                return;
            }
            CurrentLaptop = __instance;
            if (OpenXRBootstrap.IsRunning)
                VRPointer.CenterMouse();
        }
        catch (Exception e) { Log($"center error {e.Message}"); }
    }

    [HarmonyPatch(typeof(MissionLaptop), nameof(MissionLaptop.ExitLaptop))]
    [HarmonyPostfix]
    private static void ExitDone()
    {
        CurrentLaptop = null;
    }

    [HarmonyPatch(typeof(MissionLaptop), nameof(MissionLaptop.ExitLaptop))]
    [HarmonyPrefix]
    private static bool Exit(MissionLaptop __instance)
    {
        try
        {
            Log($"ExitLaptop '{__instance.name}'");
            // Standing mode never sat us down, so there's nothing to get up from: the game's own exit (called when a
            // mission starts) threw on the camera and player it never set up (2026-11-03)
            if (MissionScreen.StandingMode && CurrentLaptop == null)
            {
                Log("ExitLaptop skipped (standing at the laptop, never sat down)");
                return false;
            }
        }
        catch (Exception e) { Log($"exit log error {e.Message}"); }
        return true;
    }
}
