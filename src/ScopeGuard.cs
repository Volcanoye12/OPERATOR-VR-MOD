using System;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace OperatorVR;

/// <summary>
/// A sight's picture went black several times, each for a reason found one sight at a time: the gun aiming through a
/// hidden sight (the red dot behind a flipped magnifier), the gun stuck on its other aiming point (Elcan), a scope
/// camera switched off. Rather than testing every sight, while you aim this checks the gun in hand every half second:
/// <list type="bullet">
/// <item>the sight the game aims through (WeaponV3.ADS_Fixed.ads) is hidden: press the game's sight switch (CapsLock,
/// one frame), up to 3 times per aim;</item>
/// <item>a scope whose own ADS is that sight (ScopeInputSetter / RealisticScopeEffectV2) has its cameras off two checks
/// running: switch them on with the game's own SetCamsEnabled.</item>
/// </list>
/// Each fix is logged. (ExtraCameras also keeps any texture camera belonging to a gun or sight running.)
/// Rendering.ScopeGuard.
/// </summary>
public class ScopeGuard : MonoBehaviour
{
    public ScopeGuard(IntPtr ptr) : base(ptr) { }

    private PlayerNetworking _me;
    private float _nextFind, _nextCheck;
    private bool _aimWas;
    private int _switchTries;
    private IntPtr _camsOffSeen;
    private int _releaseStage;   // 2: release CapsLock this frame, 1: clear the "ours" flag next frame

    private void Update()
    {
        using var __t = ModTime.Measure("ScopeGuard");
        // Finish our own sight-switch press: up one frame after down, then let the flag go a frame later
        if (_releaseStage == 2)
        {
            TouchKeys.PressKey(KeyCode.CapsLock, false);
            _releaseStage = 1;
            return;
        }
        if (_releaseStage == 1)
        {
            _releaseStage = 0;
            PadMacros.s_ourSightSwitch = false;
        }
        if (!OpenXRBootstrap.IsRunning || !Plugin.ScopeGuard.Value)
            return;
        try
        {
            if (_me == null && Time.unscaledTime >= _nextFind)
            {
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
            }
            if (_me == null)
                return;
            var aiming = _me.isADS;
            if (aiming && !_aimWas)
            {
                _switchTries = 0;
                _camsOffSeen = IntPtr.Zero;
                _nextCheck = Time.unscaledTime + 0.4f;   // once the game has settled on a sight
            }
            _aimWas = aiming;
            if (!aiming || Time.unscaledTime < _nextCheck)
                return;
            _nextCheck = Time.unscaledTime + 0.5f;
            Check();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[ScopeGuard] {e.GetType().Name}: {e.Message}");
            _nextCheck = Time.unscaledTime + 10f;
        }
    }

    private void Check()
    {
        var weapon = _me.activeWeapon;
        if (weapon == null)
            return;
        var w = weapon.GetComponent<WeaponV3>();
        var fixedAds = w != null ? w.ADS_Fixed : null;
        // Grenades (and anything else without sights) have no aiming point: the guard pressed the sight switch 3 times
        // with a frag, flashbang or 9-bang in hand ("hidden sight 'none'"), and the scope was odd back on the rifle
        // (2026-11-04). Only guns with sights are checked
        if (fixedAds == null || weapon.GetComponentInChildren<ADS>(true) == null)
            return;
        var current = fixedAds.ads;

        // 1. Aiming through a sight that's hidden or switched off
        if (current == null || !current.gameObject.activeInHierarchy)
        {
            if (_switchTries++ < 3 && !PadMacros.s_ourSightSwitch)
            {
                PadMacros.s_ourSightSwitch = true;
                TouchKeys.PressKey(KeyCode.CapsLock, true);
                _releaseStage = 2;
                Plugin.Logger.LogInfo($"[ScopeGuard] '{weapon.name}' was aiming through a hidden sight " +
                                      $"('{(current != null ? current.name : "none")}'): switching sights (try {_switchTries})");
            }
            return;
        }

        // 2. The scope we're looking through has its cameras off
        foreach (var s in weapon.GetComponentsInChildren<ScopeInputSetter>(false))
        {
            if (s == null || s.ads == null || s.ads.Pointer != current.Pointer || s.camEnabled)
                continue;
            if (CamsOffTwice(s.Pointer))
            {
                s.SetCamsEnabled();
                Plugin.Logger.LogInfo($"[ScopeGuard] '{s.transform.parent?.name}' camera was off while aimed through: switched on");
            }
            return;
        }
        foreach (var r in weapon.GetComponentsInChildren<RealisticScopeEffectV2>(false))
        {
            if (r == null || r.ads == null || r.ads.Pointer != current.Pointer || r.camEnabled)
                continue;
            if (CamsOffTwice(r.Pointer))
            {
                r.SetCamsEnabled();
                Plugin.Logger.LogInfo($"[ScopeGuard] '{r.name}' camera was off while aimed through: switched on");
            }
            return;
        }
        _camsOffSeen = IntPtr.Zero;
    }

    // Off on two checks in a row (half a second apart): not just the moment the game switches sights
    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private bool CamsOffTwice(IntPtr scope)
    {
        if (_camsOffSeen == scope)
            return true;
        _camsOffSeen = scope;
        return false;
    }
}
