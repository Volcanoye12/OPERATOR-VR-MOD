using System;
using UnityEngine;

namespace OperatorVR;

/// <summary>
/// "We haven't got a button for holding your breath. Can we make that automatic when you aim down the gun?"
/// (2026-11-06). The game's Hold Breath is a held key (its binds: HoldBreath = LeftAlt, Continuous). While you're
/// aimed in (PlayerNetworking.isADS) for a moment, that key is held for you, and let go when you stop aiming. How long
/// a breath lasts is still the game's own rule; Controls.AutoHoldBreathSeconds can cap it shorter. Works with the
/// Touch controllers and the PS5 pad. Controls.AutoHoldBreath.
/// </summary>
public class AutoHoldBreath : MonoBehaviour
{
    public AutoHoldBreath(IntPtr ptr) : base(ptr) { }

    private const KeyCode BreathKey = KeyCode.LeftAlt;
    private const float SettleSeconds = 0.25f;   // aimed this long first, so a quick flick past the sights doesn't

    private bool _holding;
    private float _aimedSince = -1f, _heldSince;
    private bool _spentThisAim;
    private int _logs;

    private void Update()
    {
        using var __t = ModTime.Measure("AutoHoldBreath");
        var me = OpenXRBootstrap.IsRunning && Plugin.AutoHoldBreath.Value && TouchKeys.GameHasFocus() &&
                 !MissionScreen.HasControl && !WeaponBench.Open ? LocalPlayer.Get() : null;
        var aimed = false;
        try { aimed = me != null && me.isADS; } catch { }

        if (!aimed)
        {
            _aimedSince = -1f;
            Release("stopped aiming");
            // Still recovering from the last breath: the next aim waits for it (the game's timer back near 0)
            _spentThisAim = ReadTimer() > 0.25f;
            return;
        }
        if (_aimedSince < 0f)
            _aimedSince = Time.unscaledTime;
        if (_holding)
        {
            // The game allows holdBreathTime (4 s) of held breath; held longer it counts you out of breath and the
            // sway got worse (log 2026-11-06: held 6-33 s, its timer ran to 85 s, Breath 1.00 -> 1.35). So breathe
            // out just before the game's limit, or sooner if AutoHoldBreathSeconds says so
            var cap = GameLimit() - 0.4f;
            var mine = Plugin.AutoHoldBreathSeconds.Value;
            if (mine > 0f && (cap <= 0f || mine < cap))
                cap = mine;
            if (cap > 0f && Time.unscaledTime - _heldSince >= cap)
            {
                _spentThisAim = true;
                Release("breathing out before the game's limit");
            }
            return;
        }
        if (Time.unscaledTime - _aimedSince < SettleSeconds)
            return;
        // Another breath once the game's timer has recovered (it counts back down while you breathe), so a long aim
        // gets a held breath, a breather, then another
        if (_spentThisAim)
        {
            var t = ReadTimer();
            if (t < 0f || t > 0.25f)
                return;
            _spentThisAim = false;
        }
        if (_stamina == null)
            try { _stamina = me.GetComponentInChildren<ArmStamina>(true); } catch { }
        TouchKeys.PressKey(BreathKey, true);
        _holding = true;
        _heldSince = Time.unscaledTime;
        _timerAtStart = ReadTimer();
        _maxTimer = _timerAtStart;
        if (_logs++ < 5)
            Plugin.Logger.LogInfo($"[Breath] Aimed in: holding breath (the game's Hold Breath key); the game's breath: {Describe()}");
    }

    // Proof the game took it (2026-11-06, "didn't hear any noises, did it work?"): its ArmStamina counts
    // _holdBreathTimer while you hold your breath. The highest value seen during the hold is logged when letting go
    private ArmStamina _stamina;
    private float _timerAtStart, _maxTimer;

    private float GameLimit()
    {
        try { return _stamina != null ? _stamina.holdBreathTime : 0f; } catch { return 0f; }
    }

    private float ReadTimer()
    {
        try { return _stamina != null ? _stamina._holdBreathTimer : -1f; } catch { return -1f; }
    }

    private string Describe()
    {
        try
        {
            return _stamina == null ? "no ArmStamina found"
                : $"timer {_stamina._holdBreathTimer:0.00} of {_stamina.holdBreathTime:0.0} s, Breath {_stamina.Breath:0.00}";
        }
        catch (Exception e) { return e.GetType().Name; }
    }

    private void LateUpdate()
    {
        if (_holding)
            _maxTimer = Mathf.Max(_maxTimer, ReadTimer());
    }

    private void Release(string why)
    {
        if (!_holding)
            return;
        TouchKeys.PressKey(BreathKey, false);
        _holding = false;
        if (_logs++ < 12)
            Plugin.Logger.LogInfo($"[Breath] Let go: {why} after {Time.unscaledTime - _heldSince:0.0} s; the game's hold-breath timer went " +
                                  $"{_timerAtStart:0.00} -> {_maxTimer:0.00} ({(_maxTimer > _timerAtStart + 0.05f ? "it was holding breath" : "unchanged: the game didn't take it")}); now {Describe()}");
    }

    private void OnDisable() => Release("mod stopped");
}
