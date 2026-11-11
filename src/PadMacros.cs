using System;
using UnityEngine;

namespace OperatorVR;

/// <summary>
/// Gamepad helpers (the PS5 pad reaches the game as keyboard/mouse through Steam Input). Some game actions need keys
/// in a sequence a Steam Input binding can't send reliably:
/// - Emergency reload is R pressed twice quickly (Controls.EmergencyReloadKey, V by default: unused by the game; K,
///   the first choice, is RadioChat).
/// - Manual reload / chamber a round is Left Shift + T, with Shift down first: pressed in the same instant the game
///   missed it (seen with the Touch double tap) (Controls.ManualReloadKey, L by default).
/// The user's Steam layout sends those spare keys (double-tap Square = L, L1 + Square = V); this presses
/// the real keys one step per frame.
/// </summary>
public class PadMacros : MonoBehaviour
{
    public PadMacros(IntPtr ptr) : base(ptr) { }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static readonly (KeyCode key, bool down)[] EmergencyReload =
        { (KeyCode.R, true), (KeyCode.R, false), (KeyCode.R, true), (KeyCode.R, false) };
    private static readonly (KeyCode key, bool down)[] ManualReload =
        { (KeyCode.LeftShift, true), (KeyCode.T, true), (KeyCode.T, false), (KeyCode.LeftShift, false) };

    private bool _emergencyDown, _manualDown;
    private (KeyCode key, bool down)[] _running;
    private int _step;

    private bool _loggedBinds;
    private float _nextBindTry;

    // Which key does what in the game, once per run (the Touch controls logged it, but they're off with a gamepad)
    private void LogBindsOnce()
    {
        if (_loggedBinds || Time.unscaledTime < _nextBindTry)
            return;
        _nextBindTry = Time.unscaledTime + 5f;
        try
        {
            var binds = InputLayer.Binds;
            if (binds == null || binds.Length == 0)
                return;
            var all = new System.Collections.Generic.List<string>();
            var set = 0;
            foreach (InputKey key in Enum.GetValues(typeof(InputKey)))
            {
                var i = (int)key;
                if (key == InputKey.None || i >= binds.Length || binds[i] == null)
                    continue;
                var b = binds[i].KeyBind;
                if (b.PrimaryKey != KeyCode.None)
                    set++;
                all.Add($"{key}={(b.PreliminaryKey != KeyCode.None ? b.PreliminaryKey + "+" : "")}{b.PrimaryKey}({b.PressType})");
            }
            // At startup every action read None (keys not loaded yet): wait until they are
            if (set == 0)
                return;
            _loggedBinds = true;
            Plugin.Logger.LogInfo("[Pad] Game key binds: " + string.Join(", ", all));
        }
        catch { }
    }

    // Temporary: a double-tap of Square (manual reload) dropped the magazine. Log R and Left Shift as the pad sends
    // them, with the time, to see whether Steam also sent R (a reload) around the manual reload
    private bool _rWas, _shiftWas;
    private const int VK_R = 0x52, VK_LSHIFT = 0xA0;

    private void LogReloadKeys()
    {
        var r = (GetAsyncKeyState(VK_R) & 0x8000) != 0;
        var shift = (GetAsyncKeyState(VK_LSHIFT) & 0x8000) != 0;
        if (r != _rWas)
            Plugin.Logger.LogInfo($"[Pad] R {(r ? "down" : "up")} at {Time.unscaledTime:0.00} s{(shift ? " (Left Shift held)" : "")}");
        if (shift != _shiftWas && _running == null)
            Plugin.Logger.LogInfo($"[Pad] Left Shift {(shift ? "down" : "up")} at {Time.unscaledTime:0.00} s");
        _rWas = r;
        _shiftWas = shift;
    }

    // The tablet (OpenATAK = M, shown while held) had no pad button: Controls.TabletKey (J by default, which the
    // user's layout sends on L1 + D-pad down and the game doesn't use) holds M for as long as it's held
    private bool _tabletHeld;
    private const int VK_M = 0x4D;

    /// <summary>The tablet is out: M (OpenATAK) is held, from the pad's tablet button or the keyboard.</summary>
    internal static bool TabletOut => (GetAsyncKeyState(VK_M) & 0x8000) != 0;

    private void UpdateTablet(bool focused)
    {
        var vk = VirtualKey(Plugin.TabletKey.Value);
        var want = focused && vk != 0 && (GetAsyncKeyState(vk) & 0x8000) != 0;
        if (want == _tabletHeld)
            return;
        _tabletHeld = want;
        TouchKeys.PressKey(KeyCode.M, want);
        Plugin.Logger.LogInfo($"[Pad] Tablet {(want ? "out" : "away")} ({Plugin.TabletKey.Value} -> M)");
    }

    // One button for all weapons: the pad only reached two of the user's three (Triangle tap 3 / hold 1, with
    // L1 + Triangle for 2 easily missed). Controls.NextWeaponKey (L by default, unused by the game) switches to the
    // next weapon you actually carry, in the order primary (2), second primary (3), sidearm (1), special purpose (4),
    // by tapping that slot's own key, so the game switches exactly as if it were pressed
    private bool _cycleDown;
    private KeyCode _cycleRelease = KeyCode.None;
    private PlayerNetworking _me;
    private float _nextMeFind;

    private static readonly (PlayerNetworking.WeaponSlot slot, KeyCode key)[] CycleOrder =
    {
        (PlayerNetworking.WeaponSlot.Primary, KeyCode.Alpha2),
        (PlayerNetworking.WeaponSlot.SecondPrimary, KeyCode.Alpha3),
        (PlayerNetworking.WeaponSlot.Secondary, KeyCode.Alpha1),
        (PlayerNetworking.WeaponSlot.SpecialPurpose, KeyCode.Alpha4),
    };

    private void UpdateWeaponCycle(bool focused)
    {
        // Let go of the slot key pressed last frame
        if (_cycleRelease != KeyCode.None)
        {
            TouchKeys.PressKey(_cycleRelease, false);
            _cycleRelease = KeyCode.None;
        }
        var vk = VirtualKey(Plugin.NextWeaponKey.Value);
        var down = focused && vk != 0 && (GetAsyncKeyState(vk) & 0x8000) != 0;
        var clicked = down && !_cycleDown;
        _cycleDown = down;
        if (!clicked)
            return;

        if (_me == null && Time.unscaledTime >= _nextMeFind)
        {
            _nextMeFind = Time.unscaledTime + 2f;
            foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppInterop.Runtime.Il2CppType.Of<PlayerNetworking>(), FindObjectsSortMode.None))
            {
                var p = o.TryCast<PlayerNetworking>();
                if (p != null && p.gameObject.name.Contains("MyPlayer"))
                {
                    _me = p;
                    break;
                }
            }
        }
        if (_me == null)
            return;

        var carried = new System.Collections.Generic.List<int>();
        for (var i = 0; i < CycleOrder.Length; i++)
            if (WeaponIn(_me, CycleOrder[i].slot) != null)
                carried.Add(i);
        if (carried.Count == 0)
            return;
        // While a switch is under way the target slot counts as current, so quick presses step on
        var current = _me.CurrentTargetWeaponSlot != PlayerNetworking.WeaponSlot.None ? _me.CurrentTargetWeaponSlot : _me.CurrentWeaponSlot;
        var at = carried.FindIndex(i => CycleOrder[i].slot == current);
        var next = CycleOrder[carried[(at + 1) % carried.Count]];
        TouchKeys.PressKey(next.key, true);
        _cycleRelease = next.key;
        Plugin.Logger.LogInfo($"[Pad] Next weapon: {current} -> {next.slot} (key {next.key}; carrying {carried.Count})");
    }

    /// <summary>The weapon the local player is holding (null if not found).</summary>
    private GameObject HeldWeapon()
    {
        if (_me == null)
            foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppInterop.Runtime.Il2CppType.Of<PlayerNetworking>(), FindObjectsSortMode.None))
            {
                var p = o.TryCast<PlayerNetworking>();
                if (p != null && p.gameObject.name.Contains("MyPlayer"))
                {
                    _me = p;
                    break;
                }
            }
        try { return _me != null ? _me.activeWeapon : null; } catch { return null; }
    }

    private static WeaponV3 WeaponIn(PlayerNetworking p, PlayerNetworking.WeaponSlot slot) => slot switch
    {
        PlayerNetworking.WeaponSlot.Primary => p.primaryWeapon,
        PlayerNetworking.WeaponSlot.SecondPrimary => p.secondPrimaryWeapon,
        PlayerNetworking.WeaponSlot.Secondary => p.secondaryWeapon,
        PlayerNetworking.WeaponSlot.SpecialPurpose => p.specialPurposeWeapon,
        _ => null,
    };

    // Console-style run: Controls.RunKey (O by default: the PS5 layout sends it on a left-stick click; the game doesn't
    // use it) starts running (Left Shift held, the game's Run), and running stops by itself once you stop moving
    // forward (W let go for a moment), or on another click. Steam's own toggle on the stick click stayed on after an
    // accidental click until clicked again
    private const int VK_W = 0x57, VK_LSHIFT_RUN = 0xA0;
    private bool _runKeyDown, _runOn;
    private float _notForwardSince = -1f;

    private void UpdateRun(bool focused)
    {
        var vk = VirtualKey(Plugin.RunKey.Value);
        var down = focused && vk != 0 && (GetAsyncKeyState(vk) & 0x8000) != 0;
        var clicked = down && !_runKeyDown;
        _runKeyDown = down;

        if (!focused)
        {
            StopRun("game not in front");
            return;
        }
        if (clicked)
            ToggleRun();
        if (!_runOn)
            return;

        // Not moving forward for a moment: walk again
        var forward = (GetAsyncKeyState(VK_W) & 0x8000) != 0;
        if (forward)
            _notForwardSince = -1f;
        else if (_notForwardSince < 0f)
            _notForwardSince = Time.unscaledTime;
        else if (Time.unscaledTime - _notForwardSince > 0.25f)
        {
            StopRun("stopped moving forward");
            return;
        }
        // Something else let go of Left Shift (the manual reload's Shift + T, once it's finished): hold it again
        if (_running == null && (GetAsyncKeyState(VK_LSHIFT_RUN) & 0x8000) == 0)
            TouchKeys.PressKey(KeyCode.LeftShift, true);
    }

    private void ToggleRun()
    {
        if (_runOn)
            StopRun("clicked again");
        else
        {
            _runOn = true;
            _notForwardSince = -1f;
            TouchKeys.PressKey(KeyCode.LeftShift, true);
            Plugin.Logger.LogInfo("[Pad] Running");
        }
    }

    // Cross (the user's choice, 2026-11-01; run used to be on the left-stick click): the PS5 layout sends 8 (unused by
    // the game) instead of Space. A tap while the left stick is pushed forward starts (or stops)
    // running; a hold of CrossHoldTime kicks (a tap of Space, the game's Kick on release); any other tap is the Space tap
    // it always was (a kick too: the game's Space is Kick on release and Vault while held)
    private const int VK_CROSS = 0x38;
    private const float CrossHoldTime = 0.35f;
    private bool _crossDown, _crossKicked;
    private float _crossSince, _spaceUpAt = -1f;

    private void UpdateCross(bool focused)
    {
        if (_spaceUpAt >= 0f && Time.unscaledTime >= _spaceUpAt)
        {
            _spaceUpAt = -1f;
            TouchKeys.PressKey(KeyCode.Space, false);
        }
        var down = focused && (GetAsyncKeyState(VK_CROSS) & 0x8000) != 0;
        if (down && !_crossDown)
        {
            _crossSince = Time.unscaledTime;
            _crossKicked = false;
        }
        else if (down && !_crossKicked && Time.unscaledTime - _crossSince >= CrossHoldTime)
        {
            _crossKicked = true;
            TapSpace();
            Plugin.Logger.LogInfo("[Pad] Cross held: kick");
        }
        else if (!down && _crossDown && !_crossKicked)
        {
            if ((GetAsyncKeyState(VK_W) & 0x8000) != 0)
                ToggleRun();
            else
                TapSpace();
        }
        _crossDown = down;
    }

    // Space down now, up a few frames later, so the game sees the press and the release
    private void TapSpace()
    {
        TouchKeys.PressKey(KeyCode.Space, true);
        _spaceUpAt = Time.unscaledTime + 0.06f;
    }

    private void StopRun(string why)
    {
        if (!_runOn)
            return;
        _runOn = false;
        TouchKeys.PressKey(KeyCode.LeftShift, false);
        Plugin.Logger.LogInfo($"[Pad] Walking ({why})");
    }

    // Temporary: D-pad left (mouse Forward = the game's Mouse4, LasersToggle on release / LasersCycle while held)
    // didn't seem to switch the laser on. Log each press, how long it was held, and the gun's laser parts then
    private const int VK_XBUTTON2 = 0x06;
    private bool _laserBtnWas;
    private float _laserBtnSince;

    private void LogLaserButton()
    {
        var down = (GetAsyncKeyState(VK_XBUTTON2) & 0x8000) != 0;
        if (down == _laserBtnWas)
            return;
        _laserBtnWas = down;
        if (down)
        {
            _laserBtnSince = Time.unscaledTime;
            return;
        }
        var held = Time.unscaledTime - _laserBtnSince;
        var parts = new System.Collections.Generic.List<string>();
        try
        {
            foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppInterop.Runtime.Il2CppType.Of<Renderer>(), FindObjectsSortMode.None))
            {
                var r = o.TryCast<Renderer>();
                if (r == null || r.name.IndexOf("Laser", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (!r.transform.root.name.Contains("MyPlayer"))
                    continue;
                parts.Add($"{r.name} (active {r.gameObject.activeInHierarchy}, enabled {r.enabled}, {r.shadowCastingMode}, layer {r.gameObject.layer})");
                if (parts.Count >= 8)
                    break;
            }
        }
        catch { }
        Plugin.Logger.LogInfo($"[Pad] Laser button (mouse Forward) pressed for {held:0.00} s; laser parts: " +
                              $"{(parts.Count == 0 ? "none found" : string.Join("; ", parts))}");
    }

    // Flip-up magnifiers (the game's FlipSights). Read from the game code (GameAssembly): FlipSights.Update flips only
    // while the ZoomOptics key (Left Alt) is held AND the mouse wheel turns (up = flip up, down = flip down), on the
    // equipped weapon; its Activator field isn't used there. Flipping up sets isFlipped, turns its own sight
    // (ADSToDisableWhenSightDown) on and the main sight's ADS (WeaponMod.mainADS under ActualSightModParent) off;
    // flipping down does the reverse. LateUpdate then animates the mount from isFlipped. Earlier attempts (pressing or
    // faking Alt, or setting only isFlipped) worked at best by chance, so on CapsLock (D-pad up) the mod now does those
    // same steps itself. Controls.FlipWithSightSwitch
    private const int VK_CAPITAL = 0x14;
    private bool _capsWas;
    private int _sightPressLogs;
    private readonly System.Collections.Generic.HashSet<string> _flipLogged = new();

    // Spoken "magnifier up" / "flip magnifier" / "magnifier down" (VoiceCommands, 2026-11-04): flips the magnifier on
    // the gun in hand that way (nothing if it already is), and says so if there's a callout recorded for it
    private void VoiceMagnifier()
    {
        var cmd = VoiceBridge.TakeMagnifierCommand();
        if (cmd == 0)
            return;
        var up = cmd == 1;
        var f = HeldMagnifier();
        if (f == null)
        {
            Plugin.Logger.LogInfo("[VoiceCmd] No flip-up magnifier on the gun in hand");
            return;
        }
        if (f.isFlipped != up)
            SetFlipped(f, up);
        Plugin.Logger.LogInfo($"[VoiceCmd] Magnifier '{f.name}' {(up ? "up" : "down")}");
        VoiceBridge.Magnifier(up);
    }

    /// <summary>The flip-up magnifier on the gun in hand, or null.</summary>
    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private FlipSights HeldMagnifier()
    {
        var all = UnityEngine.Object.FindObjectsByType(Il2CppInterop.Runtime.Il2CppType.Of<FlipSights>(), FindObjectsSortMode.None);
        var held = HeldWeapon();
        FlipSights onHeld = null;
        foreach (var o in all)
        {
            var f = o.TryCast<FlipSights>();
            if (f == null || !f.transform.root.name.Contains("MyPlayer"))
                continue;
            if (f.isActiveAndEnabled && f.weapon != null && f.weapon.l_isEquiped)
                return f;
            if (onHeld == null && held != null && f.transform.IsChildOf(held.transform))
                onHeld = f;
        }
        return onHeld;
    }

    private void UpdateFlip(bool focused)
    {
        // (Our own CapsLock press, choosing the scope, isn't a D-pad press)
        var caps = focused && !s_ourSightSwitch && !_capsReleaseNext && (GetAsyncKeyState(VK_CAPITAL) & 0x8000) != 0;
        var pressed = caps && !_capsWas;
        _capsWas = caps;
        if (!pressed || !Plugin.FlipWithSightSwitch.Value)
            return;
        // Every press, so a zoom that "didn't work" shows whether the press arrived at all (13:50: no trace of one)
        if (_sightPressLogs++ < 30)
        {
            var aimingNow = false;
            try { var me = _zoomMe ?? s_zoomPlayer; aimingNow = me != null && me.isADS; } catch { }
            Plugin.Logger.LogInfo($"[Pad] Sight switch pressed (left thumbrest + right stick up), aiming {aimingNow}");
        }

        var all = UnityEngine.Object.FindObjectsByType(Il2CppInterop.Runtime.Il2CppType.Of<FlipSights>(), FindObjectsSortMode.None);
        foreach (var o in all)
        {
            var f = o.TryCast<FlipSights>();
            if (f == null || !f.isActiveAndEnabled || !f.transform.root.name.Contains("MyPlayer"))
                continue;
            var w = f.weapon;
            if (w == null || !w.l_isEquiped)
                continue;                                   // the same check the game makes: only the gun in hand
            if (_flipLogged.Add(f.name))
                Plugin.Logger.LogInfo($"[Pad] Magnifier '{f.name}' on '{w.name}' (flipped now: {f.isFlipped})");
            SetFlipped(f, !f.isFlipped);
            return;
        }
        // 2026-11-04: the HK416D's G33 magnifier ('WilcoxMount_Topbase') was there but D-pad up said "no flip-up
        // magnifier": the strict check above missed it. Fall back to a magnifier on the gun the player is holding
        // (under its active weapon), and log what each one looked like
        var held = HeldWeapon();
        foreach (var o in all)
        {
            var f = o.TryCast<FlipSights>();
            if (f == null || !f.transform.root.name.Contains("MyPlayer"))
                continue;
            var onHeld = held != null && f.transform.IsChildOf(held.transform);
            if (_flipLogged.Add("diag " + f.name))
                Plugin.Logger.LogInfo($"[Pad] Magnifier '{f.name}': active {f.isActiveAndEnabled}, weapon " +
                                      $"'{(f.weapon != null ? f.weapon.name : "none")}' equipped {(f.weapon != null && f.weapon.l_isEquiped)}, " +
                                      $"on the gun in hand {onHeld}");
            if (!onHeld)
                continue;
            Plugin.Logger.LogInfo($"[Pad] Flipping magnifier '{f.name}' (found on the gun in hand)");
            SetFlipped(f, !f.isFlipped);
            return;
        }
        // No magnifier: a scope with a zoom switch (ScopeInputSetter.ZoomMode Toggle, e.g. the Elcan SpecterDR 1x-4x).
        // The game switches it the same way as the magnifier (ZoomOptics = Left Alt held + mouse wheel: up sets
        // zoomToggled, down clears it) and sets the magnification from zoomToggled each frame; D-pad up toggles it
        if (ToggleScopeZoom())
            return;
        if (_flipLogged.Add("(none)"))
            Plugin.Logger.LogInfo("[Pad] Sight switch: no flip-up magnifier or zoom switch on the gun in hand");
    }

    private PlayerNetworking _zoomMe;

    // The game remembers each gun's last sight, and the Elcan gun stayed on its other aiming point after earlier
    // presses: every aim then had the scope's ADS not enabled, its camera off and the picture black (log, 19:16).
    // Shortly after you aim with a gun that has a zoom-switch scope, if the scope isn't the sight in use, the mod
    // presses the game's own sight switch (CapsLock) once, up to 3 times per aim, until it is
    private bool _aimWas;
    private float _scopeCheckAt = -1f;
    private int _scopeTries;
    private bool _capsReleaseNext;
    private float _nextZoomMeFind;
    // Set while the mod itself presses the game's sight switch (here or in ScopeGuard): not a D-pad press, and not
    // blocked by the FindNewADS skip
    internal static bool s_ourSightSwitch;

    private void KeepScopeSelected()
    {
        if (_capsReleaseNext)
        {
            _capsReleaseNext = false;
            TouchKeys.PressKey(KeyCode.CapsLock, false);
            s_ourSightSwitch = false;
        }
        if (_zoomMe == null && Time.unscaledTime >= _nextZoomMeFind)
        {
            _nextZoomMeFind = Time.unscaledTime + 2f;
            foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppInterop.Runtime.Il2CppType.Of<PlayerNetworking>(), FindObjectsSortMode.None))
            {
                var p = o.TryCast<PlayerNetworking>();
                if (p != null && p.gameObject.name.Contains("MyPlayer"))
                {
                    _zoomMe = p;
                    break;
                }
            }
        }
        if (_zoomMe == null)
            return;
        var aiming = false;
        try { aiming = _zoomMe.isADS; } catch { }
        if (aiming && !_aimWas)
        {
            _scopeCheckAt = Time.unscaledTime + 0.3f;
            _scopeTries = 0;
        }
        _aimWas = aiming;
        if (!aiming || _scopeCheckAt < 0f || Time.unscaledTime < _scopeCheckAt || _zoomMe.activeWeapon == null)
            return;
        _scopeCheckAt = -1f;
        foreach (var s in _zoomMe.activeWeapon.GetComponentsInChildren<ScopeInputSetter>(false))
        {
            if (s == null || s.ZoomMode != ScopeInputSetter.ZoomType.Toggle || s.ads == null || s.ads._enabled)
                continue;
            if (_scopeTries++ >= 3)
                return;
            s_ourSightSwitch = true;
            TouchKeys.PressKey(KeyCode.CapsLock, true);
            _capsReleaseNext = true;
            _scopeCheckAt = Time.unscaledTime + 0.3f;   // look again after the switch
            Plugin.Logger.LogInfo($"[Pad] Aiming through the gun's other sight: switching to the '{s.transform.parent?.name}' scope (try {_scopeTries})");
            return;
        }
    }

    // Magnifier flipped up but its picture black (2026-11-04, after going prone, and still black standing up): the
    // game had gone back to aiming through the red dot hidden behind it, and the magnifier's camera only runs while
    // its own ADS is the one in use. While aiming with the magnifier up, every half second the mod checks that the
    // magnifier's ADS is the sight in use; if not, it sets the magnifier up again (red dot aside, magnifier's ADS
    // switched off and on, which makes the game pick it), up to 3 times per aim
    private float _magCheckAt = -1f;
    private int _magTries;
    private bool _magAimWas;

    private void KeepMagnifierSelected()
    {
        if (_zoomMe == null)
            return;
        var aiming = false;
        try { aiming = _zoomMe.isADS; } catch { }
        if (aiming && !_magAimWas)
        {
            _magCheckAt = Time.unscaledTime + 0.3f;
            _magTries = 0;
        }
        _magAimWas = aiming;
        if (!aiming || Time.unscaledTime < _magCheckAt || _zoomMe.activeWeapon == null)
            return;
        _magCheckAt = Time.unscaledTime + 0.5f;
        var f = _zoomMe.activeWeapon.GetComponentInChildren<FlipSights>(false);
        if (f == null || !f.isFlipped)
            return;
        var magAds = f.ADSToDisableWhenSightDown;
        if (magAds == null || (magAds.gameObject.activeInHierarchy && magAds._enabled))
            return;
        if (_magTries++ >= 3)
            return;
        Plugin.Logger.LogInfo($"[Pad] Magnifier '{f.name}' is up but the game aims through another sight: selecting the magnifier again (try {_magTries})");
        s_inFlip = true;
        try
        {
            var mods = f.ActualSightModParent != null ? f.ActualSightModParent.GetComponentsInChildren<WeaponMod>(true) : null;
            if (mods != null)
                foreach (var m in mods)
                    if (m != null && m.mainADS != null)
                        m.mainADS.gameObject.SetActive(false);
            magAds.gameObject.SetActive(false);
            magAds.gameObject.SetActive(true);   // ADS.OnEnable: the game picks this sight
        }
        finally { s_inFlip = false; }
    }

    // Y while aiming with a zoom-switch scope (2026-11-11: the user tried the two-thumb D-pad zoom many times and only
    // ever got the night vision, the mirror-image combination): tap Y = zoom in/out. Not aiming, or no such scope:
    // false, and Y stays the fire-mode button
    private static PlayerNetworking s_zoomPlayer;

    // Y held (Touch): zoom in/out on a gun with a zoom-switch scope, aiming or not. False if the gun has none
    internal static bool ZoomScope(string how) => Zoom(how, false);

    internal static bool ZoomIfAiming() => Zoom("Y while aiming", true);

    private static bool Zoom(string how, bool onlyAiming)
    {
        try
        {
            if (s_zoomPlayer == null)
                foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppInterop.Runtime.Il2CppType.Of<PlayerNetworking>(), FindObjectsSortMode.None))
                {
                    var p = o.TryCast<PlayerNetworking>();
                    if (p != null && p.gameObject.name.Contains("MyPlayer"))
                    {
                        s_zoomPlayer = p;
                        break;
                    }
                }
            if (s_zoomPlayer == null || s_zoomPlayer.activeWeapon == null || (onlyAiming && !s_zoomPlayer.isADS))
                return false;
            foreach (var s in s_zoomPlayer.activeWeapon.GetComponentsInChildren<ScopeInputSetter>(false))
            {
                if (s == null || s.ZoomMode != ScopeInputSetter.ZoomType.Toggle)
                    continue;
                s.zoomToggled = !s.zoomToggled;
                Plugin.Logger.LogInfo($"[Pad] Scope zoom on '{s.transform.parent?.name}' switched {(s.zoomToggled ? "in" : "out")} ({how})");
                Haptics.Pulse(UnityEngine.XR.XRNode.RightHand, 0.3f, 0.03f);
                return true;
            }
        }
        catch (Exception e)
        {
            s_zoomPlayer = null;
            Plugin.Logger.LogWarning($"[Pad] Zoom: {e.GetType().Name}: {e.Message}");
        }
        return false;
    }

    private bool ToggleScopeZoom()
    {
        if (_zoomMe == null)
            foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppInterop.Runtime.Il2CppType.Of<PlayerNetworking>(), FindObjectsSortMode.None))
            {
                var p = o.TryCast<PlayerNetworking>();
                if (p != null && p.gameObject.name.Contains("MyPlayer"))
                {
                    _zoomMe = p;
                    break;
                }
            }
        if (_zoomMe == null || _zoomMe.activeWeapon == null)
            return false;
        foreach (var s in _zoomMe.activeWeapon.GetComponentsInChildren<ScopeInputSetter>(false))
        {
            if (s == null || s.ZoomMode != ScopeInputSetter.ZoomType.Toggle)
                continue;
            // With the Touch controllers, aiming or not (2026-11-11, the user: left thumbrest + right stick up zooms
            // in, the same again zooms out, as it flips a separate magnifier). With the PS5 pad it stays as it was: not
            // aiming, D-pad up is the game's own sight switch
            if (!Plugin.TouchControls.Value && !_zoomMe.isADS)
                return true;
            s.zoomToggled = !s.zoomToggled;
            Plugin.Logger.LogInfo($"[Pad] Scope zoom on '{s.transform.parent?.name}' switched {(s.zoomToggled ? "in" : "out")} (D-pad up, aiming {_zoomMe.isADS})");
            Haptics.Pulse(UnityEngine.XR.XRNode.RightHand, 0.3f, 0.03f);
            return true;
        }
        return false;
    }

    // D-pad up sends CapsLock, which is also the game's own SwitchBetweenSights: ADSFixed.GetInput then calls
    // FindNewADS to pick the next sight. Flipping the magnifier enables/disables ADS objects, and ADS.OnEnable calls
    // FindNewADS too (that's how the game's own flip selects it). Both together picked a sight twice, so about every
    // other flip the gun aimed through the hidden red dot: the magnifier's ScopeInputSetter only switches its camera
    // on while its own ADS IsInADS, so the lens stayed black. On a gun with a flip-up magnifier, FindNewADS calls
    // made while CapsLock is down (not from our flip) are skipped
    private static bool s_inFlip;
    private static bool s_skipLogged, s_zoomSkipLogged;

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private static bool HasZoomSwitch(WeaponV3 w)
    {
        foreach (var s in w.GetComponentsInChildren<ScopeInputSetter>(false))
            if (s != null && s.ZoomMode == ScopeInputSetter.ZoomType.Toggle)
                return true;
        return false;
    }

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    internal static void Apply(HarmonyLib.Harmony harmony)
    {
        try
        {
            var target = HarmonyLib.AccessTools.Method(typeof(ADSFixed), nameof(ADSFixed.FindNewADS));
            harmony.Patch(target, prefix: new HarmonyLib.HarmonyMethod(typeof(PadMacros), nameof(BeforeFindNewADS)));
            Plugin.Logger.LogInfo("[Pad] hooked ADSFixed.FindNewADS (sight switch vs magnifier flip)");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Pad] FindNewADS hook failed: {e.GetType().Name}: {e.Message}");
        }
    }

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private static bool BeforeFindNewADS(ADSFixed __instance)
    {
        try
        {
            if (s_inFlip || s_ourSightSwitch || !Plugin.FlipWithSightSwitch.Value || __instance == null ||
                (GetAsyncKeyState(VK_CAPITAL) & 0x8000) == 0)
                return true;
            var w = __instance.weapon;
            if (w == null || !w.transform.root.name.Contains("MyPlayer"))
                return true;
            if (w.GetComponentInChildren<FlipSights>(false) != null)
            {
                if (!s_skipLogged)
                {
                    s_skipLogged = true;
                    Plugin.Logger.LogInfo($"[Pad] Game's sight switch skipped on '{w.name}' (D-pad up flips the magnifier instead)");
                }
                return false;
            }
            // A scope with a zoom switch (Elcan SpecterDR): the same press also swapped to the gun's other aiming point
            // about every other time, so the scope sat away from the eye with its camera off (log: aiming, its ADS
            // not enabled, cams OFF). While aiming, D-pad up only zooms; not aiming, the game's sight switch works as
            // usual (to get back to the scope if the gun is on its other sight)
            // (Touch: aiming or not, D-pad up only zooms on such a scope, see ToggleScopeZoom. PS5 pad as before: only
            // while aiming)
            if (!HasZoomSwitch(w))
                return true;
            if (!Plugin.TouchControls.Value)
            {
                var aiming = false;
                try { aiming = __instance.playerNetworking != null && __instance.playerNetworking.isADS; } catch { }
                if (!aiming)
                    return true;
            }
            if (!s_zoomSkipLogged)
            {
                s_zoomSkipLogged = true;
                Plugin.Logger.LogInfo($"[Pad] Game's sight switch skipped on '{w.name}' (D-pad up zooms the scope instead)");
            }
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static void SetFlipped(FlipSights f, bool up)
    {
        s_inFlip = true;
        try { SetFlippedInner(f, up); }
        finally { s_inFlip = false; }
    }

    private static void SetFlippedInner(FlipSights f, bool up)
    {
        f.isFlipped = up;
        var mods = f.ActualSightModParent != null
            ? f.ActualSightModParent.GetComponentsInChildren<WeaponMod>(true)
            : null;
        if (up && f.ADSToDisableWhenSightDown != null)
            f.ADSToDisableWhenSightDown.gameObject.SetActive(true);
        if (mods != null)
            foreach (var m in mods)
                if (m != null && m.mainADS != null)
                    m.mainADS.gameObject.SetActive(!up);
        if (!up && f.ADSToDisableWhenSightDown != null)
            f.ADSToDisableWhenSightDown.gameObject.SetActive(false);
        Plugin.Logger.LogInfo($"[Pad] Magnifier flipped {(up ? "up" : "down")} ({mods?.Length ?? 0} sight(s) behind it " +
                              $"{(up ? "set aside" : "back in use")})");
    }

    private void Update()
    {
        using var __t = ModTime.Measure("PadMacros");
        LogBindsOnce();
        if (Plugin.Diagnostics.Value) try { LogLaserButton(); } catch { }
        // While the mission screen has the pad its buttons are menu keys: Cross there tapped Space (kick) as well as
        // selecting, and the user's view lurched left on each pick (2026-11-03)
        var focused = TouchKeys.GameHasFocus() && !MissionScreen.HasControl;
        try { UpdateFlip(focused); } catch (Exception e) { Plugin.Logger.LogError($"[Pad] flip: {e.GetType().Name}: {e.Message}"); }
        try { VoiceMagnifier(); } catch (Exception e) { Plugin.Logger.LogError($"[VoiceCmd] magnifier: {e.GetType().Name}: {e.Message}"); }
        try { UpdateRun(focused); } catch { }
        try { UpdateCross(focused); } catch (Exception e) { Plugin.Logger.LogError($"[Pad] cross: {e.GetType().Name}: {e.Message}"); }
        try { if (OpenXRBootstrap.IsRunning && Plugin.FlipWithSightSwitch.Value) KeepScopeSelected(); }
        catch (Exception e) { Plugin.Logger.LogError($"[Pad] scope select: {e.GetType().Name}: {e.Message}"); }
        try { if (OpenXRBootstrap.IsRunning && Plugin.FlipWithSightSwitch.Value) KeepMagnifierSelected(); }
        catch (Exception e) { Plugin.Logger.LogError($"[Pad] magnifier select: {e.GetType().Name}: {e.Message}"); }
        try { UpdateWeaponCycle(focused); } catch (Exception e) { Plugin.Logger.LogError($"[Pad] next weapon: {e.GetType().Name}: {e.Message}"); }
        if (Plugin.Diagnostics.Value)
            try { LogReloadKeys(); } catch { }
        try { UpdateTablet(focused); } catch { }
        if (!focused)
        {
            // Let go of anything half pressed
            if (_running != null)
                for (; _step < _running.Length; _step++)
                    if (!_running[_step].down)
                        TouchKeys.PressKey(_running[_step].key, false);
            _running = null;
            return;
        }

        if (_running == null)
        {
            if (Pressed(Plugin.EmergencyReloadKey.Value, ref _emergencyDown))
                Begin(EmergencyReload, $"Emergency reload ({Plugin.EmergencyReloadKey.Value}): R twice");
            else if (Pressed(Plugin.ManualReloadKey.Value, ref _manualDown))
                Begin(ManualReload, $"Manual reload ({Plugin.ManualReloadKey.Value}): Left Shift + T");
        }
        else
        {
            // Keep the edge detection current while a sequence runs
            Pressed(Plugin.EmergencyReloadKey.Value, ref _emergencyDown);
            Pressed(Plugin.ManualReloadKey.Value, ref _manualDown);
        }

        if (_running == null)
            return;
        var (key, down) = _running[_step];
        TouchKeys.PressKey(key, down);
        if (++_step >= _running.Length)
            _running = null;
    }

    // (an array of tuples: hidden from the IL2CPP class injector, which can't expose it)
    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private void Begin((KeyCode key, bool down)[] sequence, string what)
    {
        _running = sequence;
        _step = 0;
        Plugin.Logger.LogInfo($"[Pad] {what}");
    }

    // (by-ref argument: hidden from the IL2CPP class injector, which can't expose it)
    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private static bool Pressed(string keyName, ref bool wasDown)
    {
        var vk = VirtualKey(keyName);
        var down = vk != 0 && (GetAsyncKeyState(vk) & 0x8000) != 0;
        var pressed = down && !wasDown;
        wasDown = down;
        return pressed;
    }

    // A letter or digit key name -> Windows virtual-key code (letters and digits share their ASCII codes)
    private static int VirtualKey(string name)
    {
        if (string.IsNullOrEmpty(name))
            return 0;
        name = name.Trim().ToUpperInvariant();
        return name.Length == 1 && char.IsLetterOrDigit(name[0]) ? name[0] : 0;
    }
}
