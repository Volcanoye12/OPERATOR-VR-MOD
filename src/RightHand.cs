using System;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.XR;

namespace OperatorVR;

/// <summary>
/// The gun goes where your right hand really is. The game holds the weapon from 'WeaponHolder' (under 'WeaponLean',
/// moved with the camera), and its hand IK puts the right hand on the gun. A transform of our own is slipped in
/// between WeaponLean and the weapon holders, and moved every frame so the character's right hand lands on the right
/// controller's position; the game's own animation of the holder (sway, recoil, lean) still plays on top, and the
/// hands follow the gun through the game's IK as before. Where it points is still the hand-aim's job (TouchKeys).
/// Controls.GunFollowsHand, F2 switches it for the session.
/// </summary>
public class RightHand : MonoBehaviour
{
    public RightHand(IntPtr ptr) : base(ptr) { }

    // Furthest the gun is moved from where the game holds it (metres)
    private const float MaxOffset = 0.8f;

    private static RightHand s_instance;

    private PlayerNetworking _local;
    private Transform _offsetNode, _rightHand;
    private Vector3 _offset;        // world offset applied last frame
    private float _nextScan;
    private bool _loggedFirst, _gaveUp;
    private int _appliedFrame = -1;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    private const int VK_F2 = 0x71;
    private bool _f2Down;
    private static bool s_offByKey;
    // F2 flips whatever the setting says, for this session
    private static bool Enabled => Plugin.GunFollowsHand.Value != s_offByKey;

    internal static void Apply(Harmony harmony)
    {
        // Moved before the game's full-body IK runs, so the hands land on the gun where it is now
        try
        {
            var target = AccessTools.Method(typeof(CharAnimationHelper), "LateUpdate");
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(RightHand), nameof(AfterCharAnimation)));
            Plugin.Logger.LogInfo("[RightHand] hooked CharAnimationHelper.LateUpdate");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[RightHand] patching failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void AfterCharAnimation(CharAnimationHelper __instance)
    {
        using var __t = ModTime.Measure("RightHand anim hook");
        var self = s_instance;
        if (self == null || self._offsetNode == null || __instance == null || !OpenXRBootstrap.IsRunning || !Enabled)
            return;
        try
        {
            if (__instance.transform.root.Pointer != self._offsetNode.root.Pointer)
                return;
            self.Place();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[RightHand] {e.GetType().Name}: {e.Message}");
            self._offsetNode = null;
        }
    }

    private void Start()
    {
        s_instance = this;
        Application.add_onBeforeRender((UnityEngine.Events.UnityAction)new Action(CheckBeforeRender));
    }

    private void Update()
    {
        using var __t = ModTime.Measure("RightHand");
        var f2 = (GetAsyncKeyState(VK_F2) & 0x8000) != 0;
        if (f2 && !_f2Down)
        {
            s_offByKey = !s_offByKey;
            Plugin.Logger.LogInfo($"[RightHand] Gun follows the hand {(Enabled ? "on" : "off")} (F2)");
        }
        _f2Down = f2;

        if (!OpenXRBootstrap.IsRunning || !Enabled)
        {
            // Back to where the game holds it
            foreach (var node in _offsetNodes)
                if (node != null)
                    node.localPosition = Vector3.zero;
            _offset = Vector3.zero;
            return;
        }
        if (Time.unscaledTime >= _nextScan || _offsetNode == null)
        {
            _nextScan = Time.unscaledTime + 2f;
            try { Setup(); }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[RightHand] {e.GetType().Name}: {e.Message}");
                _nextScan = Time.unscaledTime + 10f;
            }
        }
    }

    private void LateUpdate()
    {
        using var __t = ModTime.Measure("RightHand late");
        // In case the hook didn't run this frame
        if (_offsetNode != null && _appliedFrame != Time.frameCount && OpenXRBootstrap.IsRunning && Enabled)
        {
            try { Place(); }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[RightHand] {e.GetType().Name}: {e.Message}");
                _offsetNode = null;
            }
        }
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
            _offsetNode = null;
            return;
        }
        if (_local != null && _local.Pointer == local.Pointer && (_offsetNode != null || _gaveUp))
            return;
        _local = local;
        _gaveUp = true;     // until it works, don't retry (and log) every two seconds

        var holder = local.ActiveWeaponParent;
        if (holder == null || holder.parent == null)
            return;

        _rightHand = null;
        foreach (var t in local.GetComponentsInChildren<Transform>(true))
            if (t != null && t.name == "hand_r" && t.parent != null && t.parent.name == "lowerarm_r" &&
                t.parent.parent != null && t.parent.parent.name == "upperarm_r")
            {
                _rightHand = t;
                break;
            }

        // First try moved only the weapon holders: the gun moved but the hands stayed, so the hands' IK targets
        // aren't on the gun. Everything that says where the hands go has to move with it: the weapon holders, the
        // first-person animation rig (FPSAnimationHelper, e.g. 'AR-15 RIG') and the full-body IK's hand targets.
        // The offset goes in just above the lowest transform they all hang off
        var things = new System.Collections.Generic.List<Transform> { holder };
        var report = new System.Collections.Generic.List<string> { $"holder '{holder.name}' @ {Parents(holder, 6)}" };
        // Local functions become methods of this class with a by-ref closure argument, which the IL2CPP class
        // injector can't expose: it stopped the whole plugin loading (Touch controls included). Hidden from it
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        void Add(Transform t, string what)
        {
            if (t == null) return;
            things.Add(t);
            report.Add($"{what} '{t.name}' @ {Parents(t, 6)}");
        }
        Add(local.ActiveSwitchWeaponParent, "switch holder");
        foreach (var h in local.GetComponentsInChildren<FPSAnimationHelper>(false))
            Add(h.transform, "FPS rig");
        var fbik = local.GetComponentInChildren<RootMotion.FinalIK.FullBodyBipedIK>(true);
        try
        {
            Add(fbik?.solver?.rightHandEffector?.target, "right hand IK target");
            Add(fbik?.solver?.leftHandEffector?.target, "left hand IK target");
        }
        catch { }
        try
        {
            var cah = local.GetComponentInChildren<CharAnimationHelper>(true);
            Add(cah?.currentRTarget, "CharAnimationHelper right target");
            Add(cah?.currentLTarget, "CharAnimationHelper left target");
        }
        catch { }
        Plugin.Logger.LogInfo($"[RightHand] Hand placement: {string.Join("; ", report)}");

        // Second try put one offset above all of them, but they only meet at the pelvis (the gun hangs off spine_03,
        // the rigs off the recoil/sway chain), which would move the whole body. Instead each branch gets its own
        // offset, as high up as it goes without holding the camera or the body, and all move by the same amount
        var cam = Camera.main != null ? Camera.main.transform : null;
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        bool HoldsCamOrBody(Transform t) =>(cam != null && cam.IsChildOf(t)) || (_rightHand != null && _rightHand.IsChildOf(t));
        _offsetNodes.Clear();
        var made = new System.Collections.Generic.List<string>();
        foreach (var t in things)
        {
            if (HoldsCamOrBody(t))
                continue;
            var top = t;
            while (top.parent != null && !HoldsCamOrBody(top.parent))
                top = top.parent;
            if (top.parent == null || top.name == OffsetName)
                continue;
            if (top.parent.name == OffsetName)
            {
                if (!_offsetNodes.Exists(n => n.Pointer == top.parent.Pointer))
                    _offsetNodes.Add(top.parent);
                continue;
            }
            var node = new GameObject(OffsetName).transform;
            node.SetParent(top.parent, false);
            node.localPosition = Vector3.zero;
            node.localRotation = Quaternion.identity;
            node.localScale = Vector3.one;
            top.SetParent(node, false);
            _offsetNodes.Add(node);
            made.Add($"'{top.name}' under '{node.parent.name}'");
        }
        _offsetNode = holder.parent != null ? FindOffsetAbove(holder) : null;
        if (_offsetNode == null)
        {
            Plugin.Logger.LogWarning("[RightHand] The gun holder couldn't be given an offset; gun stays where the game holds it");
            return;
        }
        _gaveUp = false;
        _offset = Vector3.zero;
        _haveGrip = false;
        Plugin.Logger.LogInfo($"[RightHand] Offsets put in for {string.Join(", ", made)}; right hand {(_rightHand != null ? "found" : "NOT found")}");
    }

    private const string OffsetName = "OperatorVR Gun Offset";
    private readonly System.Collections.Generic.List<Transform> _offsetNodes = new();

    private static Transform FindOffsetAbove(Transform t)
    {
        for (var p = t.parent; p != null; p = p.parent)
            if (p.name == OffsetName)
                return p;
        return null;
    }

    private static string Parents(Transform t, int count)
    {
        var names = new System.Collections.Generic.List<string>();
        for (var p = t.parent; p != null && names.Count < count; p = p.parent)
            names.Add(p.name);
        return string.Join(" < ", names);
    }

    /// <summary>Moves the gun so the character's right hand lands where the right controller is.</summary>
    private void Place()
    {
        _appliedFrame = Time.frameCount;
        var holder = _local != null ? _local.ActiveWeaponParent : null;
        var lean = _offsetNode != null ? _offsetNode.parent : null;
        if (_offsetNode == null || lean == null || holder == null || !_haveGrip || !TryGetController(out var handWorld))
            return;

        // Where the game holds the gun without our offset, and where its grip (the right hand on it) is then.
        // The hand is read relative to the holder after the IK has put it on the gun (CheckBeforeRender), since
        // during LateUpdate the hand bone is still where the animation left it
        var current = _offsetNode.position - lean.position;
        var gameHolder = holder.position - current;
        var gameGrip = gameHolder + holder.rotation * _gripLocal;
        var want = Vector3.ClampMagnitude(handWorld - gameGrip, MaxOffset);
        _offset = want;
        foreach (var node in _offsetNodes)
            if (node != null && node.parent != null)
                node.position = node.parent.position + want;

        if (!_loggedFirst)
        {
            _loggedFirst = true;
            Plugin.Logger.LogInfo($"[RightHand] Gun moved {want.magnitude:0.00} m to the controller ({want})");
        }
    }

    // The right hand's grip on the gun, relative to the weapon holder, measured once the game's IK has placed it
    private bool _haveGrip;
    private Vector3 _gripLocal;

    private void CheckBeforeRender()
    {
        var holder = _local != null ? _local.ActiveWeaponParent : null;
        if (_rightHand == null || holder == null || !OpenXRBootstrap.IsRunning)
            return;
        try
        {
            var local = Quaternion.Inverse(holder.rotation) * (_rightHand.position - holder.position);
            // Follows weapon changes, but gently so recoil or a reload doesn't shove the gun around
            _gripLocal = _haveGrip ? Vector3.Lerp(_gripLocal, local, Time.unscaledDeltaTime * 2f) : local;
            _haveGrip = true;
        }
        catch { }
    }

    private static bool TryGetController(out Vector3 worldPos)
    {
        worldPos = default;
        var device = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        if (!device.isValid)
            return false;
        var id = device.deviceId;
        // The grip pose sits in the palm, closest to the character's wrist bone
        if (!XRFeature.Vector3(id, "DevicePosition", out var pos) || !XRFeature.Quaternion(id, "DeviceRotation", out var rot))
        {
            if (!XRFeature.Vector3(id, "PointerPosition", out pos) || !XRFeature.Quaternion(id, "PointerRotation", out rot))
                return false;
        }
        return HeadTracking.TryTrackingToWorld(pos, rot, out worldPos, out _);
    }
}
