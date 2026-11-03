using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace OperatorVR;

/// <summary>
/// Our own mission screen for VR (the user's request, 2026-11-03): a big screen standing in the air over the mission
/// laptop, there as soon as you walk up to it. Standing only (the user's choice): Interact at the laptop no longer sits
/// you down (LaptopClickLog skips AccessLaptop); it hands the pad to this screen instead, and while it has the pad the
/// game reads no keys (InputLayer.GetKey patched), so the stick moves the menu, not you. Circle on the first page (or
/// Options) hands the pad back.
/// Steps: operation (normal or simulation) -> mission (target package) -> plan -> deploy. It lists the game's own
/// operations (OperationsManager.ActiveOperations / SimulationOperations and their TargetPackages) and drives the
/// laptops' own networked state (MissionLaptopNetworkState) and planning board (OperationBoardUI), so the laptop pages
/// follow along, other players see the same, and the mission is started by the game's own Execute / Start buttons.
/// Pad through Steam Input: left stick = W/A/S/D, Cross = 8, Circle = C, Options = Escape. Menus.MissionScreen.
/// </summary>
public class MissionScreen : MonoBehaviour
{
    public MissionScreen(IntPtr ptr) : base(ptr) { }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VK_W = 0x57, VK_S = 0x53, VK_A = 0x41, VK_D = 0x44, VK_8 = 0x38, VK_C = 0x43, VK_ESCAPE = 0x1B;
    private const int VK_UP = 0x26, VK_DOWN = 0x28, VK_LEFT = 0x25, VK_RIGHT = 0x27, VK_ENTER = 0x0D, VK_BACK = 0x08, VK_F = 0x46, VK_LBUTTON = 0x01;
    // With the pad untouched this long, it's handed back so you can't be left standing unable to walk
    private const float IdleRelease = 20f;
    private float _lastInput;
    private bool _inputWasDisabled;
    private const int VisibleRows = 9;
    // Shown within this distance of a laptop (metres, level); the screen stands this far beyond the laptop, this wide
    private const float ShowRange = 3.5f, BeyondLaptop = 0.35f, ScreenWidth = 1.6f;
    // Laptops this far away (level) still count for the room's window screen
    private const float LaptopSearch = 15f;
    private const float CanvasWidth = 1920f, CanvasHeight = 1080f;

    private static MissionScreen s_instance;

    /// <summary>Standing use of the laptops: on while VR runs with Menus.MissionScreen.</summary>
    internal static bool StandingMode => OpenXRBootstrap.IsRunning && Plugin.MissionScreen.Value;

    /// <summary>The screen has the pad: the game is told no key is down (see BeforeGetKey).</summary>
    internal static bool HasControl { get; private set; }

    /// <summary>The screen is up (LaptopPanel leaves the laptop's own screen where it is).</summary>
    internal static bool Showing { get; private set; }

    /// <summary>Interact at a laptop in standing mode: the pad goes to the screen.</summary>
    internal static void TakeControl(MissionLaptop laptop, PlayerNetworking player)
    {
        if (s_instance == null || laptop == null)
            return;
        // The laptop knows who's using it from AccessLaptop, which we skip: the game's mission start
        // (CerebusOpboard.Start_Operation) threw without it (2026-11-03)
        try
        {
            if (player != null)
                laptop.playerNetworking = player;
        }
        catch { }
        s_instance._laptop = laptop;
        s_instance._step = Step.Operations;
        s_instance._sel = s_instance._top = 0;
        s_instance._message = "";
        s_instance._dirty = true;
        s_instance._keys.Clear();
        s_instance._ignoreUntil = Time.unscaledTime + 0.3f;   // the Interact press itself
        s_instance._lastInput = Time.unscaledTime;
        // The Interact key is still down now; mark it so it doesn't hand the pad straight back
        s_instance._keys[VK_F] = (true, 0f);
        HasControl = true;
        // The game's own input switch too: Circle (C, crouch) still made you duck with only InputLayer.GetKey
        // blocked (2026-11-03)
        try
        {
            s_instance._inputWasDisabled = InputLayer.InputDisabled;
            InputLayer.InputDisabled = true;
        }
        catch { }
        Plugin.Logger.LogInfo($"[Missions] Pad handed to the mission screen at '{laptop.name}'");
    }

    private void ReleaseControl(string why)
    {
        if (!HasControl)
            return;
        HasControl = false;
        try { InputLayer.InputDisabled = _inputWasDisabled; } catch { }
        _dirty = true;
        Plugin.Logger.LogInfo($"[Missions] Pad handed back ({why})");
    }

    private enum Step { Operations, Missions, Options, Confirm }

    private class Row
    {
        public string Text;
        public bool Locked;
        public Action Activate;           // Cross
        public Action<int> Change;        // stick left/right (-1 / +1), for options
    }

    private Step _step = Step.Operations;
    private bool _simulation;
    private CerebusOperation _op;
    private CerebusTargetPackage _pkg;
    private readonly List<Row> _rows = new();
    private int _sel, _top;
    private string _message = "";
    private bool _dirty = true;
    private MissionLaptop _laptop;        // the one we're at (near, or the one Interact was pressed at)
    private float _nextLaptopScan, _ignoreUntil;
    private readonly List<MissionLaptop> _laptops = new();

    // UI
    private GameObject _root;
    private TextMeshProUGUI _title, _detail, _hint;
    private RawImage _map;
    // Where the map goes on the page (fractions of the page) when there's one to show; the text then sits below it
    private static readonly Vector2 MapBoxMin = new(0.52f, 0.36f), MapBoxMax = new(0.97f, 0.86f);
    private readonly List<(Image back, TextMeshProUGUI text)> _rowUi = new();
    private TMP_FontAsset _font;

    // Pad / keyboard repeat
    private readonly Dictionary<int, (bool down, float next)> _keys = new();

    private void Awake() => s_instance = this;

    // ---- Game keys while the screen has the pad ----

    internal static void Apply(HarmonyLib.Harmony harmony)
    {
        try
        {
            var m = HarmonyLib.AccessTools.Method(typeof(InputLayer), nameof(InputLayer.GetKey));
            harmony.Patch(m, prefix: new HarmonyLib.HarmonyMethod(typeof(MissionScreen), nameof(BeforeGetKey)));
            Plugin.Logger.LogInfo("[Missions] hooked InputLayer.GetKey (no game keys while the mission screen has the pad)");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Missions] InputLayer.GetKey hook failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static bool BeforeGetKey(ref bool __result)
    {
        if (!HasControl)
            return true;
        __result = false;
        return false;
    }

    // ---- Frame ----

    // The user's view shifted left for a second on each pick (2026-11-03): while the screen has the pad, log any
    // sudden move or turn of the view, with which keys were down, to find what moves it
    private Vector3 _lastEye;
    private float _lastYawSeen;
    private bool _haveLastEye;
    private int _jumpLogs;

    private void LogViewJumps(Transform cam)
    {
        if (!HasControl)
        {
            _haveLastEye = false;
            return;
        }
        var pos = cam.position;
        var yaw = cam.eulerAngles.y;
        if (_haveLastEye && _jumpLogs < 30)
        {
            var moved = pos - _lastEye;
            moved.y = 0f;
            var turned = Mathf.Abs(Mathf.DeltaAngle(_lastYawSeen, yaw));
            if (moved.magnitude > 0.03f || turned > 4f)
            {
                _jumpLogs++;
                var keys = new List<string>();
                foreach (var (vk, name) in new[] { (VK_W, "W"), (VK_S, "S"), (VK_A, "A"), (VK_D, "D"), (VK_8, "8"), (VK_C, "C"), (0x20, "Space"), (0x51, "Q"), (0x45, "E"), (0x10, "Shift") })
                    if ((GetAsyncKeyState(vk) & 0x8000) != 0)
                        keys.Add(name);
                Plugin.Logger.LogInfo($"[Missions] View jumped {moved.magnitude * 100f:0} cm, turned {turned:0}° in one frame " +
                                      $"(keys down: {(keys.Count > 0 ? string.Join(" ", keys) : "none")}, camera '{cam.name}' under '{cam.root.name}')");
            }
        }
        _lastEye = pos;
        _lastYawSeen = yaw;
        _haveLastEye = true;
    }

    private void Update()
    {
        using var __t = ModTime.Measure("MissionScreen");
        try { ShowHiddenForPage(); } catch { }
        try
        {
            if (!StandingMode || OperationsManager.singleton == null || MissionLaptopNetworkState.instance == null)
            {
                ReleaseControl("VR or the operation room left");
                Hide();
                return;
            }
            var cam = Camera.main;
            if (cam == null)
            {
                Hide();
                return;
            }
            var eye = cam.transform.position;
            try { LogViewJumps(cam.transform); } catch { }
            if (!HasControl)
            {
                var near = NearestLaptop(eye);
                if (near == null || _laptop == null || near.Pointer != _laptop.Pointer)
                {
                    // A new room / laptop starts at the first page
                    _laptop = near;
                    _step = Step.Operations;
                    _sel = _top = 0;
                    _message = "";
                    _dirty = true;
                }
            }
            if (_laptop == null)
            {
                Hide();
                return;
            }
            if (_laptop.Pointer != _windowFor)
                FindWindow(_laptop);

            // On the window it's up in that part of the room and used at the window (the user: "walk over to the
            // window and choose missions from there, not from the laptop"); with no window, by the laptop as before
            if (!HasControl && !InRange(eye))
            {
                Hide();
                return;
            }
            if (_root == null)
                Build();
            if (!_root.activeSelf)
            {
                _root.SetActive(true);
                MenusOnTop.Put(_root);
                Plugin.Logger.LogInfo($"[Missions] Mission screen up by '{_laptop.name}'");
            }
            Showing = true;

            if (HasControl && Time.unscaledTime >= _ignoreUntil)
                ReadKeys();
            else if (!HasControl)
            {
                // Interact (F; the pad's right-stick click) at the window takes the pad
                var f = Pressed(VK_F);
                if (f && _haveWindow && AtWindow(cam))
                    TakeControl(_laptop, LocalPlayer());
            }
            if (_dirty)
            {
                _dirty = false;
                Rebuild();
            }
            Paint();
        }
        catch (Exception e)
        {
            var text = e.ToString();
            Plugin.Logger.LogError($"[Missions] {(text.Length > 1500 ? text.Substring(0, 1500) : text)}");
            ReleaseControl("error");
            Hide();
        }
    }

    // ---- On the wall's window (the user's idea, 2026-11-03) ----
    // The Operation Room has a big glowing white window on the wall near the mission laptop. If one is found near the
    // laptop, the screen covers it instead of standing over the laptop. Found once per laptop by its renderer: a flat
    // panel (thin one way, at least 1.4 m wide and 0.7 m tall) within 12 m whose name says window/blind/light/glass;
    // the candidates are logged either way, so the right one can be named if this picks wrong
    private const float WindowSearch = 12f;
    private IntPtr _windowFor;
    private bool _haveWindow;
    private Vector3 _winCenter, _winNormal;
    private float _winWidth, _winHeight;

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private void FindWindow(MissionLaptop laptop)
    {
        _windowFor = laptop.Pointer;
        _haveWindow = false;
        if (!Plugin.MissionScreenOnWindow.Value)
            return;
        var near = LaptopPoint(laptop);
        var found = new List<(float score, string name, Bounds b, bool named)>();
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<MeshRenderer>(), FindObjectsSortMode.None))
        {
            var r = o.TryCast<MeshRenderer>();
            if (r == null || !r.enabled || !r.gameObject.activeInHierarchy)
                continue;
            var b = r.bounds;
            var flatX = b.size.x <= 0.25f;
            var flatZ = b.size.z <= 0.25f;
            if (flatX == flatZ)
                continue;
            var wide = flatX ? b.size.z : b.size.x;
            if (wide < 1.4f || b.size.y < 0.7f || b.size.y > 3.5f)
                continue;
            var d = b.center - near;
            d.y = 0f;
            if (d.magnitude > WindowSearch)
                continue;
            var name = r.gameObject.name;
            var lower = name.ToLowerInvariant();
            // The white panel itself is 'Windowblinds_C' (3.0 x 1.5 m); 'W_6x_Window_Small (1)' around it is 6 x 3 m
            // with wall, and covering that was "way too big" (2026-11-03), so blinds come first
            var blinds = lower.Contains("blind");
            var named = blinds || lower.Contains("window") || lower.Contains("light") || lower.Contains("glass") || lower.Contains("frost");
            found.Add(((blinds ? 200f : named ? 100f : 0f) + wide * b.size.y - d.magnitude * 0.5f, name, b, named));
        }
        found.Sort((a, b) => b.score.CompareTo(a.score));
        for (var i = 0; i < found.Count && i < 12; i++)
            Plugin.Logger.LogInfo($"[Missions] window candidate '{found[i].name}' at {found[i].b.center}, size {found[i].b.size}, named {found[i].named}");
        if (found.Count == 0 || !found[0].named)
        {
            Plugin.Logger.LogInfo($"[Missions] No window found by '{laptop.name}' ({found.Count} flat panels): the screen stands over the laptop");
            return;
        }
        var w = found[0].b;
        var normal = w.size.x <= 0.25f ? Vector3.right : Vector3.forward;
        if (Vector3.Dot(near - w.center, normal) < 0f)
            normal = -normal;
        _winCenter = w.center;
        _winNormal = normal;
        // Covering the whole panel with a little to spare, so none of its glow shows round the edge ("obstructed by the
        // whiteness of the blind", 2026-11-03); Menus.MissionScreenMaxWidth can cap it, its shape kept
        var panelWidth = (w.size.x <= 0.25f ? w.size.z : w.size.x) * 1.04f;
        var panelHeight = w.size.y * 1.05f;
        var cap = Plugin.MissionScreenMaxWidth.Value;
        _winWidth = cap > 0.5f ? Mathf.Min(panelWidth, cap) : panelWidth;
        _winHeight = panelHeight * _winWidth / panelWidth;
        _haveWindow = true;
        Plugin.Logger.LogInfo($"[Missions] Screen goes on '{found[0].name}' ({_winWidth:0.00} x {_winHeight:0.00} m) by '{laptop.name}'");
    }

    // After the game has placed its camera: on the window if there is one, else beyond the laptop at eye height
    private void LateUpdate()
    {
        if (_root == null || !_root.activeSelf || _laptop == null)
            return;
        try
        {
            var cam = Camera.main;
            if (cam == null)
                return;
            if (_laptop.Pointer != _windowFor)
                FindWindow(_laptop);
            var rt = _root.GetComponent<RectTransform>();
            if (_haveWindow)
            {
                var size = new Vector2(CanvasHeight * _winWidth / _winHeight, CanvasHeight);
                if (rt.sizeDelta != size)
                    rt.sizeDelta = size;
                // Just in front of the glass; a canvas reads from the side its forward points away from
                _root.transform.SetPositionAndRotation(_winCenter + _winNormal * 0.12f, Quaternion.LookRotation(-_winNormal, Vector3.up));
                _root.transform.localScale = Vector3.one * (_winHeight / CanvasHeight);
                return;
            }
            if (rt.sizeDelta.x != CanvasWidth)
                rt.sizeDelta = new Vector2(CanvasWidth, CanvasHeight);
            var eye = cam.transform.position;
            var anchor = LaptopPoint(_laptop);
            var away = anchor - eye;
            away.y = 0f;
            if (away.sqrMagnitude < 1e-4f)
                away = cam.transform.forward;
            away.y = 0f;
            away.Normalize();
            var pos = anchor + away * BeyondLaptop;
            pos.y = eye.y + 0.05f;
            var look = pos - eye;
            look.y = 0f;
            var t = _root.transform;
            t.SetPositionAndRotation(pos, Quaternion.LookRotation(look.sqrMagnitude > 1e-4f ? look.normalized : away, Vector3.up));
            t.localScale = Vector3.one * (ScreenWidth / CanvasWidth);
        }
        catch { }
    }

    // Shown within this distance (level) of the window; used within UseRange of it, facing it
    private const float WindowShowRange = 9f, UseRange = 3f;

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private static float Level(Vector3 d)
    {
        d.y = 0f;
        return d.magnitude;
    }

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private bool InRange(Vector3 eye) =>
        _haveWindow ? Level(eye - _winCenter) <= WindowShowRange : Level(eye - LaptopPoint(_laptop)) <= ShowRange;

    /// <summary>Close to the window and turned towards it (head or body).</summary>
    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private bool AtWindow(Camera cam)
    {
        var eye = cam.transform.position;
        if (Level(eye - _winCenter) > UseRange)
            return false;
        var to = _winCenter - eye;
        to.y = 0f;
        var look = cam.transform.forward;
        if (HeadTracking.TryApplyHeadPoseToGameCamera(out var savedPos, out var savedRot))
        {
            look = cam.transform.forward;
            HeadTracking.RestoreGameCameraPose(savedPos, savedRot);
        }
        look.y = 0f;
        return Vector3.Angle(look, to) < 55f;
    }

    private PlayerNetworking _me;

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private PlayerNetworking LocalPlayer()
    {
        if (_me != null)
            return _me;
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<PlayerNetworking>(), FindObjectsSortMode.None))
        {
            var p = o.TryCast<PlayerNetworking>();
            if (p != null && p.gameObject.name.Contains("MyPlayer"))
                return _me = p;
        }
        return null;
    }

    /// <summary>
    /// Where a floating game menu should go instead, when it belongs on the mission screen: the game's infil map
    /// ('Select Infil', shown once a mission starts) lies over the screen on the window like a page of it.
    /// </summary>
    internal static bool TryPagePose(Canvas canvas, Vector2 size, out Vector3 pos, out Quaternion rot, out float width)
    {
        pos = default;
        rot = Quaternion.identity;
        width = 0f;
        var self = s_instance;
        if (self == null || !Showing || !self._haveWindow || canvas == null)
            return false;
        var infil = canvas.transform.Find("Select Infil");
        if (infil == null || !infil.gameObject.activeInHierarchy)
        {
            // The health / map tabs (the HUD canvas with HealthUI) open with the infil map and showed over it
            // (2026-11-03): while the infil page is up they're tucked behind the mission screen, out of sight
            // Tucked behind, it still showed (2026-11-03): now switched off until the infil page goes away
            if (Time.unscaledTime - self._pageAt > 1f || !HasHealthUi(canvas))
                return false;
            if (canvas.enabled)
            {
                canvas.enabled = false;
                self._hiddenForPage.Add(canvas);
                Plugin.Logger.LogInfo($"[Missions] '{canvas.name}' (health / map tabs) hidden while the infil map is on the screen");
            }
            return false;
        }
        // Just in front of our screen, as big as fits on it with the page's own shape
        pos = self._winCenter + self._winNormal * 0.15f;
        rot = Quaternion.LookRotation(-self._winNormal, Vector3.up);
        var aspect = Mathf.Max(1f, size.x) / Mathf.Max(1f, size.y);
        width = Mathf.Min(self._winWidth, self._winHeight * aspect);
        self._pageAt = Time.unscaledTime;
        if (!self._pageLogged)
        {
            self._pageLogged = true;
            Plugin.Logger.LogInfo($"[Missions] Infil map shown as a page of the mission screen ({width:0.00} m wide)");
        }
        return true;
    }

    private static readonly Dictionary<IntPtr, bool> s_healthCanvas = new();
    private readonly List<Canvas> _hiddenForPage = new();

    // The health / map tabs come back once the infil page has gone (or the screen is down)
    private void ShowHiddenForPage()
    {
        if (_hiddenForPage.Count == 0 || Time.unscaledTime - _pageAt <= 1f)
            return;
        foreach (var c in _hiddenForPage)
            try { if (c != null) c.enabled = true; } catch { }
        _hiddenForPage.Clear();
        Plugin.Logger.LogInfo("[Missions] Health / map tabs shown again");
    }

    private static bool HasHealthUi(Canvas c)
    {
        if (s_healthCanvas.TryGetValue(c.Pointer, out var has))
            return has;
        try { has = c.GetComponentInChildren<HealthUI>(true) != null; } catch { has = false; }
        s_healthCanvas[c.Pointer] = has;
        return has;
    }

    private bool _pageLogged;
    private float _pageAt = -10f;
    private GameObject _content;

    /// <summary>The laptop's Interact in standing mode: it hands the pad over only where there's no window to use.</summary>
    internal static bool UseLaptopInstead(MissionLaptop laptop)
    {
        var self = s_instance;
        if (self == null || laptop == null)
            return true;
        if (laptop.Pointer != self._windowFor)
            self.FindWindow(laptop);
        return !self._haveWindow;
    }

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private static Vector3 LaptopPoint(MissionLaptop laptop)
    {
        try
        {
            if (laptop.osCanvas != null)
                return laptop.osCanvas.transform.position;
        }
        catch { }
        return laptop.transform.position;
    }

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private MissionLaptop NearestLaptop(Vector3 eye)
    {
        if (Time.unscaledTime >= _nextLaptopScan)
        {
            _nextLaptopScan = Time.unscaledTime + 1f;
            _laptops.Clear();
            foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<MissionLaptop>(), FindObjectsSortMode.None))
            {
                var l = o.TryCast<MissionLaptop>();
                if (l != null && l.isActiveAndEnabled)
                    _laptops.Add(l);
            }
        }
        MissionLaptop best = null;
        var bestDist = LaptopSearch;
        foreach (var l in _laptops)
        {
            if (l == null || !l.isActiveAndEnabled)
                continue;
            var d = LaptopPoint(l) - eye;
            d.y = 0f;
            if (d.magnitude < bestDist)
            {
                best = l;
                bestDist = d.magnitude;
            }
        }
        return best;
    }

    private void Hide()
    {
        Showing = false;
        if (_root != null && _root.activeSelf)
        {
            _root.SetActive(false);
            Plugin.Logger.LogInfo("[Missions] Mission screen down");
        }
    }

    // ---- Input ----

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private bool Pressed(int vk, bool repeat = false)
    {
        var down = (GetAsyncKeyState(vk) & 0x8000) != 0;
        _keys.TryGetValue(vk, out var k);
        var fire = false;
        if (down && !k.down)
        {
            fire = true;
            k.next = Time.unscaledTime + 0.35f;
        }
        else if (down && repeat && Time.unscaledTime >= k.next)
        {
            fire = true;
            k.next = Time.unscaledTime + 0.12f;
        }
        k.down = down;
        _keys[vk] = k;
        return fire;
    }

    private void ReadKeys()
    {
        var up = Pressed(VK_W, true) | Pressed(VK_UP, true);
        var down = Pressed(VK_S, true) | Pressed(VK_DOWN, true);
        var left = Pressed(VK_A, true) | Pressed(VK_LEFT, true);
        var right = Pressed(VK_D, true) | Pressed(VK_RIGHT, true);
        // The right trigger (the mouse's left button) selects: the user's choice over Cross, which is left to its
        // game job (2026-11-03)
        var select = Pressed(VK_LBUTTON) | Pressed(VK_ENTER);
        var back = Pressed(VK_C) | Pressed(VK_BACK);
        if (Pressed(VK_ESCAPE))
        {
            ReleaseControl("Options");
            return;
        }
        // Interact again (F; the pad's right-stick click becomes F through AimLean) also hands it back: with the
        // screen out of sight the user pressed it again and was left unable to walk (2026-11-03)
        if (Pressed(VK_F))
        {
            ReleaseControl("Interact again");
            return;
        }
        if (up || down || left || right || select || back)
            _lastInput = Time.unscaledTime;
        else if (Time.unscaledTime - _lastInput > IdleRelease)
        {
            ReleaseControl($"nothing pressed for {IdleRelease:0} s");
            return;
        }

        if (_rows.Count > 0)
        {
            if (up) _sel = (_sel + _rows.Count - 1) % _rows.Count;
            if (down) _sel = (_sel + 1) % _rows.Count;
        }
        if ((left || right) && _step == Step.Operations)
        {
            // Left/right switches between normal and simulation operations
            _simulation = right;
            _sel = _top = 0;
            _dirty = true;
            return;
        }
        if ((left || right) && _sel < _rows.Count && _rows[_sel].Change != null)
        {
            _rows[_sel].Change(right ? 1 : -1);
            _dirty = true;
        }
        if (select && _sel < _rows.Count)
            ActivateRow(_sel);
        if (back)
            Back();
    }

    private void ActivateRow(int i)
    {
        if (i < 0 || i >= _rows.Count)
            return;
        _sel = i;
        var row = _rows[i];
        if (row.Locked)
        {
            _message = "That one is locked.";
            return;
        }
        if (row.Activate != null)
        {
            // A choice the game can't take stays a message on the screen: an error here used to drop the pad and
            // take the screen down mid-choice, and the user was left stuck (2026-11-03)
            try { row.Activate(); }
            catch (Exception e)
            {
                _message = "That can't be chosen right now.";
                Plugin.Logger.LogWarning($"[Missions] '{row.Text}' couldn't be chosen: {e.GetType().Name}: {e.Message}");
            }
            _dirty = true;
        }
        else if (row.Change != null)
        {
            row.Change(1);
            _dirty = true;
        }
    }

    // Missions the game couldn't open (not unlocked yet), shown as locked from then on
    private readonly HashSet<IntPtr> _failedPackages = new();

    private void Back()
    {
        _message = "";
        switch (_step)
        {
            case Step.Operations:
                ReleaseControl("Circle on the first page");
                return;
            case Step.Missions: _step = Step.Operations; break;
            case Step.Options: _step = Step.Missions; break;
            case Step.Confirm:
                try { Board()?.CloseConfirmationWindow(); } catch { }
                _step = Step.Options;
                break;
        }
        _sel = _top = 0;
        _dirty = true;
    }

    // ---- Game data ----

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private static string Loc(UnityEngine.Localization.LocalizedString s)
    {
        if (s == null)
            return "";
        try { return s.GetLocalizedString() ?? ""; }
        catch { return ""; }
    }

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private OperationBoardUI Board()
    {
        if (_laptop != null)
        {
            var d = _laptop.GetComponentInChildren<MissionLaptopDisplayer>(true);
            if (d != null && d.OpBoardUI != null)
                return d.OpBoardUI;
        }
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<OperationBoardUI>(), FindObjectsSortMode.None))
        {
            var b = o.TryCast<OperationBoardUI>();
            if (b != null && b.isActiveAndEnabled)
                return b;
        }
        return null;
    }

    private void Rebuild()
    {
        _rows.Clear();
        var ops = OperationsManager.singleton;
        var state = MissionLaptopNetworkState.instance;
        switch (_step)
        {
            case Step.Operations:
            {
                var list = _simulation ? ops.SimulationOperations : ops.ActiveOperations;
                for (var i = 0; list != null && i < list.Length; i++)
                {
                    var op = list[i];
                    if (op == null)
                        continue;
                    var name = Loc(op.OperationName);
                    var row = new Row { Text = string.IsNullOrEmpty(name) ? op.name : name, Locked = op.isLocked };
                    row.Activate = () =>
                    {
                        state.SetSimulationPage(_simulation);
                        state.OperationSingleClicked(op);
                        _op = op;
                        _step = Step.Missions;
                        _sel = _top = 0;
                        Plugin.Logger.LogInfo($"[Missions] Operation '{row.Text}' chosen (simulation {_simulation}); laptop page {state.CurrentLaptopPage}, selected op {state.CurrentSelectedOperation}");
                    };
                    _rows.Add(row);
                }
                break;
            }
            case Step.Missions:
            {
                var list = _op != null ? _op.TargetPackages : null;
                for (var i = 0; list != null && i < list.Length; i++)
                {
                    var pkg = list[i];
                    if (pkg == null)
                        continue;
                    var name = PackageName(pkg);
                    // Not unlocked yet: no planning board (TargetPackage) either. Picking one made the game's own
                    // board set-up throw (MissionLaptop.SpawnTargetPackagePlanning) and the screen locked up
                    // (2026-11-03)
                    var locked = pkg.isLocked || pkg.TargetPackage == null || _failedPackages.Contains(pkg.Pointer);
                    var row = new Row { Text = name, Locked = locked };
                    row.Activate = () =>
                    {
                        try { state.TargetPackageSingleClicked(pkg); }
                        catch (Exception e)
                        {
                            // Stay on the list, with the pad still here, and remember it as locked
                            _failedPackages.Add(pkg.Pointer);
                            _message = "That mission isn't available yet.";
                            _dirty = true;
                            Plugin.Logger.LogWarning($"[Missions] Mission '{name}' couldn't be opened ({e.GetType().Name}); shown as locked");
                            return;
                        }
                        _pkg = pkg;
                        _step = Step.Options;
                        _sel = _top = 0;
                        Plugin.Logger.LogInfo($"[Missions] Mission '{name}' chosen; laptop page {state.CurrentLaptopPage}, selected package {state.CurrentSelectedTargetPackage}, board {(Board() != null ? "found" : "not found")}");
                    };
                    _rows.Add(row);
                }
                _rows.Add(new Row { Text = "< Back", Activate = Back });
                break;
            }
            case Step.Options:
                BuildOptions();
                _rows.Add(new Row { Text = "< Back", Activate = Back });
                break;
            case Step.Confirm:
                _rows.Add(new Row { Text = "START THE MISSION", Activate = StartMission });
                _rows.Add(new Row { Text = "Go back", Activate = Back });
                break;
        }
        _sel = Mathf.Clamp(_sel, 0, Math.Max(0, _rows.Count - 1));
    }

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private static string PackageName(CerebusTargetPackage pkg)
    {
        var area = pkg.OPERATION_AREA_OF_OPERATION;
        var type = Loc(pkg.OperationType);
        if (!string.IsNullOrEmpty(area) && !string.IsNullOrEmpty(type))
            return $"{area}  ({type})";
        if (!string.IsNullOrEmpty(area))
            return area;
        return string.IsNullOrEmpty(type) ? pkg.name : type;
    }

    private void BuildOptions()
    {
        var board = Board();
        var state = MissionLaptopNetworkState.instance;
        if (board == null)
        {
            _message = "The laptop's planning board isn't up yet. Go back and choose the mission again.";
            return;
        }
        _summary.Clear();
        AddSelector(board.InfilTimeSlider, "Time", i => board.SetInfiltrationTime(i));
        AddSelector(board.PrearationTimeSlider, "Preparation", i => board.SetPreparationTime(i));
        AddSelector(board.OpforDifficultySlider, "Enemy skill", i => board.SetOpforDifficulty(i));
        var count = board.EnemyCountSlider;
        if (count != null && count.isActiveAndEnabled && count.mainSlider != null)
        {
            var s = count.mainSlider;
            _summary.Add($"Enemies: <b>{Mathf.RoundToInt(s.value)}</b>");
            _rows.Add(new Row
            {
                Text = $"Enemies:  <  {Mathf.RoundToInt(s.value)}  >",
                Change = d =>
                {
                    var step = Math.Max(1f, (s.maxValue - s.minValue) / 20f);
                    s.value = Mathf.Clamp(s.value + d * step, s.minValue, s.maxValue);   // its own event passes it on
                },
            });
        }

        // Infil points: the board's list (CerebusOpboard.AvailableInfils), each on or off
        var opboard = board.CerebusOpboard;
        var infils = opboard != null ? opboard.AvailableInfils : null;
        for (var i = 0; infils != null && i < infils.Length; i++)
        {
            var index = i;
            var on = state.IsInfilActive(index);
            _rows.Add(new Row
            {
                Text = $"Infil {infils[index]}:  {(on ? "ON" : "off")}",
                Change = _ => board.InfilTargetButtonPressed(index, !state.IsInfilActive(index)),
            });
        }

        _rows.Insert(0, new Row { Text = "DEPLOY", Activate = Deploy });
        if (_optionLogs++ < 10)
            Plugin.Logger.LogInfo($"[Missions] Plan page: {string.Join(", ", _summary)}; {infils?.Length ?? 0} infil point(s)");
    }

    // The plan as chosen, shown beside the rows (time of day, preparation, enemy skill, enemies)
    private readonly List<string> _summary = new();
    private int _optionLogs;

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private void AddSelector(Michsky.DreamOS.HorizontalSelector sel, string label, Action<int> set)
    {
        // Not only while it's on show: the laptop's own page is hidden while you plan at the window, and needing it
        // visible left out the time of day and the rest ("there used to be mission time, night, day", 2026-11-03)
        if (sel == null || sel.items == null || sel.items.Count == 0)
        {
            if (_optionLogs++ < 10)
                Plugin.Logger.LogInfo($"[Missions] No '{label}' choice on the planning board ({(sel == null ? "none" : "no items")})");
            return;
        }
        var title = "";
        if (sel.index >= 0 && sel.index < sel.items.Count)
            title = sel.items[sel.index].itemTitle;
        if (string.IsNullOrEmpty(title) && sel.label != null)
            title = sel.label.text;
        _summary.Add($"{label}: <b>{title}</b>");
        _rows.Add(new Row
        {
            Text = $"{label}:  <  {title}  >",
            Change = d =>
            {
                var n = sel.items.Count;
                var i = ((sel.index + d) % n + n) % n;
                // As its own arrows do: move the selector, then tell the board
                if (d > 0) sel.NextItem(); else sel.PreviousItem();
                if (sel.index != i)
                    sel.index = i;
                set(i);
            },
        });
    }

    private void Deploy()
    {
        var board = Board();
        if (board == null)
        {
            _message = "The planning board isn't up. Go back and choose the mission again.";
            return;
        }
        if (board.NotEnoughInfilsButton != null && board.NotEnoughInfilsButton.activeInHierarchy)
        {
            _message = "Not enough infil points switched on for everyone.";
            return;
        }
        if (board.NotEnoughExfilsButton != null && board.NotEnoughExfilsButton.activeInHierarchy)
        {
            _message = "Not enough exfil points for this mission.";
            return;
        }
        board.ExecuteOperationButtonPressed();
        _step = Step.Confirm;
        _sel = 0;
        Plugin.Logger.LogInfo($"[Missions] Deploy pressed; confirmation showing {MissionLaptopNetworkState.instance.ShowingStartMissionConfirmation}");
    }

    private void StartMission()
    {
        var board = Board();
        if (board == null)
            return;
        // What sitting at the laptop would have set up
        var missing = new List<string>();
        try
        {
            var opboard = board.CerebusOpboard;
            if (opboard != null && opboard.missionLaptop == null) { opboard.missionLaptop = _laptop; missing.Add("plan's laptop"); }
            if (board.Laptop == null) { board.Laptop = _laptop; missing.Add("board's laptop"); }
            if (_laptop.playerNetworking == null) missing.Add("laptop's player (not found)");
            if (OperationsManager.singleton.activeMissionLaptop == null) { OperationsManager.singleton.activeMissionLaptop = _laptop; missing.Add("active laptop"); }
        }
        catch (Exception e) { missing.Add($"check failed: {e.Message}"); }
        if (missing.Count > 0)
            Plugin.Logger.LogInfo($"[Missions] Before starting, filled in: {string.Join(", ", missing)}");
        board.StartOperationButton();
        _message = "Starting...";
        Plugin.Logger.LogInfo($"[Missions] Start pressed; operation starting {OperationsManager.singleton.isStarting}");
        ReleaseControl("mission starting");
    }

    // ---- Screen ----

    private void Build()
    {
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<TMP_Text>(), FindObjectsSortMode.None))
        {
            var t = o.TryCast<TMP_Text>();
            if (t != null && t.font != null)
            {
                _font = t.font;
                break;
            }
        }

        _root = new GameObject("OperatorVR Mission Screen");
        UnityEngine.Object.DontDestroyOnLoad(_root);
        var canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;    // we place it ourselves, standing over the laptop
        canvas.sortingOrder = 5000;
        _root.GetComponent<RectTransform>().sizeDelta = new Vector2(CanvasWidth, CanvasHeight);

        // Solid: see-through, the glowing blinds behind showed through it
        var panel = Box(_root.transform, "Panel", Vector2.zero, Vector2.one, new Color(0.05f, 0.07f, 0.06f, 1f));
        // Everything but the backdrop, so it can be hidden while the game's infil map lies on the screen
        var contentGo = new GameObject("Content");
        contentGo.transform.SetParent(panel, false);
        var crt = contentGo.AddComponent<RectTransform>();
        crt.anchorMin = Vector2.zero;
        crt.anchorMax = Vector2.one;
        crt.offsetMin = crt.offsetMax = Vector2.zero;
        _content = contentGo;
        var body = contentGo.transform;
        _title = Label(body, "Title", new Vector2(0.03f, 0.88f), new Vector2(0.97f, 0.98f), 52f, TextAlignmentOptions.Left);
        _hint = Label(body, "Hint", new Vector2(0.03f, 0.01f), new Vector2(0.97f, 0.08f), 30f, TextAlignmentOptions.Left);
        _hint.color = new Color(0.8f, 0.8f, 0.72f);
        _detail = Label(body, "Detail", new Vector2(0.52f, 0.1f), new Vector2(0.97f, 0.86f), 32f, TextAlignmentOptions.TopLeft);
        // A long briefing shrinks to fit rather than running off the bottom
        _detail.enableAutoSizing = true;
        _detail.fontSizeMin = 20f;
        _detail.fontSizeMax = 32f;

        // The mission's map on the plan and deploy pages (the user's request), above the text
        var mapGo = new GameObject("Map");
        mapGo.transform.SetParent(body, false);
        var mrt = mapGo.AddComponent<RectTransform>();
        mrt.anchorMin = mrt.anchorMax = new Vector2((MapBoxMin.x + MapBoxMax.x) / 2f, (MapBoxMin.y + MapBoxMax.y) / 2f);
        _map = mapGo.AddComponent<RawImage>();
        _map.raycastTarget = false;
        mapGo.SetActive(false);

        for (var i = 0; i < VisibleRows; i++)
        {
            var top = 0.86f - i * (0.76f / VisibleRows);
            var row = Box(body, $"Row {i}", new Vector2(0.03f, top - 0.76f / VisibleRows + 0.006f), new Vector2(0.5f, top), new Color(1f, 1f, 1f, 0.06f));
            var text = Label(row, "Text", new Vector2(0.03f, 0f), new Vector2(0.97f, 1f), 36f, TextAlignmentOptions.Left);
            _rowUi.Add((row.GetComponent<Image>(), text));
        }
        Plugin.Logger.LogInfo("[Missions] Mission screen made");
    }

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private static Transform Box(Transform parent, string name, Vector2 min, Vector2 max, Color colour)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = min;
        rt.anchorMax = max;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        var img = go.AddComponent<Image>();
        img.color = colour;
        img.raycastTarget = false;
        return go.transform;
    }

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private TextMeshProUGUI Label(Transform parent, string name, Vector2 min, Vector2 max, float size, TextAlignmentOptions align)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = min;
        rt.anchorMax = max;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        var t = go.AddComponent<TextMeshProUGUI>();
        if (_font != null)
            t.font = _font;
        t.fontSize = size;
        t.alignment = align;
        t.color = Color.white;
        t.richText = true;
        t.raycastTarget = false;
        return t;
    }

    private void Paint()
    {
        // While the game's infil map lies on the screen as a page, the screen's own text is hidden under it, so the
        // two don't show through each other ("several images laid on top of each other", 2026-11-03)
        var pageUp = Time.unscaledTime - _pageAt < 0.5f;
        if (_content != null && _content.activeSelf == pageUp)
            _content.SetActive(!pageUp);
        if (pageUp)
            return;
        _title.text = _step switch
        {
            Step.Operations => _simulation ? "SIMULATIONS   <color=#888>(stick left: operations)</color>" : "OPERATIONS   <color=#888>(stick right: simulations)</color>",
            Step.Missions => $"{Loc(_op?.OperationName)}  -  MISSIONS",
            Step.Options => $"{(_pkg != null ? PackageName(_pkg) : "")}  -  PLAN",
            _ => "READY TO DEPLOY",
        };
        string idle;
        if (!_haveWindow)
            idle = "Press Interact at the laptop to choose a mission";
        else
        {
            var cam = Camera.main;
            idle = cam != null && AtWindow(cam) ? "Press Interact to choose a mission" : "Walk up to this screen and press Interact to choose a mission";
        }
        _hint.text = HasControl
            ? "Stick: move   Left/right: change   R2: select   Circle: back   Interact or Options: done"
            : $"<color=#FFD933>{idle}</color>";

        if (_sel < _top) _top = _sel;
        if (_sel >= _top + VisibleRows) _top = _sel - VisibleRows + 1;
        for (var i = 0; i < _rowUi.Count; i++)
        {
            var (back, text) = _rowUi[i];
            var index = _top + i;
            var on = index < _rows.Count;
            if (back.gameObject.activeSelf != on)
                back.gameObject.SetActive(on);
            if (!on)
                continue;
            var row = _rows[index];
            var selected = HasControl && index == _sel;
            text.text = row.Locked ? $"<color=#777>{row.Text}  (locked)</color>" : row.Text;
            back.color = selected ? new Color(0.85f, 0.65f, 0.15f, 0.55f) : new Color(1f, 1f, 1f, 0.06f);
        }
        _detail.text = Detail() + (string.IsNullOrEmpty(_message) ? "" : $"\n\n<color=#FFD933>{_message}</color>");
        PaintMap();
    }

    // ---- Map ----
    // The planning board shows the mission's map under OperationBoardUI.MapParent (the map prefab, a RawImage or
    // Image); the biggest picture there is the map. Fallback: OperationsManager.CurrentOperationInfo.MapImage
    private float _nextMapLook;
    private Texture _mapTex;
    private Rect _mapUv = new(0f, 0f, 1f, 1f);
    private int _mapLogs;

    private void PaintMap()
    {
        // Off: none of the board's pictures is the map itself (it picked a white square, the target's photo, then an
        // old screenshot), and the game's own infil map now comes up on this screen as a page after Start
        var want = false;
        if (want && Time.unscaledTime >= _nextMapLook)
        {
            _nextMapLook = Time.unscaledTime + 1f;
            var tex = FindMap(out var uv);
            if (tex != null && (_mapTex == null || tex.Pointer != _mapTex.Pointer))
            {
                if (_mapLogs++ < 10)
                    Plugin.Logger.LogInfo($"[Missions] Map picture '{tex.name}' {tex.width}x{tex.height} ({tex.GetIl2CppType().Name}), part {uv}");
                _mapTex = tex;
                _mapUv = uv;
            }
        }
        var show = want && _mapTex != null;
        if (_map.gameObject.activeSelf != show)
            _map.gameObject.SetActive(show);
        var drt = _detail.rectTransform;
        var textMax = new Vector2(0.97f, show ? MapBoxMin.y - 0.02f : 0.86f);
        if (drt.anchorMax != textMax)
            drt.anchorMax = textMax;
        if (!show)
            return;
        if (_map.texture == null || _map.texture.Pointer != _mapTex.Pointer)
        {
            _map.texture = _mapTex;
            _map.uvRect = _mapUv;
        }
        // Fit the map in its box, its shape kept
        var page = _root.GetComponent<RectTransform>().sizeDelta;
        var boxW = (MapBoxMax.x - MapBoxMin.x) * page.x;
        var boxH = (MapBoxMax.y - MapBoxMin.y) * page.y;
        var aspect = Mathf.Max(0.1f, _mapTex.width * _mapUv.width) / Mathf.Max(0.1f, _mapTex.height * _mapUv.height);
        var size = boxW / boxH > aspect ? new Vector2(boxH * aspect, boxH) : new Vector2(boxW, boxW / aspect);
        if (_map.rectTransform.sizeDelta != size)
            _map.rectTransform.sizeDelta = size;
    }

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private Texture FindMap(out Rect uv)
    {
        uv = new Rect(0f, 0f, 1f, 1f);
        Texture best = null;
        try
        {
            var board = Board();
            // The biggest picture FILE, not the biggest shape: the first try took 'Background', a 32x32 white square
            // stretched across the map area (2026-11-03). Small textures (UI pieces, markers) are skipped
            var bestArea = 0f;
            var places = new List<Transform>();
            if (board != null && board.MapParent != null)
                places.Add(board.MapParent);
            if (board != null && board.FullscreenMapParent != null)
                places.Add(board.FullscreenMapParent);
            try
            {
                var prefab = board != null && board.CerebusOpboard != null ? board.CerebusOpboard.MapPrefab : null;
                if (prefab != null)
                    places.Add(prefab.transform);
            }
            catch { }
            foreach (var parent in places)
                foreach (var g in parent.GetComponentsInChildren<Graphic>(true))
                {
                    if (g == null)
                        continue;
                    Texture t = null;
                    var r = new Rect(0f, 0f, 1f, 1f);
                    var raw = g.TryCast<RawImage>();
                    if (raw != null)
                    {
                        t = raw.texture;
                        r = raw.uvRect;
                    }
                    else
                    {
                        var img = g.TryCast<Image>();
                        if (img != null && img.sprite != null && img.sprite.texture != null)
                        {
                            t = img.sprite.texture;
                            var tr = img.sprite.textureRect;
                            r = new Rect(tr.x / t.width, tr.y / t.height, tr.width / t.width, tr.height / t.height);
                        }
                    }
                    if (t == null || t.width < 256 || t.height < 256)
                        continue;
                    // A map, not the target's photo ('Georgia HVT Selfie' was picked, 2026-11-03): names that say
                    // map come first, photos are skipped
                    var lower = (t.name ?? "").ToLowerInvariant();
                    if (lower.Contains("selfie") || lower.Contains("photo") || lower.Contains("portrait"))
                        continue;
                    var pixels = t.width * r.width * t.height * r.height * (lower.Contains("map") || lower.Contains("sat") ? 100f : 1f);
                    if (pixels > bestArea)
                    {
                        best = t;
                        bestArea = pixels;
                        uv = r;
                    }
                }
            if (best == null)
            {
                var info = OperationsManager.CurrentOperationInfo;
                if (info != null && info.MapImage != null)
                    best = info.MapImage;
            }
        }
        catch { }
        return best;
    }

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private string Detail()
    {
        try
        {
            switch (_step)
            {
                case Step.Operations:
                {
                    var list = _simulation ? OperationsManager.singleton.SimulationOperations : OperationsManager.singleton.ActiveOperations;
                    var op = RowObject(list);
                    if (op == null)
                        return "";
                    var missions = op.TargetPackages != null ? op.TargetPackages.Length : 0;
                    return $"<b>{Loc(op.OperationName)}</b>\n\nThreat: {Loc(op.OperationThreatLevel)}\nDuration: {Loc(op.OperationDuration)}\nMissions: {missions}\n\n{Loc(op.OperationDescription)}";
                }
                case Step.Missions:
                {
                    var pkg = RowObject(_op?.TargetPackages);
                    if (pkg == null)
                        return "";
                    var board = pkg.TargetPackage;
                    var report = board != null ? Loc(board.SituationReport) : "";
                    return $"<b>{PackageName(pkg)}</b>\n\nEnemies: {Loc(pkg.EnemyCount)}\n\n{Loc(pkg.OperationDescription)}" +
                           (string.IsNullOrEmpty(report) ? "" : $"\n\n<color=#BBB>{report}</color>");
                }
                case Step.Options:
                {
                    var plan = _summary.Count > 0 ? "<b>MISSION PLAN</b>\n\n" + string.Join("\n", _summary) + "\n\n" : "";
                    var where = _pkg != null ? $"<b>{PackageName(_pkg)}</b>\n\n" : "";
                    // The briefing too, as the laptop's planning page had it ("it gave you a briefing of the mission
                    // on the same page", 2026-11-03)
                    var briefing = "";
                    if (_pkg != null)
                    {
                        var desc = Loc(_pkg.OperationDescription);
                        var report = _pkg.TargetPackage != null ? Loc(_pkg.TargetPackage.SituationReport) : "";
                        if (!string.IsNullOrEmpty(desc) || !string.IsNullOrEmpty(report))
                            briefing = "<b>BRIEFING</b>\n" + desc + (string.IsNullOrEmpty(report) ? "" : $"\n<color=#BBB>{report}</color>") + "\n\n";
                    }
                    return where + plan + briefing + "Change a line with the stick (left/right), then DEPLOY.";
                }
                default:
                    return "R2 on START THE MISSION to go.";
            }
        }
        catch
        {
            return "";
        }
    }

    // The game object behind the selected row, for lists shown as rows in order (nulls skipped as in Rebuild)
    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private T RowObject<T>(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<T> list) where T : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
    {
        if (list == null)
            return null;
        var n = 0;
        for (var i = 0; i < list.Length; i++)
        {
            if (list[i] == null)
                continue;
            if (n++ == _sel)
                return list[i];
        }
        return null;
    }
}
