using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace OperatorVR;

/// <summary>
/// The magnifier's view was blurry in VR. The game's scopes draw at low resolution unless it thinks you're aiming
/// through them (RealisticScopeEffectV2 / ScopeInputSetter.RenderLowResWhenUnADS) and lay a 'ScopeBlur' object over
/// the lens; a magnifier flipped by us (FlipSights.isFlipped set directly, since its own key never reached it) may not
/// get switched to its sharp state. While VR runs, the player's scopes are kept at full resolution and their blur
/// object off. Rendering.SharpScopes.
/// </summary>
public class ScopeSharp : MonoBehaviour
{
    public ScopeSharp(IntPtr ptr) : base(ptr) { }

    private float _nextScan;
    private readonly HashSet<string> _logged = new();

    // The game's scope depth of field (GameManager.scopeDOFEnabled, its scopeDOFVolume and the ScopeDOF custom pass)
    // keeps a circle in the middle of the flat screen sharp while aiming through a scope and blurs the rest. In VR
    // the scope isn't where that circle is in each eye, so the magnifier itself came out blurry: off while VR runs
    // Not only the scope's: aiming down a red dot (the magnifier sits behind one) switched on the red-dot blur
    // (redDotDOFEnabled + the RedDotDOF custom pass) and the ADS volumes, so the magnifier was sharp at the hip and
    // blurry when aimed. All of the game's depth of field is off while VR runs: the flags, the volumes, the passes
    private bool _dofOff, _wasDof, _wasRedDot, _wasScope;
    private float _nextPassSearch;
    private GameManager _gm;
    private readonly List<(UnityEngine.Rendering.Volume v, bool enabled)> _dofVolumes = new();
    private readonly List<CustomPass> _dofPasses = new();

    private void ScopeDofOff()
    {
        if (_gm == null)
            foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<GameManager>(), FindObjectsSortMode.None))
            {
                _gm = o.TryCast<GameManager>();
                if (_gm != null)
                    break;
            }
        if (_gm != null)
        {
            if (!_dofOff)
            {
                _dofOff = true;
                _wasDof = _gm.dofEnabled;
                _wasRedDot = _gm.redDotDOFEnabled;
                _wasScope = _gm.scopeDOFEnabled;
                _dofVolumes.Clear();
                foreach (var v in new[] { _gm.baseDOFVolume, _gm.adsDOFVolume, _gm.adsIronsightDOFVolume, _gm.scopeDOFVolume })
                    if (v != null)
                        _dofVolumes.Add((v, v.enabled));
                Plugin.Logger.LogInfo($"[Scope] Depth of field off in VR (was: general {_wasDof}, red dot {_wasRedDot}, scope {_wasScope}; " +
                                      $"{_dofVolumes.Count} volumes)");
            }
            _gm.dofEnabled = false;
            _gm.redDotDOFEnabled = false;
            _gm.scopeDOFEnabled = false;
            foreach (var (v, _) in _dofVolumes)
                if (v != null && v.enabled)
                    v.enabled = false;
        }
        // The custom passes doing the blur, wherever they are. Searched every 5 s (they come with the scene): the
        // whole scope check every half second was ~0.45 ms a frame
        if (Time.unscaledTime < _nextPassSearch)
            return;
        _nextPassSearch = Time.unscaledTime + 5f;
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<CustomPassVolume>(), FindObjectsSortMode.None))
        {
            var cv = o.TryCast<CustomPassVolume>();
            if (cv == null || cv.customPasses == null)
                continue;
            foreach (var p in cv.customPasses)
            {
                if (p == null || !p.enabled || (p.TryCast<ScopeDOF>() == null && p.TryCast<RedDotDOF>() == null))
                    continue;
                p.enabled = false;
                if (!_dofPasses.Exists(x => x.Pointer == p.Pointer))
                    _dofPasses.Add(p);
            }
        }
    }

    // The magnifier (and other 'Ultimate Scopes' optics: UltimateScopes.ScopeController) darkens the lens with a
    // realistic scope shadow (shader keyword _USESCOPESHADOW, from exit pupil / eye relief / the eye's direction to
    // Camera.main every LateUpdate). In VR the head never sits exactly on the scope's axis and each eye is half the
    // IPD off it, so the view went dark now and then. The keyword is turned off on the player's scope materials while
    // VR runs (checked every half second, since SetShaderConfigValues turns it back on) and restored when VR stops
    private const string ScopeShadow = "_USESCOPESHADOW";
    private readonly List<Material> _shadowOff = new();

    private void ScopeShadowOff()
    {
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<UltimateScopes.ScopeController>(), FindObjectsSortMode.None))
        {
            var sc = o.TryCast<UltimateScopes.ScopeController>();
            if (sc == null || !sc.transform.root.name.Contains("MyPlayer"))
                continue;
            ShadowOff(sc._scopeMaterial, sc.name);
            if (sc.LensRenderer != null)
                foreach (var m in sc.LensRenderer.sharedMaterials)
                    ShadowOff(m, sc.name);
        }
    }

    private void ShadowOff(Material m, string scope)
    {
        if (m == null || !m.IsKeywordEnabled(ScopeShadow))
            return;
        m.DisableKeyword(ScopeShadow);
        if (!_shadowOff.Exists(x => x.Pointer == m.Pointer))
        {
            _shadowOff.Add(m);
            Plugin.Logger.LogInfo($"[Scope] '{scope}': scope shadow off in VR (material '{m.name}')");
        }
    }

    private void ScopeShadowBack()
    {
        try
        {
            foreach (var m in _shadowOff)
                if (m != null) m.EnableKeyword(ScopeShadow);
        }
        catch { }
        _shadowOff.Clear();
    }

    private void ScopeDofBack()
    {
        ScopeShadowBack();
        if (!_dofOff)
            return;
        _dofOff = false;
        try
        {
            if (_gm != null)
            {
                _gm.dofEnabled = _wasDof;
                _gm.redDotDOFEnabled = _wasRedDot;
                _gm.scopeDOFEnabled = _wasScope;
            }
            foreach (var (v, enabled) in _dofVolumes)
                if (v != null) v.enabled = enabled;
            foreach (var p in _dofPasses)
                if (p != null) p.enabled = true;
        }
        catch { }
        _dofVolumes.Clear();
        _dofPasses.Clear();
        Plugin.Logger.LogInfo("[Scope] Depth of field back as it was");
    }

    // The Elcan SpecterDR 1x-4x showed its red reticle over a black picture when aimed through. Each time you aim
    // (up to 12 times a session), log every ScopeInputSetter on the gun in hand: whether the game has its cameras on,
    // its ADS's state, the Ultimate Scopes controller (effect on, magnification, its camera on and what it draws into)
    private bool _adsWas;
    private int _scopeLogs;
    private PlayerNetworking _me;

    private void LogScopesOnAim()
    {
        if (_me == null && Time.unscaledTime >= _nextMeFind)
        {
            _nextMeFind = Time.unscaledTime + 2f;
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
        var ads = false;
        try { ads = _me != null && _me.isADS; } catch { }
        var started = ads && !_adsWas;
        _adsWas = ads;
        if (!started || _scopeLogs >= 12 || _me.activeWeapon == null)
            return;
        _aimLogAt = Time.unscaledTime + 0.5f;   // once the aim has settled
    }

    private float _aimLogAt = -1f, _nextMeFind;

    private void LogScopesNow()
    {
        if (_aimLogAt < 0f || Time.unscaledTime < _aimLogAt)
            return;
        _aimLogAt = -1f;
        if (_me == null || _me.activeWeapon == null)
            return;
        _scopeLogs++;
        var parts = new List<string>();
        foreach (var s in _me.activeWeapon.GetComponentsInChildren<ScopeInputSetter>(false))
        {
            if (s == null)
                continue;
            var line = $"'{s.transform.parent?.name}/{s.name}': cams {(s.camEnabled ? "on" : "OFF")}, low res {s.UsingLowRes}";
            try { if (s.ads != null) line += $", ADS in {s.ads.IsInADS} enabled {s.ads._enabled}"; } catch { }
            var sc = s.ScopeCamera;
            if (sc != null)
            {
                line += $", effect {(sc.effectActive ? "on" : "OFF")}, magnification {sc.CurrentMagnification:0.0}";
                var cam = sc.DualRenderCamera;
                if (cam != null)
                    line += $", camera '{cam.name}' {(cam.enabled ? "on" : "OFF")}{(cam.gameObject.activeInHierarchy ? "" : " (inactive)")} into " +
                            $"{(cam.targetTexture != null ? $"{cam.targetTexture.width}x{cam.targetTexture.height}" : "nothing")}";
                if (sc.LensRenderer != null)
                    line += $", lens {(sc.LensRenderer.enabled ? "on" : "OFF")} material '{sc.LensRenderer.sharedMaterial?.name}'";
            }
            parts.Add(line);
        }
        Plugin.Logger.LogInfo($"[Scope] Aiming with '{_me.activeWeapon.name}': {(parts.Count == 0 ? "no magnified scope on it" : string.Join(" | ", parts))}");
    }

    // A flip-up magnifier flipped aside kept showing its bright magnified picture in the corner of your eye ("when I
    // flip it down it's still on, it catches my eye", 2026-11-03). Its lens isn't drawn while it's aside
    // (Rendering.DarkMagnifierAside), and is back the moment it's flipped up
    private readonly List<(FlipSights flip, Renderer lens)> _magnifiers = new();
    private readonly Dictionary<IntPtr, Renderer> _lensesOff = new();
    private int _lensLogs;

    private void FindMagnifierLenses()
    {
        _magnifiers.Clear();
        if (!Plugin.DarkMagnifierAside.Value)
            return;
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<FlipSights>(), FindObjectsSortMode.None))
        {
            var f = o.TryCast<FlipSights>();
            if (f == null || !f.transform.root.name.Contains("MyPlayer"))
                continue;
            var sc = f.GetComponentInChildren<UltimateScopes.ScopeController>(true);
            var lens = sc != null ? sc.LensRenderer : null;
            if (lens == null)
            {
                if (_lensLogs++ < 6)
                    Plugin.Logger.LogInfo($"[Scope] Magnifier '{f.name}': no lens found under it");
                continue;
            }
            _magnifiers.Add((f, lens));
        }
    }

    private void DarkWhenFlippedAside()
    {
        foreach (var (f, lens) in _magnifiers)
        {
            if (f == null || lens == null)
                continue;
            var off = !f.isFlipped;
            // (A frozen picture while up is MagnifierLive's job: re-setting camEnabled here every frame didn't keep the
            // camera drawing)
            if (lens.forceRenderingOff == off)
                continue;
            lens.forceRenderingOff = off;
            if (off)
                _lensesOff[lens.Pointer] = lens;
            else
                _lensesOff.Remove(lens.Pointer);
            if (_lensLogs++ < 12)
                Plugin.Logger.LogInfo($"[Scope] Magnifier '{f.name}' {(off ? "aside: lens dark" : "up: lens on")}");
        }
    }

    private void LensesBack()
    {
        if (_lensesOff.Count == 0)
            return;
        foreach (var lens in _lensesOff.Values)
            try { if (lens != null) lens.forceRenderingOff = false; } catch { }
        _lensesOff.Clear();
        _magnifiers.Clear();
    }

    private void Update()
    {
        using var __t = ModTime.Measure("ScopeSharp");
        if (OpenXRBootstrap.IsRunning)
            try
            {
                LogScopesOnAim();
                LogScopesNow();
            }
            catch { }
        if (!OpenXRBootstrap.IsRunning || !Plugin.SharpScopes.Value)
        {
            ScopeDofBack();
            LensesBack();
            return;
        }
        try { DarkWhenFlippedAside(); }
        catch { }
        if (Time.unscaledTime < _nextScan)
            return;
        _nextScan = Time.unscaledTime + 1f;
        try { FindMagnifierLenses(); }
        catch (Exception e) { Plugin.Logger.LogError($"[Scope] magnifier lens: {e.GetType().Name}: {e.Message}"); }
        try { ScopeDofOff(); }
        catch (Exception e) { Plugin.Logger.LogError($"[Scope] depth of field: {e.GetType().Name}: {e.Message}"); }
        try { ScopeShadowOff(); }
        catch (Exception e) { Plugin.Logger.LogError($"[Scope] scope shadow: {e.GetType().Name}: {e.Message}"); }
        try
        {
            // Game-wide switches (static): low resolution whenever the game doesn't think you're aiming through it
            if (RealisticScopeEffectV2.RenderLowResWhenUnADS || ScopeInputSetter.RenderLowResWhenUnADS)
            {
                RealisticScopeEffectV2.RenderLowResWhenUnADS = false;
                ScopeInputSetter.RenderLowResWhenUnADS = false;
                Plugin.Logger.LogInfo("[Scope] Scopes no longer drop to low resolution when not aimed (VR)");
            }
            foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<RealisticScopeEffectV2>(), FindObjectsSortMode.None))
            {
                var s = o.TryCast<RealisticScopeEffectV2>();
                if (s == null || !s.isActiveAndEnabled || !s.transform.root.name.Contains("MyPlayer"))
                    continue;
                if (s.UsingLowRes)
                    s.SetHighRes();
                if (s.ScopeBlur != null && s.ScopeBlur.activeSelf)
                    s.ScopeBlur.SetActive(false);
                if (_logged.Add("fx:" + s.name))
                    Plugin.Logger.LogInfo($"[Scope] '{s.name}' kept sharp in VR (full resolution, no blur overlay)");
            }
            // (ScopeInputSetter.SetHighRes() was also called on the magnifier's 'Dual Render Scope Effect' every half
            // second; after that the magnifier no longer flipped visibly, though isFlipped toggled. Left alone now)
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Scope] {e.GetType().Name}: {e.Message}");
            _nextScan = Time.unscaledTime + 10f;
        }
    }
}
