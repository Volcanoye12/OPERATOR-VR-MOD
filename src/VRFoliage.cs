using System;
using System.Collections.Generic;
#if MELON
using Il2CppBRGInstancedRenderer;
#else
using BRGInstancedRenderer;
#endif
using Il2CppInterop.Runtime;
using UnityEngine;

namespace OperatorVR;

/// <summary>
/// The game draws trees and grass with BRGInstancedRenderer. Its GPU occlusion culling hides instances using a
/// depth pyramid (HZB) built from the previous frame; in multi-pass VR that's the other eye's view, so trees and
/// grass popped in and out. While VR runs (Rendering.FoliageOcclusionCulling = false, the default) that culling is
/// switched off. F6 toggles its LOD crossfade (animated per view, another candidate for the grass flashing) so
/// both looks can be compared in the headset. Everything is put back when VR stops.
/// </summary>
public class VRFoliage : MonoBehaviour
{
    public VRFoliage(IntPtr ptr) : base(ptr) { }

    private const int VK_F6 = 0x75;
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private class Saved
    {
        public BRGInstancedRendererConfig Config;
        public bool Occlusion;
        public bool CrossfadeDisabled;
    }

    private readonly Dictionary<IntPtr, Saved> _saved = new();
    private bool _crossfadeOff;
    private bool _f6Down;
    private bool _wasRunning;
    private float _nextScan;

    // Tree and grass shadows (Rendering.TreeShadows = false): the sun lost its shadows outdoors to be rid of them, but
    // Georgia then looked too dark (2026-11-03). Instead the tree/grass renderer's shadow pass draws nothing in VR, so
    // buildings, vehicles and people keep their shadows and the light stays as the game has it
    private static int s_shadowSkips;

    internal static void Apply(HarmonyLib.Harmony harmony)
    {
        try
        {
            var m = HarmonyLib.AccessTools.Method(typeof(BRGRenderer), nameof(BRGRenderer.OnPerformCullingShadow));
            harmony.Patch(m, prefix: new HarmonyLib.HarmonyMethod(typeof(VRFoliage), nameof(BeforeShadowCulling)));
            Plugin.Logger.LogInfo("[Foliage] hooked BRGRenderer.OnPerformCullingShadow (no tree/grass shadows in VR)");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Foliage] shadow hook failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static bool BeforeShadowCulling(ref Unity.Jobs.JobHandle __result)
    {
        if (Plugin.TreeShadows.Value || !OpenXRBootstrap.IsRunning)
            return true;
        __result = default;
        if (s_shadowSkips++ == 0)
            Plugin.Logger.LogInfo("[Foliage] Trees and grass cast no shadows in VR (Rendering.TreeShadows off)");
        return false;
    }

    private void Update()
    {
        using var __t = ModTime.Measure("VRFoliage");
        var running = OpenXRBootstrap.IsRunning;
        if (!running)
        {
            if (_wasRunning)
                RestoreAll();
            _wasRunning = false;
            return;
        }
        _wasRunning = true;

        var down = (GetAsyncKeyState(VK_F6) & 0x8000) != 0;
        if (down && !_f6Down)
        {
            _crossfadeOff = !_crossfadeOff;
            foreach (var s in _saved.Values)
                if (s.Config != null)
                    s.Config.debugDisableCrossfade = _crossfadeOff || s.CrossfadeDisabled;
            Plugin.Logger.LogInfo($"[Foliage] F6: tree/grass LOD crossfade {(_crossfadeOff ? "OFF" : "as the game has it")}");
        }
        _f6Down = down;

        if (Time.unscaledTime < _nextScan)
            return;
        _nextScan = Time.unscaledTime + 2f;

        try
        {
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<BRGInstancedRendererConfig>()))
            {
                var c = o.TryCast<BRGInstancedRendererConfig>();
                if (c == null || _saved.ContainsKey(c.Pointer))
                    continue;
                var saved = new Saved { Config = c, Occlusion = c.UseGPUOcclusionCulling, CrossfadeDisabled = c.debugDisableCrossfade };
                _saved[c.Pointer] = saved;
                if (!Plugin.FoliageOcclusionCulling.Value && saved.Occlusion)
                    c.SetGPUOcclusionCulling(false);
                if (_crossfadeOff)
                    c.debugDisableCrossfade = true;
                Plugin.Logger.LogInfo($"[Foliage] '{c.name}': GPU occlusion culling {saved.Occlusion} -> {c.UseGPUOcclusionCulling}, " +
                                      $"HZB retest={c.useHZBRetestPass}, crossfade mode={c.animatedCrossfadeMode}, crossfade disabled={c.debugDisableCrossfade}");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Foliage] {e.GetType().Name}: {e.Message}");
            _nextScan = Time.unscaledTime + 30f;
        }
    }

    private void RestoreAll()
    {
        foreach (var s in _saved.Values)
        {
            try
            {
                if (s.Config == null)
                    continue;
                if (s.Config.UseGPUOcclusionCulling != s.Occlusion)
                    s.Config.SetGPUOcclusionCulling(s.Occlusion);
                s.Config.debugDisableCrossfade = s.CrossfadeDisabled;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"[Foliage] Could not restore: {e.Message}");
            }
        }
        if (_saved.Count > 0)
            Plugin.Logger.LogInfo($"[Foliage] Restored {_saved.Count} renderer configs");
        _saved.Clear();
    }
}
