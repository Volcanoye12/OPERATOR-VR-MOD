using Il2CppInterop.Runtime;
using UnityEngine;

namespace OperatorVR;

/// <summary>
/// The local player's character (its name contains "MyPlayer"), found once and kept until it goes (a level change),
/// with at most one whole-scene search every 2 s while there's none. Scopes, magnifiers and the like are then searched
/// for under the player only: several parts of the mod searched the whole level every second for them, which was a
/// likely source of the armoury's small hitches (2026-11-06).
/// </summary>
internal static class LocalPlayer
{
    private static PlayerNetworking s_me;
    private static float s_nextFind;

    internal static PlayerNetworking Get()
    {
        if (s_me != null)
            return s_me;
        if (Time.unscaledTime < s_nextFind)
            return null;
        s_nextFind = Time.unscaledTime + 2f;
        foreach (var o in Object.FindObjectsByType(Il2CppType.Of<PlayerNetworking>(), FindObjectsSortMode.None))
        {
            var p = o.TryCast<PlayerNetworking>();
            if (p != null && p.gameObject.name.Contains("MyPlayer"))
            {
                s_me = p;
                break;
            }
        }
        return s_me;
    }
}
