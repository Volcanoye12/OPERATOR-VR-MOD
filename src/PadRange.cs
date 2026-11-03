using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace OperatorVR;

/// <summary>
/// The shoot house's range target switch with a gamepad. In flat mode you hold Interact (F) with the aim on the top
/// part of the small switch (target away) or the bottom part (target back); in VR those parts are hard to hit. With
/// the Touch controllers TouchKeys picks the part by where the gun points; this does the same for the pad: while F is
/// held (R3 on the user's PS5 layout) within 3 m of a switch, the game's aim level or up uses 'Forwards' (away),
/// aimed well down at the floor uses 'Backwards' (back), calling the part's Interact() every frame as the game does
/// while F is held on it, and switching it off when F is let go. Controls.PadRangeSwitch.
/// </summary>
public class PadRange : MonoBehaviour
{
    public PadRange(IntPtr ptr) : base(ptr) { }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    private const int VK_F = 0x46;

    // Aim more than this far below level = bring the target back; the booth reaches this far from the switch
    private const float BackDegrees = 50f;
    private const float Reach = 3f;

    private readonly List<InteractableRangeTargetHandle> _handles = new();
    private float _nextScan;
    private InteractableRangeTargetHandle _used;

    private void Update()
    {
        using var __t = ModTime.Measure("PadRange");
        var on = OpenXRBootstrap.IsRunning && Plugin.PadRangeSwitch.Value && !Plugin.TouchControls.Value &&
                 TouchKeys.GameHasFocus() && (GetAsyncKeyState(VK_F) & 0x8000) != 0;
        try
        {
            if (!on || !UseHandle())
                LetGo();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Range] {e.GetType().Name}: {e.Message}");
            LetGo();
            _nextScan = Time.unscaledTime + 10f;
        }
    }

    private bool UseHandle()
    {
        if (Time.unscaledTime >= _nextScan)
        {
            _nextScan = Time.unscaledTime + 3f;
            _handles.Clear();
            foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<InteractableRangeTargetHandle>(), FindObjectsSortMode.None))
            {
                var h = o.TryCast<InteractableRangeTargetHandle>();
                if (h != null)
                    _handles.Add(h);
            }
        }
        if (_handles.Count == 0 || !HeadTracking.TryGetGameCameraPose(out _, out var eye, out _) ||
            !HeadTracking.TryGetGameAim(out var pitch, out _))
            return false;

        // Unity pitch: positive looks down
        var want = pitch < BackDegrees ? "Forward" : "Backward";
        InteractableRangeTargetHandle best = null;
        var bestDist = Reach;
        foreach (var h in _handles)
        {
            if (h == null || !h.isActiveAndEnabled || h.name.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            var d = Vector3.Distance(eye, h.transform.position);
            if (d < bestDist)
            {
                best = h;
                bestDist = d;
            }
        }
        if (best == null)
            return false;

        if (_used != null && _used.Pointer != best.Pointer)
            LetGo();
        best.Interact();
        if (_used == null)
        {
            _used = best;
            Plugin.Logger.LogInfo($"[Range] Switch '{best.name}' held (aim {pitch:0}° down, {bestDist:0.0} m away)");
        }
        return true;
    }

    private void LetGo()
    {
        if (_used == null)
            return;
        try { _used.inputActive = false; } catch { }
        Plugin.Logger.LogInfo($"[Range] Switch '{_used.name}' let go");
        _used = null;
    }
}
