using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.XR;

namespace OperatorVR;

/// <summary>
/// Rendering.GpuAudit (2026-11-10, speed): finds out what the GPU spends its time on, instead of guessing. Once per
/// game, a minute into the first mission in VR, it turns one graphics feature off at a time for about two seconds and
/// measures the GPU time per frame with and without it (baseline, test, baseline, ...; the whole list twice). Results
/// go to the log as "[GpuAudit]" lines, most expensive first. The picture changes for a moment during each test; stand
/// still and look at a busy view. Two controller buzzes at the start, three at the end.
/// </summary>
public class GpuAudit : MonoBehaviour
{
    public GpuAudit(IntPtr ptr) : base(ptr) { }

    private const int SettleFrames = 20;    // GPU times arrive a few frames late, and the change needs to take hold
    private const int MeasureFrames = 80;
    private const int Rounds = 2;
    private const float StartAfter = 60f;   // seconds into the mission

    private sealed class Test
    {
        public string Name;
        public Func<bool> Apply;   // false = not applicable now (skipped)
        public Action Undo;
        public readonly List<float> Deltas = new(), MainDeltas = new(), RenderDeltas = new();
    }

    private readonly List<Test> _tests = new();
    private readonly List<(Test test, bool on)> _phases = new();   // on = the test's feature is switched off
    private int _phase = -1, _frame;
    private readonly List<float> _gpu = new(), _main = new(), _render = new();
    private (float gpu, float main, float render) _lastBaseline;
    private (float gpu, float main, float render)? _pendingTest;
    private Test _pendingTestOf;
    private readonly List<float> _baselines = new();

    private bool _done;
    private string _scene;
    private float _sceneSince;
    private Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<FrameTiming> _timings;

    private static readonly HashSet<string> NotMissions = new() { "ApplicationSplashScreen", "3d Menu", "Operation Room", "" };

    private void Update()
    {
        if (_done || !Plugin.GpuAudit.Value)
            return;
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? "";
        if (scene != _scene)
        {
            if (_phase >= 0)
                Abort($"the scene changed to '{scene}'");
            _scene = scene;
            _sceneSince = Time.unscaledTime;
        }
        if (_phase < 0)
        {
            if (!OpenXRBootstrap.IsRunning || NotMissions.Contains(scene) || Time.unscaledTime - _sceneSince < StartAfter)
                return;
            Begin();
            return;
        }
        if (!OpenXRBootstrap.IsRunning)
        {
            Abort("VR stopped");
            return;
        }
        try { Step(); }
        catch (Exception e) { Abort($"{e.GetType().Name}: {e.Message}"); }
    }

    private void Begin()
    {
        var cam = Camera.main;
        var hd = cam == null ? null : cam.GetComponent<HDAdditionalCameraData>();
        if (hd == null)
            return;
        BuildTests(hd);
        _phases.Clear();
        for (var r = 0; r < Rounds; r++)
        {
            _phases.Add((null, false));
            foreach (var t in _tests)
            {
                _phases.Add((t, true));
                _phases.Add((null, false));
            }
        }
        _phase = 0;
        _frame = 0;
        _gpu.Clear(); _main.Clear(); _render.Clear();
        var seconds = _phases.Count * (SettleFrames + MeasureFrames) / 45f;
        Plugin.Logger.LogInfo($"[GpuAudit] Starting in '{_scene}': {_tests.Count} features, {Rounds} rounds, about {seconds:0} s. " +
                              "Stand still and look at a busy view; the picture will change for a moment now and then.");
        Buzz(0.3f);
        StatusHud.ShowNotice("SPEED TEST - STAND STILL", 3600f);
    }

    private void Step()
    {
        if (Time.unscaledDeltaTime > 0.25f && _frame > 0)
        {
            // A load or a hitch: this phase's numbers are spoiled, so measure it again
            _frame = 0;
            _gpu.Clear(); _main.Clear(); _render.Clear();
            return;
        }
        _frame++;
        if (_frame > SettleFrames)
        {
            _timings ??= new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<FrameTiming>(1);
            if (FrameTimingManager.GetLatestTimings(1, _timings) == 1)
            {
                var t = _timings[0];
                if (t.gpuFrameTime > 0)
                {
                    _gpu.Add((float)t.gpuFrameTime);
                    _main.Add((float)t.cpuMainThreadFrameTime);
                    _render.Add((float)t.cpuRenderThreadFrameTime);
                }
            }
        }
        if (_frame < SettleFrames + MeasureFrames)
            return;

        var (test, on) = _phases[_phase];
        if (_gpu.Count < MeasureFrames / 2)
        {
            Abort($"no GPU timings ({_gpu.Count} of {MeasureFrames} frames)");
            return;
        }
        var result = (Median(_gpu), Median(_main), Median(_render));
        if (on)
        {
            test.Undo();
            _pendingTest = result;
            _pendingTestOf = test;
        }
        else
        {
            _baselines.Add(result.Item1);
            if (_pendingTest is { } pt)
            {
                // Against the average of the baselines just before and just after
                var b = ((_lastBaseline.gpu + result.Item1) / 2f, (_lastBaseline.main + result.Item2) / 2f, (_lastBaseline.render + result.Item3) / 2f);
                _pendingTestOf.Deltas.Add(b.Item1 - pt.gpu);
                _pendingTestOf.MainDeltas.Add(b.Item2 - pt.main);
                _pendingTestOf.RenderDeltas.Add(b.Item3 - pt.render);
                Plugin.Logger.LogInfo($"[GpuAudit] {_pendingTestOf.Name}: GPU {b.Item1:0.00} -> {pt.gpu:0.00} ms with it off " +
                                      $"(main thread {b.Item2:0.0} -> {pt.main:0.0}, render thread {b.Item3:0.0} -> {pt.render:0.0})");
                _pendingTest = null;
            }
            _lastBaseline = result;
        }

        _phase++;
        _frame = 0;
        _gpu.Clear(); _main.Clear(); _render.Clear();
        if (_phase >= _phases.Count)
        {
            Finish();
            return;
        }
        var next = _phases[_phase];
        if (next.on && !next.test.Apply())
        {
            // Not there on this map: skip the test and its following baseline gap is just another baseline
            Plugin.Logger.LogInfo($"[GpuAudit] {next.test.Name}: nothing to switch off here, skipped");
            _phases[_phase] = (null, false);
        }
    }

    private void Finish()
    {
        _done = true;
        _phase = -1;
        _tests.Sort((a, b) => Average(b.Deltas).CompareTo(Average(a.Deltas)));
        Plugin.Logger.LogInfo($"[GpuAudit] Results in '{_scene}', GPU per frame (both eyes) {Median(_baselines):0.00} ms with everything on. " +
                              "Saved by switching each off (round 1 / round 2):");
        foreach (var t in _tests)
        {
            if (t.Deltas.Count == 0)
                continue;
            Plugin.Logger.LogInfo($"[GpuAudit]   {t.Name.PadRight(36)} GPU {Average(t.Deltas).ToString("0.00").PadLeft(6)} ms ({string.Join(" / ", t.Deltas.ConvertAll(d => d.ToString("0.00")))}), " +
                                  $"main thread {Average(t.MainDeltas):0.00}, render thread {Average(t.RenderDeltas):0.00}");
        }
        Plugin.Logger.LogInfo("[GpuAudit] Done; everything is back as it was. (Rendering.GpuAudit can be set back to false.)");
        Buzz(1.0f);
        StatusHud.ShowNotice("SPEED TEST DONE", 6f);
    }

    private void Abort(string why)
    {
        if (_phase >= 0 && _phase < _phases.Count && _phases[_phase].on)
            try { _phases[_phase].test.Undo(); } catch { }
        _phase = -1;
        _pendingTest = null;
        _baselines.Clear();
        foreach (var t in _tests) { t.Deltas.Clear(); t.MainDeltas.Clear(); t.RenderDeltas.Clear(); }
        _sceneSince = Time.unscaledTime;   // try again a minute later
        StatusHud.ShowNotice("SPEED TEST STOPPED", 6f);
        if (++_aborts >= 3)
            _done = true;
        Plugin.Logger.LogInfo($"[GpuAudit] Stopped: {why}; everything is back as it was. " +
                              (_done ? "Giving up for this game." : $"Trying again in {StartAfter:0} s."));
    }

    private int _aborts;

    /// <summary>While a test runs, VREffects leaves the camera alone (its 3 s check would switch features back on)</summary>
    internal static bool Running { get; private set; }

    private void LateUpdate() => Running = _phase >= 0 && !_done;

    private static void Buzz(float seconds)
    {
        try
        {
            Haptics.Pulse(XRNode.LeftHand, 0.6f, seconds);
            Haptics.Pulse(XRNode.RightHand, 0.6f, seconds);
        }
        catch { }
    }

    // ---- The tests ----

    private void BuildTests(HDAdditionalCameraData hd)
    {
        _tests.Clear();
        // Run 1 (2026-11-10, Georgia, GPU 17.7 ms): post-processing 2.6 ms, decals 1.2, transparents 1.2, fog 1.1
        // (unsteady), shadow maps 1.05, SMAA 0.8, reflection probes 0.8, AO 0.5; the rest within the noise; foveation
        // saves 2.4. Run 2 splits post-processing into its parts, with decals, fog and foveation again for comparison
        AddFrameSetting(hd, "Post-processing (all of it)", FrameSettingsField.Postprocess);
        AddFrameSetting(hd, "Game's custom post (Milk, Suppression)", FrameSettingsField.CustomPostProcess);
        AddVolumeComponent("MilkPostSettings only", "MilkPostSettings");
        AddVolumeComponent("SuppressionPostFX only", "SuppressionPostFX");
        AddFrameSetting(hd, "Auto exposure", FrameSettingsField.ExposureControl);
        AddFrameSetting(hd, "Colour grading", FrameSettingsField.ColorGrading);
        AddFrameSetting(hd, "Tonemapping", FrameSettingsField.Tonemapping);
        AddFrameSetting(hd, "Film grain", FrameSettingsField.FilmGrain);
        AddFrameSetting(hd, "Chromatic aberration", FrameSettingsField.ChromaticAberration);
        AddFrameSetting(hd, "Lens distortion", FrameSettingsField.LensDistortion);
        AddFrameSetting(hd, "Vignette", FrameSettingsField.Vignette);
        AddFrameSetting(hd, "Dithering", FrameSettingsField.Dithering);
        AddFrameSetting(hd, "Stop NaN", FrameSettingsField.StopNaN);
        AddFrameSetting(hd, "Decals", FrameSettingsField.Decals);
        AddFrameSetting(hd, "Fog / atmosphere", FrameSettingsField.AtmosphericScattering);

        _tests.Add(new Test
        {
            Name = "Anti-aliasing (SMAA)",
            Apply = () =>
            {
                if (hd == null || hd.antialiasing == HDAdditionalCameraData.AntialiasingMode.None)
                    return false;
                _aaWas = hd.antialiasing;
                hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.None;
                return true;
            },
            Undo = () => { if (hd != null) hd.antialiasing = _aaWas; },
        });
        _tests.Add(new Test
        {
            Name = "Foveation off (cost of NOT using it)",
            Apply = () =>
            {
                if (Plugin.FoveatedRendering.Value <= 0f)
                    return false;
                NvFoveation.AuditPause = true;
                return true;
            },
            Undo = () => NvFoveation.AuditPause = false,
        });
    }

    private HDAdditionalCameraData.AntialiasingMode _aaWas;

    // A volume effect switched off in every volume (e.g. the game's own post-processing components)
    private void AddVolumeComponent(string name, string typeName)
    {
        var off = new List<VolumeComponent>();
        _tests.Add(new Test
        {
            Name = name,
            Apply = () =>
            {
                off.Clear();
                foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppInterop.Runtime.Il2CppType.Of<Volume>(), FindObjectsSortMode.None))
                {
                    var v = o.TryCast<Volume>();
                    if (v == null || !v.isActiveAndEnabled || v.sharedProfile == null)
                        continue;
                    foreach (var comp in v.sharedProfile.components)
                        if (comp != null && comp.active && comp.GetIl2CppType().Name == typeName)
                        {
                            comp.active = false;
                            off.Add(comp);
                        }
                }
                return off.Count > 0;
            },
            Undo = () =>
            {
                foreach (var comp in off)
                    try { if (comp != null) comp.active = true; } catch { }
                off.Clear();
            },
        });
    }

    private void AddFrameSetting(HDAdditionalCameraData hd, string name, FrameSettingsField field)
    {
        bool wasEnabled = false, wasMasked = false, wasCustom = false;
        _tests.Add(new Test
        {
            Name = name,
            Apply = () =>
            {
                if (hd == null)
                    return false;
                var s = hd.m_RenderingPathCustomFrameSettings;
                var m = hd.renderingPathCustomFrameSettingsOverrideMask;
                // Already off by the camera's own (or our) settings: nothing to measure
                var effective = hd.customRenderingSettings && m.mask[(uint)field] ? s.IsEnabled(field) : true;
                if (!effective)
                    return false;
                wasCustom = hd.customRenderingSettings;
                wasMasked = m.mask[(uint)field];
                wasEnabled = s.IsEnabled(field);
                s.SetEnabled(field, false);
                m.mask[(uint)field] = true;
                hd.m_RenderingPathCustomFrameSettings = s;
                hd.renderingPathCustomFrameSettingsOverrideMask = m;
                hd.customRenderingSettings = true;
                return true;
            },
            Undo = () =>
            {
                if (hd == null)
                    return;
                var s = hd.m_RenderingPathCustomFrameSettings;
                var m = hd.renderingPathCustomFrameSettingsOverrideMask;
                s.SetEnabled(field, wasEnabled);
                m.mask[(uint)field] = wasMasked;
                hd.m_RenderingPathCustomFrameSettings = s;
                hd.renderingPathCustomFrameSettingsOverrideMask = m;
                hd.customRenderingSettings = wasCustom;
            },
        });
    }

    private static float Median(List<float> v)
    {
        if (v.Count == 0)
            return 0f;
        var c = new List<float>(v);
        c.Sort();
        return c[c.Count / 2];
    }

    private static float Average(List<float> v)
    {
        if (v.Count == 0)
            return 0f;
        var sum = 0f;
        foreach (var x in v)
            sum += x;
        return sum / v.Count;
    }
}
