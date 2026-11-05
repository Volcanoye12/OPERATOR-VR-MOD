using System;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
using UnityEngine.XR;

namespace OperatorVR;

/// <summary>
/// Switches between the Touch controllers and a gamepad (the PS5 pad through Steam Input) while playing, by flipping
/// Controls.TouchControls (read by everything each frame, and saved).
///   - By hand (2026-11-04): hold both thumbsticks pressed in for 2 seconds, or press F8.
///   - By itself (Controls.AutoSwitchControls, 2026-11-04: on the pad, the Touch hand aim still steered the gun when the
///     controller was moved out of the way): any key or mouse button the PS5 pad sends while the game is in front
///     makes the Touch controllers step aside; pressing A, B, X, Y or a trigger on a Touch controller hands control
///     back (on letting go, so that press doesn't also fire or reload). Moving or gripping the controllers doesn't.
/// The pad's presses are told from the mod's own by a tag (PadWatch).
/// </summary>
public class ControlSwitch : MonoBehaviour
{
    public ControlSwitch(IntPtr ptr) : base(ptr) { }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    private const int VK_F8 = 0x77;
    private const float HoldSeconds = 2f;
    private static readonly string[] WakeButtons = { "A", "B", "X", "Y", "RightTrigger", "LeftTrigger" };

    private bool _f8Was, _comboDone, _wakeArmed, _started;
    private float _comboSince = -1f, _ignorePadUntil;
    private long _padSeen;
    private string _wakeButton;

    private void Update()
    {
        using var __t = ModTime.Measure("ControlSwitch");
        if (!OpenXRBootstrap.IsRunning)
            return;
        try
        {
            if (!_started)
            {
                _started = true;
                PadWatch.Start();
                _padSeen = PadWatch.LastTick;
            }
            var focused = TouchKeys.GameHasFocus();
            var f8 = focused && (GetAsyncKeyState(VK_F8) & 0x8000) != 0;
            if (f8 && !_f8Was)
                Set(!Plugin.TouchControls.Value, "F8");
            _f8Was = f8;

            UpdateCombo();
            if (Plugin.AutoSwitchControls.Value)
                UpdateAuto(focused);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Switch] {e.GetType().Name}: {e.Message}");
        }
    }

    private void UpdateCombo()
    {
        var both = TouchKeys.IsPressed("LeftStickClick") && TouchKeys.IsPressed("RightStickClick");
        if (!both)
        {
            _comboSince = -1f;
            _comboDone = false;
            return;
        }
        if (_comboSince < 0f)
            _comboSince = Time.unscaledTime;
        if (!_comboDone && Time.unscaledTime - _comboSince >= HoldSeconds)
        {
            _comboDone = true;   // once per hold
            Set(!Plugin.TouchControls.Value, "both thumbsticks held");
        }
    }

    private void UpdateAuto(bool focused)
    {
        var padTick = PadWatch.LastTick;
        var padNew = padTick != _padSeen;
        _padSeen = padTick;

        if (Plugin.TouchControls.Value)
        {
            _wakeArmed = false;
            if (padNew && focused && Time.unscaledTime >= _ignorePadUntil)
                Set(false, $"PS5 pad pressed ({PadWatch.LastWhat})");
            return;
        }

        // On the pad: a Touch face button or trigger, pressed and let go, hands control back
        string held = null;
        foreach (var b in WakeButtons)
            if (TouchKeys.IsPressed(b))
            {
                held = b;
                break;
            }
        if (held != null && focused)
        {
            _wakeArmed = true;
            _wakeButton = held;
        }
        else if (held == null && _wakeArmed)
        {
            _wakeArmed = false;
            Set(true, $"Touch {_wakeButton} pressed");
        }
    }

    private void Set(bool touch, string why)
    {
        if (Plugin.TouchControls.Value == touch)
            return;
        Plugin.TouchControls.Value = touch;   // saved to the config file
        Plugin.Logger.LogInfo($"[Switch] {why}: now playing with {(touch ? "the Touch controllers" : "a gamepad (PS5 pad)")}");
        if (touch)
        {
            _ignorePadUntil = Time.unscaledTime + 1f;
            Haptics.Pulse(XRNode.LeftHand, 0.6f, 0.15f);
            Haptics.Pulse(XRNode.RightHand, 0.6f, 0.15f);
        }
    }
}

/// <summary>
/// Watches the key and mouse-button presses reaching Windows (low-level hooks on a thread of their own) for ones the mod
/// didn't send: every key and mouse event the mod sends carries TouchKeys.OurInputMark; Steam Input's (the PS5 pad)
/// don't. Mouse movement and the F keys (the mod's own hotkeys) aren't counted.
/// </summary>
internal static class PadWatch
{
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string name);

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT { public int x, y; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }

    private static long s_tick;
    private static volatile int s_what;
    private static HookProc s_kbProc, s_mouseProc;   // kept alive: Windows calls them
    private static Thread s_thread;

    internal static long LastTick => Interlocked.Read(ref s_tick);

    internal static string LastWhat
    {
        get
        {
            var code = s_what;
            if (code < 1000)
                return $"key 0x{code:X2}";
            return (code - 1000) switch
            {
                0x201 => "left mouse button",
                0x204 => "right mouse button",
                0x207 => "middle mouse button",
                0x20A => "mouse wheel",
                _ => "side mouse button",
            };
        }
    }

    internal static void Start()
    {
        if (s_thread != null)
            return;
        s_thread = new Thread(Run) { IsBackground = true, Name = "OperatorVR pad watch" };
        s_thread.Start();
    }

    private static void Run()
    {
        try
        {
            s_kbProc = KeyboardHook;
            s_mouseProc = MouseHook;
            var mod = GetModuleHandle(null);
            var kb = SetWindowsHookEx(13, s_kbProc, mod, 0);      // WH_KEYBOARD_LL
            var mouse = SetWindowsHookEx(14, s_mouseProc, mod, 0); // WH_MOUSE_LL
            Plugin.Logger.LogInfo($"[Switch] Watching for PS5 pad presses (keyboard hook {(kb != IntPtr.Zero ? "on" : "FAILED")}, " +
                                  $"mouse hook {(mouse != IntPtr.Zero ? "on" : "FAILED")})");
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Switch] pad watch: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void Seen(int what)
    {
        s_what = what;
        Interlocked.Exchange(ref s_tick, Environment.TickCount64);
    }

    private static IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            var msg = (int)wParam;
            if (nCode >= 0 && (msg == 0x100 || msg == 0x104))   // WM_KEYDOWN, WM_SYSKEYDOWN
            {
                var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (k.dwExtraInfo != TouchKeys.OurInputMark && (k.vkCode < 0x70 || k.vkCode > 0x87))   // not F1-F24
                    Seen((int)k.vkCode);
            }
        }
        catch { }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private static IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            var msg = (int)wParam;
            // Buttons and the wheel only (WM_L/R/MBUTTONDOWN, WM_MOUSEWHEEL, WM_XBUTTONDOWN): not movement
            if (nCode >= 0 && (msg == 0x201 || msg == 0x204 || msg == 0x207 || msg == 0x20A || msg == 0x20B))
            {
                var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (m.dwExtraInfo != TouchKeys.OurInputMark)
                    Seen(1000 + msg);
            }
        }
        catch { }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }
}
