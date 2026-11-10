using System;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace OperatorVR;

/// <summary>
/// Rendering.LookPreview (the user's wish, 2026-11-10): during the GPU speed test in Georgia one feature switched
/// off for a moment made the picture "so clear and colourful". To tell which, without another test round: once per
/// game, 40 s into the first mission in VR, four looks are shown in turn for 8 s each, twice over, each labelled in
/// the headset (LOOK 1 NORMAL, 2 NO HAZE = fog/atmosphere off, 3 RAW COLOURS = post-processing off, 4 BOTH). Then
/// everything goes back to normal and the user names the number.
/// </summary>
public class LookPreview : MonoBehaviour
{
    public LookPreview(IntPtr ptr) : base(ptr) { }

    private const float StartAfter = 40f, Each = 8f;
    private const int Rounds = 2;
    private static readonly string[] Names = { "LOOK 1: NORMAL", "LOOK 2: NO HAZE", "LOOK 3: RAW COLOURS", "LOOK 4: NO HAZE + RAW COLOURS" };
    private static readonly string[] NotMissions = { "", "ApplicationSplashScreen", "3d Menu", "Operation Room" };

    private bool _done;
    private int _step = -1;          // index into Rounds * Names
    private float _nextAt;
    private string _scene;
    private float _sceneSince;
    private HDAdditionalCameraData _hd;

    // What the camera had before, per setting
    private bool _saved;
    private bool _custom, _fogOn, _fogMasked, _postOn, _postMasked;

    private void Update()
    {
        if (_done || !Plugin.LookPreview.Value)
            return;
        try
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? "";
            if (scene != _scene)
            {
                if (_step >= 0)
                    Stop("the scene changed");
                _scene = scene;
                _sceneSince = Time.unscaledTime;
            }
            if (!OpenXRBootstrap.IsRunning)
            {
                if (_step >= 0)
                    Stop("VR stopped");
                return;
            }
            if (_step < 0)
            {
                if (Array.IndexOf(NotMissions, scene) >= 0 || Time.unscaledTime - _sceneSince < StartAfter || GpuAudit.Running)
                    return;
                var cam = Camera.main;
                _hd = cam != null ? cam.GetComponent<HDAdditionalCameraData>() : null;
                if (_hd == null)
                    return;
                Save();
                _step = 0;
                _nextAt = Time.unscaledTime;
                Plugin.Logger.LogInfo($"[Looks] Look preview starting in '{scene}': {Names.Length} looks, {Each:0} s each, {Rounds} rounds");
            }
            if (Time.unscaledTime < _nextAt)
                return;
            if (_step >= Rounds * Names.Length)
            {
                Stop("done");
                return;
            }
            var look = _step % Names.Length;
            Apply(fogOff: look == 1 || look == 3, postOff: look == 2 || look == 3);
            StatusHud.ShowNotice(Names[look], Each + 0.5f);
            Plugin.Logger.LogInfo($"[Looks] {Names[look]}");
            _step++;
            _nextAt = Time.unscaledTime + Each;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Looks] {e.GetType().Name}: {e.Message}");
            Stop("error");
        }
    }

    private void Save()
    {
        var s = _hd.m_RenderingPathCustomFrameSettings;
        var m = _hd.renderingPathCustomFrameSettingsOverrideMask;
        _custom = _hd.customRenderingSettings;
        _fogOn = s.IsEnabled(FrameSettingsField.AtmosphericScattering);
        _fogMasked = m.mask[(uint)FrameSettingsField.AtmosphericScattering];
        _postOn = s.IsEnabled(FrameSettingsField.Postprocess);
        _postMasked = m.mask[(uint)FrameSettingsField.Postprocess];
        _saved = true;
    }

    private void Apply(bool fogOff, bool postOff)
    {
        if (_hd == null || !_saved)
            return;
        var s = _hd.m_RenderingPathCustomFrameSettings;
        var m = _hd.renderingPathCustomFrameSettingsOverrideMask;
        s.SetEnabled(FrameSettingsField.AtmosphericScattering, fogOff ? false : _fogOn);
        m.mask[(uint)FrameSettingsField.AtmosphericScattering] = fogOff || _fogMasked;
        s.SetEnabled(FrameSettingsField.Postprocess, postOff ? false : _postOn);
        m.mask[(uint)FrameSettingsField.Postprocess] = postOff || _postMasked;
        _hd.m_RenderingPathCustomFrameSettings = s;
        _hd.renderingPathCustomFrameSettingsOverrideMask = m;
        _hd.customRenderingSettings = fogOff || postOff || _custom;
    }

    private void Stop(string why)
    {
        try
        {
            if (_hd != null && _saved)
            {
                Apply(false, false);
                _hd.customRenderingSettings = _custom;
            }
        }
        catch { }
        _done = true;
        _step = -1;
        StatusHud.ShowNotice(why == "done" ? "LOOK PREVIEW DONE - WHICH NUMBER?" : "LOOK PREVIEW STOPPED", 8f);
        Plugin.Logger.LogInfo($"[Looks] Look preview {(why == "done" ? "finished" : $"stopped ({why})")}; back to the normal look");
    }
}
