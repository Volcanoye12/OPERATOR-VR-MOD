using System;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace OperatorVR;

/// <summary>
/// Lean with the stick clicks while aiming down the sights (the user's idea, 2026-11-01): left-stick click leans left,
/// right-stick click leans right, and stays leaned until the stick is clicked again (either one) or you stop aiming.
/// The game leans only while Q/E is held (a tap leaned a little and came straight back), so the lean key is held down
/// for as long as you lean. Not aiming, the right-stick click is still Use (F, held as long as the click) and the
/// left-stick click is push-to-talk (H, the game's Voicechat, held as long as the click). The PS5
/// layout sends 9 on the left-stick click and 7 on the right one (both unused by the game).
///
/// Earlier tries, all taken out: an action layer held by the aim trigger (letting go of aim re-aimed: a stray click
/// from Steam), L1/R1 while aiming ("didn't feel natural"), the D-pad while aiming (the thumb left the stick), and
/// stick direction + left click (nothing happened). Controls.StickClickLean.
/// </summary>
public class AimLean : MonoBehaviour
{
    public AimLean(IntPtr ptr) : base(ptr) { }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    private const int VK_LEFT_CLICK = 0x39, VK_RIGHT_CLICK = 0x37;

    private PlayerNetworking _me;
    private float _nextFind;
    private bool _leftWas, _rightWas, _useHeld, _talkHeld, _leftUsed, _rightUsed;
    private float _leftSince, _rightSince;
    private static AimLean s_instance;

    /// <summary>Push-to-talk is held (for the talking indicator).</summary>
    internal static bool Talking => s_instance != null && s_instance._talkHeld;

    private void Awake() => s_instance = this;
    private int _lean;      // -1 left (Q held), 0 upright, 1 right (E held)
    private int _logs;

    private void Update()
    {
        using var __t = ModTime.Measure("AimLean");
        try
        {
            if (!Plugin.StickClickLean.Value || !TouchKeys.GameHasFocus())
            {
                LeanTo(0, "game not in front");
                LetGoOfUse();
                LetGoOfTalk();
                _leftWas = _rightWas = false;
                return;
            }
            Find();
            var aiming = false;
            try { aiming = _me != null && _me.isADS; } catch { }

            // While aiming, a lean needs the click held a moment (Controls.LeanHoldTime): a stick pushed in by
            // accident leaned right and the shots went right (2026-11-03). Straightening up stays a plain click
            var now = Time.unscaledTime;
            var hold = Mathf.Max(0f, Plugin.LeanHoldTime.Value);
            var left = (GetAsyncKeyState(VK_LEFT_CLICK) & 0x8000) != 0;
            if (left && !_leftWas)
            {
                _leftSince = now;
                _leftUsed = true;
                if (_lean != 0)
                    LeanTo(0, "left-stick click");
                else if (!aiming)
                {
                    // Not aiming: push-to-talk (the game's Voicechat, H held). No pad button had it, so online there
                    // was no way to talk (2026-11-02)
                    TouchKeys.PressKey(KeyCode.H, true);
                    _talkHeld = true;
                    if (_logs++ < 20)
                        Plugin.Logger.LogInfo("[AimLean] left-stick click held: talking");
                }
                else
                    _leftUsed = false;
            }
            if (left && !_leftUsed && aiming && _lean == 0 && now - _leftSince >= hold)
            {
                _leftUsed = true;
                LeanTo(-1, "left-stick click held");
            }
            if (!left && _talkHeld)
                LetGoOfTalk();
            _leftWas = left;

            var right = (GetAsyncKeyState(VK_RIGHT_CLICK) & 0x8000) != 0;
            if (right && !_rightWas)
            {
                _rightSince = now;
                _rightUsed = true;
                if (_lean != 0)
                    LeanTo(0, "right-stick click");
                else if (!aiming)
                {
                    // Use, as the click always was
                    TouchKeys.PressKey(KeyCode.F, true);
                    _useHeld = true;
                }
                else
                    _rightUsed = false;
            }
            if (right && !_rightUsed && aiming && _lean == 0 && now - _rightSince >= hold)
            {
                _rightUsed = true;
                LeanTo(1, "right-stick click held");
            }
            if (!right && _useHeld)
                LetGoOfUse();
            _rightWas = right;

            if (!aiming && _lean != 0)
                LeanTo(0, "stopped aiming");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[AimLean] {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Holds the lean key for this side (-1 left, 1 right), or lets go of it (0).</summary>
    private void LeanTo(int want, string why)
    {
        if (want == _lean)
            return;
        if (_lean != 0)
            TouchKeys.PressKey(_lean < 0 ? KeyCode.Q : KeyCode.E, false);
        if (want != 0)
            TouchKeys.PressKey(want < 0 ? KeyCode.Q : KeyCode.E, true);
        if (_logs++ < 20)
            Plugin.Logger.LogInfo($"[AimLean] {why}: {Side(_lean)} -> {Side(want)}");
        _lean = want;
    }

    private static string Side(int lean) => lean < 0 ? "lean left" : lean > 0 ? "lean right" : "upright";

    private void LetGoOfTalk()
    {
        if (!_talkHeld)
            return;
        _talkHeld = false;
        TouchKeys.PressKey(KeyCode.H, false);
    }

    private void LetGoOfUse()
    {
        if (!_useHeld)
            return;
        _useHeld = false;
        TouchKeys.PressKey(KeyCode.F, false);
    }

    private void Find()
    {
        if (_me != null || Time.unscaledTime < _nextFind)
            return;
        _nextFind = Time.unscaledTime + 2f;
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<PlayerNetworking>(), FindObjectsSortMode.None))
        {
            var pn = o.TryCast<PlayerNetworking>();
            if (pn != null && pn.gameObject.name.Contains("MyPlayer"))
            {
                _me = pn;
                break;
            }
        }
    }
}
