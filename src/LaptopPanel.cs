using System;
using UnityEngine;

namespace OperatorVR;

/// <summary>
/// The mission laptop's screen is only ~0.4 m wide, so in VR its buttons are tiny. While you sit at a laptop
/// (MissionLaptop.AccessLaptop until ExitLaptop), its DreamOS canvas floats in front of you as a bigger panel,
/// closer than the laptop so the laptop doesn't hide it; it goes back onto the laptop when you get up or VR stops.
/// The mouse pointer / laser keep working on it (VRPointer targets it as before). Menus.LaptopPanel* settings.
/// </summary>
public class LaptopPanel : MonoBehaviour
{
    public LaptopPanel(IntPtr ptr) : base(ptr) { }

    private Canvas _canvas;
    private Vector3 _localPos;
    private Quaternion _localRot;
    private Vector3 _localScale;
    private bool _logged;

    // Update, not LateUpdate: VRPointer frames its target in LateUpdate and must see the panel where it is drawn
    private void Update()
    {
        using var __t = ModTime.Measure("LaptopPanel");
        var laptop = LaptopClickLog.CurrentLaptop;
        // With the ODG Ops modpack, F on a laptop opens its Ops Menu over the laptop session; the floated screen, drawn
        // over everything at 0.4 m, hid that menu (2026-11-03), so the screen stays on the laptop while it's open
        // Our own mission screen replaces it too while it's up
        var active = OpenXRBootstrap.IsRunning && Plugin.LaptopPanel.Value && laptop != null &&
                     !VRMenus.IsShowing(VRMenus.OdgOpsMenu) && !MissionScreen.Showing;
        if (!active)
        {
            Restore();
            return;
        }

        try
        {
            var canvasGo = laptop.osCanvas;
            var canvas = canvasGo == null ? null : canvasGo.GetComponent<Canvas>();
            if (canvas == null)
                return;
            if (_canvas == null || _canvas.Pointer != canvas.Pointer)
            {
                Restore();
                _canvas = canvas;
                var t0 = canvas.transform;
                _localPos = t0.localPosition;
                _localRot = t0.localRotation;
                _localScale = t0.localScale;
            }

            var cam = Camera.main;
            if (cam == null)
                return;
            var ct = cam.transform;
            var yaw = Quaternion.Euler(0f, HeadTracking.ViewYaw(ct.rotation), 0f);
            var d = Plugin.LaptopPanelDistance.Value;
            // Width that covers LaptopPanelDegrees of your view at that distance
            var width = 2f * d * Mathf.Tan(Plugin.LaptopPanelDegrees.Value * 0.5f * Mathf.Deg2Rad);
            var rt = canvas.GetComponent<RectTransform>();
            var scale = width / Mathf.Max(1f, rt.sizeDelta.x);

            // Over everything, so the player's arms and gun can't hide it
            MenusOnTop.Put(canvas.gameObject);
            HideOdgWall(canvas.transform);

            var t = canvas.transform;
            // A little below eye level, like a screen on a desk
            t.SetPositionAndRotation(ct.position + yaw * Vector3.forward * d + Vector3.down * (d * 0.15f), yaw);
            t.localScale = Vector3.one * scale / LossyParentScale(t);

            if (!_logged)
            {
                _logged = true;
                Plugin.Logger.LogInfo($"[Laptop] Screen floated in front of you: {width:0.00} m wide at {d:0.00} m");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Laptop] panel: {e.GetType().Name}: {e.Message}");
            Restore();
        }
    }

    private static float LossyParentScale(Transform t) =>
        t.parent == null ? 1f : Mathf.Max(1e-6f, t.parent.lossyScale.x);

    // The ODG Ops modpack puts its own board (a world-space canvas, ODG_OpsWall) on the laptop's screen. Floated with
    // the screen it showed back to front in front of it (2026-11-03), so it's switched off while the screen floats.
    // ODG updates it every frame, so this is repeated each frame
    private Canvas _odgWall;
    private float _nextWallLook;

    private void HideOdgWall(Transform screen)
    {
        if (_odgWall == null && Time.unscaledTime >= _nextWallLook)
        {
            _nextWallLook = Time.unscaledTime + 1f;
            foreach (var c in screen.GetComponentsInChildren<Canvas>(true))
                if (c != null && c.name == "ODG_OpsWall")
                {
                    _odgWall = c;
                    Plugin.Logger.LogInfo("[Laptop] ODG board on the screen hidden while it floats");
                    break;
                }
        }
        if (_odgWall != null)
            _odgWall.enabled = false;
    }

    private void Restore()
    {
        if (_canvas == null)
            return;
        if (_odgWall != null)
        {
            try { _odgWall.enabled = true; } catch { }
            _odgWall = null;
        }
        try
        {
            MenusOnTop.Release(_canvas.gameObject);
            var t = _canvas.transform;
            t.localPosition = _localPos;
            t.localRotation = _localRot;
            t.localScale = _localScale;
            Plugin.Logger.LogInfo("[Laptop] Screen back on the laptop");
        }
        catch { }
        _canvas = null;
        _logged = false;
    }
}
