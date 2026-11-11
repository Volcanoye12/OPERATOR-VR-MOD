using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.XR;

namespace OperatorVR;

/// <summary>
/// Plays OPERATOR with the Oculus Touch controllers. The game reads keyboard and mouse through its InputLayer
/// (a PS5 pad only works because Steam Input turns it into keyboard/mouse; the Input System has no gamepad), so
/// Touch buttons press the keys the player has bound to game actions (read from InputLayer.Binds, so remapped
/// keys follow), the left stick presses the walk keys, and the right stick moves the mouse. Input goes through
/// Windows SendInput, the same path as a real keyboard, so the game's tap/hold/toggle handling is unchanged.
/// The button -> action map is Controls.TouchMap (game action names, or Escape).
/// </summary>
public class TouchKeys : MonoBehaviour
{
    public TouchKeys(IntPtr ptr) : base(ptr) { }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();

    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_SCANCODE = 0x8;
    private const uint MOUSEEVENTF_MOVE = 0x1, MOUSEEVENTF_LEFTDOWN = 0x2, MOUSEEVENTF_LEFTUP = 0x4,
                       MOUSEEVENTF_RIGHTDOWN = 0x8, MOUSEEVENTF_RIGHTUP = 0x10, MOUSEEVENTF_MIDDLEDOWN = 0x20,
                       MOUSEEVENTF_MIDDLEUP = 0x40, MOUSEEVENTF_XDOWN = 0x80, MOUSEEVENTF_XUP = 0x100;

    // Touch inputs that can be mapped: name -> (hand, XR usage). Triggers and grips count as pressed past half way
    private static readonly Dictionary<string, (XRNode hand, string usage, bool analog)> Buttons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = (XRNode.RightHand, "PrimaryButton", false),
        ["B"] = (XRNode.RightHand, "SecondaryButton", false),
        ["X"] = (XRNode.LeftHand, "PrimaryButton", false),
        ["Y"] = (XRNode.LeftHand, "SecondaryButton", false),
        ["RightTrigger"] = (XRNode.RightHand, "Trigger", true),
        ["LeftTrigger"] = (XRNode.LeftHand, "Trigger", true),
        ["RightGrip"] = (XRNode.RightHand, "Grip", true),
        ["LeftGrip"] = (XRNode.LeftHand, "Grip", true),
        ["RightStickClick"] = (XRNode.RightHand, "Primary2DAxisClick", false),
        ["LeftStickClick"] = (XRNode.LeftHand, "Primary2DAxisClick", false),
        ["Menu"] = (XRNode.LeftHand, "MenuButton", false),
        ["LeftThumbrest"] = (XRNode.LeftHand, "ThumbrestTouch", false),
        ["RightThumbrest"] = (XRNode.RightHand, "ThumbrestTouch", false),
    };

    // Virtual D-pads: while Controls.TouchDpadButton is held the right stick is a D-pad (and doesn't turn); while
    // Controls.TouchLeftDpadButton is held the left stick is a second one, LeftDpad... (and doesn't walk). A
    // direction counts past 70% that way, and more that way than sideways
    private static readonly Dictionary<string, (Vector2 dir, bool leftStick)> DpadDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DpadUp"] = (Vector2.up, false),
        ["DpadDown"] = (Vector2.down, false),
        ["DpadLeft"] = (Vector2.left, false),
        ["DpadRight"] = (Vector2.right, false),
        ["LeftDpadUp"] = (Vector2.up, true),
        ["LeftDpadDown"] = (Vector2.down, true),
        ["LeftDpadLeft"] = (Vector2.left, true),
        ["LeftDpadRight"] = (Vector2.right, true),
    };

    private static bool IsButton(string name) => Buttons.ContainsKey(name) || DpadDirs.ContainsKey(name);

    // Not on the weapon bench: there the right stick turns the weapon and scrolls, and a left thumb resting on its
    // thumbrest made it a D-pad instead (2026-11-04; the D-pad is wanted in play only)
    private static bool DpadHeld() => !WeaponBench.Open && Held(Plugin.TouchDpadButton.Value);

    private static bool LeftDpadHeld() => Held(Plugin.TouchLeftDpadButton.Value);

    private static bool Held(string button)
    {
        var b = button?.Trim();
        return !string.IsNullOrEmpty(b) && Buttons.ContainsKey(b) && IsPressed(b);
    }

    private class Mapping
    {
        public string Button;
        public string Action;
        public int KeyCode;      // Unity KeyCode
        public bool Down;
    }

    private readonly List<Mapping> _map = new();
    private readonly Dictionary<string, Mapping> _interactTwins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Mapping[] _walk = new Mapping[4]; // forward, back, left, right
    private float _nextBindRead;
    private bool _loggedStart;
    private bool _loggedWaiting;
    private bool _loggedBinds;
    private static int s_comboLogs;
    private Vector2 _mouseRemainder;

    internal static bool Active { get; private set; }

    private void Update()
    {
        using var __t = ModTime.Measure("TouchKeys");
        var run = OpenXRBootstrap.IsRunning && Plugin.TouchControls.Value;
        if (!run)
        {
            ReleaseAll();
            Active = false;
            return;
        }

        try
        {
            var left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            var right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            if (!left.isValid && !right.isValid)
            {
                ReleaseAll();
                Active = false;
                return;
            }

            if (Time.unscaledTime >= _nextBindRead)
            {
                _nextBindRead = Time.unscaledTime + 5f;
                ReadBindings();
            }
            Active = _map.Count > 0;

            // Keys go to whichever window has focus; only send them to the game
            if (GetActiveWindow() == IntPtr.Zero)
            {
                ReleaseAll();
                return;
            }

            // In a menu (or at the laptop) A is the mouse click and the trigger does nothing: the trigger (Shoot, the
            // left mouse button) clicked the menu but also fired the gun
            var inMenu = VRPointer.Target != null;
            // The menu click: the right trigger (Controls.MenuClickButton; it was A until 2026-11-04)
            var clickButton = Plugin.MenuClickButton.Value?.Trim() ?? "";
            UpdateMenuClick(inMenu && Held(clickButton));

            // Interact can be on more than one button (A and the right stick click): hold-to-interact keeps one state,
            // so it runs once, on the first of them, held while any of them is (A doesn't count in a menu)
            var holdInteract = Plugin.HoldToInteract.Value;
            Mapping interactLead = null;
            var interactDown = false;
            if (holdInteract)
                foreach (var m in _map)
                    if (string.Equals(m.Action, "Interact", StringComparison.OrdinalIgnoreCase))
                    {
                        interactLead ??= m;
                        if (!(inMenu && m.Button == "A") && IsPressed(m.Button))
                            interactDown = true;
                    }

            // A double tap on an Interact button (TouchDoubleTap, e.g. A=Mouse3: the flashlight) runs alongside
            // hold-to-interact, through a stand-in mapping with no key of its own, so taps and holds still interact
            // straight away and only the double tap adds its keys
            if (holdInteract)
                foreach (var m in _map)
                    if (string.Equals(m.Action, "Interact", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!_interactTwins.TryGetValue(m.Button, out var twin))
                            _interactTwins[m.Button] = twin = new Mapping { Button = m.Button, Action = "(double tap)" };
                        UpdateButton(twin, !(inMenu && m.Button == "A") && IsPressed(m.Button));
                    }

            var autoAds = UpdateAutoAds(right, inMenu);
            foreach (var m in _map)
            {
                if (autoAds && string.Equals(m.Action, "Aim", StringComparison.OrdinalIgnoreCase))
                {
                    UpdateButton(m, true);
                    continue;
                }
                if (holdInteract && string.Equals(m.Action, "Interact", StringComparison.OrdinalIgnoreCase))
                {
                    if (m == interactLead)
                        UpdateInteract(m, interactDown);
                    else
                        Set(m, false);
                    continue;
                }
                // Magazine pulled out by hand (ManualReload): the gun doesn't fire until one goes back in
                if (ManualReload.MagOut && string.Equals(m.Action, "Shoot", StringComparison.OrdinalIgnoreCase))
                {
                    Set(m, false);
                    continue;
                }
                if (inMenu && (m.Button == "A" || string.Equals(m.Button, clickButton, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(m.Action, "Shoot", StringComparison.OrdinalIgnoreCase)))
                {
                    Set(m, false);
                    continue;
                }
                UpdateButton(m, IsPressed(m.Button));
            }

            // Left stick: walk keys, past a third of the way (not while it's the left virtual D-pad)
            var stick = LeftDpadHeld() ? Vector2.zero : Stick(left);
            Set(_walk[0], stick.y > 0.35f);
            Set(_walk[1], stick.y < -0.35f);
            Set(_walk[2], stick.x < -0.35f);
            Set(_walk[3], stick.x > 0.35f);

            // Right stick turns; the hand aims. Not while a menu or laptop screen takes the mouse, nor while the
            // right stick is the virtual D-pad
            var look = DpadHeld() ? Vector2.zero : Stick(right);
            // On the weapon bench the right stick's left/right turns the weapon round
            if (WeaponBench.Open && Mathf.Abs(look.x) > 0.15f)
                WeaponBench.SpinInput += look.x * Plugin.BenchSpinSpeed.Value * Time.unscaledDeltaTime;
            UpdateBenchDrag(right, inMenu && Held(clickButton));
            // In a menu the right stick's up/down scrolls (the mouse wheel); on the bench too, for its parts list
            UpdateMenuScroll(inMenu ? look.y : 0f);
            UpdateHeadLean(inMenu);
            UpdateHoldRecenter(right, inMenu);
            UpdateStickRecenter(right);
            UpdateSway(Plugin.HandAimNoSway.Value && (Plugin.HandAimPitch.Value || Plugin.HandAimYaw.Value));
            StockOn = _stockOn && Time.unscaledTime - _stockLastCall < 0.5f;
            var curve = look.sqrMagnitude > 0.15f * 0.15f ? look * look.magnitude : Vector2.zero; // finer near the centre
            var move = Vector2.zero;

            // The range switch is a hold (tested in flat), not a drag: no special stick handling, just log its state
            IsDraggingHandle();

            var gameYawNow = 0f;
            if (_yawFailed && Time.unscaledTime >= _yawRetryAt)
            {
                _yawFailed = false;
                Plugin.Logger.LogInfo("[Touch] Left/right hand aim back on");
            }
            var handYaw = Plugin.HandAimYaw.Value && !_yawFailed && right.isValid && HeadTracking.TryGetGameAim(out _, out gameYawNow);
            if (handYaw)
            {
                // The view gets its own yaw, turned only by the stick; the game's yaw (and the gun) follows the hand
                if (!_viewYawSet)
                {
                    _viewYaw = gameYawNow;
                    _viewYawSet = true;
                }
                if (VRPointer.Target == null)
                    _viewYaw += curve.x * Plugin.TouchTurnDegrees.Value * Time.unscaledDeltaTime;
                HeadTracking.SetViewYaw(_viewYaw);
            }
            else
            {
                _viewYawSet = false;
            }

            if (VRPointer.Target == null)
            {
                if (!handYaw)
                    move.x += curve.x * Plugin.TouchTurnSpeed.Value * Time.unscaledDeltaTime;
                move.y -= curve.y * Plugin.TouchLookUpDown.Value * Plugin.TouchTurnSpeed.Value * Time.unscaledDeltaTime;
                move += HandAimCorrection(right, Plugin.HandAimPitch.Value, handYaw);
            }
            else if (_lookToInteract)
            {
                // Near a laptop the pointer has a target, but holding Interact must still aim the game at what you look at
                move += HandAimCorrection(right, true, handYaw);
            }
            if (move != Vector2.zero)
            {
                move += _mouseRemainder;
                var dx = (int)move.x;
                var dy = (int)move.y;
                _mouseRemainder = move - new Vector2(dx, dy);
                if (dx != 0 || dy != 0)
                    Mouse(MOUSEEVENTF_MOVE, dx, dy, 0);
            }
            else
            {
                _mouseRemainder = Vector2.zero;
            }

            if (!_loggedStart && (stick != Vector2.zero || look != Vector2.zero))
            {
                _loggedStart = true;
                Plugin.Logger.LogInfo("[Touch] First Touch input sent to the game as keyboard/mouse");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Touch] {e.GetType().Name}: {e.Message}");
            ReleaseAll();
        }
    }

    // Hold to interact: while the Interact button is held the game's aim follows the head, and Interact is pressed
    // once the aim is there. Swapping the camera to the head just inside the game's interaction scan made the range
    // switch's prompt flash on and off and never take the press
    private bool _lookToInteract;
    private bool _interactSent;
    private float _interactSince;
    private float _lastAimGap = 999f;

    // A click is a whole action (2026-11-04, Interact on the right stick click): the swing to where you look waited up
    // to 0.5 s before pressing F, and a quick click was let go first, so ~20 clicks at the weapon wall, bench and door
    // never sent F. And at the bench the game already showed MODIFY WEAPON on the hand's aim, but the swing moved the
    // aim off it. Now: if the game's own scan already finds something under its aim, F goes at once with no swing;
    // otherwise the swing carries on after the click is let go until F has been pressed, and F stays down at least
    // InteractMinHold so the game sees it
    private bool _interactWasDown;
    private float _sentAt;
    private const float InteractMinHold = 0.15f;

    private void UpdateInteract(Mapping m, bool down)
    {
        var now = Time.unscaledTime;
        var pressedNow = down && !_interactWasDown;
        _interactWasDown = down;

        // Range switch: anywhere in the booth, gun ahead = target away, gun at the floor = target back, used directly
        // every frame, with F held as well so the game sees a real hold and release. The gun stays on the hand here
        // (no swinging to where you look)
        if (down && UseGazedRangeHandle())
        {
            _lookToInteract = false;
            Set(m, true);
            return;
        }

        if (pressedNow && !_lookToInteract && !_interactSent)
        {
            if (InteractScanLog.Found && now - InteractScanLog.ScannedAt < 0.3f)
            {
                // The game already offers something where it aims: use it now
                _interactSent = true;
                _sentAt = now;
                Set(m, true);
                Plugin.Logger.LogInfo("[Touch] Interact pressed at once (the game already aims at something usable)");
                Haptics.Pulse(XRNode.RightHand, 0.25f, 0.03f);
            }
            else
            {
                _lookToInteract = true;
                _interactSince = now;
                _lastAimGap = 999f;
                // How far the head (the view) is from the game's body: the scan starts from the body
                var offset = -1f;
                if (HeadTracking.TryTrackingToWorld(InputTracking.GetLocalPosition(XRNode.CenterEye),
                        InputTracking.GetLocalRotation(XRNode.CenterEye), out var headWorld, out _) &&
                    HeadTracking.TryGetGameCameraPose(out _, out var bodyPos, out _))
                    offset = Vector3.Distance(new Vector3(headWorld.x, 0f, headWorld.z), new Vector3(bodyPos.x, 0f, bodyPos.z));
                Plugin.Logger.LogInfo($"[Touch] Interact ({m.Button}): aiming where you look first; head {offset:0.00} m from the game's body");
            }
        }

        // Press Interact once the aim has reached where you look (or after half a second regardless), even if the
        // click has already been let go
        if (_lookToInteract && !_interactSent && (_lastAimGap < 3f || now - _interactSince > 0.5f))
        {
            _interactSent = true;
            _sentAt = now;
            Set(m, true);
            Plugin.Logger.LogInfo($"[Touch] Interact pressed (aim {_lastAimGap:0.0}Â° off where you look)");
            Haptics.Pulse(XRNode.RightHand, 0.25f, 0.03f);
        }

        // Done once the button is up, any swing has pressed F, and F has been down long enough
        var swinging = _lookToInteract && !_interactSent;
        if (down || swinging || (_interactSent && now - _sentAt < InteractMinHold))
            return;

        if (_lookToInteract)
            Plugin.Logger.LogInfo("[Touch] Interact let go: aim back on the hand");
        _lookToInteract = false;
        _interactSent = false;
        Set(m, false);
        // Calling the switch directly left it switched on (the game never saw F let go), so the target ran to
        // the end, the other part offered nothing and the aim stayed locked: switch it off ourselves
        if (_lastUsedHandle != null)
        {
            try
            {
                _lastUsedHandle.inputActive = false;
                Plugin.Logger.LogInfo($"[Touch] Range switch '{_lastUsedHandle.name}' let go");
            }
            catch { }
            _lastUsedHandle = null;
        }
    }

    private InteractableRangeTargetHandle _lastUsedHandle;

    /// <summary>
    /// In a range booth (near its target switch, whose parts are named "Forwards" = move the target away and
    /// "Backwards" = bring it back): gun pointing ahead (or at the switch) uses Forwards, pointing at the floor uses
    /// Backwards, calling its Interact() this frame as the game does while F is held on it. Picking the small part by
    /// looking at it was awkward in VR. The switch sits low, so aiming at it already points the gun ~20-30Â° down:
    /// only a clear point at the floor means back.
    /// Returns true if one was used.
    /// </summary>
    private bool UseGazedRangeHandle()
    {
        RefreshHandles();
        if (_handles.Count == 0)
            return false;
        var headRot = InputTracking.GetLocalRotation(XRNode.CenterEye);
        var headPos = InputTracking.GetLocalPosition(XRNode.CenterEye);
        if (!HeadTracking.TryTrackingToWorld(headPos, headRot, out var eye, out _))
            return false;

        // Which way the gun points: the right controller's aim, up or down
        var right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        if (!right.isValid || !XRFeature.Vector3(right.deviceId, "PointerPosition", out var pos) ||
            !XRFeature.Quaternion(right.deviceId, "PointerRotation", out var rot) ||
            !HeadTracking.TryTrackingToWorld(pos, rot, out _, out var gunRot))
            return false;
        var gunDown = -Mathf.Asin(Mathf.Clamp((gunRot * Vector3.forward).y, -1f, 1f)) * Mathf.Rad2Deg;
        var wantName = gunDown < BoothBackDegrees ? "Forward" : "Backward";

        // The nearest part with that name in the booth
        InteractableRangeTargetHandle best = null;
        var bestDist = BoothReach;
        foreach (var h in _handles)
        {
            if (h == null || !h.isActiveAndEnabled || h.name.IndexOf(wantName, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            var dist = Vector3.Distance(eye, h.transform.position);
            if (dist < bestDist)
            {
                best = h;
                bestDist = dist;
            }
        }
        if (best == null)
            return false;

        // Looking from one part to the other while holding: let go of the first
        if (_lastUsedHandle != null && _lastUsedHandle.Pointer != best.Pointer)
        {
            try { _lastUsedHandle.inputActive = false; } catch { }
        }
        best.Interact();
        if (_lastUsedHandle == null || _lastUsedHandle.Pointer != best.Pointer)
        {
            _lastUsedHandle = best;
            Plugin.Logger.LogInfo($"[Touch] Range switch '{best.name}' (value {best.value:0.##}) used, gun {gunDown:0}Â° down " +
                                  $"({bestDist:0.00} m away)");
        }
        return true;
    }

    // Gun pointing more than this far below level = bring the target back; the booth reaches this far from the switch
    private const float BoothBackDegrees = 50f;
    private const float BoothReach = 3f;

    private void RefreshHandles()
    {
        if (Time.unscaledTime < _nextHandleScan)
            return;
        _nextHandleScan = Time.unscaledTime + 3f;
        _handles.Clear();
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<InteractableRangeTargetHandle>(), FindObjectsSortMode.None))
        {
            var h = o.TryCast<InteractableRangeTargetHandle>();
            if (h != null)
                _handles.Add(h);
        }
    }

    // Range target handles in the scene, re-found every few seconds (the shoot house loads them with the room)
    private readonly List<InteractableRangeTargetHandle> _handles = new();
    private float _nextHandleScan;
    private bool _wasDragging;

    // The game's interaction prompt texts, logged when they change (to learn how things like the range switch are used)
    private PlayerNetworking _localPlayer;
    private string _lastPrompt;
    private float _nextPlayerFind;

    private void LogPromptChanges()
    {
        try
        {
            if (_localPlayer == null && Time.unscaledTime >= _nextPlayerFind)
            {
                _nextPlayerFind = Time.unscaledTime + 3f;
                foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<PlayerNetworking>(), FindObjectsSortMode.None))
                {
                    var p = o.TryCast<PlayerNetworking>();
                    if (p != null && p.gameObject.name.Contains("MyPlayer"))
                        _localPlayer = p;
                }
            }
            if (_localPlayer == null)
                return;
            string Text(TMPro.TextMeshProUGUI t) => t == null || !t.isActiveAndEnabled ? "" : t.text;
            var prompt = $"{Text(_localPlayer.interactTxt)} | {Text(_localPlayer.InteractionContextText)}";
            if (prompt != _lastPrompt)
            {
                _lastPrompt = prompt;
                Plugin.Logger.LogInfo($"[Touch] Prompt: {prompt.Replace("\n", " / ")}");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[Touch] Prompt log: {e.GetType().Name}: {e.Message}");
            _nextPlayerFind = Time.unscaledTime + 30f;
        }
    }

    /// <summary>True while the player is holding a range target handle (its inputActive), which the game moves with the mouse.</summary>
    private bool IsDraggingHandle()
    {
        if (Plugin.Diagnostics.Value) LogPromptChanges();
        RefreshHandles();

        InteractableRangeTargetHandle active = null;
        foreach (var h in _handles)
            if (h != null && h.inputActive)
            {
                active = h;
                break;
            }

        var dragging = active != null;
        if (dragging != _wasDragging)
        {
            _wasDragging = dragging;
            Plugin.Logger.LogInfo(dragging
                ? $"[Touch] Holding range target handle '{active.name}' (value {active.value:0.00}): right stick moves it, hand aim paused"
                : "[Touch] Let go of the range target handle: hand aim back on");
        }
        return dragging;
    }

    // The VR view's yaw while the hand aims left/right (the game's own yaw then follows the gun)
    private float _viewYaw;
    private bool _viewYawSet;
    private float _yawDivergingFor, _yawGapAtStart, _yawRetryAt;
    private bool _yawFailed;

    // Hand aim diagnostics: the first few corrections, to check direction and gain
    private int _aimLogs;
    private float _nextAimLog;

    /// <summary>
    /// Mouse pixels (right/down = positive) that move the game's aim towards where the right controller points:
    /// up/down if pitch, left/right if yaw. The game's mouse sensitivity is unknown, so this is a proportional
    /// loop: each frame closes part of the gap. Up/down was confirmed to go the right way (gaps stay under ~3Â°).
    /// The view doesn't follow (head + view yaw); only the game's aim, which the weapon follows, does.
    /// </summary>
    private Vector2 HandAimCorrection(UnityEngine.XR.InputDevice right, bool pitch, bool yaw)
    {
        if ((!pitch && !yaw) || !right.isValid || !HeadTracking.TryGetGameAim(out var gamePitch, out var gameYaw))
            return Vector2.zero;
        Quaternion worldRot;
        if (_lookToInteract)
        {
            // Holding Interact: aim where you look (the head), so the game "looks at" what you want to use
            var headRot = InputTracking.GetLocalRotation(XRNode.CenterEye);
            var headPos = InputTracking.GetLocalPosition(XRNode.CenterEye);
            if (!HeadTracking.TryTrackingToWorld(headPos, headRot, out _, out worldRot))
                return Vector2.zero;
        }
        else
        {
            var id = right.deviceId;
            if (!XRFeature.Vector3(id, "PointerPosition", out var pos) || !XRFeature.Quaternion(id, "PointerRotation", out var rot))
                return Vector2.zero;
            if (!HeadTracking.TryTrackingToWorld(pos, rot, out _, out worldRot))
                return Vector2.zero;
        }

        var dir = worldRot * Vector3.forward;
        if (!_lookToInteract)
        {
            dir = VirtualStock(right, dir);
            dir = SmoothAim(dir);
        }
        // Unity pitch: positive looks down (the hand offset only applies to the hand)
        var handPitch = -Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg + (_lookToInteract ? 0f : Plugin.HandAimPitchOffset.Value);
        var handYaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        var pitchGap = pitch ? Mathf.DeltaAngle(gamePitch, handPitch) : 0f;
        var yawGap = yaw ? Mathf.DeltaAngle(gameYaw, handYaw) : 0f;

        // Guard: if the game turned the opposite way to the mouse the yaw loop would spin the player round. It used to
        // switch left/right aim off for the session when the gap stayed over 60Â° for a second, but that also happens
        // when the game just won't turn for a moment (an animation, a scene change), and then the gun felt locked
        // for the rest of the game. Now only a gap still growing after a second counts as a spin, and aim comes back
        // after 3 s
        if (yaw)
        {
            if (Mathf.Abs(yawGap) > 60f)
            {
                if (_yawDivergingFor == 0f)
                    _yawGapAtStart = Mathf.Abs(yawGap);
                _yawDivergingFor += Time.unscaledDeltaTime;
            }
            else
                _yawDivergingFor = 0f;
            if (_yawDivergingFor > 1f)
            {
                var growing = Mathf.Abs(yawGap) > _yawGapAtStart + 30f;
                _yawDivergingFor = 0f;
                if (growing)
                {
                    _yawFailed = true;
                    _yawRetryAt = Time.unscaledTime + 3f;
                    Plugin.Logger.LogWarning($"[Touch] Left/right hand aim is running away (gap {_yawGapAtStart:0}Â° -> {yawGap:0}Â° in a second); paused for 3 s");
                    // Twice it ran away with the virtual stock on (2026-11-04): switch the stock off cleanly too
                    ResetStock("the aim ran away");
                    yawGap = 0f;
                }
            }
        }

        _lastAimGap = Mathf.Max(Mathf.Abs(pitchGap), Mathf.Abs(yawGap));
        var gain = Plugin.HandAimGain.Value;
        var px = new Vector2(
            Mathf.Abs(yawGap) < 0.3f ? 0f : Mathf.Clamp(yawGap * gain, -80f, 80f),
            Mathf.Abs(pitchGap) < 0.3f ? 0f : Mathf.Clamp(pitchGap * gain, -80f, 80f));
        if (_aimLogs < 10 && Time.unscaledTime >= _nextAimLog && px != Vector2.zero)
        {
            _aimLogs++;
            _nextAimLog = Time.unscaledTime + 1f;
            Plugin.Logger.LogInfo($"[Touch] Hand aim: pitch hand {handPitch:0.0}Â° game {gamePitch:0.0}Â°, yaw hand {handYaw:0.0}Â° game {gameYaw:0.0}Â° " +
                                  $"(view {_viewYaw:0.0}Â°) -> mouse {px.x:0.0}, {px.y:0.0} px");
        }
        return px;
    }

    // Virtual stock on a button (Controls.VirtualStockButton, the right grip; a press toggles it with
    // VirtualStockToggle, else held). The right controller where you pressed is the stock, locked against your head
    // (it turns with your head's left/right), and moving the left controller left/right/up/down aims: the aim turns
    // left/right and up/down by exactly as much as the line from the stock to the left hand does since you pressed,
    // starting from where the right hand was aiming (no jump).
    // History (2026-11-04): engaging by bringing the hands together jumped and flickered. The next version turned a
    // whole frame built on the line, and the log showed it engaging with the left hand 60-97Â° off the aim (beside the
    // right hand, not in front): the aim then moved sideways to the hand and was hard to steer, and in toggle mode it
    // stayed on with the aim stuck ("controls went static"). Now it engages only with the left hand in front, uses
    // plain left/right + up/down changes, and switches itself off if the left hand leaves (taken off the gun)
    private bool _stockOn;
    private float _stockBlend;
    private Vector3 _stockLine0, _stockLine, _stockAim0;
    private Vector3 _stockPivot;     // the right controller where you pressed, in the head's yaw frame (tracking space)
    private bool _stockButtonWas, _stockToggled, _stockSqueezed, _stockVoiceOn;
    private float _stockPressStart;
    private float _stockToggledAt = -10f;
    private IntPtr _stockGun;

    /// <summary>The stock button, with a firm squeeze needed and a clear release (for the grip or a trigger).</summary>
    private bool StockButtonDown()
    {
        var name = Plugin.VirtualStockButton.Value?.Trim() ?? "";
        if (!Buttons.TryGetValue(name, out var b))
            return false;
        if (!b.analog)
            return IsPressed(name);
        var d = InputDevices.GetDeviceAtXRNode(b.hand);
        if (!d.isValid || !XRFeature.Float(d.deviceId, b.usage, out var v))
            return _stockSqueezed = false;
        _stockSqueezed = _stockSqueezed ? v > 0.35f : v > 0.75f;
        return _stockSqueezed;
    }
    private float _stockAwaySince = -1f;


    private static float Yaw(Vector3 d) => Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
    private static float Pitch(Vector3 d) => Mathf.Asin(Mathf.Clamp(d.y, -1f, 1f)) * Mathf.Rad2Deg;

    // Weapon sway off while aiming with the hand (Controls.HandAimNoSway). The game's WeaponSwayV2 swings the gun on
    // springs against the mouse's movement (and bobs it as you walk). Hand aiming moves the "mouse" every frame, so
    // the gun was always swinging and settling: "bouncy", "floaty", even with the smoothing (2026-11-04). Switched off
    // on the player's weapons while the hand aims, back on otherwise. Checked every 2 s (weapons come and go)
    private readonly Dictionary<IntPtr, WeaponSwayV2> _swayOff = new();
    private float _nextSwayScan;
    private bool _swayLogged;

    private void UpdateSway(bool want)
    {
        if (!want)
        {
            if (_swayOff.Count == 0)
                return;
            foreach (var s in _swayOff.Values)
                try { if (s != null) s.EnableSway = true; } catch { }
            _swayOff.Clear();
            Plugin.Logger.LogInfo("[Touch] Weapon sway back on");
            return;
        }
        if (Time.unscaledTime < _nextSwayScan)
            return;
        _nextSwayScan = Time.unscaledTime + 2f;
        try
        {
            foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<WeaponSwayV2>(), FindObjectsSortMode.None))
            {
                var s = o.TryCast<WeaponSwayV2>();
                if (s == null || !s.EnableSway || !s.transform.root.name.Contains("MyPlayer"))
                    continue;
                s.EnableSway = false;
                s.ResetValues = true;   // settle the springs where they are
                _swayOff[s.Pointer] = s;
                if (!_swayLogged)
                {
                    _swayLogged = true;
                    Plugin.Logger.LogInfo($"[Touch] Weapon sway off while aiming with the hand ('{s.name}')");
                }
            }
        }
        catch (Exception e)
        {
            _nextSwayScan = Time.unscaledTime + 10f;
            Plugin.Logger.LogWarning($"[Touch] weapon sway: {e.GetType().Name}: {e.Message}");
        }
    }

    // Hand-aim smoothing (Controls.HandAimSmoothing; 2026-11-04: "the gun seems like it was floating around in my
    // hand, couldn't keep it steady"). A "one euro" filter on the aim's left/right and up/down angles: when the hand is
    // nearly still it filters hard, so the small shake of a hand held out doesn't reach the gun; when it moves the
    // filter opens up with the speed, so a deliberate swing isn't slowed. HandAimMinCutoff sets how hard it filters at
    // rest (lower = steadier), HandAimBeta how quickly it opens up with speed (higher = less lag on fast swings)
    private float _smYaw, _smPitch, _smDYaw, _smDPitch, _smLast = -10f;
    private bool _smHave;

    private Vector3 SmoothAim(Vector3 dir)
    {
        if (!Plugin.HandAimSmoothing.Value)
        {
            _smHave = false;
            return dir;
        }
        var now = Time.unscaledTime;
        var dt = now - _smLast;
        _smLast = now;
        var yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        var pitch = Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg;
        // First frame, or a gap (a menu, Interact, a load): start from where the hand is
        if (!_smHave || dt <= 0f || dt > 0.3f)
        {
            _smHave = true;
            _smYaw = yaw;
            _smPitch = pitch;
            _smDYaw = _smDPitch = 0f;
            return dir;
        }
        var minCutoff = Mathf.Max(0.05f, Plugin.HandAimMinCutoff.Value);
        var beta = Mathf.Max(0f, Plugin.HandAimBeta.Value);
        const float dCutoff = 1f;

        static float Alpha(float cutoff, float dt)
        {
            var tau = 1f / (2f * Mathf.PI * cutoff);
            return 1f / (1f + tau / dt);
        }

        // Left/right (around the circle, so 359Â° -> 1Â° is a 2Â° step) and up/down, each filtered on its own
        var yawStep = Mathf.DeltaAngle(_smYaw, yaw);
        var dYaw = yawStep / dt;
        _smDYaw += Alpha(dCutoff, dt) * (dYaw - _smDYaw);
        _smYaw += Alpha(minCutoff + beta * Mathf.Abs(_smDYaw), dt) * yawStep;

        var dPitch = (pitch - _smPitch) / dt;
        _smDPitch += Alpha(dCutoff, dt) * (dPitch - _smDPitch);
        _smPitch += Alpha(minCutoff + beta * Mathf.Abs(_smDPitch), dt) * (pitch - _smPitch);

        return Quaternion.Euler(-_smPitch, _smYaw, 0f) * Vector3.forward;
    }

    private float _stockLastCall = -10f;

    /// <summary>The virtual stock is on (for the status display).</summary>
    internal static bool StockOn { get; private set; }

    /// <summary>Which controller a Touch button (or virtual D-pad direction) is on.</summary>
    private static XRNode HandOf(string button)
    {
        if (Buttons.TryGetValue(button, out var b))
            return b.hand;
        if (DpadDirs.TryGetValue(button, out var d))
            return d.leftStick ? XRNode.LeftHand : XRNode.RightHand;
        return XRNode.RightHand;
    }

    // Lean by tilting your head (Controls.HeadLean): the game leans only while Q/E is held, so the key is held while
    // the head is tilted past HeadLeanAngle, and let go once it's back under half of that
    private int _headLean;   // -1 left (Q held), 0 upright, 1 right (E held)
    private float _tiltRest, _lastTilt;

    // Lean speed (Controls.LeanSpeed): the lean with a weapon in hand was slow (2026-11-04). The game's PlayerLean
    // (MasterController.playerLean) moves the weapon's lean at weapLeanSpeed: that's multiplied, once per PlayerLean
    private MasterController _master;
    private float _nextMasterFind;
    private IntPtr _leanSped;
    private float _leanSpeedBase, _leanSpeedApplied;

    private void ApplyLeanSpeed()
    {
        try
        {
            if (_master == null && Time.unscaledTime >= _nextMasterFind)
            {
                _nextMasterFind = Time.unscaledTime + 3f;
                foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<MasterController>(), FindObjectsSortMode.None))
                {
                    var mc = o.TryCast<MasterController>();
                    if (mc != null && mc.transform.root.name.Contains("MyPlayer"))
                    {
                        _master = mc;
                        break;
                    }
                }
            }
            var lean = _master != null ? _master.playerLean : null;
            if (lean == null)
                return;
            var k = Mathf.Clamp(Plugin.LeanSpeed.Value, 0.25f, 5f);
            if (lean.Pointer != _leanSped)
            {
                _leanSped = lean.Pointer;
                _leanSpeedBase = lean.weapLeanSpeed;
                _leanSpeedApplied = -1f;
            }
            var want = _leanSpeedBase * k;
            if (Mathf.Abs(lean.weapLeanSpeed - want) > 1e-4f)
            {
                lean.weapLeanSpeed = want;
                if (_leanSpeedApplied < 0f)
                    Plugin.Logger.LogInfo($"[Touch] Lean speed x{k:0.0}: weapon lean {_leanSpeedBase:0.##} -> {want:0.##} " +
                                          $"(lean output {lean.outputMin:0.##}..{lean.outputMax:0.##}, multiplier {lean.multiplier:0.##})");
                _leanSpeedApplied = want;
            }
        }
        catch (Exception e)
        {
            _master = null;
            _nextMasterFind = Time.unscaledTime + 10f;
            Plugin.Logger.LogWarning($"[Touch] Lean speed: {e.GetType().Name}: {e.Message}");
        }
    }

    private void UpdateHeadLean(bool inMenu)
    {
        if (Time.frameCount % 30 == 0)
            ApplyLeanSpeed();
        var want = 0;
        if (Plugin.HeadLean.Value && !inMenu && !WeaponBench.Open)
        {
            var rot = InputTracking.GetLocalRotation(XRNode.CenterEye);
            var yaw = Quaternion.Euler(0f, rot.eulerAngles.y, 0f);
            // How far the top of the head leans right (+) or left (-), in degrees
            var raw = Mathf.Asin(Mathf.Clamp(Vector3.Dot(rot * Vector3.up, yaw * Vector3.right), -1f, 1f)) * Mathf.Rad2Deg;
            var on = Mathf.Max(3f, Plugin.HeadLeanAngle.Value);
            var off = on * 0.5f;
            // Measured from how you normally hold your head, not from dead level: left lean came slower than right
            // (2026-11-04), as if the head (or the headset on it) sat a little tilted right, so left needed more.
            // The upright tilt is learnt slowly (over ~5 s) while you're not leaning
            if (_headLean == 0 && Mathf.Abs(raw - _tiltRest) < off)
                _tiltRest = Mathf.Lerp(_tiltRest, raw, 1f - Mathf.Exp(-0.2f * Time.unscaledDeltaTime));
            var tilt = raw - _tiltRest;
            _lastTilt = tilt;
            want = _headLean switch
            {
                < 0 => tilt < -off ? -1 : tilt > on ? 1 : 0,
                > 0 => tilt > off ? 1 : tilt < -on ? -1 : 0,
                _ => tilt < -on ? -1 : tilt > on ? 1 : 0,
            };
        }
        if (want == _headLean)
            return;
        if (_headLean != 0)
            Press(_headLean < 0 ? (int)KeyCode.Q : (int)KeyCode.E, false);
        if (want != 0)
            Press(want < 0 ? (int)KeyCode.Q : (int)KeyCode.E, true);
        Plugin.Logger.LogInfo($"[Touch] Head lean: {(want < 0 ? "lean left" : want > 0 ? "lean right" : "upright")} " +
                              $"(tilt {_lastTilt:0.0}Â°, your upright is {_tiltRest:0.0}Â°)");
        _headLean = want;
    }

    /// <summary>
    /// Switches the virtual stock off at once and forgets it all (no blend back), so the next press starts fresh
    /// from where the right hand points. Used when the aim runs away, and after a gap (a menu, the laptop, holding
    /// Interact, a load), where a stock left on came back aiming at the old line.
    /// </summary>
    private void ResetStock(string why)
    {
        var was = _stockOn || _stockToggled || _stockBlend > 0f;
        _stockOn = false;
        _stockToggled = false;
        _stockVoiceOn = false;
        _stockBlend = 0f;
        _stockAwaySince = -1f;
        _yawDivergingFor = 0f;
        if (was)
        {
            Plugin.Logger.LogInfo($"[Touch] Virtual stock reset ({why}): aiming with the right controller");
            Haptics.Pulse(XRNode.RightHand, 0.3f, 0.04f);
        }
    }

    private Vector3 VirtualStock(UnityEngine.XR.InputDevice right, Vector3 handDir)
    {
        // Not called for a while (menu, laptop, Interact held, loading): start clean
        if (Time.unscaledTime - _stockLastCall > 0.5f)
            ResetStock("paused for a while");
        _stockLastCall = Time.unscaledTime;
        var left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
        // Toggle (VirtualStockToggle): one press turns it on, the next turns it off; otherwise on while held
        // A grip resting half-squeezed chattered across 50% and toggled the stock on and off several times a second,
        // leaving it on with the right hand ignored (log 2026-11-04, Georgia): a squeeze counts past 75% and the grip
        // must open below 35% before the next, and a toggle needs 0.4 s since the last
        var button = Plugin.VirtualStock.Value && StockButtonDown();
        // In toggle mode the toggle now happens when the button is let go, and only after a short press: held for a
        // second the right grip recenters instead (UpdateHoldRecenter; the user chose it, 2026-11-10)
        if (button && !_stockButtonWas)
            _stockPressStart = Time.unscaledTime;
        var pressedNow = Plugin.VirtualStockToggle.Value
            ? !button && _stockButtonWas && Time.unscaledTime - _stockPressStart < HoldRecenterTime && !_gripRecentered &&
              Time.unscaledTime - _stockToggledAt > 0.4f
            : button && !_stockButtonWas && Time.unscaledTime - _stockToggledAt > 0.4f;
        if (pressedNow)
        {
            _stockToggled = !_stockToggled;
            _stockToggledAt = Time.unscaledTime;
        }
        _stockButtonWas = button;
        if (!Plugin.VirtualStock.Value)
            _stockToggled = false;
        // 2026-11-11 (12:01 play): the user squeezed the right grip to take hold of the pistol, which switched the
        // stock on: the pistol then followed the LEFT hand ("the gun started in the middle of the left hand, couldn't
        // grip it with the right hand"), and it stayed on for the rifles after. A pistol has no stock: never on with
        // one. And a stock left on doesn't carry over to the next gun
        if (ManualReload.PistolInHand || ManualReload.GunId != _stockGun)
        {
            _stockGun = ManualReload.GunId;
            if (_stockOn || _stockToggled || _stockVoiceOn)
                ResetStock(ManualReload.PistolInHand ? "a pistol has no stock" : "another gun");
            if (ManualReload.PistolInHand)
            {
                VoiceBridge.TakeStockCommand();
                return handDir;
            }
        }
        // Spoken "activate gunstock" / "deactivate gunstock" (VoiceCommands). Already on: just answer again
        var spoken = VoiceBridge.TakeStockCommand();
        if (spoken == 1)
        {
            if (_stockOn)
                VoiceBridge.Stock();
            _stockToggled = _stockVoiceOn = true;
        }
        else if (spoken == 2)
            _stockToggled = _stockVoiceOn = false;
        else if (pressedNow)
            _stockVoiceOn = false;   // a press takes over from the voice
        var want = Plugin.VirtualStockToggle.Value ? _stockToggled : button || _stockVoiceOn;
        var headPos = InputTracking.GetLocalPosition(XRNode.CenterEye);
        var headYaw = Quaternion.Euler(0f, InputTracking.GetLocalRotation(XRNode.CenterEye).eulerAngles.y, 0f);

        // The line from the stock to the left hand, in the game world (so turning with the stick turns it too)
        var haveLine = false;
        var line = Vector3.zero;
        var dist = 0f;
        if (want && left.isValid &&
            XRFeature.Vector3(right.deviceId, "DevicePosition", out var rp) &&
            XRFeature.Vector3(left.deviceId, "DevicePosition", out var lp))
        {
            var pivotLocal = _stockOn ? _stockPivot : Quaternion.Inverse(headYaw) * (rp - headPos);
            var pivot = headPos + headYaw * pivotLocal;
            var l = lp - pivot;
            dist = l.magnitude;
            if (dist > 0.05f && HeadTracking.TryTrackingToWorld(pivot, Quaternion.LookRotation(l, Vector3.up), out _, out var rot))
            {
                line = rot * Vector3.forward;
                haveLine = true;
                if (!_stockOn)
                    _stockPivot = pivotLocal;
            }
        }

        // The left hand can be anywhere (the user's choice, 2026-11-04: beside the right hand or far from it): the aim
        // only follows how the line turns, so where it starts doesn't matter
        if (want && !_stockOn && haveLine)
        {
            var off = Vector3.Angle(line, handDir);
            {
                _stockOn = true;
                Haptics.Pulse(XRNode.RightHand, 0.5f, 0.08f);
                VoiceBridge.Stock();
                _stockLine0 = _stockLine = line;
                _stockAim0 = handDir;
                _stockAwaySince = -1f;
                Plugin.Logger.LogInfo($"[Touch] Virtual stock on (left hand {dist * 100f:0} cm from the stock, {off:0}Â° off " +
                                      "the aim): moving the left hand aims");
            }
        }
        else if (_stockOn && !want)
        {
            _stockOn = false;
            Plugin.Logger.LogInfo("[Touch] Virtual stock off: aiming with the right controller");
            Haptics.Pulse(XRNode.RightHand, 0.3f, 0.04f);
        }
        else if (_stockOn)
        {
            // Left controller lost (tracking): switch off by itself after half a second. Distance no longer counts
            if (!haveLine)
            {
                if (_stockAwaySince < 0f)
                    _stockAwaySince = Time.unscaledTime;
                else if (Time.unscaledTime - _stockAwaySince > 0.5f)
                {
                    _stockOn = false;
                    _stockToggled = false;
                    Plugin.Logger.LogInfo($"[Touch] Virtual stock off by itself: left hand {dist * 100f:0} cm from the stock");
                }
            }
            else
                _stockAwaySince = -1f;
        }

        // Blend over ~0.2 s so the aim doesn't jump when the stock engages or lets go
        var dt = Time.unscaledDeltaTime;
        _stockBlend = Mathf.MoveTowards(_stockBlend, _stockOn ? 1f : 0f, dt * 5f);
        if (_stockOn && haveLine)
            _stockLine = Vector3.Slerp(_stockLine, line, 1f - Mathf.Exp(-25f * dt)).normalized;   // light smoothing
        if (_stockBlend <= 0f)
            return handDir;

        // The aim at engage, turned left/right and up/down by as much as the line has turned since
        var yaw = Yaw(_stockAim0) + Mathf.DeltaAngle(Yaw(_stockLine0), Yaw(_stockLine));
        var pitch = Mathf.Clamp(Pitch(_stockAim0) + Pitch(_stockLine) - Pitch(_stockLine0), -85f, 85f);
        var stockAim = Quaternion.Euler(-pitch, yaw, 0f) * Vector3.forward;
        return Vector3.Slerp(handDir, stockAim, _stockBlend).normalized;
    }

    private static Vector2 Stick(UnityEngine.XR.InputDevice d) =>
        d.isValid && XRFeature.Vector2(d.deviceId, "Primary2DAxis", out var v) ? v : Vector2.zero;

    private static readonly Dictionary<string, bool> s_dpadDown = new(StringComparer.OrdinalIgnoreCase);

    internal static bool IsPressed(string button)
    {
        if (DpadDirs.TryGetValue(button, out var pad))
        {
            if (!(pad.leftStick ? LeftDpadHeld() : DpadHeld()))
            {
                s_dpadDown[button] = false;
                return false;
            }
            var dir = pad.dir;
            var v = Stick(InputDevices.GetDeviceAtXRNode(pad.leftStick ? XRNode.LeftHand : XRNode.RightHand));
            var along = Vector2.Dot(v, dir);
            // Pressed past 70% (and more that way than sideways); let go only once back under 40%. Without the gap a
            // single flick chattered across 70% and counted as a double tap (LeftDpadRight: grenades did the unjam,
            // 2026-11-04)
            s_dpadDown.TryGetValue(button, out var was);
            var now = was
                ? along > 0.4f
                : along > 0.7f && along > Mathf.Abs(v.x * dir.y) + Mathf.Abs(v.y * dir.x);
            s_dpadDown[button] = now;
            return now;
        }
        if (!Buttons.TryGetValue(button, out var b))
            return false;
        // Manual reload (Controls.ManualReload): the left grip takes magazines from the belt, so it doesn't sprint
        // while the hand is there or holding one
        if (ManualReload.TakesLeftGrip && string.Equals(button, "LeftGrip", StringComparison.OrdinalIgnoreCase))
            return false;
        // Right stick held in + left stick click = recenter (UpdateStickRecenter): that left click isn't Prone
        if (s_stickComboLeft && string.Equals(button, "LeftStickClick", StringComparison.OrdinalIgnoreCase))
            return false;
        var d = InputDevices.GetDeviceAtXRNode(b.hand);
        if (!d.isValid)
            return false;
        if (b.analog)
        {
            var down = XRFeature.Float(d.deviceId, b.usage, out var v) && v > 0.5f;
            if (string.Equals(button, "LeftTrigger", StringComparison.OrdinalIgnoreCase) && LeftTriggerGrabs(down))
                return false;
            return down;
        }
        var have = XRFeature.Bool(d.deviceId, b.usage, out var p);
        if (button == "Menu")
            LogMenuRaw(have, p);
        return have && p;
    }

    // Left trigger with the left hand off the gun (2026-11-11, the user: "once you've reloaded and you move your hand
    // to the gun, one click of the trigger attaches your hand to the front of the gun"; they chose the left trigger):
    // that press puts the hand on the front grip (LeftHand.RequestGrabFront) and doesn't aim; held on past that, or
    // pressed with the hand already on the gun, it aims as before. Not while holding a magazine
    private static int s_trigFrame = -1;
    private static bool s_trigWas, s_trigGrabbing, s_trigResult;

    private static bool LeftTriggerGrabs(bool down)
    {
        if (s_trigFrame == Time.frameCount)
            return s_trigResult;
        s_trigFrame = Time.frameCount;
        // (Only with the hand near the front grip: away from the gun the left trigger aims as normal)
        if (down && !s_trigWas && Plugin.TouchControls.Value && Plugin.FreeLeftHand.Value && !LeftHand.Gripped &&
            LeftHand.CanGrabFront && !ManualReload.HoldingMagazine)
        {
            s_trigGrabbing = true;
            LeftHand.RequestGrabFront = true;
        }
        if (!down)
            s_trigGrabbing = false;
        s_trigWas = down;
        s_trigResult = s_trigGrabbing;
        return s_trigResult;
    }

    // The left menu button never reached the game in testing (SteamVR / Virtual Desktop may keep it for their own
    // menus): log once whether the controller reports it at all, and every change, to tell which
    private static bool s_menuChecked, s_menuLast;

    private static void LogMenuRaw(bool have, bool pressed)
    {
        if (!s_menuChecked)
        {
            s_menuChecked = true;
            Plugin.Logger.LogInfo($"[Touch] Left menu button: {(have ? "reported by the controller" : "NOT reported (no MenuButton on the left controller)")}");
        }
        if (have && pressed != s_menuLast)
        {
            s_menuLast = pressed;
            Plugin.Logger.LogInfo($"[Touch] Left menu button raw {(pressed ? "down" : "up")}");
        }
    }

    /// <summary>The key each mapped action is bound to, from the game's InputLayer (re-read so rebinds follow).</summary>
    private void ReadBindings()
    {
        // Static on InputLayer; filled once the game has loaded its key binds
        var binds = InputLayer.Binds;
        if (binds == null || binds.Length == 0)
        {
            if (!_loggedWaiting)
            {
                _loggedWaiting = true;
                Plugin.Logger.LogInfo("[Touch] Waiting for the game's key bindings");
            }
            return;
        }

        int KeyFor(string action)
        {
            if (string.Equals(action, "Escape", StringComparison.OrdinalIgnoreCase))
                return 27; // KeyCode.Escape
            if (string.Equals(action, "Recenter", StringComparison.OrdinalIgnoreCase))
                return 291; // KeyCode.F10, which HeadTracking always reads as recenter
            // The mod's own keys that PadMacros reads (a letter or digit): next weapon, and Run = the console-style
            // run toggle (click to start running, click again or stop moving forward to walk), and ChamberRound
            // (unjam / chamber a round: the game's Left Shift + T, which only works with Shift down first, so it goes
            // through ManualReloadKey and PadMacros presses the keys in order)
            var modKey = string.Equals(action, "NextWeapon", StringComparison.OrdinalIgnoreCase) ? Plugin.NextWeaponKey
                       : string.Equals(action, "Run", StringComparison.OrdinalIgnoreCase) ? Plugin.RunKey
                       : string.Equals(action, "ChamberRound", StringComparison.OrdinalIgnoreCase) ? Plugin.ManualReloadKey
                       : null;
            if (modKey != null)
            {
                var k = modKey.Value?.Trim() ?? "";
                if (k.Length != 1 || !char.IsLetterOrDigit(k[0]))
                    return 0;
                return char.IsDigit(k[0]) ? k[0] : char.ToLowerInvariant(k[0]); // KeyCode.Alpha0-9 / KeyCode.A-Z
            }
            if (!Enum.TryParse<InputKey>(action, true, out var key) || (int)key >= binds.Length || binds[(int)key] == null)
                return 0;
            return (int)binds[(int)key].KeyBind.PrimaryKey;
        }

        var parsed = new List<Mapping>();
        foreach (var part in Plugin.TouchMap.Value.Split(','))
        {
            var kv = part.Split('=');
            if (kv.Length != 2)
                continue;
            var button = kv[0].Trim();
            var action = kv[1].Trim();
            if (!IsButton(button))
                continue;
            var keyCode = KeyFor(action);
            if (keyCode == 0)
                continue;
            var old = _map.Find(m => m.Button == button && m.KeyCode == keyCode);
            parsed.Add(old ?? new Mapping { Button = button, Action = action, KeyCode = keyCode });
        }

        // Release keys whose mapping went away or changed
        foreach (var m in _map)
            if (!parsed.Contains(m))
                Set(m, false);
        var changed = parsed.Count != _map.Count || parsed.Exists(m => !_map.Contains(m));
        _map.Clear();
        _map.AddRange(parsed);

        string[] walkActions = { "WalkForward", "WalkBackward", "WalkLeft", "WalkRight" };
        for (var i = 0; i < 4; i++)
        {
            var code = KeyFor(walkActions[i]);
            if (_walk[i] == null || _walk[i].KeyCode != code)
            {
                if (_walk[i] != null)
                    Set(_walk[i], false);
                _walk[i] = new Mapping { Button = "LeftStick", Action = walkActions[i], KeyCode = code };
                changed = true;
            }
        }

        if (!_loggedBinds)
        {
            _loggedBinds = true;
            // Which game action each key does and how (double-tap X = Shift+T kept going: is it a hold/toggle action?)
            var all = new List<string>();
            foreach (InputKey key in Enum.GetValues(typeof(InputKey)))
            {
                var i = (int)key;
                if (key == InputKey.None || i >= binds.Length || binds[i] == null)
                    continue;
                var b = binds[i].KeyBind;
                all.Add($"{key}={(b.PreliminaryKey != KeyCode.None ? b.PreliminaryKey + "+" : "")}{b.PrimaryKey}({b.PressType})");
            }
            Plugin.Logger.LogInfo("[Touch] Game key binds: " + string.Join(", ", all));
        }

        if (changed)
            Plugin.Logger.LogInfo("[Touch] Controls: " + string.Join(", ", _map.ConvertAll(m => $"{m.Button}={m.Action} ({(KeyCode)m.KeyCode})")) +
                                  $"; left stick = {string.Join("/", Array.ConvertAll(_walk, w => ((KeyCode)w.KeyCode).ToString()))}");
    }

    // Bench drag (2026-11-04): with the arrow on the weapon (empty space, not the page), holding the click and swinging
    // the right controller left/right turns the weapon, as dragging with the mouse does. The game's own drag reads the
    // mouse's movement, and the laser places the arrow without any (the log: "weapon turned 0.0Â°" on every press)
    private bool _benchDragging, _benchClickWas;
    private float _benchDragYaw;

    private void UpdateBenchDrag(UnityEngine.XR.InputDevice right, bool click)
    {
        var pressed = click && !_benchClickWas;
        _benchClickWas = click;
        if (!WeaponBench.Open || !click || !right.isValid ||
            !XRFeature.Quaternion(right.deviceId, "PointerRotation", out var rot))
        {
            _benchDragging = false;
            return;
        }
        var fwd = rot * Vector3.forward;
        var yaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
        if (pressed)
        {
            _benchDragging = !VRPointer.ArrowOverMenu();
            _benchDragYaw = yaw;
            if (_benchDragging)
                Plugin.Logger.LogInfo("[Touch] Bench: dragging the weapon round with the controller");
            return;
        }
        if (!_benchDragging)
            return;
        WeaponBench.SpinInput += Mathf.DeltaAngle(_benchDragYaw, yaw) * Plugin.BenchDragGain.Value;
        _benchDragYaw = yaw;
    }

    // Right stick up/down as the mouse wheel in menus: a notch every so often, faster the further it's pushed
    private float _scrollAcc;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;

    private void UpdateMenuScroll(float y)
    {
        if (Mathf.Abs(y) < 0.3f)
        {
            _scrollAcc = 0f;
            return;
        }
        // Up to 12 notches a second at full tilt
        _scrollAcc += y * 12f * Time.unscaledDeltaTime;
        while (Mathf.Abs(_scrollAcc) >= 1f)
        {
            var up = _scrollAcc > 0f;
            _scrollAcc -= up ? 1f : -1f;
            Mouse(MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)(up ? 120 : -120)));
        }
    }

    // A as the left mouse button while a menu is open
    private bool _menuClickDown;
    private const int KeyMouse0 = 323; // KeyCode.Mouse0

    private void UpdateMenuClick(bool down)
    {
        if (down == _menuClickDown)
            return;
        _menuClickDown = down;
        Press(KeyMouse0, down);
    }

    private void Set(Mapping m, bool down)
    {
        if (m == null || m.KeyCode == 0 || m.Down == down)
            return;
        m.Down = down;
        // The user reported the menu button doing nothing: log it to see whether the press arrives at all
        if (m.Button == "Menu")
            Plugin.Logger.LogInfo($"[Touch] Menu button {(down ? "pressed" : "let go")}: {(KeyCode)m.KeyCode} {(down ? "down" : "up")}");
        Press(m.KeyCode, down);
    }

    // Double tap: a second press soon after the first presses a key combination instead (Controls.TouchDoubleTap)
    private const float DoubleTapWindow = 0.35f;

    // Keys of a combination are pressed one per frame, then released one per frame straight away, as a single
    // tap: pressing Shift and T in the same instant didn't register (the game wants Shift down before T), and
    // holding them (as long as X was held) made the game repeat the action and empty the magazine

    // A button with a double tap waits to see what you do before sending anything (X: tap = reload,
    // hold = check the magazine, double tap = manual reload, so the first tap mustn't reload on its own)
    private enum TapState { Idle, FirstDown, Holding, WaitSecond, TapOut, Combo }

    // Hold: a long press taps other keys instead of holding the button's own (Controls.TouchHold, e.g. B=Space: B
    // tap = crouch, hold = kick). A double tap can also hold its keys ("Hold:Space": vault is Space held) for as long
    // as the button stays down, at least ComboMinHold
    private const float ComboMinHold = 0.6f;

    private class DoubleTap
    {
        public int[] Keys;       // double tap (null = none)
        public bool KeysHeld;    // double tap holds Keys while the button is held, not a tap
        public int[] HoldKeys;   // long press taps these (null = hold the button's own key)
        public int[] Active;     // the combination being pressed now
        public bool ActiveHeld;
        public bool WasDown;
        public TapState State;
        public float Since;      // when the current state started
        public int Pressed;      // how many of Active are down (combo)
        public bool Releasing;   // letting go: never press again (without this T bounced down/up forever)
    }

    private readonly Dictionary<string, DoubleTap> _doubleTaps = new(StringComparer.OrdinalIgnoreCase);
    private string _doubleTapConfig;

    private void UpdateButton(Mapping m, bool down)
    {
        ReadDoubleTaps();
        if (!_doubleTaps.TryGetValue(m.Button, out var dt))
        {
            Set(m, down);
            return;
        }

        var now = Time.unscaledTime;
        var pressed = down && !dt.WasDown;
        var released = !down && dt.WasDown;
        dt.WasDown = down;

        void Go(TapState s)
        {
            dt.State = s;
            dt.Since = now;
        }

        void StartCombo(int[] keys, bool held)
        {
            dt.Active = keys;
            dt.ActiveHeld = held;
            dt.Pressed = 0;
            dt.Releasing = false;
            Go(TapState.Combo);
        }

        switch (dt.State)
        {
            case TapState.Idle:
                if (pressed)
                    Go(TapState.FirstDown);
                break;

            case TapState.FirstDown:
                if (released)
                {
                    if (dt.Keys != null)
                        Go(TapState.WaitSecond);
                    else
                    {
                        // No double tap to wait for: a short press of the button's own key now
                        LogTap(m, "tap");
                        if (ReloadByHand(m))
                            Go(TapState.Idle);
                        else
                        {
                            Set(m, true);
                            Go(TapState.TapOut);
                        }
                    }
                }
                else if (now - dt.Since >= DoubleTapWindow)
                {
                    if (dt.HoldKeys != null)
                    {
                        // Long press with its own action: tap those keys once
                        Plugin.Logger.LogInfo($"[Touch] Hold on {m.Button}");
                        StartCombo(dt.HoldKeys, false);
                        goto case TapState.Combo;
                    }
                    // Y held (2026-11-11, the user: "Y as a longer press for the scope; a quick press changes fire
                    // rate"): on a gun with a zoom scope, a long press of the Firemode button zooms in/out
                    if (string.Equals(m.Action, "Firemode", StringComparison.OrdinalIgnoreCase) && PadMacros.ZoomScope("Y held"))
                    {
                        Go(TapState.Holding);
                        break;
                    }
                    // Long press: hold the button's own key for as long as the button is held
                    LogTap(m, "hold");
                    Set(m, true);
                    Go(TapState.Holding);
                }
                break;

            case TapState.Holding:
                if (!down)
                {
                    Set(m, false);
                    Go(TapState.Idle);
                }
                break;

            case TapState.WaitSecond:
                if (pressed)
                {
                    Plugin.Logger.LogInfo($"[Touch] Double tap on {m.Button}");
                    Haptics.Pulse(HandOf(m.Button), 0.4f, 0.04f);
                    StartCombo(dt.Keys, dt.KeysHeld);
                    goto case TapState.Combo;
                }
                if (now - dt.Since >= DoubleTapWindow)
                {
                    // Just one tap: a short press of the button's own key
                    LogTap(m, "tap");
                    if (ReloadByHand(m))
                        Go(TapState.Idle);
                    else
                    {
                        Set(m, true);
                        Go(TapState.TapOut);
                    }
                }
                break;

            case TapState.TapOut:
                if (now - dt.Since >= 0.08f)
                {
                    Set(m, false);
                    Go(TapState.Idle);
                }
                break;

            case TapState.Combo:
                if (!dt.Releasing && dt.Pressed < dt.Active.Length)
                {
                    // Press the next key (one per frame), even if the button was already let go
                    LogCombo(dt.Active[dt.Pressed], true);
                    Press(dt.Active[dt.Pressed++], true);
                }
                else if (!dt.Releasing && dt.ActiveHeld && (down || now - dt.Since < ComboMinHold))
                {
                    // A held combination: keep the keys down while the button is held (and at least ComboMinHold)
                }
                else
                {
                    // Let go (a tap: straight away, whether or not the button is still held), in reverse order, one
                    // per frame
                    dt.Releasing = true;
                    LogCombo(dt.Active[dt.Pressed - 1], false);
                    Press(dt.Active[--dt.Pressed], false);
                    if (dt.Pressed == 0)
                    {
                        dt.Releasing = false;
                        Go(TapState.Idle);
                    }
                }
                break;
        }
    }

    // Recenter by holding the right stick down for a moment, no other button (2026-11-10: the user flicked it down
    // without the left thumb on its thumbrest, which the D-pad's DpadDown=Recenter needs, and nothing happened; the
    // gun was then too close to the face). Right stick down does nothing else in play (TouchLookUpDown 0, the hand
    // aims up/down)
    // Recenter: hold the RIGHT GRIP for a second (the user's choice, 2026-11-10, after holding the right stick down
    // proved awkward: the pushes reached only a third to half way, mostly sideways). A short squeeze still toggles the
    // virtual stock (now on release, see VirtualStock)
    private const float HoldRecenterTime = 1f;
    private float _recenterHeld;
    private bool _recenterDone, _gripRecentered, _recenterGripDown;

    // ---- Weapon switching by reaching (the user, 2026-11-10) ----
    // Right hand over the right shoulder + grip: the second main weapon (and back to the main one); right hand down
    // at the right hip + grip: the pistol. Measured from the real head, in its left/right heading. Such a squeeze
    // neither recenters nor toggles the virtual stock
    private int _lastSlot = 2;          // 2 = main weapon (Alpha2), 3 = second main (Alpha3), 1 = pistol (Alpha1)
    private int _switchKeyUp, _switchLogs;
    private float _switchKeyUpAt = -1f;

    /// <summary>On a fresh right-grip squeeze: if the hand is at the shoulder or hip, switch weapon; true if it did.</summary>
    private bool GestureSwitch()
    {
        var head = InputTracking.GetLocalPosition(XRNode.CenterEye);
        var hand = InputTracking.GetLocalPosition(XRNode.RightHand);
        var yaw = Quaternion.Euler(0f, InputTracking.GetLocalRotation(XRNode.CenterEye).eulerAngles.y, 0f);
        var rel = Quaternion.Inverse(yaw) * (hand - head);   // x right, y up, z forward from the eyes
        var shoulder = rel.x > 0.02f && rel.y > -0.25f && rel.z < 0.12f;
        var hip = rel.x > 0.05f && rel.y < -0.5f && rel.z < 0.3f;
        if (_switchLogs++ < 30)
            Plugin.Logger.LogInfo($"[Touch] Right grip squeezed with the hand at {rel.x * 100f:0} cm right, {rel.y * 100f:0} cm up, " +
                                  $"{rel.z * 100f:0} cm forward of the eyes: {(shoulder ? "shoulder" : hip ? "hip" : "neither")}");
        int slot;
        if (shoulder)
            slot = _lastSlot == 2 ? 3 : 2;
        else if (hip)
            slot = 1;
        else
            return false;
        var key = slot == 1 ? 49 : slot == 2 ? 50 : 51;   // KeyCode.Alpha1/2/3: the game's SwitchSecondary/Primary/SecondPrimary
        Press(key, true);
        _switchKeyUp = key;
        _switchKeyUpAt = Time.unscaledTime + 0.08f;
        _lastSlot = slot;
        Haptics.Pulse(XRNode.RightHand, 0.5f, 0.06f);
        Plugin.Logger.LogInfo($"[Touch] Reached to the {(shoulder ? "shoulder" : "hip")}: {(slot == 1 ? "pistol" : slot == 2 ? "main weapon" : "second weapon")}");
        return true;
    }

    // Recenter by the sticks (2026-11-11, the user: the recenter didn't seem to work; "when you hold down the right
    // stick, as in click, and then you click the left stick while the right one is pressed, it re-centers"). The left
    // click doesn't go prone while the right stick is held in (IsPressed), until it's let go
    private static bool s_stickComboLeft;
    private bool _leftClickWas;

    private void UpdateStickRecenter(UnityEngine.XR.InputDevice right)
    {
        var left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
        var rightIn = right.isValid && XRFeature.Bool(right.deviceId, "Primary2DAxisClick", out var r) && r;
        var leftIn = left.isValid && XRFeature.Bool(left.deviceId, "Primary2DAxisClick", out var l) && l;
        if (leftIn && !_leftClickWas && rightIn)
        {
            s_stickComboLeft = true;
            HeadTracking.RequestRecenter();
            Haptics.Pulse(XRNode.RightHand, 0.7f, 0.12f);
            Haptics.Pulse(XRNode.LeftHand, 0.7f, 0.12f);
            Plugin.Logger.LogInfo("[Touch] Right stick held in + left stick click: recenter");
        }
        if (!leftIn)
            s_stickComboLeft = false;
        _leftClickWas = leftIn;
    }

    private void UpdateHoldRecenter(UnityEngine.XR.InputDevice right, bool inMenu)
    {
        if (_switchKeyUpAt > 0f && Time.unscaledTime >= _switchKeyUpAt)
        {
            Press(_switchKeyUp, false);
            _switchKeyUpAt = -1f;
        }
        var v = 0f;
        if (right.isValid)
            XRFeature.Float(right.deviceId, "Grip", out v);
        var wasDown = _recenterGripDown;
        _recenterGripDown = _recenterGripDown ? v > 0.35f : v > 0.75f;
        // A fresh squeeze at the shoulder or hip switches weapon, and that squeeze does nothing else (the flag stays
        // set until the next squeeze, so the stock's let-go check sees it whichever runs first)
        if (_recenterGripDown && !wasDown)
        {
            _gripRecentered = false;
            if (!inMenu && !WeaponBench.Open && GestureSwitch())
            {
                _recenterDone = true;
                _gripRecentered = true;
            }
        }
        if (!_recenterGripDown || inMenu || WeaponBench.Open)
        {
            _recenterHeld = 0f;
            if (!_recenterGripDown)
                _recenterDone = false;
            return;
        }
        _recenterHeld += Time.unscaledDeltaTime;
        if (_recenterDone || _recenterHeld < HoldRecenterTime)
            return;
        _recenterDone = true;
        _gripRecentered = true;
        HeadTracking.RequestRecenter();
        Haptics.Pulse(XRNode.RightHand, 0.7f, 0.12f);
        Plugin.Logger.LogInfo("[Touch] Right grip held: recenter");
    }

    // A tap of the Reload button with hand reloading on drops the magazine instead (ManualReload.AskDrop)
    // (A tap of Y while aiming zoomed the scope 13:53-15:00; now a tap of Y is always the fire mode and a long press
    // zooms, see the FirstDown case)
    private static bool ReloadByHand(Mapping m) =>
        string.Equals(m.Action, "Reload", StringComparison.OrdinalIgnoreCase) && ManualReload.AskDrop();

    // 2026-11-04: left stick right (grenades) seemed to do nothing and single taps left no trace: log them
    private static int s_tapLogs;

    private static void LogTap(Mapping m, string what)
    {
        if (Plugin.Diagnostics.Value && m.KeyCode != 0 && s_tapLogs++ < 80)
            Plugin.Logger.LogInfo($"[Touch] {m.Button} {what}: {m.Action} ({(KeyCode)m.KeyCode})");
    }

    private static void LogCombo(int key, bool down)
    {
        if (Plugin.Diagnostics.Value && s_comboLogs++ < 40)
            Plugin.Logger.LogInfo($"[Touch] combo key {(KeyCode)key} {(down ? "down" : "up")} at frame {Time.frameCount}");
    }

    private static void SetCombo(DoubleTap dt, bool down)
    {
        // Only used to let go of everything at once (VR stopped, settings changed)
        if (down)
            return;
        while (dt.Active != null && dt.Pressed > 0)
            Press(dt.Active[--dt.Pressed], false);
        dt.Pressed = 0;
        dt.Releasing = false;
        dt.State = TapState.Idle;
    }

    private static int[] ParseKeys(string text)
    {
        var keys = new List<int>();
        foreach (var name in text.Split('+'))
            if (Enum.TryParse<KeyCode>(name.Trim(), true, out var code) && code != KeyCode.None)
                keys.Add((int)code);
        return keys.Count == 0 ? null : keys.ToArray();
    }

    private static string Describe(int[] keys) => string.Join("+", Array.ConvertAll(keys, k => ((KeyCode)k).ToString()));

    /// <summary>
    /// Parses Controls.TouchDoubleTap ("X=LeftShift+T, B=Hold:Space", Unity KeyCode names) and Controls.TouchHold
    /// ("B=Space") when they change.
    /// </summary>
    private void ReadDoubleTaps()
    {
        var doubles = Plugin.TouchDoubleTap.Value ?? "";
        var holds = Plugin.TouchHold.Value ?? "";
        var config = doubles + "|" + holds;
        if (config == _doubleTapConfig)
            return;
        _doubleTapConfig = config;
        foreach (var dt in _doubleTaps.Values)
            SetCombo(dt, false);
        _doubleTaps.Clear();

        DoubleTap Entry(string button)
        {
            if (!_doubleTaps.TryGetValue(button, out var e))
                _doubleTaps[button] = e = new DoubleTap();
            return e;
        }

        var described = new List<string>();
        foreach (var part in doubles.Split(','))
        {
            var kv = part.Split('=');
            if (kv.Length != 2 || !IsButton(kv[0].Trim()))
                continue;
            var value = kv[1].Trim();
            var held = value.StartsWith("Hold:", StringComparison.OrdinalIgnoreCase);
            var keys = ParseKeys(held ? value.Substring(5) : value);
            if (keys == null)
                continue;
            var e = Entry(kv[0].Trim());
            e.Keys = keys;
            e.KeysHeld = held;
            described.Add($"{kv[0].Trim()} x2 = {(held ? "hold " : "")}{Describe(keys)}");
        }
        foreach (var part in holds.Split(','))
        {
            var kv = part.Split('=');
            if (kv.Length != 2 || !IsButton(kv[0].Trim()))
                continue;
            var keys = ParseKeys(kv[1]);
            if (keys == null)
                continue;
            Entry(kv[0].Trim()).HoldKeys = keys;
            described.Add($"{kv[0].Trim()} held = {Describe(keys)}");
        }
        if (described.Count > 0)
            Plugin.Logger.LogInfo("[Touch] Double taps / holds: " + string.Join(", ", described));
    }

    private void ReleaseAll()
    {
        UpdateMenuClick(false);
        UpdateSway(false);
        StockOn = false;
        if (_headLean != 0)
        {
            Press(_headLean < 0 ? (int)KeyCode.Q : (int)KeyCode.E, false);
            _headLean = 0;
        }
        foreach (var m in _map)
            Set(m, false);
        foreach (var w in _walk)
            Set(w, false);
        foreach (var dt in _doubleTaps.Values)
        {
            SetCombo(dt, false);
            dt.WasDown = false;
        }
    }

    /// <summary>Presses or releases a Unity KeyCode as a real key or mouse button.</summary>
    /// <summary>Presses or lets go of a key as a real keyboard would (for PadMacros).</summary>
    internal static void PressKey(KeyCode key, bool down) => Press((int)key, down);

    // Auto-ADS (2026-11-04, "Can we have auto-ADS"): raise the gun to your face and the game aims down the sights, as
    // if the Aim button (left trigger) were held; lower it and it stops. "At your face" = the right controller within
    // Controls.AutoAdsDistance of the eyes and pointing within AutoAdsAngle of where you look. It lets go a little
    // further out (5 cm, 10Â°) so it doesn't flicker at the edge. The left trigger still aims as before
    private bool _autoAdsOn;
    private int _autoAdsLogs;

    private bool UpdateAutoAds(InputDevice right, bool inMenu)
    {
        // The pointer pose: the same one the hand aim steers the gun by
        if (!Plugin.AutoAds.Value || inMenu || WeaponBench.Open || !right.isValid ||
            !XRFeature.Vector3(right.deviceId, "PointerPosition", out var handPos) ||
            !XRFeature.Quaternion(right.deviceId, "PointerRotation", out var handRot))
        {
            if (_autoAdsOn)
                Plugin.Logger.LogInfo("[Touch] Auto-ADS off");
            _autoAdsOn = false;
            return false;
        }
        var headPos = InputTracking.GetLocalPosition(XRNode.CenterEye);
        var headRot = InputTracking.GetLocalRotation(XRNode.CenterEye);
        var dist = Vector3.Distance(handPos, headPos);
        var angle = Vector3.Angle(handRot * Vector3.forward, headRot * Vector3.forward);
        var maxDist = Plugin.AutoAdsDistance.Value + (_autoAdsOn ? 0.05f : 0f);
        var maxAngle = Plugin.AutoAdsAngle.Value + (_autoAdsOn ? 10f : 0f);
        var on = dist <= maxDist && angle <= maxAngle;
        if (on != _autoAdsOn && _autoAdsLogs++ < 20)
            Plugin.Logger.LogInfo($"[Touch] Auto-ADS {(on ? "on" : "off")}: hand {dist * 100f:0} cm from the eyes, " +
                                  $"pointing {angle:0}Â° off where you look");
        _autoAdsOn = on;
        return on;
    }

    private static void Press(int keyCode, bool down)
    {
        switch (keyCode)
        {
            case 323: Mouse(down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP, 0, 0, 0); return;    // Mouse0
            case 324: Mouse(down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP, 0, 0, 0); return;  // Mouse1
            case 325: Mouse(down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP, 0, 0, 0); return; // Mouse2
            case 326: Mouse(down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, 0, 0, 1); return;           // Mouse3
            case 327: Mouse(down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, 0, 0, 2); return;           // Mouse4
        }
        var (vk, extended) = ToVirtualKey(keyCode);
        if (vk == 0)
        {
            Plugin.Logger.LogWarning($"[Touch] No Windows key for {(KeyCode)keyCode}");
            return;
        }
        var scan = (ushort)MapVirtualKey(vk, 0);
        var flags = KEYEVENTF_SCANCODE | (extended ? KEYEVENTF_EXTENDEDKEY : 0) | (down ? 0 : KEYEVENTF_KEYUP);
        var input = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = (ushort)vk, wScan = scan, dwFlags = flags, extra = OurInputMark } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>Moves the mouse by this much, as a real mouse would (for HeadAim).</summary>
    internal static void MoveMouse(int dx, int dy) => Mouse(MOUSEEVENTF_MOVE, dx, dy, 0);

    internal static bool GameHasFocus() => GetActiveWindow() != IntPtr.Zero;

    private static void Mouse(uint flags, int dx, int dy, uint data)
    {
        var input = new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags, extra = OurInputMark } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>Tag on every key and mouse event the mod sends ("OVR!"), so ControlSwitch can tell them from the
    /// PS5 pad's (Steam Input) and a real keyboard's.</summary>
    internal static readonly IntPtr OurInputMark = (IntPtr)0x4F565221;

    /// <summary>Unity KeyCode -> Windows virtual-key code (and whether it's an extended key).</summary>
    private static (uint vk, bool extended) ToVirtualKey(int k)
    {
        if (k >= 97 && k <= 122) return ((uint)(k - 32), false);         // a-z -> 'A'-'Z'
        if (k >= 48 && k <= 57) return ((uint)k, false);                 // Alpha0-9
        if (k >= 256 && k <= 265) return ((uint)(0x60 + k - 256), false); // Keypad0-9
        if (k >= 282 && k <= 296) return ((uint)(0x70 + k - 282), false); // F1-F15
        return k switch
        {
            8 => (0x08, false),    // Backspace
            9 => (0x09, false),    // Tab
            13 => (0x0D, false),   // Return
            27 => (0x1B, false),   // Escape
            32 => (0x20, false),   // Space
            39 => (0xDE, false),   // Quote
            44 => (0xBC, false),   // Comma
            45 => (0xBD, false),   // Minus
            46 => (0xBE, false),   // Period
            47 => (0xBF, false),   // Slash
            59 => (0xBA, false),   // Semicolon
            61 => (0xBB, false),   // Equals
            91 => (0xDB, false),   // LeftBracket
            92 => (0xDC, false),   // Backslash
            93 => (0xDD, false),   // RightBracket
            96 => (0xC0, false),   // BackQuote
            127 => (0x2E, true),   // Delete
            273 => (0x26, true),   // UpArrow
            274 => (0x28, true),   // DownArrow
            275 => (0x27, true),   // RightArrow
            276 => (0x25, true),   // LeftArrow
            277 => (0x2D, true),   // Insert
            278 => (0x24, true),   // Home
            279 => (0x23, true),   // End
            280 => (0x21, true),   // PageUp
            281 => (0x22, true),   // PageDown
            301 => (0x14, false),  // CapsLock
            303 => (0xA1, false),  // RightShift
            304 => (0xA0, false),  // LeftShift
            305 => (0xA3, true),   // RightControl
            306 => (0xA2, false),  // LeftControl
            307 => (0xA5, true),   // RightAlt
            308 => (0xA4, false),  // LeftAlt
            _ => (0u, false),
        };
    }
}
