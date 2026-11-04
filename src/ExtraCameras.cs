using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace OperatorVR;

/// <summary>
/// In 'Georgia 1 Day' a second camera ('Camera', rendering into a texture) draws alongside the main camera,
/// and the right eye freezes while the left flickers; 'Operation Room' has no such camera and works. A camera
/// rendering mid-frame can upset multi-pass XR, so while VR runs, other cameras that render into textures
/// are switched off, and manual Camera.Render() calls on them are skipped. Everything is put back when VR stops.
/// Controlled by Rendering.SkipExtraCameras.
/// </summary>
public class ExtraCameras : MonoBehaviour
{
    public ExtraCameras(IntPtr ptr) : base(ptr) { }

    private readonly Dictionary<IntPtr, Camera> _turnedOff = new();
    private static readonly HashSet<string> s_skippedLogged = new();
    private float _nextScan;
    private bool _wasRunning;

    internal static void Apply(Harmony harmony)
    {
        try
        {
            harmony.Patch(AccessTools.Method(typeof(Camera), nameof(Camera.Render)),
                prefix: new HarmonyMethod(typeof(ExtraCameras), nameof(RenderPrefix)));
            Plugin.Logger.LogInfo("[Cameras] hooked Camera.Render");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Cameras] hooking Camera.Render failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static bool ShouldSkip(Camera c)
    {
        if (!Plugin.SkipExtraCameras.Value || !OpenXRBootstrap.IsRunning || c == null)
            return false;
        var main = Camera.main;
        return (main == null || c.Pointer != main.Pointer) && !IsOurs(c);
    }

    // The mod's own cameras (e.g. the flat customisation view) are never switched off, nor are magnified scopes'
    // ('Scope Camera' under 'Dual Render Scope Effect', drawing the zoomed view onto the lens): switched off, an
    // ACOG's lens showed a stale picture that didn't line up with its reticle
    internal static bool IsOurs(Camera c) => c != null && (c.name.StartsWith("OperatorVR") || IsScope(c));

    // Not only cameras named "Scope": any camera drawing into a texture that belongs to a gun or a sight (under a
    // WeaponV3, a WeaponMod or a scope component), so a sight whose camera has another name isn't switched off and
    // left black. Decided once per camera (this is asked on every Camera.Render)
    private static readonly Dictionary<IntPtr, bool> s_sightCams = new();

    private static bool IsScope(Camera c)
    {
        if (c.targetTexture == null)
            return false;
        if (c.name.Contains("Scope"))
            return true;
        if (s_sightCams.TryGetValue(c.Pointer, out var known))
            return known;
        var sight = false;
        try
        {
            sight = c.GetComponentInParent<WeaponV3>(true) != null || c.GetComponentInParent<WeaponMod>(true) != null ||
                    c.GetComponentInParent<ScopeInputSetter>(true) != null || c.GetComponentInParent<RealisticScopeEffectV2>(true) != null ||
                    c.GetComponentInParent<UltimateScopes.ScopeController>(true) != null;
        }
        catch { }
        s_sightCams[c.Pointer] = sight;
        if (sight)
            Plugin.Logger.LogInfo($"[Cameras] '{c.name}' belongs to a gun or sight: kept on like a scope camera");
        return sight;
    }

    // Scope cameras are plain cameras in VR (eye None), so HDRP doesn't treat them as headset views
    private readonly Dictionary<IntPtr, (Camera cam, StereoTargetEyeMask eye)> _scopes = new();
    private readonly Dictionary<IntPtr, UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData> _scopeXr = new();
    private static ExtraCameras s_instance;

    private void Start() => s_instance = this;

    /// <summary>A magnified scope's camera is drawing right now (the scope is on the gun and in use).</summary>
    internal static bool ScopeActive
    {
        get
        {
            var self = s_instance;
            if (self == null)
                return false;
            foreach (var (cam, _) in self._scopes.Values)
                if (cam != null && cam.isActiveAndEnabled)
                    return true;
            return false;
        }
    }

    private static bool RenderPrefix(Camera __instance)
    {
        using var __t = ModTime.Measure("ExtraCameras render hook");
        try
        {
            if (!ShouldSkip(__instance))
                return true;
            if (s_skippedLogged.Add(__instance.name))
                Plugin.Logger.LogInfo($"[Cameras] Skipping manual renders of '{LaptopDiagnostics.Path(__instance.transform)}' " +
                                      $"(target={(__instance.targetTexture == null ? "screen" : __instance.targetTexture.name)})");
            return false;
        }
        catch
        {
            return true;
        }
    }

    private void LateUpdate()
    {
        using var __t = ModTime.Measure("ExtraCameras");
        var running = OpenXRBootstrap.IsRunning && Plugin.SkipExtraCameras.Value;
        if (!running)
        {
            if (_wasRunning)
                RestoreAll();
            _wasRunning = false;
            return;
        }
        _wasRunning = true;

        if (Time.unscaledTime < _nextScan)
            return;
        _nextScan = Time.unscaledTime + 2f;

        try
        {
            var main = Camera.main;
            foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<Camera>(), FindObjectsSortMode.None))
            {
                var c = o.TryCast<Camera>();
                if (c != null && IsScope(c) && !_scopes.ContainsKey(c.Pointer))
                {
                    _scopes[c.Pointer] = (c, c.stereoTargetEye);
                    c.stereoTargetEye = StereoTargetEyeMask.None;
                    // HDRP ignores stereoTargetEye ("only with the built-in renderer"): its own xrRendering flag made the
                    // scope camera draw as a headset view into its 1024x1024 texture, and Unity logged "Dimensions of
                    // color surface do not match depth surface" every frame while a magnifier was up (3500 times in a
                    // session, 2026-11-04). Off for scope cameras while VR runs
                    try
                    {
                        var hd = c.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData>();
                        if (hd != null && hd.xrRendering)
                        {
                            hd.xrRendering = false;
                            _scopeXr[c.Pointer] = hd;
                        }
                    }
                    catch (Exception e) { Plugin.Logger.LogWarning($"[Cameras] scope XR off: {e.Message}"); }
                    Plugin.Logger.LogInfo($"[Cameras] Scope camera kept on (as a plain camera, XR rendering off): '{LaptopDiagnostics.Path(c.transform)}'");
                }
                if (c == null || !c.enabled || (main != null && c.Pointer == main.Pointer) || IsOurs(c))
                    continue;
                // Only cameras that don't draw into the headset: into a texture, or to the monitor only
                if (c.targetTexture == null && c.stereoTargetEye != StereoTargetEyeMask.None)
                    continue;
                c.enabled = false;
                _turnedOff[c.Pointer] = c;
                Plugin.Logger.LogInfo($"[Cameras] Turned off '{LaptopDiagnostics.Path(c.transform)}' " +
                                      $"(target={(c.targetTexture == null ? "screen" : c.targetTexture.name)}, eye={c.stereoTargetEye}, depth={c.depth})");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Cameras] {e.GetType().Name}: {e.Message}");
            _nextScan = Time.unscaledTime + 10f;
        }
    }

    private void RestoreAll()
    {
        foreach (var c in _turnedOff.Values)
        {
            try
            {
                if (c != null)
                    c.enabled = true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"[Cameras] Could not restore a camera: {e.Message}");
            }
        }
        if (_turnedOff.Count > 0)
            Plugin.Logger.LogInfo($"[Cameras] Restored {_turnedOff.Count} cameras");
        _turnedOff.Clear();
        foreach (var (cam, eye) in _scopes.Values)
            try { if (cam != null) cam.stereoTargetEye = eye; } catch { }
        _scopes.Clear();
        foreach (var hd in _scopeXr.Values)
            try { if (hd != null) hd.xrRendering = true; } catch { }
        _scopeXr.Clear();
    }
}
