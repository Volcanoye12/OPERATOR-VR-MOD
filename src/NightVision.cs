using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace OperatorVR;

/// <summary>
/// Night vision in VR showed as a small box of tubes floating in front of the view, with the night-vision picture only
/// inside the tubes. The game draws the picture on the goggles' lens meshes (NvgLensFollower.LensRenderers, masked by
/// NVGGrainCustomPass), and on a flat screen narrows the view (GameManager.NightVisionFOV) so the lenses fill it; the
/// headset's view can't be narrowed, and the goggles (under 'Cinemachine Camera/NVGs') follow the game's camera, not
/// the head. While the player's goggles are on in VR, the 'NVGs' object is moved under the rendering camera so it
/// follows the head, the lenses are enlarged (Rendering.NightVisionLensScale) to cover the view, and the housing
/// around them is hidden. All put back when the goggles go off or VR stops. Rendering.NightVisionFill.
/// </summary>
public class NightVision : MonoBehaviour
{
    public NightVision(IntPtr ptr) : base(ptr) { }

    private Transform _nvgs, _origParent;
    private Vector3 _origLocalPos;
    private Quaternion _origLocalRot;
    private readonly List<(Transform t, Vector3 scale)> _lenses = new();
    private readonly List<Renderer> _hidden = new();
    private bool _applied, _loggedOnce, _loggedMissing;

    // The reticle wasn't visible when aiming with night vision on (F5 02:41:36). Sights keep a separate night-vision
    // reticle (HWSReticleBrightness.ReticleRendererNVG) and brightness steps the pad can't reach (arrow keys). Each
    // time you aim with the goggles filled, log the gun's reticles once: which renderer shows, layer, and brightness
    private bool _adsWas;
    private int _reticleLogs;

    // The log then showed both of the EOTech's reticles on (day 'exps3_reticle (2)' layer 0, night-vision
    // 'exps3_reticle (1)' layer 16), brightness 8 of 15, yet no reticle when aiming. The night-vision picture is drawn
    // on the goggles' lens, nearest the eye, and the game puts the night-vision reticle over it in a later pass that
    // doesn't show it in VR. While the goggles are filled, the gun's night-vision reticles go on MenusOnTop's layer
    // (drawn after everything) and come back when the goggles go off
    private readonly System.Collections.Generic.Dictionary<IntPtr, GameObject> _reticlesUp = new();

    private void ReticlesOnTop()
    {
        if (MenusOnTop.Layer < 0 || _me == null || _me.activeWeapon == null)
            return;
        // A gun put away keeps its reticle as it was (drawn on top it could show through the body)
        var weapon = _me.activeWeapon.transform;
        List<IntPtr> away = null;
        foreach (var kv in _reticlesUp)
            if (kv.Value == null || !kv.Value.transform.IsChildOf(weapon))
                (away ??= new List<IntPtr>()).Add(kv.Key);
        if (away != null)
            foreach (var p in away)
            {
                try { if (_reticlesUp[p] != null) MenusOnTop.Release(_reticlesUp[p]); } catch { }
                _reticlesUp.Remove(p);
            }
        foreach (var h in _me.activeWeapon.GetComponentsInChildren<HWSReticleBrightness>(false))
        {
            var r = h != null ? h.ReticleRendererNVG : null;
            if (r == null || _reticlesUp.ContainsKey(r.gameObject.Pointer))
                continue;
            MenusOnTop.Put(r.gameObject);
            _reticlesUp[r.gameObject.Pointer] = r.gameObject;
            Plugin.Logger.LogInfo($"[NVG] Night-vision reticle '{r.name}' on '{h.name}' drawn on top while the goggles are on");
        }
    }

    // A faint double ring showed on the right of the right eye's view with the goggles filled. The gain control
    // (NVGAutogateCustomPass) meters and masks per tube, placing its circles from the lens centres as on one flat
    // screen (FillMeterCircles); with the lenses enlarged in VR they land off to one side. Its own debugDisableMask
    // switch (the path it takes anyway when no lens follower is active: "metering the whole screen") is set while
    // the goggles are filled, and put back after
    private readonly List<(NVGAutogateCustomPass pass, bool was)> _masksOff = new();

    private void TubeMaskOff()
    {
        if (_masksOff.Count > 0)
            return;
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<UnityEngine.Rendering.HighDefinition.CustomPassVolume>(), FindObjectsSortMode.None))
        {
            var v = o.TryCast<UnityEngine.Rendering.HighDefinition.CustomPassVolume>();
            if (v == null || v.customPasses == null)
                continue;
            foreach (var p in v.customPasses)
            {
                var a = p?.TryCast<NVGAutogateCustomPass>();
                if (a == null)
                    continue;
                _masksOff.Add((a, a.debugDisableMask));
                a.debugDisableMask = true;
            }
        }
        if (_masksOff.Count > 0)
            Plugin.Logger.LogInfo($"[NVG] Per-tube gain circles off ({_masksOff.Count} pass(es)): the whole view is metered");
    }

    private void TubeMaskBack()
    {
        if (_masksOff.Count == 0)
            return;
        foreach (var (pass, was) in _masksOff)
            try { if (pass != null) pass.debugDisableMask = was; } catch { }
        _masksOff.Clear();
    }

    private void ReticlesBack()
    {
        if (_reticlesUp.Count == 0)
            return;
        foreach (var go in _reticlesUp.Values)
            try { if (go != null) MenusOnTop.Release(go); } catch { }
        _reticlesUp.Clear();
    }

    private void LogReticles()
    {
        var ads = false;
        try { ads = _me != null && _me.isADS; } catch { }
        var started = ads && !_adsWas;
        _adsWas = ads;
        if (!started || _reticleLogs >= 6 || _me == null || _me.activeWeapon == null)
            return;
        _reticleLogs++;
        var parts = new List<string>();
        foreach (var h in _me.activeWeapon.GetComponentsInChildren<HWSReticleBrightness>(false))
        {
            if (h == null)
                continue;
            [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
            string R(MeshRenderer r) => r == null ? "none" :
                $"'{r.name}' {(r.gameObject.activeInHierarchy ? "active" : "inactive")} {(r.enabled ? "on" : "OFF")}{(r.forceRenderingOff ? " FORCED OFF" : "")} " +
                $"layer {r.gameObject.layer} shader '{(r.sharedMaterial != null && r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "-")}'";
            parts.Add($"sight '{h.name}': brightness {h.CurrentBrightnessSetting} (default {h.DefaultBrightnessSetting}, " +
                      $"{(h.reticleSettings != null ? h.reticleSettings.Length : 0)} steps); reticle {R(h.ReticleRenderer)}; night-vision reticle {R(h.ReticleRendererNVG)}");
        }
        Plugin.Logger.LogInfo($"[NVG] Aiming with night vision: {(parts.Count == 0 ? "no sight with a brightness control found" : string.Join(" | ", parts))}");
    }
    private float _nextCheck;
    private PlayerNetworking _me;

    private void Update()
    {
        using var __t = ModTime.Measure("NightVision");
        if (Time.unscaledTime < _nextCheck)
            return;
        _nextCheck = Time.unscaledTime + 0.25f;
        try
        {
            var want = OpenXRBootstrap.IsRunning && Plugin.NightVisionFill.Value && GogglesOn();
            if (want && !_applied)
            {
                Apply();
                // Searching every object for the goggles is costly: don't retry a failed search four times a second
                if (!_applied)
                    _nextCheck = Time.unscaledTime + 3f;
            }
            else if (!want && _applied)
                Undo("goggles off or VR stopped");
            else if (_applied && (_nvgs == null || Camera.main == null || _nvgs.parent == null || _nvgs.parent.Pointer != Camera.main.transform.Pointer))
            {
                // The camera changed (scene load): start again
                Undo("camera changed");
            }
            if (_applied)
            {
                LogReticles();
                ReticlesOnTop();
                TubeMaskOff();
            }
            else
            {
                ReticlesBack();
                TubeMaskBack();
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[NVG] {e.GetType().Name}: {e.Message}");
            try { Undo("error"); } catch { }
            _nextCheck = Time.unscaledTime + 10f;
        }
    }

    private bool GogglesOn()
    {
        if (_me == null)
            foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<PlayerNetworking>(), FindObjectsSortMode.None))
            {
                var p = o.TryCast<PlayerNetworking>();
                if (p != null && p.gameObject.name.Contains("MyPlayer"))
                {
                    _me = p;
                    break;
                }
            }
        return _me != null && _me.nvgsOn;
    }

    private void Apply()
    {
        var cam = Camera.main;
        if (cam == null)
            return;
        // The first-person goggles: 'NVGs' under the game's 'Cinemachine Camera'. (MainCameraSingleton.NVG, tried
        // first, is a spare 'NVG' under 'GAME CAMERA' with one switched-off spectator lens: nothing changed)
        _nvgs = FindGoggles();
        if (_nvgs == null)
        {
            if (!_loggedMissing)
                Plugin.Logger.LogWarning("[NVG] Goggles on, but no 'NVGs' under 'Cinemachine Camera' was found");
            _loggedMissing = true;
            return;
        }

        // The lenses that are showing: the game's own lens list where its followers are active, otherwise (the log had
        // "No active NvgLensFollower") the showing parts drawn with the goggles' lens shader ('RealisticNVG')
        _lenses.Clear();
        var lensPtrs = new HashSet<IntPtr>();
        foreach (var f in _nvgs.GetComponentsInChildren<NvgLensFollower>(true))
            if (f != null && f.LensRenderers != null)
                foreach (var r in f.LensRenderers)
                    if (r != null && r.gameObject.activeInHierarchy && lensPtrs.Add(r.transform.Pointer))
                        _lenses.Add((r.transform, r.transform.localScale));
        if (_lenses.Count == 0)
            foreach (var r in _nvgs.GetComponentsInChildren<Renderer>(false))
            {
                var m = r != null ? r.sharedMaterial : null;
                if (m != null && m.shader != null && m.shader.name.IndexOf("NVG", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    lensPtrs.Add(r.transform.Pointer))
                    _lenses.Add((r.transform, r.transform.localScale));
            }

        Log(cam);
        if (_lenses.Count == 0)
            return;

        _origParent = _nvgs.parent;
        _origLocalPos = _nvgs.localPosition;
        _origLocalRot = _nvgs.localRotation;
        // Same pose relative to the camera, but now under the camera that follows the head when drawing
        _nvgs.SetParent(cam.transform, true);

        var k = Mathf.Clamp(Plugin.NightVisionLensScale.Value, 1f, 30f);
        foreach (var (t, s) in _lenses)
            t.localScale = s * k;

        _hidden.Clear();
        foreach (var r in _nvgs.GetComponentsInChildren<Renderer>(false))
            if (r != null && !lensPtrs.Contains(r.transform.Pointer) && !r.forceRenderingOff)
            {
                r.forceRenderingOff = true;
                _hidden.Add(r);
            }
        _applied = true;
        Plugin.Logger.LogInfo($"[NVG] Night vision filled for VR: goggles follow the head, {_lenses.Count} lens(es) x{k:0.#}, {_hidden.Count} housing part(s) hidden");
    }

    private void Undo(string why)
    {
        if (!_applied)
            return;
        _applied = false;
        foreach (var (t, s) in _lenses)
            if (t != null)
                t.localScale = s;
        foreach (var r in _hidden)
            if (r != null)
                r.forceRenderingOff = false;
        if (_nvgs != null && _origParent != null)
        {
            _nvgs.SetParent(_origParent, false);
            _nvgs.localPosition = _origLocalPos;
            _nvgs.localRotation = _origLocalRot;
        }
        _lenses.Clear();
        _hidden.Clear();
        Plugin.Logger.LogInfo($"[NVG] Night vision back to the game's own ({why})");
    }

    private static Transform FindGoggles()
    {
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<Transform>(), FindObjectsSortMode.None))
        {
            var t = o.TryCast<Transform>();
            if (t != null && t.name == "NVGs" && t.parent != null && t.parent.name.StartsWith("Cinemachine Camera") &&
                t.gameObject.activeInHierarchy)
                return t;
        }
        return null;
    }

    private void Log(Camera cam)
    {
        if (_loggedOnce)
            return;
        _loggedOnce = true;
        var lines = new List<string>();
        // Every showing part of the goggles: which model is fitted, and which parts are lenses
        foreach (var r in _nvgs.GetComponentsInChildren<Renderer>(false))
        {
            if (r == null || lines.Count > 40)
                continue;
            var local = Quaternion.Inverse(cam.transform.rotation) * (r.bounds.center - cam.transform.position);
            var m = r.sharedMaterial;
            lines.Add($"part '{r.name}' @ '{(r.transform.parent != null ? r.transform.parent.name : "-")}' at ({local.x:+0.000;-0.000}, {local.y:+0.000;-0.000}, " +
                      $"{local.z:+0.000;-0.000}) m, size {r.bounds.size.x:0.000}x{r.bounds.size.y:0.000}, shader '{(m != null && m.shader != null ? m.shader.name : "-")}'");
        }
        foreach (var f in _nvgs.GetComponentsInChildren<NvgLensFollower>(true))
        {
            if (f == null)
                continue;
            lines.Add($"follower '{f.name}' active {f.isActiveAndEnabled}, {f.NumLenses} lenses, radius {f.LensRadius:0.000} m");
            if (f.LensRenderers != null)
                foreach (var r in f.LensRenderers)
                    if (r != null)
                    {
                        var local = Quaternion.Inverse(cam.transform.rotation) * (r.bounds.center - cam.transform.position);
                        lines.Add($"  lens '{r.name}' {(r.gameObject.activeInHierarchy ? "on" : "off")} at ({local.x:+0.000;-0.000}, {local.y:+0.000;-0.000}, " +
                                  $"{local.z:+0.000;-0.000}) m, size {r.bounds.size.x:0.000}x{r.bounds.size.y:0.000}, layer {r.gameObject.layer}, " +
                                  $"material '{(r.sharedMaterial != null ? r.sharedMaterial.name : "-")}' / '{(r.sharedMaterial != null && r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "-")}'");
                    }
        }
        Plugin.Logger.LogInfo($"[NVG] Goggles '{_nvgs.name}' under '{(_nvgs.parent != null ? _nvgs.parent.name : "-")}', camera near {cam.nearClipPlane:0.00} fov {cam.fieldOfView:0}; " +
                              (lines.Count == 0 ? "no lens followers found" : string.Join(" | ", lines)));
    }
}
