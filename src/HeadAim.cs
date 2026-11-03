using System;
using UnityEngine;
using UnityEngine.XR;

namespace OperatorVR;

/// <summary>
/// Playing with a gamepad (Controls.TouchControls off): while aiming down the sights, the game's up/down aim follows
/// your head. The view follows your head, but the game aims (and lines the sights up) along its own camera, so with
/// the pad's own up/down the reticle was above or below where you looked when raising the gun. Small mouse moves
/// steer the game's pitch to the head's, like the Touch hand aim did; left/right stays on the pad, and outside ADS the
/// pad's up/down works as normal. Controls.HeadAimPitch.
/// </summary>
public class HeadAim : MonoBehaviour
{
    public HeadAim(IntPtr ptr) : base(ptr) { }

    private float _remainder;
    private bool _logged;
    private PlayerNetworking _local;
    private float _nextFind;

    /// <summary>Is the local player aiming down the sights (the game's own isADS)?</summary>
    private bool Aiming()
    {
        try
        {
            if (_local == null && Time.unscaledTime >= _nextFind)
            {
                _nextFind = Time.unscaledTime + 2f;
                foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppInterop.Runtime.Il2CppType.Of<PlayerNetworking>(), FindObjectsSortMode.None))
                {
                    var p = o.TryCast<PlayerNetworking>();
                    if (p != null && p.gameObject.name.Contains("MyPlayer"))
                    {
                        _local = p;
                        break;
                    }
                }
            }
            return _local != null && _local.isADS;
        }
        catch
        {
            _local = null;
            return false;
        }
    }

    private void Update()
    {
        using var __t = ModTime.Measure("HeadAim");
        if (!OpenXRBootstrap.IsRunning || Plugin.TouchControls.Value || !Plugin.HeadAimPitch.Value)
            return;
        // Not in menus or at the laptop (the pad drives the pointer there), nor when the game isn't in front.
        // Only while aiming down the sights: always on, it took up/down away from the pad's right stick, and the user
        // wanted the stick back (the reticle only matters when the sights are up)
        if (VRPointer.Target != null || Cursor.lockState != CursorLockMode.Locked || !TouchKeys.GameHasFocus() || !Aiming())
        {
            _remainder = 0f;
            return;
        }
        try
        {
            var headRot = InputTracking.GetLocalRotation(XRNode.CenterEye);
            var headPos = InputTracking.GetLocalPosition(XRNode.CenterEye);
            if (!HeadTracking.TryTrackingToWorld(headPos, headRot, out _, out var worldRot) ||
                !HeadTracking.TryGetGameAim(out var gamePitch, out _))
                return;

            // Unity pitch: positive looks down
            var fwd = worldRot * Vector3.forward;
            var headPitch = -Mathf.Asin(Mathf.Clamp(fwd.y, -1f, 1f)) * Mathf.Rad2Deg;
            var gap = Mathf.DeltaAngle(gamePitch, headPitch);
            if (Mathf.Abs(gap) < 0.3f)
            {
                _remainder = 0f;
                return;
            }
            var dy = Mathf.Clamp(gap * Plugin.HandAimGain.Value, -80f, 80f) + _remainder;
            var px = (int)dy;
            _remainder = dy - px;
            if (px != 0)
                TouchKeys.MoveMouse(0, px);

            if (!_logged)
            {
                _logged = true;
                Plugin.Logger.LogInfo($"[HeadAim] Up/down aim follows the head (head {headPitch:0.0}°, game {gamePitch:0.0}°)");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[HeadAim] {e.GetType().Name}: {e.Message}");
            enabled = false;
        }
    }
}
