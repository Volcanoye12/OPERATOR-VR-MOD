using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.XR;
using ISInputDevice = UnityEngine.InputSystem.InputDevice;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace OperatorVR;

/// <summary>
/// Plays OPERATOR with the Oculus Touch controllers: the game already understands gamepads (a PS5 pad works), so
/// while VR runs a virtual Gamepad is added to its Input System and fed from the Touch sticks and buttons:
///   left stick = left stick (move), right stick = right stick (turn), triggers = triggers (aim / fire),
///   A/B/X/Y = south/east/west/north (Cross/Circle/Square/Triangle), grips = shoulders (L1/R1),
///   stick clicks = L3/R3, left menu button = Start (Options).
/// Events are queued only when something changes, so a real pad still works alongside.
/// The state is written as raw bytes in GamepadState's layout and queued as a StateEvent ('STAT' / 'GPAD'),
/// which avoids the Input System's generic APIs (not usable through IL2CPP interop).
/// </summary>
public class TouchGamepad : MonoBehaviour
{
    public TouchGamepad(IntPtr ptr) : base(ptr) { }

    // GamepadButton bits in GamepadState.buttons
    private const int DpadUp = 0, DpadDown = 1, DpadLeft = 2, DpadRight = 3;
    private const int North = 4, East = 5, South = 6, West = 7;
    private const int LeftStickPress = 8, RightStickPress = 9, LeftShoulder = 10, RightShoulder = 11, Start = 12, Select = 13;

    // InputEvent header is 20 bytes (type, size, device id, event id, time); StateEvent adds the 4-byte state format,
    // then GamepadState: uint buttons, Vector2 leftStick, Vector2 rightStick, float leftTrigger, float rightTrigger
    private const int HeaderSize = 24;
    private const int StateSize = 28;
    private const int EventSize = HeaderSize + StateSize;
    private static readonly int StatType = FourCC('S', 'T', 'A', 'T');
    private static readonly int GamepadFormat = FourCC('G', 'P', 'A', 'D');

    private ISInputDevice _pad;
    private IntPtr _buffer;
    private readonly byte[] _state = new byte[StateSize];
    private readonly byte[] _lastSent = new byte[StateSize];
    private bool _sentAny;
    private bool _loggedFirstInput;
    private bool _failed;
    private float _nextDiag;
    private int _diagCount;

    /// <summary>
    /// First Touch input reached the virtual gamepad but the game ignored it. If the game uses PlayerInput, each
    /// player only listens to the devices paired with its InputUser (here the PS5 pad); pair the virtual pad too.
    /// </summary>
    private void PairWithPlayers()
    {
        try
        {
            var players = UnityEngine.InputSystem.PlayerInput.all;
            Plugin.Logger.LogInfo($"[Touch] PlayerInput components: {players.Count}");
            for (var i = 0; i < players.Count; i++)
            {
                var p = players[i];
                var user = p.user;
                var devices = user.pairedDevices;
                var names = new System.Collections.Generic.List<string>();
                for (var d = 0; d < devices.Count; d++)
                    names.Add($"{devices[d].name} ({devices[d].layout})");
                Plugin.Logger.LogInfo($"[Touch]   '{p.name}': scheme={p.currentControlScheme}, autoSwitch={!p.neverAutoSwitchControlSchemes}, " +
                                      $"inputActive={p.inputIsActive}, paired=[{string.Join(", ", names)}]");
                UnityEngine.InputSystem.Users.InputUser.PerformPairingWithDevice(_pad, user, UnityEngine.InputSystem.Users.InputUserPairingOptions.None);
                Plugin.Logger.LogInfo($"[Touch]   paired the virtual gamepad with '{p.name}'");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[Touch] Pairing with players: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>What the game's Input System sees: all devices, the current gamepad, and our pad's own state.</summary>
    private void LogInputState()
    {
        try
        {
            var all = InputSystem.devices;
            var names = new System.Collections.Generic.List<string>();
            for (var i = 0; i < all.Count; i++)
                names.Add($"{all[i].name} [{all[i].layout}] t={all[i].lastUpdateTime:0.0}");
            var current = Gamepad.current;
            var pad = _pad.TryCast<Gamepad>();
            Plugin.Logger.LogInfo($"[Touch] Input System devices: {string.Join("; ", names)}");
            Plugin.Logger.LogInfo($"[Touch] Gamepad.current='{(current == null ? "-" : current.name)}', virtual pad: " +
                                  $"A(south) pressed={pad?.buttonSouth.isPressed}, last update {_pad.lastUpdateTime:0.0}, now {Time.realtimeSinceStartupAsDouble:0.0}");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[Touch] Input state log: {e.GetType().Name}: {e.Message}");
        }
    }

    private static int FourCC(char a, char b, char c, char d) => (a << 24) | (b << 16) | (c << 8) | d;

    private void Update()
    {
        using var __t = ModTime.Measure("TouchGamepad");
        if (!OpenXRBootstrap.IsRunning || !Plugin.TouchAsGamepad.Value || _failed)
        {
            RemovePad();
            return;
        }

        try
        {
            var left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            var right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            if (!left.isValid && !right.isValid)
                return;

            if (_pad == null)
            {
                _pad = InputSystem.AddDevice("Gamepad", "OperatorVR Touch", null);
                _buffer = Marshal.AllocHGlobal(EventSize);
                Plugin.Logger.LogInfo($"[Touch] Virtual gamepad added (device id {_pad.deviceId}) for the Touch controllers");
            }

            BuildState(left, right);
            if (_sentAny && SameAsLast())
                return;

            Send();
            Array.Copy(_state, _lastSent, StateSize);
            _sentAny = true;
            if (!AllZero())
            {
                // Games often read Gamepad.current, which only follows devices that are made current
                _pad.TryCast<Gamepad>()?.MakeCurrent();
                if (!_loggedFirstInput)
                {
                    _loggedFirstInput = true;
                    Plugin.Logger.LogInfo("[Touch] First Touch input sent to the game");
                    PairWithPlayers();
                    _nextDiag = Time.unscaledTime + 1f;
                }
            }
            if (_loggedFirstInput && Time.unscaledTime >= _nextDiag && _diagCount < 6)
            {
                _diagCount++;
                _nextDiag = Time.unscaledTime + 5f;
                LogInputState();
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Touch] {e.GetType().Name}: {e.Message}");
            // Off until the game restarts, without saving that to the config
            _failed = true;
            Plugin.Logger.LogWarning("[Touch] Touch-as-gamepad switched off until the game restarts, after the error above");
        }
    }

    private void BuildState(UnityEngine.XR.InputDevice left, UnityEngine.XR.InputDevice right)
    {
        uint buttons = 0;
        void Button(UnityEngine.XR.InputDevice d, string usage, int bit)
        {
            if (d.isValid && XRFeature.Bool(d.deviceId, usage, out var v) && v)
                buttons |= 1u << bit;
        }
        Vector2 Stick(UnityEngine.XR.InputDevice d) =>
            d.isValid && XRFeature.Vector2(d.deviceId, "Primary2DAxis", out var v) ? v : Vector2.zero;
        float Axis(UnityEngine.XR.InputDevice d, string usage) =>
            d.isValid && XRFeature.Float(d.deviceId, usage, out var v) ? v : 0f;

        Button(right, "PrimaryButton", South);     // A
        Button(right, "SecondaryButton", East);    // B
        Button(left, "PrimaryButton", West);       // X
        Button(left, "SecondaryButton", North);    // Y
        Button(left, "GripButton", LeftShoulder);
        Button(right, "GripButton", RightShoulder);
        Button(left, "Primary2DAxisClick", LeftStickPress);
        Button(right, "Primary2DAxisClick", RightStickPress);
        Button(left, "MenuButton", Start);

        var ls = Stick(left);
        var rs = Stick(right);
        Write(0, buttons);
        Write(4, ls.x);
        Write(8, ls.y);
        Write(12, rs.x);
        Write(16, rs.y);
        Write(20, Axis(left, "Trigger"));
        Write(24, Axis(right, "Trigger"));
    }

    private void Write(int offset, uint value) => BitConverter.TryWriteBytes(new Span<byte>(_state, offset, 4), value);

    private void Write(int offset, float value)
    {
        // Tiny stick drift would otherwise send events forever and keep taking over from a real pad
        if (Mathf.Abs(value) < 0.05f)
            value = 0f;
        BitConverter.TryWriteBytes(new Span<byte>(_state, offset, 4), value);
    }

    private bool SameAsLast()
    {
        for (var i = 0; i < StateSize; i++)
            if (_state[i] != _lastSent[i])
                return false;
        return true;
    }

    private bool AllZero()
    {
        foreach (var b in _state)
            if (b != 0)
                return false;
        return true;
    }

    private unsafe void Send()
    {
        var p = (byte*)_buffer;
        *(int*)(p + 0) = StatType;                          // InputEvent.type
        *(ushort*)(p + 4) = EventSize;                      // sizeInBytes
        *(ushort*)(p + 6) = (ushort)_pad.deviceId;          // deviceId
        *(uint*)(p + 8) = 0;                                // eventId (assigned by the system)
        *(double*)(p + 12) = Time.realtimeSinceStartupAsDouble; // time
        *(int*)(p + 20) = GamepadFormat;                    // StateEvent.stateFormat
        Marshal.Copy(_state, 0, _buffer + HeaderSize, StateSize);
        // The interop declares InputEvent with managed members, but the pointer is only handed to native code
#pragma warning disable CS8500
        InputSystem.QueueEvent(new InputEventPtr((InputEvent*)_buffer));
#pragma warning restore CS8500
    }

    private void RemovePad()
    {
        if (_pad == null)
            return;
        try
        {
            InputSystem.RemoveDevice(_pad);
            Plugin.Logger.LogInfo("[Touch] Virtual gamepad removed");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[Touch] Removing the virtual gamepad: {e.Message}");
        }
        _pad = null;
        _sentAny = false;
        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }
    }
}
