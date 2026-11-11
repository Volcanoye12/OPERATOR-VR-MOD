using System;
using System.Collections;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;

namespace OperatorVR;

/// <summary>
/// Moves the gun, with both hands on it, a fixed amount from where the game holds it (Controls.GunShiftRight / Up /
/// Forward, in metres, relative to where you aim). The gun hangs off 'WeaponHolderParent' (under spine_03) and the
/// right hand's IK target off the first-person rig ('AR-15 RIG' &lt; WeaponStance &lt; 'RIG_Parent', under the recoil/
/// sway chain); the left hand's target is on the gun. Both are moved by the same amount before the game's IK runs,
/// so the hands stay on the gun, and put back once the frame is drawn. (Moving only the gun holder left the hands
/// behind. The first version slotted transforms of ours into the hierarchy; after it the laptop could no longer be
/// used, so nothing is re-parented now.) Aiming down the sights the shift fades out so the sights reach the eye.
/// Ctrl + arrow keys nudge it while playing.
/// </summary>
public class GunShift : MonoBehaviour
{
    public GunShift(IntPtr ptr) : base(ptr) { }

    private static readonly string[] ShiftNames = { "WeaponHolderParent", "RIG_Parent" };

    private static GunShift s_instance;
    private PlayerNetworking _local;
    private readonly System.Collections.Generic.List<Transform> _moved = new();
    private readonly System.Collections.Generic.List<Vector3> _savedLocal = new();
    private bool _shifted;
    private float _nextScan;
    private int _placedFrame = -1, _blendFrame = -1;
    private float _shiftWeight = 1f;    // 0 while aiming down the sights

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    private const int VK_CONTROL = 0x11, VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28;
    private bool _leftDown, _rightDown, _upDown, _downDown;

    internal static void Apply(Harmony harmony)
    {
        try
        {
            var target = AccessTools.Method(typeof(CharAnimationHelper), "LateUpdate");
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(GunShift), nameof(AfterCharAnimation)));
            Plugin.Logger.LogInfo("[GunShift] hooked CharAnimationHelper.LateUpdate");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[GunShift] patching failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void AfterCharAnimation(CharAnimationHelper __instance)
    {
        using var __t = ModTime.Measure("GunShift anim hook");
        var self = s_instance;
        if (self == null || self._moved.Count == 0 || __instance == null || !OpenXRBootstrap.IsRunning)
            return;
        try
        {
            if (self._local == null || __instance.transform.root.Pointer != self._local.transform.root.Pointer)
                return;
            self.Place();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[GunShift] {e.GetType().Name}: {e.Message}");
            self._moved.Clear();
        }
    }

    private void Start()
    {
        s_instance = this;
        StartCoroutine(RestoreAfterFrame().WrapToIl2Cpp());
    }

    private void Update()
    {
        using var __t = ModTime.Measure("GunShift");
        if (!OpenXRBootstrap.IsRunning)
            return;
        Nudge();
        if (Time.unscaledTime >= _nextScan)
        {
            _nextScan = Time.unscaledTime + 2f;
            try { Setup(); }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[GunShift] {e.GetType().Name}: {e.Message}");
                _nextScan = Time.unscaledTime + 10f;
            }
        }
    }

    private void LateUpdate()
    {
        using var __t = ModTime.Measure("GunShift late");
        // In case the hook didn't run this frame
        if (_moved.Count > 0 && _placedFrame != Time.frameCount && OpenXRBootstrap.IsRunning)
        {
            try { Place(); }
            catch { _moved.Clear(); }
        }
    }

    // Ctrl + Left/Right moves the gun sideways, Ctrl + Up/Down up and down, Ctrl + Shift + Up/Down further away /
    // nearer (the user: "sometimes it feels too close to the face", 2026-11-10), 1 cm a press; saved to the settings
    private const int VK_SHIFT = 0x10;

    private void Nudge()
    {
        var ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
        var shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
        var changed = false;
        if (KeyPressed(ctrl, VK_RIGHT, ref _rightDown)) { Plugin.GunShiftRight.Value += 0.01f; changed = true; }
        if (KeyPressed(ctrl, VK_LEFT, ref _leftDown)) { Plugin.GunShiftRight.Value -= 0.01f; changed = true; }
        if (KeyPressed(ctrl, VK_UP, ref _upDown))
        {
            if (shift) Plugin.GunShiftForward.Value = Mathf.Min(0.25f, Plugin.GunShiftForward.Value + 0.01f);
            else Plugin.GunShiftUp.Value += 0.01f;
            changed = true;
        }
        if (KeyPressed(ctrl, VK_DOWN, ref _downDown))
        {
            if (shift) Plugin.GunShiftForward.Value = Mathf.Max(-0.1f, Plugin.GunShiftForward.Value - 0.01f);
            else Plugin.GunShiftUp.Value -= 0.01f;
            changed = true;
        }
        if (changed)
            Plugin.Logger.LogInfo($"[GunShift] Gun shifted {Plugin.GunShiftRight.Value * 100:0} cm right, {Plugin.GunShiftUp.Value * 100:0} cm up, " +
                                  $"{Plugin.GunShiftForward.Value * 100:0} cm forward");
    }

    // (by-ref argument: hidden from the IL2CPP class injector, which can't expose it)
    [HideFromIl2Cpp]
    private static bool KeyPressed(bool ctrl, int vk, ref bool was)
    {
        var down = ctrl && (GetAsyncKeyState(vk) & 0x8000) != 0;
        var pressed = down && !was;
        was = down;
        return pressed;
    }

    private void Setup()
    {
        PlayerNetworking local = null;
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<PlayerNetworking>(), FindObjectsSortMode.None))
        {
            var p = o.TryCast<PlayerNetworking>();
            if (p != null && p.gameObject.name.Contains("MyPlayer"))
            {
                local = p;
                break;
            }
        }
        if (local == null)
        {
            _local = null;
            _moved.Clear();
            return;
        }
        if (_local != null && _local.Pointer == local.Pointer && _moved.Count > 0)
            return;
        _local = local;
        _moved.Clear();
        foreach (var t in local.GetComponentsInChildren<Transform>(true))
            if (t != null && t.parent != null && Array.IndexOf(ShiftNames, t.name) >= 0)
                _moved.Add(t);
        Plugin.Logger.LogInfo(_moved.Count > 0
            ? $"[GunShift] Ready to move {string.Join(", ", _moved.ConvertAll(t => $"'{t.name}' (under '{t.parent.name}')"))}"
            : $"[GunShift] Nothing named {string.Join("/", ShiftNames)} found on the player");
    }

    private void Place()
    {
        _placedFrame = Time.frameCount;
        var cam = Camera.main;
        if (cam == null || _shifted)
            return;

        // Aiming down the sights the game puts the sights at its camera (your eye): the shift fades out so they stay
        // there, and comes back when you stop aiming
        var ads = false;
        try { ads = _local != null && _local.isADS; } catch { }
        if (_placedFrame != _blendFrame)
        {
            _blendFrame = _placedFrame;
            _shiftWeight = Mathf.MoveTowards(_shiftWeight, ads ? 0f : 1f, Time.unscaledDeltaTime * 6f);
        }

        // Relative to where the game aims (its camera before the head pose is added for drawing)
        var t = cam.transform;
        // (Not forward for a pistol: 2026-11-11 the user saw the pistol "not even attached to the right hand" when
        // held up. The forward shift moves the gun but not the hand; on a rifle 5 cm doesn't show, on a pistol it does)
        // (Touch controllers only: with the PS5 pad everything stays as it was)
        var touchPistol = Plugin.TouchControls.Value && ManualReload.PistolInHand;
        var forward = touchPistol ? 0f : Plugin.GunShiftForward.Value;
        var shift = (t.right * Plugin.GunShiftRight.Value + t.up * Plugin.GunShiftUp.Value + t.forward * forward) * _shiftWeight;
        // Aiming down the sights the game lines them up with its camera, which in VR sits between the eyes: neither
        // eye looks straight down them. Move the gun half the distance between the eyes towards the aiming eye
        // (Controls.AimEye), fading in as the shift above fades out
        shift += t.right * (AimEyeOffset() * (1f - _shiftWeight));
        // And along the line of the sights, away from the eye (Controls.AdsGunForward; 2026-11-10: aiming put the eye
        // inside the scope's tube). Straight along the game's aim, so the sights stay lined up
        // Not for a pistol (2026-11-11): this push was added on 10 Nov for the rifle's scope, and it is why the right
        // hand stopped holding the pistol when held out ("like it was earlier"). The pistol is held by the body's own
        // arm, which is already straight when aiming and can't follow the gun 8 cm further: the log measured the hand
        // a steady 7.8-8 cm short of the grip, exactly this push. The rifles bring their own arms, so they're fine
        if (!touchPistol)
            shift += t.forward * (Mathf.Clamp(Plugin.AdsGunForward.Value, -0.1f, 0.3f) * (1f - _shiftWeight));
        shift += SteadyCorrection(t) * _shiftWeight;
        // The sights to your eye (Touch, not pistols; 2026-11-11, the user: after a recenter "the rifle came to my eye,
        // it was to the right, couldn't really see down the scope". The game lines its sights up with its own camera;
        // your eye in VR is that camera plus where your head is now compared with the last recenter (the log: recentered
        // with the head 5 cm to one side). Aiming in, the rifle now also moves sideways/up-down by that difference, so
        // the scope is in front of the eye you look with)
        // (Taken out 15:45: the user "you've totally ruined aiming down the sights, it's all set to the right" — the
        // measured eye offset is not to be trusted. Left here switched off)
        if (false && Plugin.TouchControls.Value && !touchPistol && _shiftWeight < 0.999f && TryEye(out var eye))
        {
            var d = eye - t.position;
            var side = d - t.forward * Vector3.Dot(d, t.forward);
            // More than 12 cm off can't be a head offset: something else is wrong with the measure, so leave it alone
            var trusted = side.magnitude < 0.12f;
            if (trusted)
                shift += side * (1f - _shiftWeight);
            if (ads && !_eyeLogged)
            {
                _eyeLogged = true;
                Plugin.Logger.LogInfo($"[GunShift] Aimed in: the eye is {Vector3.Dot(side, t.right) * 100f:0.0} cm right, {Vector3.Dot(side, t.up) * 100f:0.0} cm up of the game's aim line: {(trusted ? "sights moved to it" : "too far to be right, left alone")}");
            }
        }
        if (!ads)
            _eyeLogged = false;
        if (shift.sqrMagnitude < 1e-8f)
            return;
        _savedLocal.Clear();
        foreach (var m in _moved)
        {
            _savedLocal.Add(m != null ? m.localPosition : Vector3.zero);
            if (m != null)
                m.position += shift;
        }
        _shifted = true;
    }

    private bool _eyeLogged;

    // Where your eyes are in the game world (the headset's centre eye, placed as it's drawn)
    private static bool TryEye(out Vector3 eye)
    {
        eye = default;
        var d = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.Head);
        if (!d.isValid)
            return false;
        if (!XRFeature.Vector3(d.deviceId, "CenterEyePosition", out var p) && !XRFeature.Vector3(d.deviceId, "DevicePosition", out p))
            return false;
        return HeadTracking.TryTrackingToWorld(p, Quaternion.identity, out eye, out _);
    }

    // ---- Steady gun (Controls.SteadyGun) ----
    // "The gun position moves several times ... can we not lock the gun to the player" (2026-11-03): the game's bob,
    // sway and body bending move the gun about in front of a VR view that only follows your head. The gun holder's
    // place relative to the game's camera is followed slowly (about a second); anything quicker (bob, sway, jumps) is
    // taken out, so the gun stays put in view and still settles after a stance or weapon change. Off while aiming down
    // the sights (the weight above fades it), so the sights still come to the eye
    private const float SteadyFollowSeconds = 1f;
    private Vector3 _steadyRest;
    private bool _haveRest;
    private IntPtr _steadyWeapon;
    private int _steadyLogs;

    [HideFromIl2Cpp]
    private Vector3 SteadyCorrection(Transform cam)
    {
        if (!Plugin.SteadyGun.Value || _moved.Count == 0 || _moved[0] == null)
        {
            _haveRest = false;
            return Vector3.zero;
        }
        var holder = _moved[0];
        var local = cam.InverseTransformPoint(holder.position);
        // A different weapon in hand, coming out of aiming, or a big jump (respawn, vehicle): start again from here
        IntPtr weapon = IntPtr.Zero;
        try { weapon = _local != null && _local.activeWeapon != null ? _local.activeWeapon.Pointer : IntPtr.Zero; } catch { }
        if (!_haveRest || weapon != _steadyWeapon || _shiftWeight < 0.99f || (local - _steadyRest).sqrMagnitude > 0.25f)
        {
            _steadyRest = local;
            _haveRest = true;
            _steadyWeapon = weapon;
            return Vector3.zero;
        }
        var k = 1f - Mathf.Exp(-Time.deltaTime / SteadyFollowSeconds);
        _steadyRest = Vector3.Lerp(_steadyRest, local, k);
        var correction = cam.TransformPoint(_steadyRest) - holder.position;
        if (_steadyLogs++ == 0)
            Plugin.Logger.LogInfo("[GunShift] Steady gun on: bob and sway taken out of the gun's place in view (Controls.SteadyGun)");
        return correction;
    }

    private float _halfIpd = 0.032f;
    private float _nextIpdRead;
    private bool _loggedIpd;

    /// <summary>Metres to the right (negative = left) from between the eyes to the aiming eye.</summary>
    private float AimEyeOffset()
    {
        var eye = (Plugin.AimEye.Value ?? "").Trim().ToLowerInvariant();
        var side = eye.StartsWith("r") ? 1f : eye.StartsWith("l") ? -1f : 0f;
        // Not with a magnified scope: it shows its own zoomed picture, and moving it off the game's aim line put the
        // shots ~3 cm to the side of its crosshair (ACOG at the range: hits low and right of the X)
        if (side == 0f || ExtraCameras.ScopeActive)
            return 0f;
        // Half the headset's eye distance, read every few seconds (falls back to 32 mm)
        if (Time.unscaledTime >= _nextIpdRead)
        {
            _nextIpdRead = Time.unscaledTime + 5f;
            try
            {
                var l = UnityEngine.XR.InputTracking.GetLocalPosition(UnityEngine.XR.XRNode.LeftEye);
                var r = UnityEngine.XR.InputTracking.GetLocalPosition(UnityEngine.XR.XRNode.RightEye);
                var d = Vector3.Distance(l, r);
                if (d > 0.045f && d < 0.085f)
                {
                    _halfIpd = d * 0.5f;
                    if (!_loggedIpd)
                    {
                        _loggedIpd = true;
                        Plugin.Logger.LogInfo($"[GunShift] Eyes {d * 1000f:0} mm apart: the sights move {_halfIpd * 1000f:0} mm to the {(side > 0 ? "right" : "left")} eye when aiming");
                    }
                }
            }
            catch { }
        }
        return side * _halfIpd;
    }

    // Put them back once the frame is drawn, so the game's own code never sees them moved
    [HideFromIl2Cpp]
    private IEnumerator RestoreAfterFrame()
    {
        while (true)
        {
            yield return new WaitForEndOfFrame();
            if (!_shifted)
                continue;
            _shifted = false;
            for (var i = 0; i < _moved.Count && i < _savedLocal.Count; i++)
                try { if (_moved[i] != null) _moved[i].localPosition = _savedLocal[i]; } catch { }
        }
    }
}
