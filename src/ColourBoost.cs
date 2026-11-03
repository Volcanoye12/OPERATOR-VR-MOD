using System;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace OperatorVR;

/// <summary>
/// Colours looked washed out in the headset (pale green trees). While VR runs, a global HDRP volume of our own
/// overrides Color Adjustments saturation with the game's own value plus Rendering.Saturation (and contrast likewise
/// with Rendering.Contrast, only when that isn't 0). The game's 'Global Volume' sets saturation -15 and contrast 50 at
/// priority 1,000,000, which beat our first volume (priority 1000), so ours now sits above it; the pain volumes only
/// set contrast/exposure, which we leave alone by default. The game's value is re-read every 5 s (scenes change it).
/// The volume is switched off when VR stops.
/// </summary>
public class ColourBoost : MonoBehaviour
{
    public ColourBoost(IntPtr ptr) : base(ptr) { }

    private GameObject _go;
    private Volume _volume;
    private ColorAdjustments _adjust;
    private float _nextCheck;
    private float _gameSat, _gameContrast;
    private string _lastLog;

    private void Update()
    {
        using var __t = ModTime.Measure("ColourBoost");
        var on = OpenXRBootstrap.IsRunning && (Plugin.Saturation.Value != 0f || Plugin.Contrast.Value != 0f);
        try
        {
            if (!on)
            {
                if (_volume != null && _volume.enabled)
                {
                    _volume.enabled = false;
                    Plugin.Logger.LogInfo("[Colour] Colour boost off");
                }
                return;
            }
            if (_volume == null)
                Create();
            _volume.enabled = true;
            if (Time.unscaledTime >= _nextCheck)
            {
                _nextCheck = Time.unscaledTime + 5f;
                ReadGameValues();
            }
            _adjust.saturation.value = Mathf.Clamp(_gameSat + Plugin.Saturation.Value, -100f, 100f);
            _adjust.contrast.overrideState = Plugin.Contrast.Value != 0f;
            _adjust.contrast.value = Mathf.Clamp(_gameContrast + Plugin.Contrast.Value, -100f, 100f);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Colour] {e.GetType().Name}: {e.Message}");
            enabled = false;
        }
    }

    private void Create()
    {
        _go = new GameObject("OperatorVR Colour Boost");
        UnityEngine.Object.DontDestroyOnLoad(_go);
        _volume = _go.AddComponent<Volume>();
        _volume.isGlobal = true;
        _volume.priority = 5000000f;   // above the game's 'Global Volume' (1,000,000)
        _volume.weight = 1f;
        var profile = ScriptableObject.CreateInstance<VolumeProfile>();
        profile.name = "OperatorVR Colour Boost";
        _adjust = profile.Add(Il2CppType.Of<ColorAdjustments>(), false).Cast<ColorAdjustments>();
        _adjust.saturation.overrideState = true;
        _volume.sharedProfile = profile;
    }

    /// <summary>The game's own saturation/contrast: the highest-priority active global volume that sets each.</summary>
    private void ReadGameValues()
    {
        float sat = 0f, con = 0f, satPri = float.MinValue, conPri = float.MinValue;
        string satFrom = "none", conFrom = "none";
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<Volume>(), FindObjectsSortMode.None))
        {
            var v = o.TryCast<Volume>();
            if (v == null || v.Pointer == _volume.Pointer || !v.isActiveAndEnabled || !v.isGlobal || v.weight < 0.5f ||
                v.sharedProfile == null)
                continue;
            foreach (var c in v.sharedProfile.components)
            {
                var ca = c?.TryCast<ColorAdjustments>();
                if (ca == null || !ca.active)
                    continue;
                if (ca.saturation.overrideState && v.priority > satPri)
                {
                    satPri = v.priority;
                    sat = ca.saturation.value;
                    satFrom = v.name;
                }
                if (ca.contrast.overrideState && v.priority > conPri)
                {
                    conPri = v.priority;
                    con = ca.contrast.value;
                    conFrom = v.name;
                }
            }
        }
        _gameSat = sat;
        _gameContrast = con;
        var log = $"[Colour] Saturation: game {sat:0} ('{satFrom}') + {Plugin.Saturation.Value:0} = " +
                  $"{Mathf.Clamp(sat + Plugin.Saturation.Value, -100f, 100f):0}" +
                  (Plugin.Contrast.Value != 0f ? $"; contrast: game {con:0} ('{conFrom}') + {Plugin.Contrast.Value:0}" : "");
        if (log != _lastLog)
        {
            _lastLog = log;
            Plugin.Logger.LogInfo(log);
        }
    }
}
