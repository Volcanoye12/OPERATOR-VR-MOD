using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.XR;
using DisplayDescriptors = Il2CppSystem.Collections.Generic.List<UnityEngine.XR.XRDisplaySubsystemDescriptor>;
using InputDescriptors = Il2CppSystem.Collections.Generic.List<UnityEngine.XR.XRInputSubsystemDescriptor>;
using static OperatorVR.OpenXRNative;

namespace OperatorVR;

/// <summary>
/// Starts and stops OpenXR by hand, following the same steps as the package's OpenXRLoaderBase
/// (Initialize -> Start -> Stop -> Deinitialize). F4 starts VR, F9 stops it (F4, not F8: the ODG Ops modpack uses F8).
/// </summary>
public class OpenXRBootstrap : MonoBehaviour
{
    public OpenXRBootstrap(IntPtr ptr) : base(ptr) { }

    private const int VK_F4 = 0x73;
    private const int VK_F9 = 0x78;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    // Kept in a static so the GC never collects the delegate native code is holding
    private static ReceiveNativeEventDelegate s_callback;
    private static OpenXRBootstrap s_instance;

    /// <summary>True while the display subsystem is running and frames go to the headset.</summary>
    internal static bool IsRunning => s_instance != null && s_instance._display != null && s_instance._display.running;

    private XRDisplaySubsystem _display;

    /// <summary>How many render passes the XR display asks for this frame (2 in multi-pass), or -1.</summary>
    internal static int RenderPassCount => IsRunning ? s_instance._display.GetRenderPassCount() : -1;
    private XRInputSubsystem _input;
    private bool _initialized;
    private bool _sessionBegun;
    private bool _actionsAttached;
    private NativeEvent _state;
    private bool _f4Down, _f9Down;

    private static void Log(string msg) => Plugin.Logger.LogInfo($"[OpenXR] {msg}");
    private static void Warn(string msg) => Plugin.Logger.LogWarning($"[OpenXR] {msg}");
    private static void Error(string msg) => Plugin.Logger.LogError($"[OpenXR] {msg}");

    // Auto start: once per run, this many seconds after the game is up (Steam launch option --vr, or VR.AutoStart)
    private float _autoStartAt = -1f;
    private int _autoTries;
    private bool _autoDone;
    private const int MaxAutoTries = 8;
    private const float AutoRetrySeconds = 10f;
    private bool _loggedFrontEndWait;
    private bool _wasInGame;
    private float _nextPump;

    // The game's loading screen and main menu scenes
    private static readonly string[] FrontEndScenes = { "ApplicationSplashScreen", "3d Menu" };

    private static bool InFrontEnd()
    {
        var name = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? "";
        return name.Length == 0 || Array.IndexOf(FrontEndScenes, name) >= 0;
    }

    private void Awake()
    {
        s_instance = this;
        Log("Ready. Press F4 to start VR, F9 to stop.");

        var delay = Plugin.AutoStartDelay.Value;
        var fromLaunchOption = false;
        foreach (var arg in Environment.GetCommandLineArgs())
        {
            var a = arg.TrimStart('-').ToLowerInvariant();
            if (a == "vr")
                fromLaunchOption = true;
            else if (a.StartsWith("vr=") && float.TryParse(a.Substring(3), System.Globalization.NumberStyles.Float,
                         System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            {
                fromLaunchOption = true;
                delay = seconds;
            }
        }
        if (fromLaunchOption || Plugin.AutoStart.Value)
        {
            _autoStartAt = Time.realtimeSinceStartup + Math.Max(0f, delay);
            Log($"VR starts by itself in {delay:0} s ({(fromLaunchOption ? "launch option" : "VR.AutoStart")}); F4 starts it sooner");
        }
    }

    private void Update()
    {
        using var __t = ModTime.Measure("OpenXR");
        if (KeyPressed(VK_F4, ref _f4Down))
        {
            _autoStartAt = -1f;
            _autoDone = true;   // started by hand: no more automatic tries
            TryRun("start", StartVR);
        }
        // Not on the loading screen or the main menu: with a minute-long loading screen VR started before the menu, and
        // the main menu in VR couldn't be clicked. Wait until a playable scene (the armoury, a mission) is loaded
        if (_autoStartAt > 0f && Time.realtimeSinceStartup >= _autoStartAt && InFrontEnd())
        {
            _autoStartAt = Time.realtimeSinceStartup + 2f;
            if (!_loggedFrontEndWait)
            {
                _loggedFrontEndWait = true;
                Log($"VR waits until you're in the game (now on '{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}'); F4 starts it now");
            }
        }
        if (_autoStartAt > 0f && Time.realtimeSinceStartup >= _autoStartAt)
        {
            _autoStartAt = -1f;
            if (!IsRunning && !_initialized)
            {
                _autoTries++;
                Log(_autoTries == 1 ? "Starting VR by itself" : $"Starting VR by itself (try {_autoTries} of {MaxAutoTries})");
                TryRun("auto start", StartVR);
                // Started from the desktop, SteamVR isn't up yet: the first try wakes it, then fails while it starts
                // ("XrInstanceChanged", subsystems not created). Keep trying until it's ready
                if (!_initialized && _autoTries < MaxAutoTries)
                {
                    _autoStartAt = Time.realtimeSinceStartup + AutoRetrySeconds;
                    Log($"VR didn't start (SteamVR may still be starting); trying again in {AutoRetrySeconds:0} s");
                }
            }
        }
        // A try that got as far as a session but was then ended by the runtime (still starting up) also tries again
        if (_autoTries > 0 && _autoTries < MaxAutoTries && _autoStartAt < 0f && !_initialized && !_autoDone)
        {
            _autoStartAt = Time.realtimeSinceStartup + AutoRetrySeconds;
            Log($"VR stopped while starting; trying again in {AutoRetrySeconds:0} s");
        }
        if (IsRunning)
            _autoDone = true;
        // Leaving a match goes back to the main menu, which can't be clicked in VR (2026-11-01: stuck there after
        // leaving the outdoor range). Pause VR there, so the menu is used on the desktop as at startup (Virtual Desktop
        // shows it in the headset), and start it again by itself once you're back in the game
        // Only on the way back from the game: F4 pressed on the menu itself still starts VR there
        if (IsRunning && !InFrontEnd())
            _wasInGame = true;
        if (_initialized && _wasInGame && Plugin.PauseInMainMenu.Value && InFrontEnd())
        {
            _wasInGame = false;
            Log("Back at the main menu: VR pauses so the menu can be used on the desktop; it comes back by itself in the game");
            TryRun("pause for main menu", StopVR);
            _autoTries = 0;
            _autoDone = false;
            _loggedFrontEndWait = false;
            _autoStartAt = Time.realtimeSinceStartup + 2f;
        }
        if (KeyPressed(VK_F9, ref _f9Down))
        {
            _autoDone = true;   // stopped on purpose: don't start again by itself
            _autoStartAt = -1f;
            TryRun("stop", StopVR);
        }

        // The package pumps from Application.onBeforeRender. Every frame it took ~1.5 ms of the main thread (the mod's
        // costliest part); the events it delivers (session state changes) don't need it that often. Every 4th frame was
        // still ~0.4 ms/frame on average (2026-11-05): now five times a second, whatever the frame rate
        if (_initialized && Time.realtimeSinceStartup >= _nextPump)
        {
            _nextPump = Time.realtimeSinceStartup + 0.2f;
            PumpMessageLoop();
        }
    }

    private void OnApplicationQuit()
    {
        TryRun("shutdown", StopVR);
    }

    private static bool KeyPressed(int vk, ref bool wasDown)
    {
        var down = (GetAsyncKeyState(vk) & 0x8000) != 0;
        var pressed = down && !wasDown;
        wasDown = down;
        return pressed;
    }

    private static void TryRun(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Error($"{what} threw {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
        }
    }

    // ---- Initialize (OpenXRLoaderBase.InitializeInternal) ----

    private void StartVR()
    {
        if (_initialized)
        {
            Log("Already running");
            return;
        }

        Log("Initializing...");
        SetSuccessfullyInitialized(false);

        ChooseRuntime();

        // Full path without extension, like the package passes in the editor; the native side adds .dll
        var loaderPath = System.IO.Path.Combine(PluginDir, "openxr_loader");
        Log($"Loading OpenXR loader from {loaderPath}.dll");
        if (!LoadOpenXRLibrary(ToWideString(loaderPath)))
        {
            Error("Failed to load openxr_loader.dll. Is a VR runtime (SteamVR / Oculus) installed and set as the OpenXR runtime?");
            return;
        }
        Log("openxr_loader loaded");

        SetProcAddressPtrAndLoadStage1(GetProcAddressPtr(true));

        if (!InitializeSession())
        {
            Error("InitializeSession failed. Is the headset connected and the VR runtime running?");
            UnloadOpenXRLibrary();
            return;
        }
        Log("Session initialized");

        s_callback ??= OnNativeEvent;
        SetCallbacks(s_callback);

        SetSymmetricProjection(false);
        SetRenderMode(Plugin.StereoRenderMode.Value);
        Log($"Render mode: {GetRenderMode()}");

        // In some levels the right eye's frames were never shown (old image, or black after a restart).
        // With depth submission the runtime gets a depth image per eye too and may drop a view whose depth is bad
        Log($"Depth submission was {GetDepthSubmissionMode()}");
        SetDepthSubmissionMode(Plugin.DepthSubmission.Value);
        Log($"Depth submission now {GetDepthSubmissionMode()}");

        SetApplicationInfo(Application.productName, Application.version, VersionHash(Application.version), Application.unityVersion);

        bool created;
        try { created = CreateSubsystems(); }
        catch (Exception e)
        {
            // SteamVR still starting: the runtime reported XrInstanceChanged and the descriptors were gone
            Error($"Creating the OpenXR subsystems threw {e.GetType().Name}: {e.Message} (VR runtime still starting?)");
            created = false;
        }
        if (!created)
        {
            Error("Could not create OpenXR display/input subsystems. Check UnitySubsystems\\UnityOpenXR\\UnitySubsystemsManifest.json is in OPERATOR_Data.");
            Teardown();
            return;
        }

        _initialized = true;
        Log("Initialized. Creating session...");

        // ---- Start (OpenXRLoaderBase.StartInternal); the rest happens when XrReady arrives ----
        if (!CreateSessionIfNeeded())
        {
            Error("CreateSessionIfNeeded failed");
            Teardown();
            return;
        }
        Log("Waiting for the runtime to report XrReady (put the headset on)...");
    }

    // Which OpenXR runtime the game talks to (VR.Runtime). SteamVR is the PC's default; Virtual Desktop's own runtime
    // (VDXR) skips SteamVR in between, often faster on a Quest. The OpenXR loader takes XR_RUNTIME_JSON over the
    // system's choice, so this only affects OPERATOR
    private const string VirtualDesktopRuntime = @"C:\Program Files\Virtual Desktop Streamer\OpenXR\virtualdesktop-openxr.json";

    private static void ChooseRuntime()
    {
        var want = (Plugin.Runtime.Value ?? "").Trim();
        string json = null;
        if (want.Equals("VirtualDesktop", StringComparison.OrdinalIgnoreCase))
            json = VirtualDesktopRuntime;
        else if (want.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            json = want;
        if (json != null && !System.IO.File.Exists(json))
        {
            Warn($"VR.Runtime '{want}': {json} not found; using the PC's default runtime");
            json = null;
        }
        Environment.SetEnvironmentVariable("XR_RUNTIME_JSON", json);
        Log(json == null ? "Runtime: the PC's default (SteamVR)" : $"Runtime: {json}");
    }

    private bool CreateSubsystems()
    {
        var displays = new DisplayDescriptors();
        SubsystemManager.GetSubsystemDescriptors(displays);
        foreach (var d in displays)
        {
            if (d.id == "OpenXR Display")
            {
                _display = d.Create();
                break;
            }
        }

        var inputs = new InputDescriptors();
        SubsystemManager.GetSubsystemDescriptors(inputs);
        foreach (var d in inputs)
        {
            if (d.id == "OpenXR Input")
            {
                _input = d.Create();
                break;
            }
        }

        Log($"Display descriptors={displays.Count}, input descriptors={inputs.Count}, display={(_display != null)}, input={(_input != null)}");
        return _display != null && _input != null;
    }

    private void StartSubsystems()
    {
        if (_display == null || _input == null)
            return;

        // Display must start before input so input can see the session
        if (!_display.running)
            _display.Start();
        if (!_display.running)
        {
            Error("Display subsystem failed to start");
            return;
        }

        BeginSession();
        _sessionBegun = true;

        // Same point as the package's loader: after xrBeginSession, before input starts; once per OpenXR instance
        if (!_actionsAttached)
            _actionsAttached = OpenXRInputSetup.Attach();

        if (!_input.running)
            _input.Start();

        SetSuccessfullyInitialized(true);

        // Eye images are 2688x2880 at 1.0 with this headset/runtime; the woodland managed ~27 fps rendering both
        // eyes, so render smaller and let the runtime scale up
        _display.scaleOfAllRenderTargets = Mathf.Clamp(Plugin.RenderScale.Value, 0.3f, 1.5f);
        Log($"Render scale {_display.scaleOfAllRenderTargets:0.00}");

        // Foveated rendering: Unity's own switch did nothing with HDRP on D3D11 (2026-11-01); NvFoveation does it
        // through the NVIDIA driver instead

        Log($"VR started: display={_display.running}, input={_input.running}, renderPasses={_display.GetRenderPassCount()}, " +
            $"eyeTexture={XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight}, device='{XRSettings.loadedDeviceName}'");
    }

    // ---- Stop + Deinitialize ----

    private void StopVR()
    {
        if (!_initialized)
            return;

        Log("Stopping...");
        if (_input != null && _input.running)
            _input.Stop();
        if (_display != null && _display.running)
            _display.Stop();
        if (_sessionBegun)
        {
            EndSession();
            _sessionBegun = false;
        }
        PumpMessageLoop();
        Teardown();
        Log("Stopped");
    }

    private void Teardown()
    {
        RequestExitSession();
        PumpMessageLoop();

        _input?.Destroy();
        _display?.Destroy();
        _input = null;
        _display = null;

        DestroySession();
        PumpMessageLoop();
        UnloadOpenXRLibrary();
        _initialized = false;
        _actionsAttached = false;
    }

    // ---- Native callback (OpenXRLoaderBase.ReceiveNativeEvent) ----

    private static long s_unfocusedAt;

    private static void OnNativeEvent(NativeEvent e, ulong payload)
    {
        var self = s_instance;
        if (self == null)
            return;

        if (e != NativeEvent.XrSessionStateChanged)
            Log($"event {e}");

        self._state = e;
        switch (e)
        {
            case NativeEvent.XrReady:
                TryRun("start subsystems", self.StartSubsystems);
                break;
            case NativeEvent.XrFocused:
                Log("Headset focused: you should now see the game in VR");
                // Back after the headset was off or the Quest menu was up for a while (a pause, the bathroom): the
                // head is somewhere else now, so recenter on it once it has settled (the user, 2026-11-10)
                if (s_unfocusedAt > 0 && Environment.TickCount64 - s_unfocusedAt > 3000)
                    HeadTracking.RecenterAfter(1500, $"back after {(Environment.TickCount64 - s_unfocusedAt) / 1000} s away from the game");
                s_unfocusedAt = 0;
                break;
            case NativeEvent.XrVisible:
            case NativeEvent.XrSynchronized:
            case NativeEvent.XrIdle:
                if (s_unfocusedAt == 0)
                    s_unfocusedAt = Environment.TickCount64;
                break;
            case NativeEvent.XrChangedSpaceApp:
                // The headset's own recenter (holding the Oculus button), or its floor/space reset after it was put
                // back on: follow it
                HeadTracking.RecenterAfter(800, "the headset reset its space (Oculus button held, or put back on)");
                break;
            case NativeEvent.XrStopping:
                TryRun("session stopping", () =>
                {
                    if (self._sessionBegun)
                    {
                        EndSession();
                        self._sessionBegun = false;
                    }
                });
                break;
            case NativeEvent.XrExiting:
            case NativeEvent.XrLossPending:
            case NativeEvent.XrInstanceLossPending:
                Warn($"Runtime ended the session ({e}); press F4 to try again");
                TryRun("teardown", self.StopVR);
                break;
            case NativeEvent.XrRequestRestartLoop:
            case NativeEvent.XrRequestGetSystemLoop:
                Warn("Runtime could not find a headset; connect it and press F4 again");
                TryRun("teardown", self.StopVR);
                break;
        }
    }

    private static uint VersionHash(string version)
    {
        using var md5 = MD5.Create();
        var data = md5.ComputeHash(Encoding.UTF8.GetBytes(version));
        if (BitConverter.IsLittleEndian)
            Array.Reverse(data);
        return BitConverter.ToUInt32(data, 0);
    }
}
