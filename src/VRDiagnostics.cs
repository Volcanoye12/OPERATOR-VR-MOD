using System;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.XR;
using Il2CppList = Il2CppSystem.Collections.Generic.List<UnityEngine.XR.XRDisplaySubsystemDescriptor>;
using Il2CppInputList = Il2CppSystem.Collections.Generic.List<UnityEngine.XR.XRInputSubsystemDescriptor>;
using Il2CppDisplayList = Il2CppSystem.Collections.Generic.List<UnityEngine.XR.XRDisplaySubsystem>;

namespace OperatorVR;

/// <summary>
/// Step 1 of the VR mod: logs everything we need to know about the game's XR and HDRP setup
/// before we try to start OpenXR. Output goes to BepInEx\LogOutput.log.
/// </summary>
public class VRDiagnostics : MonoBehaviour
{
    public VRDiagnostics(IntPtr ptr) : base(ptr) { }

    private float _nextCameraCheck;
    private string _lastCamera = "";

    private void Start()
    {
        var log = Plugin.Logger;
        log.LogInfo("===== OperatorVR diagnostics =====");

        // The game turns Unity's logger off, so its errors never reached BepInEx (which listens to it).
        // Warnings and errors only: rendering errors are what we're after
        Section("Unity log", () =>
        {
            var unityLog = Debug.unityLogger;
            log.LogInfo($"Unity logger was enabled={unityLog.logEnabled}");
            unityLog.logEnabled = true;
            var logger = unityLog.TryCast<UnityEngine.Logger>();
            if (logger != null)
                logger.filterLogType = LogType.Warning;
            log.LogInfo("Unity logger on (warnings and errors), shown as 'Unity' lines");
            // BepInEx's own listener didn't pass Unity messages on, so listen directly
            Application.add_logMessageReceived(
                Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Application.LogCallback>(
                    new Action<string, string, LogType>(OnUnityLog)));
            // Proves capture works: should appear as a '[Unity Warning]' line
            Debug.LogWarning("OperatorVR test warning: Unity log capture works");
        });

        Section("Engine", () =>
        {
            log.LogInfo($"Unity {Application.unityVersion}, {Application.productName} {Application.version}");
            log.LogInfo($"Graphics: {SystemInfo.graphicsDeviceType} on {SystemInfo.graphicsDeviceName}");
        });

        Section("XRSettings", () =>
        {
            log.LogInfo($"enabled={XRSettings.enabled}, deviceActive={XRSettings.isDeviceActive}, loadedDevice='{XRSettings.loadedDeviceName}'");
            log.LogInfo($"supportedDevices=[{string.Join(", ", (string[])XRSettings.supportedDevices)}]");
        });

        Section("XR subsystem descriptors", () =>
        {
            var displays = new Il2CppList();
            SubsystemManager.GetSubsystemDescriptors(displays);
            log.LogInfo($"Display descriptors: {displays.Count}");
            foreach (var d in displays)
                log.LogInfo($"  display: {d.id}");

            var inputs = new Il2CppInputList();
            SubsystemManager.GetSubsystemDescriptors(inputs);
            log.LogInfo($"Input descriptors: {inputs.Count}");
            foreach (var d in inputs)
                log.LogInfo($"  input: {d.id}");

            var running = new Il2CppDisplayList();
            SubsystemManager.GetSubsystems(running);
            log.LogInfo($"Display subsystem instances: {running.Count}");
        });

        Section("Render pipeline", () =>
        {
            var rp = GraphicsSettings.currentRenderPipeline;
            log.LogInfo($"Current pipeline asset: {(rp == null ? "none" : rp.name)}");

            var hdrp = rp?.TryCast<HDRenderPipelineAsset>();
            if (hdrp == null)
            {
                log.LogWarning("Pipeline is not an HDRenderPipelineAsset");
                return;
            }

            var xr = hdrp.currentPlatformRenderPipelineSettings.xrSettings;
            // GlobalXRSettings.enabled is stripped in this build; only these fields survive
            log.LogInfo($"HDRP XR: singlePass={xr.singlePass}, occlusionMesh={xr.occlusionMesh}, cameraJitter={xr.cameraJitter}, motionBlur={xr.allowMotionBlur}");
        });

        log.LogInfo("===== end diagnostics (camera changes will follow) =====");
    }

    // Frame-rate log while VR runs: low or stalling frame rates make the headset reproject old frames
    private float _fpsWindowStart;
    private int _fpsFrames;
    private float _fpsWorst;
    private int _fpsSlow;
    private bool _wasVrForFps;

    // Camera renders per frame, from RenderPipelineManager events: HDRP raises them once per camera per XR
    // pass, so the main camera should show 2 per frame in multi-pass. In 'Georgia 1 Day' the right eye never
    // gets written; 1 per frame there would mean HDRP skips the right-eye pass
    private static readonly System.Collections.Generic.Dictionary<string, int> s_begins = new();
    private static readonly System.Collections.Generic.Dictionary<string, int> s_ends = new();
    private bool _hooked;

    private void HookCameraEvents()
    {
        if (_hooked)
            return;
        _hooked = true;
        try
        {
            RenderPipelineManager.add_beginCameraRendering(
                Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ScriptableRenderContext, Camera>>(
                    new Action<ScriptableRenderContext, Camera>((_, c) => Bump(s_begins, c))));
            RenderPipelineManager.add_endCameraRendering(
                Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ScriptableRenderContext, Camera>>(
                    new Action<ScriptableRenderContext, Camera>((_, c) => Bump(s_ends, c))));
            Plugin.Logger.LogInfo("[Perf] counting camera renders");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Perf] camera events: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void Bump(System.Collections.Generic.Dictionary<string, int> d, Camera c)
    {
        if (!OpenXRBootstrap.IsRunning)
            return;
        var name = c == null ? "-" : c.name;
        d.TryGetValue(name, out var n);
        d[name] = n + 1;
    }

    private static string PerFrame(System.Collections.Generic.Dictionary<string, int> d, int frames)
    {
        var parts = new System.Collections.Generic.List<string>();
        foreach (var kv in d)
            parts.Add($"'{kv.Key}' {kv.Value / (float)Math.Max(1, frames):0.00}/frame");
        d.Clear();
        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    // HDRP keeps one HDCamera per camera per XR pass, each with its own frame counter. If the right eye's
    // counter climbs in 'Georgia 1 Day', the right eye is rendered and its output lost; if not, it's skipped
    private readonly System.Collections.Generic.Dictionary<IntPtr, int> _lastHdFrames = new();

    private void LogHdCameras()
    {
        try
        {
            var all = UnityEngine.Rendering.HighDefinition.HDCamera.s_Cameras;
            if (all == null)
                return;
            var parts = new System.Collections.Generic.List<string>();
            foreach (var hd in all.Values)
            {
                if (hd == null)
                    continue;
                var frames = (int)hd.cameraFrameCount;
                _lastHdFrames.TryGetValue(hd.Pointer, out var before);
                _lastHdFrames[hd.Pointer] = frames;
                var xr = hd.xr;
                var eye = xr == null ? "?" : xr.enabled ? xr.multipassId.ToString() : "none";
                parts.Add($"'{(hd.camera == null ? "-" : hd.camera.name)}' eye {eye} frames {frames} (+{frames - before}) {hd.actualWidth}x{hd.actualHeight} views={hd.viewCount}");
            }
            Plugin.Logger.LogInfo($"[Perf] HDRP cameras: {(parts.Count == 0 ? "none" : string.Join("; ", parts))}");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Perf] HDRP cameras: {e.GetType().Name}: {e.Message}");
        }
    }

    // CPU vs GPU time per frame, to know which one holds the frame rate down
    private Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<FrameTiming> _timings;

    private void LogFrameTimings()
    {
        try
        {
            _timings ??= new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<FrameTiming>(60);
            var n = FrameTimingManager.GetLatestTimings(60, _timings);
            if (n == 0)
            {
                Plugin.Logger.LogInfo($"[Perf] frame timings unavailable (feature enabled={FrameTimingManager.IsFeatureEnabled()})");
                return;
            }
            double cpu = 0, main = 0, render = 0, gpu = 0;
            for (var i = 0; i < n; i++)
            {
                var t = _timings[i];
                cpu += t.cpuFrameTime;
                main += t.cpuMainThreadFrameTime;
                render += t.cpuRenderThreadFrameTime;
                gpu += t.gpuFrameTime;
            }
            Plugin.Logger.LogInfo($"[Perf] last {n} frames, average ms: CPU {cpu / n:0.0} (main thread {main / n:0.0}, render thread {render / n:0.0}), GPU {gpu / n:0.0}");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Perf] frame timings: {e.GetType().Name}: {e.Message}");
        }
    }

    private void LogFrameRate()
    {
        try { FrameTimingManager.CaptureFrameTimings(); } catch { }
        // The game may switch Unity's logger back off (e.g. on scene load); keep it on so errors reach the log
        if (!Debug.unityLogger.logEnabled)
        {
            Debug.unityLogger.logEnabled = true;
            Plugin.Logger.LogInfo("[Perf] Unity logger had been switched off again; turned it back on");
        }

        if (!OpenXRBootstrap.IsRunning)
        {
            // Flat screen too, to compare with VR (the user's request, 2026-11-03): frame rate only
            if (_wasVrForFps)
            {
                _wasVrForFps = false;
                _fpsFrames = 0;
                _fpsWorst = 0f;
                _fpsSlow = 0;
                _fpsWindowStart = Time.unscaledTime;
            }
            var flatDt = Time.unscaledDeltaTime;
            _fpsFrames++;
            _fpsWorst = Mathf.Max(_fpsWorst, flatDt);
            if (flatDt > 1f / 60f)
                _fpsSlow++;
            var flatElapsed = Time.unscaledTime - _fpsWindowStart;
            if (flatElapsed < 10f)
                return;
            Plugin.Logger.LogInfo($"[Perf] (flat screen) {_fpsFrames / flatElapsed:0} fps over {flatElapsed:0}s, worst frame {_fpsWorst * 1000f:0} ms, " +
                                  $"{_fpsSlow} frames under 60 fps, scene '{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}', " +
                                  $"{Screen.width}x{Screen.height}");
            LogFrameTimings();
            _fpsWindowStart = Time.unscaledTime;
            _fpsFrames = 0;
            _fpsWorst = 0f;
            _fpsSlow = 0;
            return;
        }
        if (!_wasVrForFps)
        {
            _wasVrForFps = true;
            _fpsFrames = 0;
            _fpsWorst = 0f;
            _fpsSlow = 0;
            _fpsWindowStart = Time.unscaledTime;
        }
        HookCameraEvents();

        var dt = Time.unscaledDeltaTime;
        _fpsFrames++;
        _fpsWorst = Mathf.Max(_fpsWorst, dt);
        if (dt > 1f / 45f)
            _fpsSlow++;

        var elapsed = Time.unscaledTime - _fpsWindowStart;
        if (elapsed < 10f)
            return;
        Plugin.Logger.LogInfo($"[Perf] {_fpsFrames / elapsed:0} fps over {elapsed:0}s, worst frame {_fpsWorst * 1000f:0} ms, " +
                              $"{_fpsSlow} frames under 45 fps, scene '{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}', " +
                              $"XR passes {OpenXRBootstrap.RenderPassCount}");
        LogHdCameras();
        LogFrameTimings();
        Plugin.Logger.LogInfo($"[Perf] mod scripts: {ModTime.Report(_fpsFrames)}");
        var cam = Camera.main;
        Plugin.Logger.LogInfo($"[Perf] camera renders begun: {PerFrame(s_begins, _fpsFrames)}; finished: {PerFrame(s_ends, _fpsFrames)}; " +
                              $"main camera near={(cam == null ? 0 : cam.nearClipPlane):0.###} far={(cam == null ? 0 : cam.farClipPlane):0.#}");
        _fpsWindowStart = Time.unscaledTime;
        _fpsFrames = 0;
        _fpsWorst = 0f;
        _fpsSlow = 0;
    }

    private void Update()
    {
        using var __t = ModTime.Measure("VRDiagnostics");
        LogFrameRate();

        if (Time.unscaledTime < _nextCameraCheck)
            return;
        _nextCameraCheck = Time.unscaledTime + 2f;

        var cam = Camera.main;
        var desc = cam == null
            ? "none"
            : $"'{cam.name}' fov={cam.fieldOfView:0.#} near={cam.nearClipPlane:0.###} stereo={cam.stereoEnabled}";

        if (desc == _lastCamera)
            return;
        _lastCamera = desc;
        Plugin.Logger.LogInfo($"Main camera: {desc}");
    }

    // Same message repeated every frame would flood the log: count repeats, log the first few in full
    private static readonly System.Collections.Generic.Dictionary<string, int> s_unitySeen = new();

    private static readonly System.Collections.Generic.HashSet<LogType> s_stacksOff = new();
    private static string Shorten(string s) => s == null ? "" : s.Length <= 60 ? s : s.Substring(0, 60) + "...";

    private static void OnUnityLog(string message, string stackTrace, LogType type)
    {
        try
        {
            if (type == LogType.Log)
                return;
            var key = type + message;
            s_unitySeen.TryGetValue(key, out var n);
            s_unitySeen[key] = ++n;
            // A message repeating every frame (the scope camera's "Dimensions of color surface ..." about once a frame
            // while aimed, thousands a session) made Unity collect a call stack each time, on the main thread, right
            // while aiming (2026-11-06). After 50 repeats of anything, Unity stops collecting stacks for that kind of
            // message; the first ones above still show where they came from
            if (n == 50 && (type == LogType.Error || type == LogType.Warning) && !s_stacksOff.Contains(type))
            {
                s_stacksOff.Add(type);
                try
                {
                    Application.SetStackTraceLogType(type, StackTraceLogType.None);
                    Plugin.Logger.LogInfo($"[Unity] '{Shorten(message)}' repeats every frame: Unity {type} lines no longer collect call stacks (saves main-thread time)");
                }
                catch { }
            }
            if (n > 3 && n % 500 != 0)
                return;
            var firstFrames = string.Join(" | ", (stackTrace ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(4));
            Plugin.Logger.LogWarning($"[Unity {type}] (x{n}) {message} @ {firstFrames}");
        }
        catch
        {
            // never throw from Unity's log callback
        }
    }

    private static void Section(string name, Action body)
    {
        try
        {
            body();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[{name}] failed: {e.GetType().Name}: {e.Message}");
        }
    }
}
