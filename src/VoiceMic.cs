using System;
using Il2CppInterop.Runtime;
#if MELON
using Il2CppMetaVoiceChat.Input.Mic;
#else
using MetaVoiceChat.Input.Mic;
#endif
using UnityEngine;

namespace OperatorVR;

/// <summary>
/// Online, nobody heard the player (2026-11-02), though push-to-talk (H) reached the game. The game's voice chat
/// (MetaVoiceChat) records from one microphone it picks (VcMic.ActiveDevice); Windows also lists the DualSense's
/// headset socket and Steam's streaming microphone, which would record nothing. While a game runs this logs the
/// microphones the voice chat sees and the one it uses, and if one matching Controls.VoiceMic (the Quest's microphone
/// through Virtual Desktop by default) is there but not in use, selects it. Empty Controls.VoiceMic leaves it alone.
/// </summary>
public class VoiceMic : MonoBehaviour
{
    public VoiceMic(IntPtr ptr) : base(ptr) { }

    private float _nextCheck;
    private string _lastReport = "";

    private void Update()
    {
        if (Time.unscaledTime < _nextCheck)
            return;
        _nextCheck = Time.unscaledTime + 3f;
        using var __t = ModTime.Measure("VoiceMic");
        try
        {
            foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<VcMicAudioInput>(), FindObjectsSortMode.None))
            {
                var input = o.TryCast<VcMicAudioInput>();
                var mic = input != null ? input.Mic : null;
                if (mic == null || input.metaVc == null || !input.metaVc.isLocalPlayer)
                    continue;
                Check(input, mic);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Voice] {e.GetType().Name}: {e.Message}");
            _nextCheck = Time.unscaledTime + 30f;
        }
    }

    private void Check(VcMicAudioInput input, VcMic mic)
    {
        var devices = mic.Devices;
        var names = new System.Collections.Generic.List<string>();
        string wanted = null;
        var match = Plugin.VoiceMic.Value;
        if (devices != null)
            foreach (var d in devices)
            {
                names.Add(d);
                if (wanted == null && !string.IsNullOrEmpty(match) && d.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0)
                    wanted = d;
            }
        var active = mic.ActiveDevice ?? "";
        var report = $"microphones seen: {(names.Count == 0 ? "none" : string.Join(", ", names))}; chosen '{mic.SelectedDevice}', " +
                     $"in use '{active}', recording {mic.IsRecording}, can speak {!input.metaVc.CannotSpeak}";
        if (report != _lastReport)
        {
            _lastReport = report;
            Plugin.Logger.LogInfo($"[Voice] {report}");
        }
        if (wanted != null && active != wanted && mic.SelectedDevice != wanted)
        {
            Plugin.Logger.LogInfo($"[Voice] Switching the voice chat's microphone to '{wanted}' (Controls.VoiceMic)");
            input.SetSelectedDevice(wanted);
        }
    }
}
