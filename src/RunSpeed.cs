using System;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace OperatorVR;

/// <summary>
/// Running felt slow. The player's MasterController holds the move speeds (WalkSpeed, JogSpeed, SprintSpeed,
/// SprintSpeedHighReady, before the weight penalty); the jog and sprint ones are multiplied by Controls.RunSpeed on
/// the local player (checked every second, so a respawned player gets it too). If the game changes a value itself, the
/// new value becomes the base. Walking is left alone.
/// </summary>
public class RunSpeed : MonoBehaviour
{
    public RunSpeed(IntPtr ptr) : base(ptr) { }

    private float _nextScan;
    private IntPtr _player;
    private MasterController _master;
    // Base values and what we last set: jog, sprint, sprint high ready
    private readonly float[] _base = new float[3], _set = new float[3];

    private void Update()
    {
        using var __t = ModTime.Measure("RunSpeed");
        if (Time.unscaledTime < _nextScan)
            return;
        _nextScan = Time.unscaledTime + 1f;
        try
        {
            // The player found last time, while it's still there: searching every second showed up in the timings
            if (_master != null)
            {
                Apply(_master);
                return;
            }
            _master = null;
            foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<MasterController>(), FindObjectsSortMode.None))
            {
                var m = o.TryCast<MasterController>();
                if (m == null || !m.transform.root.name.Contains("MyPlayer"))
                    continue;
                _master = m;
                Apply(m);
                return;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Speed] {e.GetType().Name}: {e.Message}");
            _nextScan = Time.unscaledTime + 10f;
        }
    }

    private void Apply(MasterController m)
    {
        var mult = Mathf.Clamp(Plugin.RunSpeed.Value, 0.5f, 3f);
        var now = new[] { m.JogSpeed, m.SprintSpeed, m.SprintSpeedHighReady };
        var fresh = m.Pointer != _player;
        _player = m.Pointer;
        for (var i = 0; i < 3; i++)
        {
            // A new player, or the game set it itself: that's the base
            if (fresh || !Mathf.Approximately(now[i], _set[i]))
                _base[i] = now[i];
            _set[i] = _base[i] * mult;
        }
        if (fresh)
            Plugin.Logger.LogInfo($"[Speed] Walk {m.WalkSpeed:0.00}, jog {_base[0]:0.00} → {_set[0]:0.00}, sprint {_base[1]:0.00} → " +
                                  $"{_set[1]:0.00} (high ready {_base[2]:0.00} → {_set[2]:0.00}); weight {m.totalWeight:0.0} of " +
                                  $"max {m.maxWeight:0.0}, speed modifier {m.SpeedModifer:0.00}");
        m.JogSpeed = _set[0];
        m.SprintSpeed = _set[1];
        m.SprintSpeedHighReady = _set[2];
    }
}
