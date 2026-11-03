using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace OperatorVR;

/// <summary>
/// The right stick's up/down turns the game's aim, and the character bends at the waist to follow it. In VR the view
/// stays level with your head, so with the aim far down (-59°, 2026-11-03) the headset ended up inside your own chest
/// and arms ("my character all twisted"). While VR runs, the game's own look limits (CMF.CameraController
/// upperVerticalLimit / lowerVerticalLimit) are narrowed to Head.MaxAimUpDown degrees; put back when VR stops.
/// </summary>
public class PitchLimit : MonoBehaviour
{
    public PitchLimit(IntPtr ptr) : base(ptr) { }

    private readonly Dictionary<IntPtr, (CMF.CameraController cc, float up, float down)> _changed = new();
    private float _nextScan;
    private bool _wasRunning;

    private void Update()
    {
        using var __t = ModTime.Measure("PitchLimit");
        var running = OpenXRBootstrap.IsRunning && Plugin.MaxAimUpDown.Value > 0f;
        if (!running)
        {
            if (_wasRunning)
                RestoreAll();
            _wasRunning = false;
            return;
        }
        _wasRunning = true;
        if (Time.unscaledTime < _nextScan)
            return;
        _nextScan = Time.unscaledTime + 2f;
        try
        {
            var limit = Mathf.Clamp(Plugin.MaxAimUpDown.Value, 20f, 89f);
            foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<CMF.CameraController>(), FindObjectsSortMode.None))
            {
                var cc = o.TryCast<CMF.CameraController>();
                if (cc == null || _changed.ContainsKey(cc.Pointer))
                    continue;
                _changed[cc.Pointer] = (cc, cc.upperVerticalLimit, cc.lowerVerticalLimit);
                cc.upperVerticalLimit = Mathf.Min(cc.upperVerticalLimit, limit);
                cc.lowerVerticalLimit = Mathf.Min(cc.lowerVerticalLimit, limit);
                Plugin.Logger.LogInfo($"[Pitch] '{LaptopDiagnostics.Path(cc.transform)}': look up/down limited to {limit:0}° in VR " +
                                      $"(was up {_changed[cc.Pointer].up:0}°, down {_changed[cc.Pointer].down:0}°)");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Pitch] {e.GetType().Name}: {e.Message}");
            _nextScan = Time.unscaledTime + 10f;
        }
    }

    private void RestoreAll()
    {
        foreach (var (cc, up, down) in _changed.Values)
        {
            try
            {
                if (cc == null)
                    continue;
                cc.upperVerticalLimit = up;
                cc.lowerVerticalLimit = down;
            }
            catch { }
        }
        if (_changed.Count > 0)
            Plugin.Logger.LogInfo($"[Pitch] Look limits put back on {_changed.Count} camera controller(s)");
        _changed.Clear();
    }
}
