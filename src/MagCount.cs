using System;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace OperatorVR;

/// <summary>
/// The user asked to see how many rounds are left when checking a magazine. The game already shows a short message
/// for a mag check (OnScreenNotification.SetMagCheckString, on the HUD), and the gun in hand knows its magazine
/// (WeaponV3.gunFunction.currentMagazine: ammoAmount of ammoAmountMax) and whether a round is chambered. After the game
/// sets its message, the exact count is added below it, for any rifle or pistol. Controls.ExactMagCount.
/// </summary>
internal static class MagCount
{
    private static PlayerNetworking s_me;
    private static bool s_logged;

    internal static void Apply(Harmony harmony)
    {
        try
        {
            var m = AccessTools.Method(typeof(OnScreenNotification), nameof(OnScreenNotification.SetMagCheckString));
            harmony.Patch(m, postfix: new HarmonyMethod(typeof(MagCount), nameof(AfterSetMagCheck)));
            Plugin.Logger.LogInfo("[Mag] hooked OnScreenNotification.SetMagCheckString");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Mag] hooking failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void AfterSetMagCheck(OnScreenNotification __instance)
    {
        if (!Plugin.ExactMagCount.Value || __instance == null)
            return;
        try
        {
            var text = __instance.MagCheckText;
            var count = Describe();
            if (text == null || count == null)
                return;
            text.text = string.IsNullOrWhiteSpace(text.text) ? count : $"{text.text}\n{count}";
            if (!s_logged)
            {
                s_logged = true;
                Plugin.Logger.LogInfo($"[Mag] Magazine check shows the exact count: {count}");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Mag] {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>"23 / 30 rounds + 1 in the chamber" for the gun in hand, or null.</summary>
    private static string Describe()
    {
        if (s_me == null)
            foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<PlayerNetworking>(), FindObjectsSortMode.None))
            {
                var p = o.TryCast<PlayerNetworking>();
                if (p != null && p.gameObject.name.Contains("MyPlayer"))
                {
                    s_me = p;
                    break;
                }
            }
        var weapon = s_me != null && s_me.activeWeapon != null ? s_me.activeWeapon.GetComponent<WeaponV3>() : null;
        var mag = weapon != null && weapon.gunFunction != null ? weapon.gunFunction.currentMagazine : null;
        if (mag == null)
            return null;
        var chamber = weapon.roundInChamber ? " + 1 in the chamber" : "";
        return $"{mag.ammoAmount} / {mag.ammoAmountMax} rounds{chamber}";
    }
}
