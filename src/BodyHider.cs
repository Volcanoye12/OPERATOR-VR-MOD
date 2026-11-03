using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;

namespace OperatorVR;

/// <summary>
/// In VR the player's own body (helmet, vest, backpack, legs...) gets in the way. While VR runs this hides the local
/// player's body and gear except the arms, hands and gloves, anything hanging off the arms (the GPS on the wrist),
/// the weapon in use and weapon parts, the tablet, and scope/NVG view effects. Hidden meshes are drawn shadows-only
/// (invisible; the game still has them). Everything goes back when VR stops. Rendering.HideBody.
/// </summary>
public class BodyHider : MonoBehaviour
{
    public BodyHider(IntPtr ptr) : base(ptr) { }

    // Bone names (UE-style rig seen in the log: root/pelvis/spine_01..03/clavicle_l/upperarm_l/lowerarm_l)
    // Gear hanging off these is kept: the forearms and hands (wrist GPS, the gun, a magazine in a hand). It was the
    // clavicles, but the tablet still showed beside the right hand: gear on the upper arm/shoulder is hidden now
    private static readonly string[] ArmBones = { "lowerarm_l", "lowerarm_r" };
    // Kept wherever they hang, matched in the object's or a parent's name: the tablet and view effects ("NVG Effect"
    // hidden could break night vision). Weapon-like names (Magazine, Pistol, Light...) used to be kept too, but
    // that left the pouch magazines, holstered pistol, tourniquet and the like floating below you; the gun in your
    // hands is kept anyway (under the hands or the active weapon's parent), and so is a magazine once it's in a hand
    // ("ATAK Marker" objects are the map's own markers, drawn for the tablet's map: left alone)
    private static readonly string[] KeepNames = { "NVG", "ATAK Marker" };
    // The chest tablet (ATAK) shows only while the left hand is on it (the game puts the hand there when you open it).
    // Always shown, it floated beside the gun once the rest of the body was hidden and looked stuck to the right hand
    private static readonly string[] TabletNames = { "Tablet" };
    private readonly Dictionary<IntPtr, (Renderer r, ShadowCastingMode mode, bool shown)> _tablets = new();
    private Transform _leftHand;
    private bool _tabletErrorLogged, _tabletReported;
    // The character is split into skinned meshes (log: SK_Body, SK_Hands, SK_Legs, SK_Feet, SK_Arms_Full,
    // SK_Arms_Short, SK_Arms_Forearms, SK_Gloves2B, PantsD, AAA Pack 01): keep the arm/hand ones by name.
    // (Shrinking the body's head/thigh bones was tried first; that run came up upside down, so no bones are touched.)
    // The shirt (SKM_CRYE_Shirt_Rolled) covers the upper arms: hidden, both arms ended above the elbow, so it's kept
    // (its body shows too when you look down). Switching on SK_Arms_Full instead "looked terrible"
    private static readonly string[] KeepSkinned = { "Arm", "Hand", "Glove", "Sleeve", "Shirt" };

    private GameObject _player;
    private float _nextScan;
    private bool _wasRunning;
    private readonly Dictionary<IntPtr, (Renderer r, ShadowCastingMode mode)> _hidden = new();
    private bool _logged;
    private float _nextHandCheck;
    // The forearm bones themselves, for quick IsChildOf checks (comparing parent names cost an IL2CPP string each)
    private readonly List<Transform> _forearms = new();

    private void LateUpdate()
    {
        using var __t = ModTime.Measure("BodyHider");
        // Not on the customisation page: the whole character is shown there
        var running = OpenXRBootstrap.IsRunning && Plugin.HideBody.Value && !CustomisationScreen.Open;
        if (!running)
        {
            if (_wasRunning)
                RestoreAll();
            _wasRunning = false;
            return;
        }
        _wasRunning = true;

        // A hidden magazine taken out of its pouch into a hand (reloading) shows again straight away. Five times a
        // second: done every frame over ~200 hidden meshes it cost the main thread noticeably, and at ten times a
        // second the body hiding was still ~0.45 ms a frame
        if (Time.unscaledTime >= _nextHandCheck)
        {
            _nextHandCheck = Time.unscaledTime + 0.2f;
            try { ShowWhatMovedToHands(); }
            catch { }
        }
        try { UpdateTorso(); }
        catch { }
        try { UpdateTablets(); }
        catch (Exception e)
        {
            // It failed silently before (tablet found, never hidden): say why, once
            if (!_tabletErrorLogged)
            {
                _tabletErrorLogged = true;
                Plugin.Logger.LogError($"[Body] Tablet: {e.GetType().Name}: {e.Message}");
            }
        }

        if (Time.unscaledTime < _nextScan)
            return;
        _nextScan = Time.unscaledTime + 2f;
        try
        {
            Scan();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Body] {e.GetType().Name}: {e.Message}");
            _nextScan = Time.unscaledTime + 10f;
        }
    }

    // The shirt is kept for the upper arms, but its body comes with it. With the stick aiming steeply up or down the
    // game bends the character over, while the view stays at standing height (HeadTracking's steady eyes), so you
    // looked down on your own twisted torso ("my character all twisted", 2026-11-03). Past Head.HideTorsoPitch the
    // shirt is hidden too, back as soon as the aim comes level or you aim down the sights
    private static readonly string[] TorsoSkinned = { "Shirt" };
    private readonly Dictionary<IntPtr, (Renderer r, ShadowCastingMode mode)> _torso = new();
    private bool _torsoHidden;
    private int _torsoLogs;

    private void UpdateTorso()
    {
        var limit = Plugin.HideTorsoPitch.Value;
        var hide = limit > 0f && HeadTracking.EyeHeld >= limit;
        if (hide == _torsoHidden || _torso.Count == 0)
            return;
        _torsoHidden = hide;
        foreach (var (r, mode) in _torso.Values)
            try { if (r != null) r.shadowCastingMode = hide ? ShadowCastingMode.ShadowsOnly : mode; } catch { }
        if (_torsoLogs++ < 6)
            Plugin.Logger.LogInfo($"[Body] Aim {HeadTracking.EyeHeld:0}° {(hide ? "steep: torso hidden" : "back: torso shown")} ({_torso.Count} part(s))");
    }

    private void Scan()
    {
        var networking = FindLocalPlayer();
        if (networking == null)
            return;
        var player = networking.gameObject;
        if (_player == null || _player.Pointer != player.Pointer)
        {
            RestoreAll();
            _player = player;
            _logged = false;
        }

        // The weapon in use may hang off its own parent rather than the hand: keep it whatever the rig does
        var weaponParents = _weaponParents;
        weaponParents.Clear();
        try
        {
            if (networking.ActiveWeaponParent != null) weaponParents.Add(networking.ActiveWeaponParent);
            if (networking.ActiveSwitchWeaponParent != null) weaponParents.Add(networking.ActiveSwitchWeaponParent);
        }
        catch { }
        // The first-person rigs too ('AR-15 RIG' etc. under RIG_Parent): some weapons (the M79) are held there,
        // not under the weapon holder, and were hidden with the hands that hold them
        if (_rigParents.Count == 0)
            foreach (var t in player.GetComponentsInChildren<Transform>(true))
                if (t != null && t.name == "RIG_Parent")
                    _rigParents.Add(t);
        weaponParents.AddRange(_rigParents);

        // The main skeleton's left hand (for the tablet) and forearms (for gear moving into a hand)
        if (_leftHand == null || _forearms.Count == 0)
        {
            _forearms.Clear();
            foreach (var t in player.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || Array.IndexOf(ArmBones, t.name) < 0 || t.parent == null || !t.parent.name.StartsWith("upperarm_"))
                    continue;
                _forearms.Add(t);
                if (t.name == "lowerarm_l" && _leftHand == null)
                    for (var i = 0; i < t.childCount; i++)
                        if (t.GetChild(i).name == "hand_l")
                            _leftHand = t.GetChild(i);
            }
        }

        // The tablet by what it is: the game's Tablet component (and its ATAK screen parent). The name match alone
        // found nothing on the user's loadout ("tablet parts: 0"), so the tablet was hidden as vest gear and the
        // pad's tablet button showed nothing
        var tabletRoots = _tabletRoots;
        tabletRoots.Clear();
        foreach (var tb in player.GetComponentsInChildren<Tablet>(true))
        {
            if (tb == null)
                continue;
            tabletRoots.Add(tb.transform);
            try { if (tb.ATAK_ScreenParent != null) tabletRoots.Add(tb.ATAK_ScreenParent); } catch { }
        }
        if (tabletRoots.Count > 0 && _tabletRootsLogged.Add(tabletRoots[0].Pointer))
            Plugin.Logger.LogInfo($"[Body] Tablet found: {string.Join(", ", tabletRoots.ConvertAll(x => $"'{x.name}' @ {Parents(x, 4)}"))}");

        var hidden = new List<string>();
        var kept = new List<string>();
        var keptGear = new List<string>();
        foreach (var r in player.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null || _tablets.ContainsKey(r.Pointer))
                continue;
            var t = r.transform;
            var isTablet = !IsUnderAny(t, weaponParents) && !HasName(t, KeepNames) &&
                           (IsUnderAny(t, tabletRoots) || HasName(t, TabletNames));
            // Hidden earlier as gear (before the tablet was recognised): hand it over to the tablet's rules
            if (isTablet && _hidden.TryGetValue(r.Pointer, out var was))
            {
                _hidden.Remove(r.Pointer);
                r.shadowCastingMode = was.mode;
            }
            else if (_hidden.ContainsKey(r.Pointer) || r.shadowCastingMode == ShadowCastingMode.ShadowsOnly)
                continue;
            if (isTablet)
            {
                // A part that turns up later (the screen is a spawned clone) starts in the tablet's current state
                _tablets[r.Pointer] = (r, r.shadowCastingMode, _tabletsShown);
                if (!_tabletsShown)
                {
                    r.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                    r.forceRenderingOff = true;
                }
                continue;
            }
            if (IsUnder(t, ArmBones) || HasName(t, KeepNames) || IsUnderAny(t, weaponParents) || r.TryCast<LineRenderer>() != null)
            {
                // Listed with where it hangs, leaving out the gun's own parts
                if (r.TryCast<SkinnedMeshRenderer>() == null && !IsUnderAny(t, weaponParents))
                    keptGear.Add($"{r.name} @ {Parents(t, 4)}");
                continue;
            }
            if (r.TryCast<SkinnedMeshRenderer>() != null && NameHas(r.name, KeepSkinned))
            {
                kept.Add(r.name);
                if (NameHas(r.name, TorsoSkinned) && !_torso.ContainsKey(r.Pointer))
                    _torso[r.Pointer] = (r, r.shadowCastingMode);
                continue;
            }

            _hidden[r.Pointer] = (r, r.shadowCastingMode);
            r.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
            hidden.Add(r.name);
        }

        // The tablet's UI canvases (new ones too: the ATAK screen is a spawned clone), kept in step with the tablet
        foreach (var c in player.GetComponentsInChildren<Canvas>(true))
        {
            if (c == null || _tabletCanvases.ContainsKey(c.Pointer))
                continue;
            var ct = c.transform;
            if (IsUnderAny(ct, weaponParents) || HasName(ct, KeepNames) ||
                !(IsUnderAny(ct, tabletRoots) || HasName(ct, TabletNames)))
                continue;
            _tabletCanvases[c.Pointer] = (c, c.enabled);
            if (!_tabletsShown)
                c.enabled = false;
        }

        if (!_logged && (hidden.Count > 0 || kept.Count > 0))
        {
            _logged = true;
            Plugin.Logger.LogInfo($"[Body] Player '{player.name}': hid {hidden.Count} meshes ({string.Join(", ", hidden.GetRange(0, Math.Min(40, hidden.Count)))}{(hidden.Count > 40 ? ", ..." : "")}); " +
                                  $"kept body meshes: {string.Join(", ", kept)}; kept other things ({keptGear.Count}): " +
                                  $"{string.Join(", ", keptGear.GetRange(0, Math.Min(80, keptGear.Count)))}; " +
                                  $"tablet parts: {_tablets.Count}; weapon parents: " +
                                  $"{string.Join(", ", weaponParents.ConvertAll(w => $"'{w.name}' @ {Parents(w, 4)}"))}");
        }
    }

    private readonly List<Transform> _weaponParents = new();
    private readonly List<Transform> _tabletRoots = new();
    private readonly HashSet<IntPtr> _tabletRootsLogged = new();
    private readonly List<Transform> _rigParents = new();

    private void UpdateTablets()
    {
        if (_tablets.Count == 0)
            return;
        // The tablet's nearest part to the hand decides; a little give so it doesn't flicker at the edge.
        // (Bounds.ClosestPoint seemed to fail here: plain distance to each part's position)
        var nearest = float.MaxValue;
        foreach (var kv in _tablets)
            if (kv.Value.r != null && _leftHand != null)
                nearest = Math.Min(nearest, Vector3.Distance(kv.Value.r.bounds.center, _leftHand.position));
        var anyShown = false;
        foreach (var kv in _tablets)
            anyShown |= kv.Value.shown;
        // The tablet sits on the vest only ~0.3 m from the left hand on the gun's handguard, which counted as "on
        // it" at 0.35 m: only a hand right on it shows it
        var show = anyShown ? nearest < 0.2f : nearest < 0.14f;
        // Out on purpose (M held): the game raises it to the view rather than the hand going to the vest, so the
        // hand check alone kept it hidden when the pad's tablet button was held
        bool tabletOut;
        try { tabletOut = PadMacros.TabletOut; } catch { tabletOut = false; }
        show |= tabletOut;
        // Second try still left it on the chest with nothing logged: say once what was found and how far it is
        if (!_tabletReported && _leftHand != null)
        {
            _tabletReported = true;
            var parts = new List<string>();
            foreach (var kv in _tablets)
                if (kv.Value.r != null)
                    parts.Add($"{kv.Value.r.name} ({kv.Value.r.GetIl2CppType().Name}, {Vector3.Distance(kv.Value.r.bounds.center, _leftHand.position):0.00} m, " +
                              $"enabled {kv.Value.r.enabled}, {kv.Value.r.shadowCastingMode}) @ {Parents(kv.Value.r.transform, 4)}");
            Plugin.Logger.LogInfo($"[Body] Tablet parts: {string.Join("; ", parts)}; nearest {nearest:0.00} m -> {(show ? "show" : "hide")}");
        }

        List<IntPtr> changed = null;
        foreach (var kv in _tablets)
            if (kv.Value.shown != show)
                (changed ??= new List<IntPtr>()).Add(kv.Key);
        if (changed == null)
            return;
        foreach (var p in changed)
        {
            var (r, mode, _) = _tablets[p];
            if (r != null)
            {
                r.shadowCastingMode = show ? mode : ShadowCastingMode.ShadowsOnly;
                // Its screen still showed with the rest hidden: make sure nothing draws it
                r.forceRenderingOff = !show;
            }
            _tablets[p] = (r, mode, show);
        }
        _tabletsShown = show;
        SetTabletCanvases(show);
        Plugin.Logger.LogInfo($"[Body] Tablet {(show ? "shown (left hand on it)" : "hidden")}, hand {nearest:0.00} m from it " +
                              $"({_tabletCanvases.Count} screen canvases)");
    }

    // UI canvases on the tablet (the ATAK map screen is UI, not a mesh): switched off with it
    private readonly Dictionary<IntPtr, (Canvas c, bool enabled)> _tabletCanvases = new();
    private bool _tabletsShown = true;

    private void SetTabletCanvases(bool show)
    {
        foreach (var (c, enabled) in _tabletCanvases.Values)
            try { if (c != null) c.enabled = show && enabled; } catch { }
    }

    private void ShowWhatMovedToHands()
    {
        if (_hidden.Count == 0)
            return;
        List<IntPtr> shown = null;
        foreach (var kv in _hidden)
        {
            var r = kv.Value.r;
            if (r == null)
                continue;
            var t = r.transform;
            if (IsChildOfAny(t, _forearms) || IsChildOfAny(t, _weaponParents))
            {
                r.shadowCastingMode = kv.Value.mode;
                (shown ??= new List<IntPtr>()).Add(kv.Key);
            }
        }
        if (shown != null)
            foreach (var p in shown)
                _hidden.Remove(p);
    }

    private static bool IsChildOfAny(Transform t, List<Transform> parents)
    {
        foreach (var p in parents)
            if (p != null && t.IsChildOf(p))
                return true;
        return false;
    }

    /// <summary>The local player's character: its name contains "MyPlayer" (seen in the log).</summary>
    private static PlayerNetworking FindLocalPlayer()
    {
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<PlayerNetworking>(), FindObjectsSortMode.None))
        {
            var p = o.TryCast<PlayerNetworking>();
            if (p != null && p.gameObject.name.Contains("MyPlayer"))
                return p;
        }
        return null;
    }

    private static bool IsUnderAny(Transform t, List<Transform> parents)
    {
        if (parents.Count == 0)
            return false;
        for (var p = t; p != null; p = p.parent)
            foreach (var q in parents)
                if (p.Pointer == q.Pointer)
                    return true;
        return false;
    }

    private static string Parents(Transform t, int count)
    {
        var names = new List<string>();
        for (var p = t.parent; p != null && names.Count < count; p = p.parent)
            names.Add(p.name);
        return string.Join(" < ", names);
    }

    private static bool NameHas(string name, string[] parts)
    {
        foreach (var part in parts)
            if (name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        return false;
    }

    private static bool IsUnder(Transform t, string[] names)
    {
        for (var p = t; p != null; p = p.parent)
            if (Array.IndexOf(names, p.name) >= 0)
                return true;
        return false;
    }

    private static bool HasName(Transform t, string[] parts)
    {
        for (var p = t; p != null; p = p.parent)
            if (NameHas(p.name, parts))
                return true;
        return false;
    }

    private void RestoreAll()
    {
        foreach (var (r, mode) in _torso.Values)
            try { if (r != null) r.shadowCastingMode = mode; } catch { }
        _torso.Clear();
        _torsoHidden = false;
        foreach (var (r, mode) in _hidden.Values)
            try { if (r != null) r.shadowCastingMode = mode; } catch { }
        if (_hidden.Count > 0)
            Plugin.Logger.LogInfo($"[Body] Body shown again ({_hidden.Count} meshes)");
        _hidden.Clear();
        foreach (var (r, mode, _) in _tablets.Values)
            try
            {
                if (r == null) continue;
                r.shadowCastingMode = mode;
                r.forceRenderingOff = false;
            }
            catch { }
        _tablets.Clear();
        SetTabletCanvases(true);
        _tabletCanvases.Clear();
        _tabletsShown = true;
        _leftHand = null;
        _forearms.Clear();
        _rigParents.Clear();
        _player = null;
    }
}
