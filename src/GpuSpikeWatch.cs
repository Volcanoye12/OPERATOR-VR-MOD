using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace OperatorVR;

/// <summary>
/// Rendering.GpuSpikeWatch (2026-11-10): in 'Suburb Night' a thrown frag dropped VR from 45 to 5 fps for good (GPU
/// 17 -> 175 ms per frame; the headset picture looked frozen) and nothing in the log said why. This watches the GPU
/// time; when it jumps to 2.5x its usual (and over 45 ms) for 3 s, it logs what's in view (particle effects and how
/// close/big, lights casting shadows, screen-effect volumes in force, cameras), then switches the likely features off
/// one at a time for a moment and logs what each one was costing ("[GpuSpike]" lines). Once per game; everything is
/// put back.
/// </summary>
public class GpuSpikeWatch : MonoBehaviour
{
    public GpuSpikeWatch(IntPtr ptr) : base(ptr) { }

    private const float SpikeFactor = 2.5f, SpikeMinMs = 45f, SpikeSeconds = 3f;
    private const int Settle = 4, Measure = 8;

    private Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<FrameTiming> _timings;
    private float _base = -1f, _spikeSince = -1f;
    private bool _done;
    private string _scene;
    private float _sceneSince;

    // The quick test
    private readonly List<(string name, Func<bool> apply, Action undo)> _tests = new();
    private int _test = -1, _frame;
    private bool _off;
    private readonly List<float> _samples = new();
    private float _before;
    private HDAdditionalCameraData _hd;

    private void Update()
    {
        if (_done || !Plugin.GpuSpikeWatch.Value)
            return;
        try
        {
            if (!OpenXRBootstrap.IsRunning || GpuAudit.Running)
            {
                if (_test >= 0)
                    Stop("VR stopped or the speed test started");
                _spikeSince = -1f;
                return;
            }
            var gpu = LatestGpu();
            if (_test >= 0)
            {
                StepTest(gpu);
                return;
            }
            // Missions only, and not while one is loading in: a false alarm there would use up the one go
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? "";
            if (scene != _scene)
            {
                _scene = scene;
                _sceneSince = Time.unscaledTime;
                _spikeSince = -1f;
            }
            if (gpu <= 0f || scene is "" or "ApplicationSplashScreen" or "3d Menu" or "Operation Room" ||
                Time.unscaledTime - _sceneSince < 30f)
                return;
            // The usual GPU time: a slow average of normal frames
            if (gpu < SpikeMinMs && (_base < 0f || gpu < _base * SpikeFactor))
            {
                _base = _base < 0f ? gpu : Mathf.Lerp(_base, gpu, 0.01f);
                _spikeSince = -1f;
                return;
            }
            if (_base < 0f || gpu < _base * SpikeFactor)
            {
                _spikeSince = -1f;
                return;
            }
            if (_spikeSince < 0f)
                _spikeSince = Time.unscaledTime;
            if (Time.unscaledTime - _spikeSince < SpikeSeconds)
                return;
            Plugin.Logger.LogWarning($"[GpuSpike] GPU {gpu:0} ms per frame for {SpikeSeconds:0} s (usually {_base:0.0} ms) in " +
                                     $"'{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}': what's in view, then a quick test");
            Snapshot();
            StartTest();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[GpuSpike] {e.GetType().Name}: {e.Message}");
            Stop("error");
        }
    }

    private float LatestGpu()
    {
        _timings ??= new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<FrameTiming>(1);
        return FrameTimingManager.GetLatestTimings(1, _timings) == 1 ? (float)_timings[0].gpuFrameTime : 0f;
    }

    // ---- What's in view ----

    private static void Snapshot()
    {
        var cam = Camera.main;
        var eye = cam != null ? cam.transform.position : Vector3.zero;

        // Particle effects (smoke, fire, sparks): how many are drawing, and the biggest-looking ones
        try
        {
            var shown = new List<(float look, string text)>();
            var count = 0;
            foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<Renderer>(), FindObjectsSortMode.None))
            {
                var r = o.TryCast<Renderer>();
                if (r == null || !r.enabled || !r.isVisible || r.GetIl2CppType().Name != "ParticleSystemRenderer")
                    continue;
                count++;
                var b = r.bounds;
                var d = Mathf.Max(0.3f, Vector3.Distance(eye, b.center));
                var look = b.size.magnitude / d;   // roughly how much of the view it can cover
                shown.Add((look, $"'{r.name}' ({Path(r.transform)}) {b.size.magnitude:0.0} m across, {d:0.0} m away"));
            }
            shown.Sort((a, b) => b.look.CompareTo(a.look));
            Plugin.Logger.LogInfo($"[GpuSpike] Particle effects drawing: {count}");
            for (var i = 0; i < shown.Count && i < 10; i++)
                Plugin.Logger.LogInfo($"[GpuSpike]   {shown[i].text}");
        }
        catch (Exception e) { Plugin.Logger.LogInfo($"[GpuSpike] particles: {e.GetType().Name}: {e.Message}"); }

        // Lights casting shadows (each one draws the scene again)
        try
        {
            var lights = new List<string>();
            foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<Light>(), FindObjectsSortMode.None))
            {
                var l = o.TryCast<Light>();
                if (l != null && l.isActiveAndEnabled && l.shadows != LightShadows.None)
                    lights.Add($"'{l.name}' {l.type} {Vector3.Distance(eye, l.transform.position):0} m");
            }
            Plugin.Logger.LogInfo($"[GpuSpike] Lights with shadows: {lights.Count}: {string.Join("; ", lights.GetRange(0, Math.Min(10, lights.Count)))}");
        }
        catch (Exception e) { Plugin.Logger.LogInfo($"[GpuSpike] lights: {e.GetType().Name}: {e.Message}"); }

        // Screen-effect volumes in force
        try
        {
            foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<Volume>(), FindObjectsSortMode.None))
            {
                var v = o.TryCast<Volume>();
                if (v == null || !v.isActiveAndEnabled || v.weight <= 0.001f || v.sharedProfile == null)
                    continue;
                var on = new List<string>();
                foreach (var c in v.sharedProfile.components)
                    if (c != null && c.active)
                        on.Add(c.GetIl2CppType().Name);
                Plugin.Logger.LogInfo($"[GpuSpike] Volume '{v.name}' weight {v.weight:0.00}{(v.isGlobal ? " global" : "")}: {string.Join(", ", on)}");
            }
        }
        catch (Exception e) { Plugin.Logger.LogInfo($"[GpuSpike] volumes: {e.GetType().Name}: {e.Message}"); }

        // Cameras drawing
        try
        {
            var cams = new List<string>();
            foreach (var c in Camera.allCameras)
                if (c != null)
                    cams.Add($"'{c.name}'{(c.targetTexture != null ? $" -> {c.targetTexture.width}x{c.targetTexture.height}" : "")}");
            Plugin.Logger.LogInfo($"[GpuSpike] Cameras on: {string.Join(", ", cams)}");
        }
        catch { }
    }

    private static string Path(Transform t)
    {
        var parts = new List<string>();
        for (var p = t.parent; p != null && parts.Count < 3; p = p.parent)
            parts.Insert(0, p.name);
        return parts.Count == 0 ? "-" : string.Join("/", parts);
    }

    // ---- Quick test: each feature off for a moment ----

    private void StartTest()
    {
        var cam = Camera.main;
        _hd = cam != null ? cam.GetComponent<HDAdditionalCameraData>() : null;
        _tests.Clear();
        if (_hd != null)
        {
            AddField("Transparent objects (smoke, fire, particles, glass)", FrameSettingsField.TransparentObjects);
            AddField("Shadow maps", FrameSettingsField.ShadowMaps);
            AddField("Post-processing", FrameSettingsField.Postprocess);
            AddField("Custom passes", FrameSettingsField.CustomPass);
            AddField("Decals", FrameSettingsField.Decals);
            AddField("Fog / atmosphere", FrameSettingsField.AtmosphericScattering);
            AddField("Opaque objects", FrameSettingsField.OpaqueObjects);
        }
        _test = 0;
        _frame = 0;
        _off = false;
        _samples.Clear();
        if (_tests.Count == 0)
            Stop("no camera settings to test");
    }

    private void AddField(string name, FrameSettingsField f)
    {
        bool wasEnabled = false, wasMasked = false, wasCustom = false;
        var hd = _hd;
        _tests.Add((name, () =>
        {
            var s = hd.m_RenderingPathCustomFrameSettings;
            var m = hd.renderingPathCustomFrameSettingsOverrideMask;
            wasCustom = hd.customRenderingSettings;
            wasMasked = m.mask[(uint)f];
            wasEnabled = s.IsEnabled(f);
            s.SetEnabled(f, false);
            m.mask[(uint)f] = true;
            hd.m_RenderingPathCustomFrameSettings = s;
            hd.renderingPathCustomFrameSettingsOverrideMask = m;
            hd.customRenderingSettings = true;
            return true;
        }, () =>
        {
            if (hd == null)
                return;
            var s = hd.m_RenderingPathCustomFrameSettings;
            var m = hd.renderingPathCustomFrameSettingsOverrideMask;
            s.SetEnabled(f, wasEnabled);
            m.mask[(uint)f] = wasMasked;
            hd.m_RenderingPathCustomFrameSettings = s;
            hd.renderingPathCustomFrameSettingsOverrideMask = m;
            hd.customRenderingSettings = wasCustom;
        }));
    }

    // Per test: measure with it on, switch it off, measure, put it back
    private void StepTest(float gpu)
    {
        if (_hd == null)
        {
            Stop("the camera went away");
            return;
        }
        _frame++;
        if (_frame > Settle && gpu > 0f)
            _samples.Add(gpu);
        if (_frame < Settle + Measure)
            return;
        var median = Median(_samples);
        var (name, apply, undo) = _tests[_test];
        if (!_off)
        {
            _before = median;
            apply();
            _off = true;
        }
        else
        {
            undo();
            _off = false;
            Plugin.Logger.LogInfo($"[GpuSpike] {name}: GPU {_before:0} -> {median:0} ms with it off");
            _test++;
            if (_test >= _tests.Count)
            {
                Stop("done");
                return;
            }
        }
        _frame = 0;
        _samples.Clear();
    }

    private void Stop(string why)
    {
        if (_test >= 0 && _off && _test < _tests.Count)
            try { _tests[_test].undo(); } catch { }
        if (_test >= 0)
            Plugin.Logger.LogInfo($"[GpuSpike] Test {(why == "done" ? "finished" : $"stopped ({why})")}; everything is back as it was");
        _test = -1;
        _off = false;
        _done = true;   // once per game
    }

    private static float Median(List<float> v)
    {
        if (v.Count == 0)
            return 0f;
        var c = new List<float>(v);
        c.Sort();
        return c[c.Count / 2];
    }
}
