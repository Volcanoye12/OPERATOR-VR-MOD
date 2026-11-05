using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace OperatorVR;

/// <summary>
/// Turns off HDRP effects that don't belong in VR while it runs: depth of field (which also blurred
/// the floating menus) and motion blur. Restores them when VR stops.
/// </summary>
public class VREffects : MonoBehaviour
{
    public VREffects(IntPtr ptr) : base(ptr) { }

    private static readonly HashSet<string> Disabled = new() { "DepthOfField", "MotionBlur" };

    // Costly effects that multi-pass draws twice (once per eye); the woodland ran at ~40 fps with them on.
    // Only when Rendering.VRPerformance is on
    private static readonly HashSet<string> CostlyInVR = new()
    {
        "VolumetricClouds", "ScreenSpaceReflection", "ContactShadows", "GlobalIllumination",
    };

    // Sun glare in the headset (Rendering.SunGlare = false): bloom and screen-space lens flares
    private static readonly HashSet<string> GlareInVR = new() { "Bloom", "ScreenSpaceLensFlare" };

    // The game's hit and flashbang feedback ('HighPain', 'Pain Volume', 'Flash Volume'): their lens effects only run
    // while you're hurt or flashed, so they cost nothing otherwise and are kept (2026-11-04: "Yes, put them back")
    private static bool IsFeedbackVolume(string name) =>
        name.IndexOf("Pain", StringComparison.OrdinalIgnoreCase) >= 0 ||
        name.IndexOf("Flash", StringComparison.OrdinalIgnoreCase) >= 0 ||
        name.IndexOf("Suppress", StringComparison.OrdinalIgnoreCase) >= 0 ||
        name.IndexOf("Damage", StringComparison.OrdinalIgnoreCase) >= 0;

    // Camera-lens effects (Rendering.PlainLensEffects): a full-screen pass per eye each, and wrong in a headset
    private static readonly HashSet<string> LensInVR = new()
    {
        "ChromaticAberration", "FilmGrain", "Vignette", "LensDistortion", "PaniniProjection",
    };

    // Native pointer -> component, so each override is switched off (and back on) once
    private readonly Dictionary<IntPtr, VolumeComponent> _turnedOff = new();
    // Fog components whose volumetric part we turned off (the distance fog stays), with the original override
    private readonly Dictionary<IntPtr, (Fog fog, bool overrideState, bool value)> _volumetricFogOff = new();
    private float _nextScan;
    private bool _wasRunning;

    private void LateUpdate()
    {
        using var __t = ModTime.Measure("VREffects");
        var running = OpenXRBootstrap.IsRunning;
        if (!running)
        {
            if (_wasRunning)
                RestoreAll();
            _wasRunning = false;
            return;
        }
        _wasRunning = true;

        try { UpdateSunShadows(); }
        catch (Exception e) { Plugin.Logger.LogError($"[Effects] sun shadows: {e.GetType().Name}: {e.Message}"); _nextRoofCheck = Time.unscaledTime + 10f; }
        try { SunShadowsShared(); }
        catch (Exception e)
        {
            if (!_sharedFailed)
                Plugin.Logger.LogError($"[Effects] shared sun shadows: {e.GetType().Name}: {e.Message}");
            _sharedFailed = true;
        }

        if (Time.unscaledTime < _nextScan)
            return;
        // Menus and scene changes swap volumes in, so keep checking: every 3 s (each check searches the whole scene
        // several times over, and every second was a likely source of the occasional long frames)
        _nextScan = Time.unscaledTime + 3f;

        try { EnsureAoVolume(); }
        catch (Exception e) { Plugin.Logger.LogError($"[Effects] ambient occlusion: {e.GetType().Name}: {e.Message}"); }

        try
        {
            FixCamera();
            TrimScopeCameras();
            ApplyLodScale();
            ShadowsOff();
            SunGlareOff();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Effects] camera: {e.GetType().Name}: {e.Message}");
        }

        try
        {
            DisableNonStereoAddons();
            LogCustomPassesOncePerScene();
            ProbesOnce();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Effects] add-ons: {e.GetType().Name}: {e.Message}");
        }

        try
        {
            foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<Volume>(), FindObjectsSortMode.None))
            {
                var v = o.TryCast<Volume>();
                if (v == null || v.sharedProfile == null)
                    continue;
                foreach (var comp in v.sharedProfile.components)
                {
                    if (comp == null || !comp.active || _turnedOff.ContainsKey(comp.Pointer))
                        continue;
                    var type = comp.GetIl2CppType().Name;
                    if (type == "Fog" && Plugin.VRPerformance.Value)
                    {
                        VolumetricFogOff(comp, v.name);
                        continue;
                    }
                    if (type == "HDShadowSettings")
                    {
                        CapShadowDistance(comp, v.name);
                        continue;
                    }
                    if (!Disabled.Contains(type) && !(Plugin.VRPerformance.Value && CostlyInVR.Contains(type))
                        && !(!Plugin.SunGlare.Value && GlareInVR.Contains(type))
                        && !(Plugin.PlainLensEffects.Value && LensInVR.Contains(type) && !IsFeedbackVolume(v.name)))
                        continue;
                    comp.active = false;
                    _turnedOff[comp.Pointer] = comp;
                    Plugin.Logger.LogInfo($"[Effects] Turned off {type} in volume '{v.name}'");
                }
            }
            LogEffectsStillOn();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Effects] {e.GetType().Name}: {e.Message}");
            _nextScan = Time.unscaledTime + 30f;
        }
    }

    // Camera features with nothing left to do in VR, each costing a pass per eye. Motion vectors feed only TAA and
    // motion blur (both off in VR), yet the wind-animated trees and grass were still drawn into them every frame
    private static readonly FrameSettingsField[] UnusedInVR =
    {
        FrameSettingsField.MotionVectors,
        FrameSettingsField.ObjectMotionVectors,
        FrameSettingsField.TransparentsWriteMotionVector,
        FrameSettingsField.SSR,
        FrameSettingsField.ContactShadows,
        FrameSettingsField.VolumetricClouds,
        FrameSettingsField.Distortion,
        // 2026-11-01, frame rate (~35 fps at 80 Hz; per frame ~21 ms render thread, ~23 ms GPU, both eyes): further
        // per-eye passes whose loss is hard to see in the headset: screen-space ambient occlusion, subsurface
        // scattering and transmission (skin, leaves), real-time planar reflections, and the extra transparent
        // depth / low-resolution transparent passes
        FrameSettingsField.SSAO,
        FrameSettingsField.SubsurfaceScattering,
        FrameSettingsField.Transmission,
        FrameSettingsField.PlanarProbe,
        FrameSettingsField.TransparentPrepass,
        FrameSettingsField.TransparentPostpass,
        FrameSettingsField.LowResTransparent,
        // 2026-11-04: the volumetric lighting pass. Its fog is already off in every volume (VolumetricFogOff), but the
        // camera still had the pass enabled
        FrameSettingsField.Volumetrics,
        FrameSettingsField.ReprojectionForVolumetrics,
    };

    private readonly Dictionary<IntPtr, (HDAdditionalCameraData hd, FrameSettings settings, FrameSettingsOverrideMask mask)> _frameSettings = new();

    // The scope's picture (2026-11-05, speed): while aiming it draws the whole scene a second time with the full set of
    // effects, which is where the 45 fps dipped. In a small lens these can't be seen: the eyes' list plus ambient
    // occlusion and the per-object decals. Shadows stay (without them the sun lights rooms through the roof)
    private static readonly FrameSettingsField[] UnusedInScope =
    {
        FrameSettingsField.SSAO,
        FrameSettingsField.Decals,
    };

    // ---- Sun shadows drawn once, shared (Rendering.SunShadowFrames) ----
    // 2026-11-05, speed: the sun's shadow map was drawn for each eye every frame (and again for the scope). With the
    // light set to update on request, HDRP draws it when asked and the other eye (and the scope) reuse it; asked every
    // SunShadowFrames frames. The shadows stay fixed to the world; only which area they cover follows you a frame later
    private readonly Dictionary<IntPtr, (HDAdditionalLightData hd, ShadowUpdateMode mode)> _sharedShadows = new();
    private bool _sharedFailed;
    private int _sharedLogs;

    private void SunShadowsShared()
    {
        var every = Plugin.SunShadowFrames.Value;
        if (every <= 0 || _sharedFailed)
        {
            SharedShadowsBack();
            return;
        }
        foreach (var (light, _) in _suns.Values)
        {
            if (light == null)
                continue;
            if (!_sharedShadows.TryGetValue(light.Pointer, out var entry))
            {
                var hd = light.GetComponent<HDAdditionalLightData>();
                if (hd == null)
                    continue;
                entry = (hd, hd.shadowUpdateMode);
                _sharedShadows[light.Pointer] = entry;
                hd.shadowUpdateMode = ShadowUpdateMode.OnDemand;
                hd.RequestShadowMapRendering();
                if (_sharedLogs++ < 4)
                    Plugin.Logger.LogInfo($"[Effects] Sun '{light.name}': shadows drawn once every {every} frame(s), shared by both eyes " +
                                          $"(was {entry.mode})");
            }
            else if (entry.hd != null && entry.hd.shadowUpdateMode != ShadowUpdateMode.OnDemand)
                entry.hd.shadowUpdateMode = ShadowUpdateMode.OnDemand;   // the game set it back
            if (entry.hd != null && Time.frameCount % every == 0)
                entry.hd.RequestShadowMapRendering();
        }
    }

    private void SharedShadowsBack()
    {
        foreach (var (hd, mode) in _sharedShadows.Values)
            try { if (hd != null) hd.shadowUpdateMode = mode; } catch { }
        _sharedShadows.Clear();
    }

    private void TrimScopeCameras()
    {
        if (!Plugin.CheapScopes.Value)
            return;
        foreach (var cam in ExtraCameras.ScopeCameras())
        {
            var hd = cam.GetComponent<HDAdditionalCameraData>();
            if (hd != null)
                TrimFrameSettings(hd, scope: true);
        }
    }

    private void TrimFrameSettings(HDAdditionalCameraData hd, bool scope = false)
    {
        if (!Plugin.VRPerformance.Value)
            return;
        var settings = hd.m_RenderingPathCustomFrameSettings;
        var mask = hd.renderingPathCustomFrameSettingsOverrideMask;

        // The game's camera script may apply its own settings again (e.g. after a graphics change), so check each scan
        var off = Plugin.AllowDLSS.Value && !scope
            ? Array.FindAll(UnusedInVR, f => f != FrameSettingsField.MotionVectors && f != FrameSettingsField.ObjectMotionVectors)
            : UnusedInVR;
        if (scope)
        {
            var all = new List<FrameSettingsField>(off);
            all.AddRange(UnusedInScope);
            off = all.ToArray();
        }
        // Shading in corners and under things back on (Rendering.AmbientOcclusion, 2026-11-03): at RenderScale 1.0
        // Georgia held 40 fps with the GPU at ~19.5 of 25 ms. HDRP's own pass, which draws both eyes (the game's
        // HTraceAO stays off: it only drew the left eye)
        var aoOn = Plugin.AmbientOcclusion.Value && !scope;
        // (2026-11-05: subsurface scattering was put back on while the customisation page was open, for the
        // character's eyes; the F3 close-ups looked the same and the user thought worse, so it was taken out again)
        if (aoOn)
            off = Array.FindAll(off, f => f != FrameSettingsField.SSAO);
        var needed = !hd.customRenderingSettings;
        foreach (var f in off)
            if (settings.IsEnabled(f) || !mask.mask[(uint)f])
                needed = true;
        if (aoOn && (!settings.IsEnabled(FrameSettingsField.SSAO) || !mask.mask[(uint)FrameSettingsField.SSAO]))
            needed = true;
        if (!needed)
            return;

        var first = !_frameSettings.ContainsKey(hd.Pointer);
        if (first)
            _frameSettings[hd.Pointer] = (hd, settings, mask);
        foreach (var f in off)
        {
            settings.SetEnabled(f, false);
            mask.mask[(uint)f] = true;
        }
        if (aoOn)
        {
            settings.SetEnabled(FrameSettingsField.SSAO, true);
            mask.mask[(uint)FrameSettingsField.SSAO] = true;
        }
        hd.m_RenderingPathCustomFrameSettings = settings;
        hd.renderingPathCustomFrameSettingsOverrideMask = mask;
        hd.customRenderingSettings = true;
        Plugin.Logger.LogInfo($"[Effects] Camera '{hd.name}': {(first ? "turned off" : "turned off again")} {string.Join(", ", off)} in VR");
    }

    // Draw distance: the game sets the far plane back to 20000 m every frame, so capping it once a second made it
    // flip between 500 and 20000 (trees popping in and out) and saved nothing. Cap it right before each frame renders
    private static bool s_beforeRenderHooked;
    private static float s_gameFar = -1f;
    private static bool s_loggedFar;

    private static void HookBeforeRender()
    {
        if (s_beforeRenderHooked)
            return;
        s_beforeRenderHooked = true;
        RenderPipelineManager.add_beginContextRendering(
            Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ScriptableRenderContext, Il2CppSystem.Collections.Generic.List<Camera>>>(
                new Action<ScriptableRenderContext, Il2CppSystem.Collections.Generic.List<Camera>>((_, _) => CapFarPlane())));
    }

    private static void CapFarPlane()
    {
        try
        {
            var view = Plugin.ViewDistance.Value;
            // Online with other players the main thread is the limit (2026-11-02: ~35 fps, CPU ~28 ms): draw less far
            if (s_online && Plugin.OnlineViewDistance.Value > 0f && (view <= 0f || Plugin.OnlineViewDistance.Value < view))
                view = Plugin.OnlineViewDistance.Value;
            if (!OpenXRBootstrap.IsRunning || view <= 0f)
                return;
            var cam = Camera.main;
            if (cam == null)
                return;
            // Back offline: let it reach further again (up to the game's own distance)
            if (cam.farClipPlane < view && s_gameFar > cam.farClipPlane)
            {
                cam.farClipPlane = Mathf.Min(s_gameFar, view);
                return;
            }
            if (cam.farClipPlane <= view)
                return;
            s_gameFar = cam.farClipPlane;
            cam.farClipPlane = view;
            if (!s_loggedFar)
            {
                s_loggedFar = true;
                Plugin.Logger.LogInfo($"[Effects] Camera '{cam.name}': draw distance {s_gameFar:0} m -> {view:0} m in VR (applied every frame)");
            }
        }
        catch
        {
            // never throw from the render callback
        }
    }

    // LOD bias: the game's own value, and what we last set (the game may set its own again on level load)
    private float _lodBase = -1f, _lodSet = -1f;
    private readonly Dictionary<IntPtr, (HDShadowSettings s, bool overrideState, float value)> _shadowCapped = new();

    private void ApplyLodScale()
    {
        UpdateOnline();
        var scale = Plugin.LodScale.Value;
        if (scale <= 0f)
            scale = 1f;
        if (s_online && Plugin.OnlineLodScale.Value > 0f)
            scale *= Plugin.OnlineLodScale.Value;
        if (Mathf.Approximately(scale, 1f) && _lodSet < 0f)
            return;
        var current = QualitySettings.lodBias;
        // Ours already, for the same online state: nothing to do
        if (_lodSet > 0f && Mathf.Abs(current - _lodSet) < 0.0001f && _lodOnline == s_online)
            return;
        // The game's own value, unless it's the one we set
        if (_lodSet < 0f || Mathf.Abs(current - _lodSet) >= 0.0001f)
            _lodBase = current;
        _lodSet = _lodBase * scale;
        _lodOnline = s_online;
        QualitySettings.lodBias = _lodSet;
        Plugin.Logger.LogInfo($"[Effects] LOD bias {_lodBase:0.##} -> {_lodSet:0.##} in VR{(s_online ? " (online: simpler sooner)" : "")}");
    }

    // Whether other people are in the game (another human player's PlayerController; bots are named BOT ...)
    private static bool s_online;
    private bool _lodOnline;
    private float _nextOnlineCheck;

    private void UpdateOnline()
    {
        if (Time.unscaledTime < _nextOnlineCheck)
            return;
        _nextOnlineCheck = Time.unscaledTime + 5f;
        var others = 0;
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<PlayerNetworking>(), FindObjectsSortMode.None))
        {
            var p = o.TryCast<PlayerNetworking>();
            if (p != null && p.gameObject.name.StartsWith("PlayerController") && !p.gameObject.name.Contains("MyPlayer"))
                others++;
        }
        var online = others > 0;
        if (online == s_online)
            return;
        s_online = online;
        Plugin.Logger.LogInfo(online
            ? $"[Effects] Online with {others} other player(s): drawing up to {Plugin.OnlineViewDistance.Value:0} m, simpler far objects"
            : "[Effects] No other players: normal draw distance and detail");
    }

    // Lights whose shadows we switched off (Rendering.Shadows = false), with their original setting
    private readonly Dictionary<IntPtr, (Light light, LightShadows shadows)> _shadowsOff = new();
    // Suns kept for Rendering.SunShadows, with their original setting
    private readonly Dictionary<IntPtr, (Light light, LightShadows shadows)> _suns = new();
    private int _sunShadowsOn = -1;          // -1 not applied yet, 0 off, 1 on
    private float _nextRoofCheck;
    private int _sunLogs;

    // The sun's shadows were kept so houses aren't lit through their roofs (2026-11-02), but outdoors that brought
    // back the trees' shadows the user had been glad to lose (2026-11-03). Unless Rendering.SunShadowsOutdoors, the sun
    // only casts shadows while something is overhead (a ray straight up from the eye hits a roof within 40 m)
    private void UpdateSunShadows()
    {
        if (_suns.Count == 0 || Time.unscaledTime < _nextRoofCheck)
            return;
        _nextRoofCheck = Time.unscaledTime + 0.5f;
        var want = Plugin.SunShadowsOutdoors.Value || UnderRoof();
        var state = want ? 1 : 0;
        if (state == _sunShadowsOn)
            return;
        _sunShadowsOn = state;
        foreach (var (light, shadows) in _suns.Values)
            try { if (light != null) light.shadows = want ? shadows : LightShadows.None; } catch { }
        if (_sunLogs++ < 20)
            Plugin.Logger.LogInfo(want ? "[Effects] Under a roof: sun shadows on" : "[Effects] In the open: sun shadows off (no tree shadows)");
    }

    private static bool UnderRoof()
    {
        var cam = Camera.main;
        if (cam == null)
            return false;
        if (!HeadTracking.TryGetGameCameraPose(out _, out var pos, out _))
            pos = cam.transform.position;
        var self = cam.transform.root;
        foreach (var hit in Physics.RaycastAll(pos + Vector3.up * 0.3f, Vector3.up, 40f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            var c = hit.collider;
            // Not the player's own body or gear
            if (c != null && (self == null || c.transform.root.Pointer != self.Pointer))
                return true;
        }
        return false;
    }

    private void ShadowsOff()
    {
        if (Plugin.Shadows.Value)
            return;
        var count = 0;
        // Lights appear with the player, weapons and level sections, so keep checking
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<Light>(), FindObjectsSortMode.None))
        {
            var l = o.TryCast<Light>();
            if (l == null || _shadowsOff.ContainsKey(l.Pointer) || _suns.ContainsKey(l.Pointer) || l.shadows == LightShadows.None)
                continue;
            // The sun keeps its shadows (Rendering.SunShadows): without them it lit the inside of houses through the
            // roof and walls, and day maps looked overexposed indoors (2026-11-02, 'Suburb Day'). UpdateSunShadows
            // switches them with whether you're under a roof
            if (Plugin.SunShadows.Value && l.type == LightType.Directional)
            {
                _suns[l.Pointer] = (l, l.shadows);
                _sunShadowsOn = -1;
                Plugin.Logger.LogInfo($"[Effects] Sun '{l.name}' keeps its shadows in VR (Rendering.SunShadows){(Plugin.SunShadowsOutdoors.Value ? "" : ", only under a roof")}");
                continue;
            }
            _shadowsOff[l.Pointer] = (l, l.shadows);
            l.shadows = LightShadows.None;
            count++;
        }
        if (count > 0)
            Plugin.Logger.LogInfo($"[Effects] Shadows off on {count} lights in VR ({_shadowsOff.Count} in all)");
    }

    // Lens flare components (sun, lamps) we switched off, and the sun's halo in the sky (flare multiplier) we zeroed
    private readonly Dictionary<IntPtr, Behaviour> _flaresOff = new();
    private readonly Dictionary<IntPtr, (HDAdditionalLightData light, float flare)> _sunHaloOff = new();

    private void SunGlareOff()
    {
        if (Plugin.SunGlare.Value)
            return;
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<LensFlareComponentSRP>(), FindObjectsSortMode.None))
        {
            var f = o.TryCast<LensFlareComponentSRP>();
            if (f == null || !f.enabled || _flaresOff.ContainsKey(f.Pointer))
                continue;
            f.enabled = false;
            _flaresOff[f.Pointer] = f;
            Plugin.Logger.LogInfo($"[Effects] Lens flare off on '{f.name}' in VR");
        }
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<HDAdditionalLightData>(), FindObjectsSortMode.None))
        {
            var l = o.TryCast<HDAdditionalLightData>();
            if (l == null || _sunHaloOff.ContainsKey(l.Pointer) || l.flareMultiplier <= 0f)
                continue;
            var light = l.GetComponent<Light>();
            if (light == null || light.type != LightType.Directional)
                continue;
            _sunHaloOff[l.Pointer] = (l, l.flareMultiplier);
            Plugin.Logger.LogInfo($"[Effects] Sun halo off on '{l.name}' in VR (flare multiplier {l.flareMultiplier:0.##} -> 0)");
            l.flareMultiplier = 0f;
        }
    }

    // Sun shadow layers (cascades), Rendering.SunShadowCascades: each layer draws the scene again from the sun
    private readonly Dictionary<IntPtr, (HDShadowSettings s, bool overrideState, int value)> _cascadesCut = new();

    private void CutCascades(VolumeComponent comp, string volume)
    {
        var want = Plugin.SunShadowCascades.Value;
        if (want <= 0 || _cascadesCut.ContainsKey(comp.Pointer))
            return;
        var s = comp.TryCast<HDShadowSettings>();
        if (s == null)
            return;
        want = Mathf.Clamp(want, 1, 4);
        var p = s.cascadeShadowSplitCount;
        if (p.overrideState && p.value <= want)
            return;
        _cascadesCut[comp.Pointer] = (s, p.overrideState, p.value);
        Plugin.Logger.LogInfo($"[Effects] Sun shadow layers {(p.overrideState ? p.value.ToString() : "default")} -> {want} in volume '{volume}'");
        p.overrideState = true;
        p.value = want;
    }

    // Reflection probes re-captured every frame (Rendering.ReflectionProbesOnce): 6 views each, per frame
    private readonly Dictionary<IntPtr, (HDProbe probe, ProbeSettings.RealtimeMode mode)> _probesOnce = new();

    private void ProbesOnce()
    {
        if (!Plugin.ReflectionProbesOnce.Value)
            return;
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<HDProbe>(), FindObjectsSortMode.None))
        {
            var p = o.TryCast<HDProbe>();
            if (p == null || _probesOnce.ContainsKey(p.Pointer) || p.mode != ProbeSettings.Mode.Realtime
                || p.realtimeMode != ProbeSettings.RealtimeMode.EveryFrame)
                continue;
            _probesOnce[p.Pointer] = (p, p.realtimeMode);
            p.realtimeMode = ProbeSettings.RealtimeMode.OnEnable;
            Plugin.Logger.LogInfo($"[Effects] Reflection probe '{p.name}' captured once instead of every frame in VR");
        }
    }

    // Once per scene: which volume effects are still on after all of the above, for the next frame-rate pass
    private string _effectsLoggedFor;

    private void LogEffectsStillOn()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        if (scene == _effectsLoggedFor)
            return;
        _effectsLoggedFor = scene;
        var on = new SortedSet<string>();
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<Volume>(), FindObjectsSortMode.None))
        {
            var v = o.TryCast<Volume>();
            if (v == null || !v.isActiveAndEnabled || v.sharedProfile == null)
                continue;
            foreach (var comp in v.sharedProfile.components)
                if (comp != null && comp.active)
                    on.Add(comp.GetIl2CppType().Name);
        }
        Plugin.Logger.LogInfo($"[Effects] Still on in '{scene}': {string.Join(", ", on)}");
    }

    private void CapShadowDistance(VolumeComponent comp, string volume)
    {
        CutCascades(comp, volume);
        var cap = Plugin.ShadowDistance.Value;
        if (cap <= 0f || _shadowCapped.ContainsKey(comp.Pointer))
            return;
        var s = comp.TryCast<HDShadowSettings>();
        if (s == null)
            return;
        var p = s.maxShadowDistance;
        if (p.overrideState && p.value <= cap)
            return;
        _shadowCapped[comp.Pointer] = (s, p.overrideState, p.value);
        Plugin.Logger.LogInfo($"[Effects] Shadow distance {(p.overrideState ? $"{p.value:0} m" : "default")} -> {cap:0} m in volume '{volume}'");
        p.overrideState = true;
        p.value = cap;
    }

    /// <summary>Keeps the distance fog (the woodland's haze) but drops its volumetric part (light shafts), which is costly per eye.</summary>
    private void VolumetricFogOff(VolumeComponent comp, string volume)
    {
        if (_volumetricFogOff.ContainsKey(comp.Pointer))
            return;
        var fog = comp.TryCast<Fog>();
        if (fog == null)
            return;
        var p = fog.enableVolumetricFog;
        _volumetricFogOff[comp.Pointer] = (fog, p.overrideState, p.value);
        p.overrideState = true;
        p.value = false;
        Plugin.Logger.LogInfo($"[Effects] Volumetric fog off in volume '{volume}' (distance fog kept)");
    }

    private class CameraSettings
    {
        public HDAdditionalCameraData Data;
        public Camera Camera;
        public float Far;
        public bool DynamicResolution, Dlss, Fsr2;
        public HDAdditionalCameraData.AntialiasingMode Antialiasing;
    }

    // Native pointer -> original settings of each camera we changed
    private readonly Dictionary<IntPtr, CameraSettings> _cameras = new();
    private string _lastCameraState;

    private static string Components(GameObject go)
    {
        var names = new List<string>();
        foreach (var c in go.GetComponents<Component>())
            if (c != null)
                names.Add(c.GetIl2CppType().Name + (c.TryCast<Behaviour>() is { } b && !b.enabled ? "(off)" : ""));
        return string.Join(", ", names);
    }

    /// <summary>
    /// In the woodland level part of each eye kept showing the menu area and flickered: parts of the eye image
    /// weren't being redrawn. Resolution scaling (dynamic resolution / DLSS / FSR2) renders smaller and
    /// upscales, and TAA blends in previous frames; with manually driven multi-pass XR either can leave stale
    /// pixels. Turn them off on the main camera while VR runs. The game re-applies its settings on level load,
    /// so this runs on every scan.
    /// </summary>
    private void FixCamera()
    {
        var cam = Camera.main;
        if (cam == null)
            return;
        var hd = cam.GetComponent<HDAdditionalCameraData>();
        if (hd == null)
            return;

        if (!_cameras.ContainsKey(hd.Pointer))
        {
            _cameras[hd.Pointer] = new CameraSettings
            {
                Data = hd,
                Camera = cam,
                Far = cam.farClipPlane,
                DynamicResolution = hd.allowDynamicResolution,
                Dlss = hd.allowDeepLearningSuperSampling,
                Fsr2 = hd.allowFidelityFX2SuperResolution,
                Antialiasing = hd.antialiasing,
            };
            var drs = DynamicResolutionHandler.instance;
            Plugin.Logger.LogInfo($"[Effects] Camera '{cam.name}' had dynamicRes={hd.allowDynamicResolution} DLSS={hd.allowDeepLearningSuperSampling} " +
                                  $"FSR2={hd.allowFidelityFX2SuperResolution} AA={hd.antialiasing} " +
                                  $"scale={(drs == null ? "-" : drs.GetCurrentScale().ToString("0.00"))} rect={cam.rect}");
        }

        // Whenever the camera's output-related settings or attached components change (e.g. on level load), log them:
        // in 'Georgia 1 Day' both eyes are rendered but the right eye's output never reaches its images
        var state = $"xrRendering={hd.xrRendering} customRender={hd.hasCustomRender} passthrough={hd.fullscreenPassthrough} " +
                    $"flipY={hd.flipYMode} clearColor={hd.clearColorMode} customFrameSettings={hd.customRenderingSettings} " +
                    $"target={(cam.targetTexture == null ? "screen" : cam.targetTexture.name)} eye={cam.stereoTargetEye} " +
                    $"stereo={cam.stereoEnabled} rect={cam.rect} components=[{Components(cam.gameObject)}]";
        if (state != _lastCameraState)
        {
            _lastCameraState = state;
            Plugin.Logger.LogInfo($"[Effects] Camera '{cam.name}' in '{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}': {state}");
        }

        HookBeforeRender();
        TrimFrameSettings(hd);

        if (Plugin.AntiAliasing.Value && hd.antialiasing == HDAdditionalCameraData.AntialiasingMode.None)
        {
            // SMAA rather than FXAA: FXAA softened the whole image, which looked blurry in the headset
            hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            Plugin.Logger.LogInfo($"[Effects] Camera '{cam.name}': anti-aliasing was off, using SMAA in VR");
        }

        // Experimental (Rendering.AllowDLSS, the user asked to try it): DLSS on instead of off. HDRP's DLSS draws at
        // a lower resolution (DlssPercent, through the dynamic resolution handler) and upscales with motion vectors,
        // which TrimFrameSettings then leaves on
        if (Plugin.AllowDLSS.Value)
        {
            if (!hd.allowDynamicResolution || !hd.allowDeepLearningSuperSampling)
            {
                hd.allowDynamicResolution = true;
                hd.allowDeepLearningSuperSampling = true;
                hd.allowFidelityFX2SuperResolution = false;
                SetDlssScale();
                Plugin.Logger.LogInfo($"[Effects] Camera '{cam.name}': DLSS allowed in VR (experimental), drawing at " +
                                      $"{Mathf.Clamp(Plugin.DlssPercent.Value, 33f, 100f):0}% then upscaled; HDRP's scale now " +
                                      $"{DynamicResolutionHandler.instance.GetCurrentScale():0.00}");
            }
            return;
        }

        if (hd.allowDynamicResolution || hd.allowDeepLearningSuperSampling || hd.allowFidelityFX2SuperResolution
            || hd.antialiasing == HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing)
        {
            hd.allowDynamicResolution = false;
            hd.allowDeepLearningSuperSampling = false;
            hd.allowFidelityFX2SuperResolution = false;
            if (hd.antialiasing == HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing)
                hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            Plugin.Logger.LogInfo($"[Effects] Camera '{cam.name}': resolution scaling off, AA={hd.antialiasing}");
        }
    }

    private static bool s_dlssScaler;

    private static void SetDlssScale()
    {
        if (s_dlssScaler)
            return;
        s_dlssScaler = true;
        try
        {
            DynamicResolutionHandler.SetDynamicResScaler(
                Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<PerformDynamicRes>(
                    new Func<float>(() => Mathf.Clamp(Plugin.DlssPercent.Value, 33f, 100f))),
                DynamicResScalePolicyType.ReturnsPercentage);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Effects] DLSS scale: {e.GetType().Name}: {e.Message}");
        }
    }

    // In 'Georgia 1 Day' only the left eye updated (it flickered between both views; the right eye kept the
    // loading screen) while the menu scene worked in both. These third-party full-screen add-ons are the
    // likeliest to write only the first eye. Matched by full type name so no extra assembly references are needed.
    private static readonly string[] NonStereoAddons =
    {
        "HTraceAO.Rendering.HTraceAO, HTraceAO.Rendering",
        "TND.Upscaling.Framework.UpscalerController, TND.Upscaling.Framework.Runtime.Core",
    };
    private static readonly string[] NonStereoPassNamespaces = { "HTraceAO.", "TND." };

    private readonly Dictionary<IntPtr, Behaviour> _addonsOff = new();
    private readonly Dictionary<IntPtr, UnityEngine.Rendering.HighDefinition.CustomPass> _passesOff = new();
    private string _passesLoggedFor;

    private void DisableNonStereoAddons()
    {
        foreach (var typeName in NonStereoAddons)
        {
            var type = Il2CppSystem.Type.GetType(typeName);
            if (type == null)
                continue;
            foreach (var o in UnityEngine.Object.FindObjectsByType(type, FindObjectsSortMode.None))
            {
                var b = o.TryCast<Behaviour>();
                if (b == null || !b.enabled)
                    continue;
                b.enabled = false;
                _addonsOff[b.Pointer] = b;
                Plugin.Logger.LogInfo($"[Effects] Turned off {type.Name} on '{b.name}'");
            }
        }

        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<CustomPassVolume>(), FindObjectsSortMode.None))
        {
            var v = o.TryCast<CustomPassVolume>();
            if (v == null)
                continue;
            foreach (var pass in v.customPasses)
                TurnOffIfNonStereo(pass, $"'{v.name}'");
        }
        foreach (var (point, pass) in GlobalPasses())
            TurnOffIfNonStereo(pass, $"global {point}");
    }

    private void TurnOffIfNonStereo(UnityEngine.Rendering.HighDefinition.CustomPass pass, string where)
    {
        if (pass == null || !pass.enabled || !IsNonStereoPass(pass))
            return;
        pass.enabled = false;
        _passesOff[pass.Pointer] = pass;
        Plugin.Logger.LogInfo($"[Effects] Turned off custom pass {pass.GetIl2CppType().FullName} in {where}");
    }

    /// <summary>Passes registered from code (CustomPassVolume.RegisterGlobalCustomPass) aren't in any volume.</summary>
    private static List<(CustomPassInjectionPoint, UnityEngine.Rendering.HighDefinition.CustomPass)> GlobalPasses()
    {
        var result = new List<(CustomPassInjectionPoint, UnityEngine.Rendering.HighDefinition.CustomPass)>();
        foreach (CustomPassInjectionPoint point in Enum.GetValues(typeof(CustomPassInjectionPoint)))
        {
            var list = CustomPassVolume.GetGlobalCustomPasses(point);
            if (list == null)
                continue;
            for (var i = 0; i < list.Count; i++)
                if (list[i].instance != null)
                    result.Add((point, list[i].instance));
        }
        return result;
    }

    private static bool IsNonStereoPass(UnityEngine.Rendering.HighDefinition.CustomPass pass)
    {
        var name = pass.GetIl2CppType().FullName;
        foreach (var ns in NonStereoPassNamespaces)
            if (name.StartsWith(ns))
                return true;
        return false;
    }

    /// <summary>Lists every custom pass in the scene once, so the log shows what else could be single-eye.</summary>
    private void LogCustomPassesOncePerScene()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        if (scene == _passesLoggedFor)
            return;
        _passesLoggedFor = scene;

        Plugin.Logger.LogInfo($"===== [Effects] custom passes in '{scene}' =====");
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<CustomPassVolume>(), FindObjectsSortMode.None))
        {
            var v = o.TryCast<CustomPassVolume>();
            if (v == null)
                continue;
            foreach (var pass in v.customPasses)
                if (pass != null)
                    Plugin.Logger.LogInfo($"[Effects]   '{v.name}' global={v.isGlobal} active={v.isActiveAndEnabled} {v.injectionPoint}: " +
                                          $"{pass.GetIl2CppType().FullName} '{pass.name}' enabled={pass.enabled}");
        }
        foreach (var (point, pass) in GlobalPasses())
            Plugin.Logger.LogInfo($"[Effects]   registered from code, {point}: {pass.GetIl2CppType().FullName} '{pass.name}' enabled={pass.enabled}");

        // Other cameras can draw into the eyes too (e.g. a weapon or sky camera only in levels)
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<Camera>(), FindObjectsSortMode.None))
        {
            var c = o.TryCast<Camera>();
            if (c != null && c.isActiveAndEnabled)
                Plugin.Logger.LogInfo($"[Effects]   camera '{c.name}' depth={c.depth} target={(c.targetTexture == null ? "screen" : c.targetTexture.name)} " +
                                      $"stereoEye={c.stereoTargetEye} mask=0x{c.cullingMask:X}");
        }
        Plugin.Logger.LogInfo("===== [Effects] end custom passes =====");
    }

    // Our own global volume with HDRP ambient occlusion, so it's there whatever the level's volumes say (the game's
    // AO was its HTraceAO add-on, off in VR). Half resolution and a modest strength to keep the cost down
    private GameObject _aoVolume;

    private void EnsureAoVolume()
    {
        if (!Plugin.AmbientOcclusion.Value)
        {
            RemoveAoVolume();
            return;
        }
        if (_aoVolume != null)
            return;
        var go = new GameObject("OperatorVR Ambient Occlusion");
        UnityEngine.Object.DontDestroyOnLoad(go);
        var v = go.AddComponent<Volume>();
        v.isGlobal = true;
        v.priority = 1000f;
        var profile = ScriptableObject.CreateInstance<VolumeProfile>();
        var ao = profile.Add(Il2CppType.Of<ScreenSpaceAmbientOcclusion>(), false).TryCast<ScreenSpaceAmbientOcclusion>();
        ao.active = true;
        ao.intensity.Override(Mathf.Clamp(Plugin.AmbientOcclusionStrength.Value, 0f, 4f));
        ao.radius.Override(1.5f);
        ao.m_FullResolution.Override(false);
        ao.temporalAccumulation.Override(false);
        ao.rayTracing.Override(false);
        v.sharedProfile = profile;
        _aoVolume = go;
        Plugin.Logger.LogInfo($"[Effects] Ambient occlusion on in VR (HDRP, strength {ao.intensity.value:0.0}, half resolution)");
    }

    private void RemoveAoVolume()
    {
        if (_aoVolume == null)
            return;
        UnityEngine.Object.Destroy(_aoVolume);
        _aoVolume = null;
    }

    private void RestoreAll()
    {
        RemoveAoVolume();
        foreach (var b in _addonsOff.Values)
        {
            try
            {
                if (b != null)
                    b.enabled = true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"[Effects] Could not restore an add-on: {e.Message}");
            }
        }
        foreach (var p in _passesOff.Values)
        {
            try
            {
                if (p != null)
                    p.enabled = true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"[Effects] Could not restore a custom pass: {e.Message}");
            }
        }
        if (_addonsOff.Count + _passesOff.Count > 0)
            Plugin.Logger.LogInfo($"[Effects] Restored {_addonsOff.Count} add-ons and {_passesOff.Count} custom passes");
        _addonsOff.Clear();
        _passesOff.Clear();
        _passesLoggedFor = null;

        foreach (var c in _cameras.Values)
        {
            try
            {
                if (c.Data == null)
                    continue;
                c.Data.allowDynamicResolution = c.DynamicResolution;
                c.Data.allowDeepLearningSuperSampling = c.Dlss;
                c.Data.allowFidelityFX2SuperResolution = c.Fsr2;
                c.Data.antialiasing = c.Antialiasing;
                if (c.Camera != null && s_gameFar > 0f && c.Camera.farClipPlane < s_gameFar)
                    c.Camera.farClipPlane = s_gameFar;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"[Effects] Could not restore camera settings: {e.Message}");
            }
        }
        _cameras.Clear();

        foreach (var comp in _turnedOff.Values)
        {
            try
            {
                if (comp != null)
                    comp.active = true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"[Effects] Could not restore an effect: {e.Message}");
            }
        }
        if (_turnedOff.Count > 0)
            Plugin.Logger.LogInfo($"[Effects] Restored {_turnedOff.Count} effects");
        _turnedOff.Clear();

        foreach (var (fog, overrideState, value) in _volumetricFogOff.Values)
        {
            try
            {
                if (fog == null)
                    continue;
                fog.enableVolumetricFog.overrideState = overrideState;
                fog.enableVolumetricFog.value = value;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"[Effects] Could not restore fog: {e.Message}");
            }
        }
        _volumetricFogOff.Clear();

        foreach (var (s, overrideState, value) in _shadowCapped.Values)
        {
            try
            {
                if (s == null)
                    continue;
                s.maxShadowDistance.overrideState = overrideState;
                s.maxShadowDistance.value = value;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"[Effects] Could not restore shadow distance: {e.Message}");
            }
        }
        _shadowCapped.Clear();

        foreach (var (s, overrideState, value) in _cascadesCut.Values)
            try
            {
                if (s == null)
                    continue;
                s.cascadeShadowSplitCount.overrideState = overrideState;
                s.cascadeShadowSplitCount.value = value;
            }
            catch { }
        _cascadesCut.Clear();

        foreach (var (probe, mode) in _probesOnce.Values)
            try { if (probe != null) probe.realtimeMode = mode; } catch { }
        _probesOnce.Clear();
        _effectsLoggedFor = null;

        foreach (var (light, shadows) in _shadowsOff.Values)
        {
            try
            {
                if (light != null)
                    light.shadows = shadows;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"[Effects] Could not restore a light's shadows: {e.Message}");
            }
        }
        if (_shadowsOff.Count > 0)
            Plugin.Logger.LogInfo($"[Effects] Shadows back on for {_shadowsOff.Count} lights");
        _shadowsOff.Clear();
        SharedShadowsBack();
        foreach (var (light, shadows) in _suns.Values)
            try { if (light != null) light.shadows = shadows; } catch { }
        _suns.Clear();
        _sunShadowsOn = -1;

        foreach (var f in _flaresOff.Values)
            try { if (f != null) f.enabled = true; } catch { }
        _flaresOff.Clear();
        foreach (var (light, flare) in _sunHaloOff.Values)
            try { if (light != null) light.flareMultiplier = flare; } catch { }
        _sunHaloOff.Clear();

        foreach (var (hd, settings, mask) in _frameSettings.Values)
        {
            try
            {
                if (hd == null)
                    continue;
                hd.m_RenderingPathCustomFrameSettings = settings;
                hd.renderingPathCustomFrameSettingsOverrideMask = mask;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"[Effects] Could not restore camera frame settings: {e.Message}");
            }
        }
        _frameSettings.Clear();

        if (_lodBase > 0f && Mathf.Abs(QualitySettings.lodBias - _lodSet) < 0.0001f)
            QualitySettings.lodBias = _lodBase;
        _lodBase = _lodSet = -1f;
    }
}
