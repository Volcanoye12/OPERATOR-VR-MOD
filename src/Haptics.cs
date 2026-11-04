using System;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.XR;

namespace OperatorVR;

/// <summary>
/// Controller vibration with the Touch controllers (Controls.Haptics):
///   - each shot: a kick in the right hand (and a lighter one in the left, Controls.HapticsBothHands). A shot is a round
///     leaving the gun in hand: its magazine plus chamber count going down with the same magazine in (a reload swaps
///     the magazine; chambering a round moves one from the magazine to the chamber and doesn't change the total)
///   - short buzzes as feedback (TouchKeys): the virtual stock on/off, a double tap recognised, Interact sent.
/// Added 2026-11-04: most of a day's confusion came from not knowing what state things were in.
/// </summary>
public class Haptics : MonoBehaviour
{
    public Haptics(IntPtr ptr) : base(ptr) { }

    private static bool s_logged, s_failedLogged;

    /// <summary>A vibration on one controller: strength 0..1, for this many seconds.</summary>
    internal static void Pulse(XRNode hand, float strength, float seconds)
    {
        if (!Plugin.Haptics.Value || !OpenXRBootstrap.IsRunning || !Plugin.TouchControls.Value)
            return;
        try
        {
            var d = InputDevices.GetDeviceAtXRNode(hand);
            if (!d.isValid)
                return;
            var ok = d.SendHapticImpulse(0u, Mathf.Clamp01(strength), Mathf.Max(0.005f, seconds));
            if (ok && !s_logged)
            {
                s_logged = true;
                Plugin.Logger.LogInfo($"[Haptics] Controller vibration works ({hand})");
            }
            else if (!ok && !s_failedLogged)
            {
                s_failedLogged = true;
                Plugin.Logger.LogWarning($"[Haptics] The {hand} controller didn't take a vibration (not supported by this runtime?)");
            }
        }
        catch (Exception e)
        {
            if (!s_failedLogged)
            {
                s_failedLogged = true;
                Plugin.Logger.LogWarning($"[Haptics] vibration failed: {e.GetType().Name}: {e.Message}");
            }
        }
    }

    // Shot detection: the gun in hand, its magazine, and rounds left (magazine + chamber) last frame
    private PlayerNetworking _me;
    private float _nextFind;
    private IntPtr _weapon, _mag;
    private int _rounds = -1;
    private int _shotLogs;

    // "Laser on" / "Laser off" in the radio voice (Controls.LaserVoice, 2026-11-04): the gun's Laser parts
    // (lightState 0 = off), found again when the gun in hand changes, checked every frame
    private IntPtr _laserWeapon;
    private Laser[] _lasers = Array.Empty<Laser>();
    private int _laserState = -1;
    private int _laserLogs;

    // The gun's lasers aren't under it (GetComponentsInChildren found none, 2026-11-04: "No laser on the gun in hand"
    // every time): the game lists them on the gun (WeaponV3.Lasers, GameObjects) and each Laser script points back at
    // its gun (Laser.weapon). Both are looked at, again every 2 s (attachments come and go on the bench)
    private float _nextLaserScan;
    private int _laserObjects;
    private bool _laserListed;
    private WeaponV3 _laserGun;

    private void WatchLaser(WeaponV3 weapon)
    {
        if (weapon.Pointer != _laserWeapon || Time.unscaledTime >= _nextLaserScan)
        {
            var newGun = weapon.Pointer != _laserWeapon;
            _laserWeapon = weapon.Pointer;
            _laserGun = weapon;
            _nextLaserScan = Time.unscaledTime + 2f;
            var list = new System.Collections.Generic.List<Laser>();
            foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<Laser>(), FindObjectsSortMode.None))
            {
                var l = o.TryCast<Laser>();
                if (l != null && l.weapon != null && l.weapon.Pointer == weapon.Pointer)
                    list.Add(l);
            }
            _lasers = list.ToArray();
            try { _laserObjects = weapon.Lasers != null ? weapon.Lasers.Count : 0; } catch { _laserObjects = 0; }
            if (newGun)
            {
                _laserState = -1;   // a new gun: learn its state without speaking
                _laserListed = false;
            }
            if (!_laserListed && (_lasers.Length > 0 || _laserObjects > 0))
            {
                _laserListed = true;
                var names = new System.Collections.Generic.List<string>();
                try { foreach (var g in weapon.Lasers) if (g != null) names.Add($"{g.name} (active {g.activeSelf})"); } catch { }
                Plugin.Logger.LogInfo($"[Voice] '{weapon.name}': {_lasers.Length} Laser script(s), lightState " +
                                      $"{string.Join("/", Array.ConvertAll(_lasers, l => l.lightState.ToString()))}; laser list: {string.Join(", ", names)}");
            }
        }
        var state = 0;
        foreach (var l in _lasers)
            if (l != null && l.lightState != 0)
            {
                state = l.lightState;
                break;
            }
        // No Laser script: the gun's laser list, on when one of them is switched on
        if (_lasers.Length == 0 && _laserObjects > 0)
            try { foreach (var g in weapon.Lasers) if (g != null && g.activeSelf) { state = 1; break; } } catch { }
        if (state == _laserState)
            return;
        if (_laserState >= 0 && (state == 0) != (_laserState == 0))
            VoiceBridge.Laser(state != 0);
        if (_laserLogs++ < 10)
            Plugin.Logger.LogInfo($"[Voice] Laser state {_laserState} -> {state} ({_lasers.Length} laser part(s) on the gun)");
        _laserState = state;
    }

    // Spoken "activate laser" / "turn off laser" (VoiceCommands): taps the game's laser toggle (LasersToggle, mouse
    // Forward = Mouse4, which acts on release) if the laser isn't already that way; already so, the callout is said again
    private bool _laserKeyDown;

    private void LaserCommand()
    {
        var cmd = VoiceBridge.TakeLaserCommand();
        if (cmd == 0)
            return;
        var wantOn = cmd == 1;
        // Can't tell the laser's state: press the toggle anyway (the game knows whether there's a laser)
        var known = _lasers.Length > 0 || _laserObjects > 0;
        if (known && (_laserState > 0) == wantOn)
        {
            VoiceBridge.Laser(wantOn);
            return;
        }
        TouchKeys.PressKey(KeyCode.Mouse4, true);
        _laserKeyDown = true;
        Plugin.Logger.LogInfo($"[VoiceCmd] Laser {(wantOn ? "on" : "off")}: pressing the laser toggle" +
                              (known ? "" : " (laser state unknown on this gun)"));
        if (!known)
            VoiceBridge.Laser(wantOn);
    }

    private void Update()
    {
        using var __t = ModTime.Measure("Haptics");
        // Let go of the laser toggle pressed last frame (a tap)
        if (_laserKeyDown)
        {
            _laserKeyDown = false;
            TouchKeys.PressKey(KeyCode.Mouse4, false);
        }
        // (The laser callout rides on the same watch, so this runs with vibration off too; Pulse checks Haptics)
        if (!OpenXRBootstrap.IsRunning || !Plugin.TouchControls.Value)
        {
            _rounds = -1;
            return;
        }
        try
        {
            if (_me == null)
            {
                if (Time.unscaledTime < _nextFind)
                    return;
                _nextFind = Time.unscaledTime + 2f;
                foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<PlayerNetworking>(), FindObjectsSortMode.None))
                {
                    var p = o.TryCast<PlayerNetworking>();
                    if (p != null && p.gameObject.name.Contains("MyPlayer"))
                    {
                        _me = p;
                        break;
                    }
                }
                if (_me == null)
                    return;
            }
            var weapon = _me.activeWeapon != null ? _me.activeWeapon.GetComponent<WeaponV3>() : null;
            var mag = weapon != null && weapon.gunFunction != null ? weapon.gunFunction.currentMagazine : null;
            if (weapon == null || mag == null)
            {
                _rounds = -1;
                return;
            }
            var rounds = mag.ammoAmount + (weapon.roundInChamber ? 1 : 0);
            var same = weapon.Pointer == _weapon && mag.Pointer == _mag && _rounds >= 0;
            if (same && rounds < _rounds && Plugin.HapticsShot.Value > 0f)
            {
                var k = Plugin.HapticsShot.Value;
                Pulse(XRNode.RightHand, k, 0.06f);
                if (Plugin.HapticsBothHands.Value)
                    Pulse(XRNode.LeftHand, k * 0.6f, 0.05f);
                if (_shotLogs++ < 3)
                    Plugin.Logger.LogInfo($"[Haptics] Shot: {_rounds} -> {rounds} rounds, controller kick");
            }
            _weapon = weapon.Pointer;
            _mag = mag.Pointer;
            _rounds = rounds;
            WatchLaser(weapon);
            LaserCommand();
        }
        catch
        {
            _me = null;
            _rounds = -1;
            _nextFind = Time.unscaledTime + 5f;
        }
    }
}
