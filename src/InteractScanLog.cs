using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace OperatorVR;

/// <summary>
/// Temporary: the laptop stopped offering its prompt (Interact pressed with the aim on it, nothing happened). Logs
/// what the game's interaction scan (PlayerNetworking.ScanForInteractable, a raycast from its camera) hits for the
/// local player, whenever that changes.
/// </summary>
internal static class InteractScanLog
{
    private static string s_last;
    private static bool s_interactWas;
    private static int s_pressLogs;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    internal static void Apply(Harmony harmony)
    {
        try
        {
            var target = AccessTools.Method(typeof(PlayerNetworking), nameof(PlayerNetworking.ScanForInteractable));
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(InteractScanLog), nameof(BeforeScan)),
                postfix: new HarmonyMethod(typeof(InteractScanLog), nameof(AfterScan)));
            Plugin.Logger.LogInfo("[Scan] hooked PlayerNetworking.ScanForInteractable");
            var nearby = AccessTools.Method(typeof(PlayerNetworking), nameof(PlayerNetworking.ScanForNearbyInteractable));
            harmony.Patch(nearby, postfix: new HarmonyMethod(typeof(InteractScanLog), nameof(AfterNearbyScan)));
            Plugin.Logger.LogInfo("[Scan] hooked PlayerNetworking.ScanForNearbyInteractable");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Scan] patching failed: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Whether something usable is nearby (the game's own proximity check), and when that was last true.</summary>
    internal static float NearbyAt { get; private set; } = -1f;
    private static bool s_nearbyLast;

    private static void AfterNearbyScan(PlayerNetworking __instance, bool __result)
    {
        if (!OpenXRBootstrap.IsRunning || __instance == null || !__result)
            return;
        try
        {
            if (!__instance.gameObject.name.Contains("MyPlayer"))
                return;
            NearbyAt = Time.unscaledTime;
            if (!s_nearbyLast)
            {
                s_nearbyLast = true;
                Plugin.Logger.LogInfo("[Scan] Something usable nearby (first time this run)");
            }
        }
        catch { }
    }

    /// <summary>Whether the latest scan found something to use (for the gaze dot), and when.</summary>
    internal static bool Found { get; private set; }
    internal static float ScannedAt { get; private set; } = -1f;

    // The outdoor laptop: an Interact press there hit nothing within the game's 1.2 m reach (log, 2026-11-01). In VR the
    // head isn't where the game expects to lean over a table, so the reach gets a little longer (Controls.InteractReach)
    private static void BeforeScan(PlayerNetworking __instance)
    {
        if (!OpenXRBootstrap.IsRunning || __instance == null)
            return;
        try
        {
            var reach = Plugin.InteractReach.Value;
            if (reach > 0f && __instance.interactionDetectionRange < reach)
                __instance.interactionDetectionRange = reach;
            HideOwnWeapon(__instance);
        }
        catch { }
    }

    // In VR the gun is held out in front, often right across the game's aim line, where on a flat screen it sits below
    // it. Its parts (a 'Lock' 0.85 m ahead, 2026-11-01) can block the line before it reaches the laptop. For the scan
    // only, the held weapon's solid parts go on the Ignore Raycast layer, and back straight after
    private const int IgnoreRaycastLayer = 2;
    private static readonly List<(GameObject go, int layer)> s_hidden = new();
    private static int s_hideLogs;
    private static IntPtr s_partsOf;
    private static float s_partsAt;
    private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Collider> s_parts;

    private static void HideOwnWeapon(PlayerNetworking me)
    {
        if (!Plugin.InteractPastGun.Value || !me.gameObject.name.Contains("MyPlayer") || me.activeWeapon == null)
            return;
        // The scan runs every frame; look the parts up again only when the held weapon changes, or every 2 s for
        // attachments added on the bench
        if (s_partsOf != me.activeWeapon.Pointer || Time.unscaledTime >= s_partsAt)
        {
            s_partsOf = me.activeWeapon.Pointer;
            s_partsAt = Time.unscaledTime + 2f;
            s_parts = me.activeWeapon.GetComponentsInChildren<Collider>(false);
        }
        foreach (var c in s_parts)
        {
            if (c == null || c.isTrigger || !c.enabled || c.gameObject.layer == IgnoreRaycastLayer)
                continue;
            s_hidden.Add((c.gameObject, c.gameObject.layer));
            c.gameObject.layer = IgnoreRaycastLayer;
        }
        if (s_hidden.Count > 0 && s_hideLogs++ == 0)
            Plugin.Logger.LogInfo($"[Scan] The held weapon's {s_hidden.Count} solid parts are ignored by the use-aim (first: '{s_hidden[0].go.name}')");
    }

    private static void ShowOwnWeapon()
    {
        foreach (var (go, layer) in s_hidden)
            if (go != null)
                go.layer = layer;
        s_hidden.Clear();
    }

    private static bool s_computerLogged;

    private static string Scripts(GameObject go)
    {
        var names = new List<string>();
        foreach (var c in go.GetComponents<Component>())
            if (c != null && !(c.TryCast<Transform>() is { }))
                names.Add(c.GetIl2CppType().Name + (c.TryCast<Behaviour>() is { } b && !b.enabled ? " (off)" : ""));
        return names.Count == 0 ? "-" : string.Join(", ", names);
    }

    private static void LogAimAndLaptops(Transform cam)
    {
        try
        {
            var pos = cam.position;
            var fwd = cam.forward;
            var any = Physics.Raycast(pos, fwd, out var hit, 5f, ~0, QueryTriggerInteraction.Collide)
                ? $"'{hit.collider.name}' (layer {LayerMask.LayerToName(hit.collider.gameObject.layer)}) at {hit.distance:0.00} m"
                : "nothing within 5 m";
            Plugin.Logger.LogInfo($"[Scan]   game camera at {pos}, looking {fwd}; on any layer the aim line hits {any}");
            // Everything along the line, nearest first, with where it lives: 'Lock' (Default layer) sat 0.85 m ahead in
            // every press, in two places, so it moves with the player
            var all = new List<RaycastHit>(Physics.RaycastAll(pos, fwd, 3f, ~0, QueryTriggerInteraction.Collide));
            all.Sort((a, b) => a.distance.CompareTo(b.distance));
            foreach (var h in all)
                if (h.collider != null)
                    Plugin.Logger.LogInfo($"[Scan]     {h.distance:0.00} m: '{LaptopDiagnostics.Path(h.collider.transform)}' " +
                                          $"(layer {LayerMask.LayerToName(h.collider.gameObject.layer)}, trigger {h.collider.isTrigger})");
            var n = 0;
            foreach (var c in Physics.OverlapSphere(pos, 3f, ~0, QueryTriggerInteraction.Collide))
            {
                if (c == null || n >= 6)
                    continue;
                var laptop = c.GetComponentInParent<MissionLaptop>();
                if (laptop == null && !c.name.ToLowerInvariant().Contains("laptop"))
                    continue;
                n++;
                var to = c.bounds.center - pos;
                Plugin.Logger.LogInfo($"[Scan]   laptop collider '{c.name}' (layer {LayerMask.LayerToName(c.gameObject.layer)}, trigger {c.isTrigger}, " +
                                      $"enabled {c.enabled}) {to.magnitude:0.00} m away, {Vector3.Angle(fwd, to):0}° off the aim line, " +
                                      $"size {c.bounds.size}, script on '{(laptop != null ? laptop.name : "-")}'");
            }
            if (n == 0)
                Plugin.Logger.LogInfo("[Scan]   no laptop colliders within 3 m");
            // The shoot house's 'Range Computer ShootHouse 1' takes the aim on Default-layer parts only, while the game
            // sometimes sees something usable nearby: where are the usable (Interactable layer) colliders?
            var interactable = LayerMask.NameToLayer("Interactable");
            n = 0;
            foreach (var c in Physics.OverlapSphere(pos, 3f, 1 << interactable, QueryTriggerInteraction.Collide))
            {
                if (c == null || n++ >= 8)
                    continue;
                var to = c.bounds.center - pos;
                Plugin.Logger.LogInfo($"[Scan]   usable '{LaptopDiagnostics.Path(c.transform)}' ({c.GetIl2CppType().Name}, trigger {c.isTrigger}) " +
                                      $"{to.magnitude:0.00} m away, {Vector3.Angle(fwd, to):0}° off the aim line, centre {c.bounds.center}, size {c.bounds.size}; " +
                                      $"scripts: {Scripts(c.gameObject)}");
            }
            if (n == 0)
                Plugin.Logger.LogInfo("[Scan]   nothing on the Interactable layer within 3 m");
            if (!s_computerLogged)
                foreach (var h in all)
                {
                    var root = h.collider != null ? h.collider.transform : null;
                    while (root != null && !root.name.Contains("Computer"))
                        root = root.parent;
                    if (root == null)
                        continue;
                    s_computerLogged = true;
                    Plugin.Logger.LogInfo($"[Scan]   '{root.name}' at {root.position}, scripts: {Scripts(root.gameObject)}");
                    foreach (var t in root.GetComponentsInChildren<Transform>(true))
                        if (t != root)
                            Plugin.Logger.LogInfo($"[Scan]     part '{LaptopDiagnostics.Path(t)}' active {t.gameObject.activeInHierarchy}, " +
                                                  $"layer {LayerMask.LayerToName(t.gameObject.layer)}, scripts: {Scripts(t.gameObject)}");
                    break;
                }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogInfo($"[Scan]   aim/laptop log failed: {e.Message}");
        }
    }

    private static void AfterScan(PlayerNetworking __instance, bool __result)
    {
        using var __t = ModTime.Measure("InteractScan hook");
        ShowOwnWeapon();
        if (!OpenXRBootstrap.IsRunning || __instance == null)
            return;
        try
        {
            if (!__instance.gameObject.name.Contains("MyPlayer"))
                return;
            Found = __result;
            ScannedAt = Time.unscaledTime;
            // Each press of Interact (F; R3 on the pad) logs what the scan hits at that moment, up to 40 a session: the
            // shoot house's laptop outdoors couldn't be used, and nothing showed why
            var interact = (GetAsyncKeyState(0x46) & 0x8000) != 0;
            var pressed = interact && !s_interactWas;
            s_interactWas = interact;
            if (pressed && s_pressLogs < 40)
            {
                s_pressLogs++;
                var h = __instance._interactableRayHit;
                var c = h.collider;
                var cam0 = Camera.main;
                Plugin.Logger.LogInfo($"[Scan] Interact pressed: {(__result ? "can use" : "nothing usable")}; aim hits " +
                                      $"'{(c != null ? c.name : "-")}' at {h.distance:0.00} m (range {__instance.interactionDetectionRange:0.0} m), " +
                                      $"game aim pitch {(cam0 != null ? Mathf.DeltaAngle(0f, cam0.transform.eulerAngles.x) : 0f):0}°, " +
                                      $"something usable nearby: {Time.unscaledTime - NearbyAt < 0.5f}");
                // The aim hit nothing usable with the laptop in front (2026-11-01): what the same line hits on any
                // layer, and where laptops around are compared with it
                if (cam0 != null)
                    LogAimAndLaptops(cam0.transform);
            }
            // The log line (built on every scan) only with diagnostics on
            if (!Plugin.Diagnostics.Value)
                return;
            var hit = __instance._interactableRayHit;
            var col = hit.collider;
            var what = __result
                ? $"found '{(col != null ? col.name : "?")}' at {hit.distance:0.00} m"
                : $"nothing (ray hit '{(col != null ? col.name : "-")}' at {hit.distance:0.00} m)";
            if (what == s_last)
                return;
            s_last = what;
            var cam = Camera.main;
            Plugin.Logger.LogInfo($"[Scan] {what}; range {__instance.interactionDetectionRange:0.0} m; camera " +
                                  $"{(cam != null ? $"{cam.transform.position} looking {cam.transform.forward}" : "-")}");
        }
        catch { }
    }
}
