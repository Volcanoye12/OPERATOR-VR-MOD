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
    };

    private class Mapping
    {
        public string Button;
        public string Action;
        public int KeyCode;      // Unity KeyCode
        public bool Down;
    }

    private readonly List<Mapping> _map = new();
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
            UpdateMenuClick(inMenu && IsPressed("A"));
            foreach (var m in _map)
            {
                if (inMenu && (m.Button == "A" || string.Equals(m.Action, "Shoot", StringComparison.OrdinalIgnoreCase)))
                {
                    if (m.Button == "A" && Plugin.HoldToInteract.Value && string.Equals(m.Action, "Interact", StringComparison.OrdinalIgnoreCase))
                        UpdateInteract(m, false);
                    else
                        Set(m, false);
                    continue;
                }
                if (Plugin.HoldToInteract.Value && string.Equals(m.Action, "Interact", StringComparison.OrdinalIgnoreCase))
                    UpdateInteract(m, IsPressed(m.Button));
                else
                    UpdateButton(m, IsPressed(m.Button));
            }

            // Left stick: walk keys, past a third of the way
            var stick = Stick(left);
            Set(_walk[0], stick.y > 0.35f);
            Set(_walk[1], stick.y < -0.35f);
            Set(_walk[2], stick.x < -0.35f);
            Set(_walk[3], stick.x > 0.35f);

            // Right stick turns; the hand aims. Not while a menu or laptop screen takes the mouse
            var look = Stick(right);
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

    private void UpdateInteract(Mapping m, bool down)
    {
        if (!down)
        {
            if (_lookToInteract)
                Plugin.Logger.LogInfo("[Touch] Interact released: aim back on the hand");
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
            return;
        }
        // Range switch: anywhere in the booth, gun ahead = target away, gun at the floor = target back, used directly
        // every frame, with F held as well so the game sees a real hold and release. The gun stays on the hand here
        // (no swinging to where you look)
        if (UseGazedRangeHandle())
        {
            _lookToInteract = false;
            Set(m, true);
            return;
        }
        if (!_lookToInteract)
        {
            _lookToInteract = true;
            _interactSince = Time.unscaledTime;
            _lastAimGap = 999f;
            Plugin.Logger.LogInfo("[Touch] Interact held: aiming where you look");
        }

        // Press Interact once the aim has reached where you look (or after half a second regardless)
        if (!_interactSent && (_lastAimGap < 3f || Time.unscaledTime - _interactSince > 0.5f))
        {
            _interactSent = true;
            Set(m, true);
            Plugin.Logger.LogInfo($"[Touch] Interact pressed (aim {_lastAimGap:0.0}° off where you look)");
        }
    }

    private InteractableRangeTargetHandle _lastUsedHandle;

    /// <summary>
    /// In a range booth (near its target switch, whose parts are named "Forwards" = move the target away and
    /// "Backwards" = bring it back): gun pointing ahead (or at the switch) uses Forwards, pointing at the floor uses
    /// Backwards, calling its Interact() this frame as the game does while F is held on it. Picking the small part by
    /// looking at it was awkward in VR. The switch sits low, so aiming at it already points the gun ~20-30° down:
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
            Plugin.Logger.LogInfo($"[Touch] Range switch '{best.name}' (value {best.value:0.##}) used, gun {gunDown:0}° down " +
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
        LogPromptChanges();
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
    /// loop: each frame closes part of the gap. Up/down was confirmed to go the right way (gaps stay under ~3°).
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
        // Unity pitch: positive looks down (the hand offset only applies to the hand)
        var handPitch = -Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg + (_lookToInteract ? 0f : Plugin.HandAimPitchOffset.Value);
        var handYaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        var pitchGap = pitch ? Mathf.DeltaAngle(gamePitch, handPitch) : 0f;
        var yawGap = yaw ? Mathf.DeltaAngle(gameYaw, handYaw) : 0f;

        // Guard: if the game turned the opposite way to the mouse the yaw loop would spin the player round. It used to
        // switch left/right aim off for the session when the gap stayed over 60° for a second, but that also happens
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
                    Plugin.Logger.LogWarning($"[Touch] Left/right hand aim is running away (gap {_yawGapAtStart:0}° -> {yawGap:0}° in a second); paused for 3 s");
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
            Plugin.Logger.LogInfo($"[Touch] Hand aim: pitch hand {handPitch:0.0}° game {gamePitch:0.0}°, yaw hand {handYaw:0.0}° game {gameYaw:0.0}° " +
                                  $"(view {_viewYaw:0.0}°) -> mouse {px.x:0.0}, {px.y:0.0} px");
        }
        return px;
    }

    private static Vector2 Stick(UnityEngine.XR.InputDevice d) =>
        d.isValid && XRFeature.Vector2(d.deviceId, "Primary2DAxis", out var v) ? v : Vector2.zero;

    private static bool IsPressed(string button)
    {
        if (!Buttons.TryGetValue(button, out var b))
            return false;
        var d = InputDevices.GetDeviceAtXRNode(b.hand);
        if (!d.isValid)
            return false;
        if (b.analog)
            return XRFeature.Float(d.deviceId, b.usage, out var v) && v > 0.5f;
        var have = XRFeature.Bool(d.deviceId, b.usage, out var p);
        if (button == "Menu")
            LogMenuRaw(have, p);
        return have && p;
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
            if (!Buttons.ContainsKey(button))
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

    private class DoubleTap
    {
        public int[] Keys;
        public bool WasDown;
        public TapState State;
        public float Since;      // when the current state started
        public int Pressed;      // how many of Keys are down (combo)
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

        switch (dt.State)
        {
            case TapState.Idle:
                if (pressed)
                    Go(TapState.FirstDown);
                break;

            case TapState.FirstDown:
                if (released)
                    Go(TapState.WaitSecond);
                else if (now - dt.Since >= DoubleTapWindow)
                {
                    // Long press: hold the button's own key for as long as the button is held
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
                    Go(TapState.Combo);
                    goto case TapState.Combo;
                }
                if (now - dt.Since >= DoubleTapWindow)
                {
                    // Just one tap: a short press of the button's own key
                    Set(m, true);
                    Go(TapState.TapOut);
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
                if (!dt.Releasing && dt.Pressed < dt.Keys.Length)
                {
                    // Press the next key (one per frame), even if the button was already let go
                    LogCombo(dt.Keys[dt.Pressed], true);
                    Press(dt.Keys[dt.Pressed++], true);
                }
                else
                {
                    // Let go straight away (whether or not the button is still held), in reverse order, one per frame
                    dt.Releasing = true;
                    LogCombo(dt.Keys[dt.Pressed - 1], false);
                    Press(dt.Keys[--dt.Pressed], false);
                    if (dt.Pressed == 0)
                    {
                        dt.Releasing = false;
                        Go(TapState.Idle);
                    }
                }
                break;
        }
    }

    private static void LogCombo(int key, bool down)
    {
        if (s_comboLogs++ < 40)
            Plugin.Logger.LogInfo($"[Touch] combo key {(KeyCode)key} {(down ? "down" : "up")} at frame {Time.frameCount}");
    }

    private static void SetCombo(DoubleTap dt, bool down)
    {
        // Only used to let go of everything at once (VR stopped, settings changed)
        if (down)
            return;
        while (dt.Pressed > 0)
            Press(dt.Keys[--dt.Pressed], false);
        dt.Releasing = false;
        dt.State = TapState.Idle;
    }

    /// <summary>Parses Controls.TouchDoubleTap ("X=LeftShift+T, ...", Unity KeyCode names) when it changes.</summary>
    private void ReadDoubleTaps()
    {
        var config = Plugin.TouchDoubleTap.Value ?? "";
        if (config == _doubleTapConfig)
            return;
        _doubleTapConfig = config;
        foreach (var dt in _doubleTaps.Values)
            SetCombo(dt, false);
        _doubleTaps.Clear();

        var described = new List<string>();
        foreach (var part in config.Split(','))
        {
            var kv = part.Split('=');
            if (kv.Length != 2 || !Buttons.ContainsKey(kv[0].Trim()))
                continue;
            var keys = new List<int>();
            foreach (var name in kv[1].Split('+'))
                if (Enum.TryParse<KeyCode>(name.Trim(), true, out var code) && code != KeyCode.None)
                    keys.Add((int)code);
            if (keys.Count == 0)
                continue;
            _doubleTaps[kv[0].Trim()] = new DoubleTap { Keys = keys.ToArray() };
            described.Add($"{kv[0].Trim()} x2 = {string.Join("+", keys.ConvertAll(k => ((KeyCode)k).ToString()))}");
        }
        if (described.Count > 0)
            Plugin.Logger.LogInfo("[Touch] Double taps: " + string.Join(", ", described));
    }

    private void ReleaseAll()
    {
        UpdateMenuClick(false);
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
        var input = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = (ushort)vk, wScan = scan, dwFlags = flags } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>Moves the mouse by this much, as a real mouse would (for HeadAim).</summary>
    internal static void MoveMouse(int dx, int dy) => Mouse(MOUSEEVENTF_MOVE, dx, dy, 0);

    internal static bool GameHasFocus() => GetActiveWindow() != IntPtr.Zero;

    private static void Mouse(uint flags, int dx, int dy, uint data)
    {
        var input = new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

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
