using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace OperatorVR;

/// <summary>
/// The armoury bench's weapon modding page ('WeaponModdingCanvas') is a world-space panel beside the rifle. The game
/// frames it from a fixed camera spot in flat mode; in VR your head is lower and closer, and the bottom of the
/// modifications list sat behind the table top where you couldn't see or reach it. While the page is open in VR it
/// is drawn on top of everything (MenusOnTop's layer, like the floating menus), and put back afterwards.
/// Menus.WeaponBenchOnTop.
/// </summary>
public class WeaponBench : MonoBehaviour
{
    public WeaponBench(IntPtr ptr) : base(ptr) { }

    private readonly Dictionary<IntPtr, GameObject> _onTop = new();

    private static WeaponBench s_bench;
    private static int s_wallWhyLogs;
    private const float MaxGunDistance = 2.5f;
    private const float MaxPartDistance = 1.2f;
    private int _farPartLogs;
    private void Awake() => s_bench = this;

    /// <summary>
    /// Where the bench page goes when it's drawn as part of the room (Menus.WeaponBenchOnTop off): flat against the
    /// wall behind the gun, facing you ("put the black screens back on the wall and the gun in front, that's how it
    /// used to be", 2026-11-03). The wall is found by a ray from the gun's middle, straight on from your eye.
    /// </summary>
    internal static bool TryWallPose(Vector3 eye, out Vector3 pos, out Quaternion rot)
    {
        pos = default;
        rot = Quaternion.identity;
        var self = s_bench;
        var why = self == null ? "bench helper missing" : self._holder == null ? "gun not found yet" : null;
        var middle = Vector3.zero;
        if (why == null && !self.WeaponMiddle(out middle))
            why = "gun has no visible parts";
        // The parts' bounds once put the gun 11 m away and the page across the room ("it flashed up and went away",
        // 2026-11-03): a gun that far isn't the one on the bench in front of you, so use the bench's turning point
        if (why == null && Vector3.Distance(eye, middle) > MaxGunDistance)
        {
            var bounds = middle;
            if (self._turner != null && Vector3.Distance(eye, self._turner.transform.position) <= MaxGunDistance)
                middle = self._turner.transform.position;
            else
                why = $"gun measured {Vector3.Distance(eye, bounds):0.0} m away";
        }
        if (why != null)
        {
            if (s_wallWhyLogs++ < 10)
                Plugin.Logger.LogInfo($"[Bench] Page not on the wall yet: {why}");
            return false;
        }
        var dir = middle - eye;
        dir.y = 0f;
        if (dir.sqrMagnitude < 1e-4f)
            return false;
        dir.Normalize();
        // Just behind the gun, closer still if the wall is nearer: a page further back sat behind the real wall and
        // couldn't be seen ("you just see the gun", 2026-11-03)
        var behind = 0.15f;
        var face = dir;
        try
        {
            var best = float.MaxValue;
            var normal = Vector3.zero;
            foreach (var hit in Physics.RaycastAll(middle, dir, 1.5f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                var t = hit.collider != null ? hit.collider.transform : null;
                if (t == null || hit.distance < 0.05f || t.IsChildOf(self._holder))
                    continue;
                if (hit.distance < best)
                {
                    best = hit.distance;
                    normal = hit.normal;
                }
            }
            if (best < float.MaxValue)
                behind = Mathf.Clamp(best - 0.03f, 0.05f, 0.15f);
            // Flat to the wall, not turned towards wherever you stood when it opened: from one side the page came up
            // at an angle (2026-11-03)
            normal.y = 0f;
            if (normal.sqrMagnitude > 0.25f && Vector3.Dot(-normal.normalized, dir) > 0.5f)
                face = -normal.normalized;
        }
        catch { }
        pos = middle + dir * behind;
        rot = Quaternion.LookRotation(face, Vector3.up);
        return true;
    }
    private float _nextScan, _layoutAt;
    private Canvas _layoutCanvas;

    // On the bench the movement keys (the left stick) slide the game's camera about, up to DragMouse.MaxCameraOffset
    // (DragMouse.WeaponModdingLoop, 2026-11-01). On a screen that pans the view; in VR it moved the whole view as the
    // stick moved ("the box moves when I move my joystick"). While VR runs the limit is zero, so it stays put
    private readonly Dictionary<IntPtr, (DragMouse drag, Vector3 max)> _benchCams = new();

    private void HoldBenchCamera()
    {
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<DragMouse>(), FindObjectsSortMode.None))
        {
            var d = o.TryCast<DragMouse>();
            if (d == null || _benchCams.ContainsKey(d.Pointer))
                continue;
            var max = d.MaxCameraOffset;
            _benchCams[d.Pointer] = (d, max);
            d.MaxCameraOffset = Vector3.zero;
            Plugin.Logger.LogInfo($"[Bench] '{d.name}': camera no longer slides with the movement keys in VR (limit was {max})");
        }
    }

    // The bench turns the weapon about DragMouse's own transform, which isn't at the weapon's middle: in VR it swung
    // round as it turned, pointing at you at a quarter turn and into the wall behind at a half (2026-11-01). After the
    // game turns it each frame, the turning point is moved so the weapon's middle stays where it was. Only frames where
    // the turn changed move it, so parts added or removed don't make it wander. Put back when the page closes
    private DragMouse _turner;
    private Transform _holder;
    private Vector3 _lastMiddle;
    private bool _haveMiddle;
    private float _lastYaw, _nextRenderers;
    private Transform _tipper;            // DragMouse's 'PitchTransform', which tips the weapon up and down
    private Quaternion _tipHome;
    private float _tipValueHome;
    private readonly List<Renderer> _weaponRenderers = new();
    private int _spinLogs;

    // With the Touch controllers the right stick doesn't reach the bench's turner (a menu takes the mouse, so the
    // stick sends no mouse movement), so the weapon couldn't be turned as with the PS5 pad (2026-11-04). TouchKeys
    // adds the right stick's left/right here (degrees), and it's added to the turner's own turn
    internal static bool Open { get; private set; }
    internal static float SpinInput;
    private bool _spinLogged;

    private void LateUpdate()
    {
        Open = OpenXRBootstrap.IsRunning && PageCanvas() != null;
        try
        {
            if (Open && _turner == null && Mathf.Abs(SpinInput) > 0f && !_spinLogged)
            {
                _spinLogged = true;
                Plugin.Logger.LogWarning("[Bench] Right stick turning the weapon, but the bench's turner isn't found");
            }
            if (Open && _turner != null && Mathf.Abs(SpinInput) > 0f)
            {
                _turner._yRotation += SpinInput;
                if (!_spinLogged)
                {
                    _spinLogged = true;
                    Plugin.Logger.LogInfo("[Bench] Turning the weapon with the Touch right stick");
                }
            }
        }
        catch (Exception e) { Plugin.Logger.LogError($"[Bench] stick turn: {e.GetType().Name}: {e.Message}"); }
        SpinInput = 0f;

        try { FixLeftWeapons(); }
        catch (Exception e) { Plugin.Logger.LogError($"[Bench] weapon size back: {e.GetType().Name}: {e.Message}"); }
        try
        {
            if (!OpenXRBootstrap.IsRunning || PageCanvas() == null)
            {
                HeadTracking.ViewBack = 0f;
                PutTurnerBack();
                return;
            }
            // The gun felt too close to the face, and the page can't go further back without going into the wall
            // (2026-11-03): your view steps back a little instead, while the page is open
            HeadTracking.ViewBack = Plugin.BenchStepBack.Value;
            // The open bench's own DragMouse: every bench in the room has an enabled one, and taking the first found
            // kept 'WeaponModdingTable (6)' (0 parts) while the user was at table (1) (2026-11-03)
            var openBench = OpenBench();
            if (_turner == null || !_turner.isActiveAndEnabled || !IsTurnerOf(_turner, openBench))
            {
                PutTurnerBack();
                foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<DragMouse>(), FindObjectsSortMode.None))
                {
                    var d = o.TryCast<DragMouse>();
                    if (d != null && d.isActiveAndEnabled && d.weaponModding != null && IsTurnerOf(d, openBench))
                    {
                        _turner = d;
                        break;
                    }
                }
                _haveMiddle = false;
                if (_turner == null)
                    return;
            }
            if (Time.unscaledTime >= _nextRenderers)
            {
                _nextRenderers = Time.unscaledTime + 1f;
                // The weapon isn't under DragMouse itself ('WeaponParent_Left/Centre (Pivot)/Drag Mouse' carried no
                // parts, 2026-11-02): use the nearest ancestor that holds the weapon, and move that one
                // The bench's own Weapon when it has one (the ancestor search found 'WeaponParent_Left' with no parts
                // on another bench, 2026-11-02); otherwise the nearest ancestor of DragMouse that holds renderers
                Transform holder = null;
                // Only while it's on this bench's turner: once handed back it's the gun in the player's hands
                try { var w = _turner.weaponModding != null ? _turner.weaponModding.Weapon : null; if (w != null && w.transform.IsChildOf(_turner.transform)) holder = w.transform; } catch { }
                _weaponRenderers.Clear();
                if (holder != null)
                    foreach (var r in holder.GetComponentsInChildren<Renderer>(false))
                        if (r != null && r.enabled && r.gameObject.layer != MenusOnTop.Layer)
                            _weaponRenderers.Add(r);
                if (_weaponRenderers.Count == 0)
                {
                    holder = _turner.transform;
                    while (holder != null)
                    {
                        foreach (var r in holder.GetComponentsInChildren<Renderer>(false))
                            if (r != null && r.enabled && r.gameObject.layer != MenusOnTop.Layer)
                                _weaponRenderers.Add(r);
                        if (_weaponRenderers.Count > 0 || holder.parent == null || holder.name.StartsWith("WeaponParent"))
                            break;
                        holder = holder.parent;
                    }
                }
                if (holder != null && (_holder == null || holder.Pointer != _holder.Pointer))
                {
                    _holder = holder;
                    if (_spinLogs++ < 12)
                        Plugin.Logger.LogInfo($"[Bench] Keeping '{LaptopDiagnostics.Path(holder)}' ({_weaponRenderers.Count} parts) in place as '{_turner.name}' turns it");
                    NewWeapon();
                }
            }
            if (_holder == null || _tipper == null)
                return;
            // Handed to the player (or elsewhere): nothing of it is ours to put back, so just stop following it
            if (!_holder.IsChildOf(_turner.transform))
            {
                _holder = null;
                return;
            }
            // Only round and round, like a turntable: the stick's up/down also tipped it over, so it swung through all
            // sorts of angles ("just have it rotate on its axis, all the way around", 2026-11-03). The tip is held
            // where it was when the bench opened
            HoldLevel();
            var yaw = _turner._yRotation;
            // Its middle stays on one spot every frame (not only when the turn changes), so it spins in place. The
            // middle is followed through the weapon's own transform: renderer bounds only catch up when it's drawn, so
            // moving by them every frame added the same shift again and again and the gun slid away out of sight
            // (2026-11-03)
            // If the game moves the bench's pivot (bringing the weapon in, say), the spot moves with it
            var pivot = _turner.transform.parent != null ? _turner.transform.parent : _turner.transform;
            if (_haveMiddle && _pivotKnown && (pivot.position - _anchorPivot).sqrMagnitude > 1e-8f)
                _lastMiddle += pivot.position - _anchorPivot;
            _anchorPivot = pivot.position;
            _pivotKnown = _haveMiddle;
            if (_haveMiddle)
            {
                var shift = _lastMiddle - _holder.TransformPoint(_localMiddle);
                if (shift.sqrMagnitude > 1e-8f)
                {
                    _tipper.position += shift;
                    if (Mathf.Abs(Mathf.DeltaAngle(yaw, _lastYaw)) > 0.01f && _spinLogs++ < 3)
                        Plugin.Logger.LogInfo($"[Bench] Weapon turned to {yaw:0}°: moved {shift.magnitude * 100f:0.0} cm to keep its middle in place");
                }
            }
            _lastYaw = yaw;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Bench] keeping the weapon in place: {e.GetType().Name}: {e.Message}");
            _turner = null;
        }
    }

    /// <summary>The bench whose modding page is open (null if it can't be told, then any bench's DragMouse will do).</summary>
    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private WeaponModding OpenBench()
    {
        try
        {
            var page = PageCanvas();
            return page != null ? page.GetComponentInParent<WeaponModding>(true) : null;
        }
        catch { return null; }
    }

    /// <summary>The open bench page, whether drawn on top (tracked here) or as part of the room (a floating menu).</summary>
    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private GameObject PageCanvas()
    {
        foreach (var go in _onTop.Values)
            if (go != null && go.activeInHierarchy)
                return go;
        foreach (var c in VRMenus.FloatingCanvases())
            if (c != null && c.isActiveAndEnabled && c.name.StartsWith("WeaponModdingCanvas"))
                return c.gameObject;
        return null;
    }

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private static bool IsTurnerOf(DragMouse d, WeaponModding bench)
    {
        if (bench == null)
            return true;
        try { return d.weaponModding != null && d.weaponModding.Pointer == bench.Pointer; }
        catch { return false; }
    }

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private bool WeaponMiddle(out Vector3 middle)
    {
        // From each part's own box through its transform as it is now: renderer.bounds only catches up when drawn,
        // and the game moves the weapon onto the bench after it appears, so the pinned middle was a stale spot by the
        // wall and the page hid the gun (2026-11-03)
        // Only the weapon's own solid parts near it: after a trip to the hands the HK416 also had something whose box
        // put its middle 18 m away, and the gun and page went with it (2026-11-03)
        var any = false;
        var b = new Bounds();
        var near = _holder != null ? _holder.position : (_turner != null ? _turner.transform.position : Vector3.zero);
        foreach (var r in _weaponRenderers)
        {
            if (r == null || !r.enabled || !r.gameObject.activeInHierarchy)
                continue;
            if (r.TryCast<MeshRenderer>() == null && r.TryCast<SkinnedMeshRenderer>() == null)
                continue;
            var local = r.localBounds;
            var t = r.transform;
            var c = local.center;
            var e = local.extents;
            var centre = t.TransformPoint(c);
            if ((centre - near).sqrMagnitude > MaxPartDistance * MaxPartDistance || t.TransformVector(e).sqrMagnitude > MaxPartDistance * MaxPartDistance)
            {
                if (_farPartLogs++ < 10)
                    Plugin.Logger.LogInfo($"[Bench] Part '{r.name}' ({r.GetIl2CppType().Name}) left out of the weapon's middle: {Vector3.Distance(centre, near):0.0} m from it, {t.TransformVector(e).magnitude * 2f:0.0} m across");
                continue;
            }
            for (var i = 0; i < 8; i++)
            {
                var p = t.TransformPoint(c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z));
                if (any) b.Encapsulate(p); else { b = new Bounds(p, Vector3.zero); any = true; }
            }
        }
        middle = b.center;
        return any;
    }

    // Everything we change is on the bench's own 'PitchTransform' (size, place, tip), never on the weapon: the weapon
    // is the player's own gun, handed back and forth as weapons are switched, and changes left on it put the HK416 22 m
    // away when it came back to the bench (2026-11-03). Put back when the page closes
    private void PutTurnerBack()
    {
        if (_tipper != null)
            try
            {
                _tipper.localPosition = _tipPosHome;
                _tipper.localRotation = _tipHome;
                _tipper.localScale = _tipScaleHome;
            }
            catch { }
        _haveMiddle = false;
        _turner = null;
        _holder = null;
        _tipper = null;
    }

    private bool FindTipper()
    {
        if (_tipper != null)
            return true;
        if (_turner == null)
            return false;
        _tipper = _turner.transform.Find("PitchTransform");
        if (_tipper == null)
        {
            if (_spinLogs++ < 12)
                Plugin.Logger.LogInfo($"[Bench] '{_turner.name}' has no PitchTransform: weapon left as the game shows it");
            return false;
        }
        _tipHome = _tipper.localRotation;
        _tipPosHome = _tipper.localPosition;
        _tipScaleHome = _tipper.localScale;
        _tipValueHome = _turner._zRotation;
        var size = Mathf.Clamp(Plugin.BenchGunSize.Value, 0.2f, 1f);
        _tipper.localScale = _tipScaleHome * size;
        Plugin.Logger.LogInfo($"[Bench] Weapon only turns round now (tip held at {_tipValueHome:0}°), shown at {size * 100f:0}% size (Menus.BenchGunSize)");
        return true;
    }

    private void HoldLevel()
    {
        if (!FindTipper())
            return;
        _turner._zRotation = _tipValueHome;
        _tipper.localRotation = _tipHome;
    }

    // A weapon put on the bench (or switched to): face it the way the first one faced, and spin it about its own middle,
    // kept on the same spot as the first ("when I changed weapons they were all at an angle", 2026-11-03)
    private void NewWeapon()
    {
        // Measured before the first shrink, so the smaller weapon sits where the full-size one was
        if (_holder == null || !WeaponMiddle(out var middle) || !FindTipper())
            return;
        // A middle far from the bench is a wrong reading, not the weapon: leave the weapon where the game puts it
        if (Vector3.Distance(middle, _turner.transform.position) > 1.5f)
        {
            Plugin.Logger.LogInfo($"[Bench] Weapon middle measured {Vector3.Distance(middle, _turner.transform.position):0.0} m from the bench: not used");
            return;
        }
        // Side-on, as the game first shows it: switching weapons kept the last one's turn, so they came up at an angle
        // (2026-11-03)
        _turner._yRotation = 0f;
        var first = !_haveMiddle;
        if (!_benchWeapons.ContainsKey(_holder.Pointer))
            _benchWeapons[_holder.Pointer] = (_holder, _holder.localScale);
        _localMiddle = _holder.InverseTransformPoint(middle);
        if (first)
        {
            _lastMiddle = middle;
            _haveMiddle = true;
            _pivotKnown = false;
        }
        var eye = Camera.main != null ? Camera.main.transform.position : middle;
        Plugin.Logger.LogInfo($"[Bench] Weapon middle {(first ? "kept" : "brought")} to {_lastMiddle} ({Vector3.Distance(eye, _lastMiddle):0.00} m from the view; it was {Vector3.Distance(middle, _lastMiddle):0.00} m from there)");
    }

    // Life size, a metre away, the weapon covered the middle of the page behind it ("make the gun smaller in the
    // middle", 2026-11-03): the bench's PitchTransform is shrunk (Menus.BenchGunSize), and the per-frame pin above
    // brings the middle back to its spot
    // Weapons that sat on the shrunk bench, with their own size there. The game moves a weapon into the player's hands
    // keeping its size as seen, so the shrink went with it and every gun was tiny in the hands (2026-11-03): once one
    // is off the bench it gets its own size back
    private readonly Dictionary<IntPtr, (Transform t, Vector3 scale)> _benchWeapons = new();
    private int _sizeLogs;

    private void FixLeftWeapons()
    {
        if (_benchWeapons.Count == 0)
            return;
        List<IntPtr> done = null;
        foreach (var kv in _benchWeapons)
        {
            var (t, scale) = kv.Value;
            if (t != null && _tipper != null && t.IsChildOf(_tipper))
                continue;
            (done ??= new List<IntPtr>()).Add(kv.Key);
            if (t == null || (t.localScale - scale).sqrMagnitude < 1e-8f)
                continue;
            if (_sizeLogs++ < 20)
                Plugin.Logger.LogInfo($"[Bench] '{t.name}' left the bench: size {t.localScale} put back to {scale}");
            t.localScale = scale;
        }
        if (done != null)
            foreach (var k in done)
                _benchWeapons.Remove(k);
    }

    private Vector3 _localMiddle, _anchorPivot, _tipPosHome, _tipScaleHome;
    private bool _pivotKnown;

    // A part slot on the bench opens a drop-down; its list ('Dropdown List', made when it opens, with its own canvas)
    // didn't show in VR, while the drop-down's full-page 'Blocker' took the next click and closed it, so no part could
    // be picked (2026-11-02). Each frame the page is open, a new list is put flat on the page and on the on-top layer
    private IntPtr _listSeen;
    private int _listLogs;

    private float _nextListCheck;

    private void ShowDropdownLists()
    {
        if (_onTop.Count == 0 || Time.unscaledTime < _nextListCheck)
            return;
        _nextListCheck = Time.unscaledTime + 0.1f;
        foreach (var go in _onTop.Values)
        {
            if (go == null)
                continue;
            var page = go.transform;
            // The list has its own Canvas, wherever the drop-down put it on the page
            foreach (var c in go.GetComponentsInChildren<Canvas>(false))
            {
                var list = c != null ? c.transform : null;
                if (list == null || list.Pointer == page.Pointer || !list.name.StartsWith("Dropdown List") || list.Pointer == _listSeen)
                    continue;
                _listSeen = list.Pointer;
                var local = page.InverseTransformPoint(list.position);
                var canvas = list.GetComponent<Canvas>();
                if (_listLogs++ < 10)
                    Plugin.Logger.LogInfo($"[Bench] Drop-down list opened at page ({local.x:0}, {local.y:0}, {local.z:0}), layer {list.gameObject.layer}, " +
                                          $"scale {list.localScale}, own canvas {(canvas != null ? $"sort {canvas.sortingOrder} override {canvas.overrideSorting}" : "none")}");
                if (Mathf.Abs(local.z) > 1f)
                    list.position = page.TransformPoint(new Vector3(local.x, local.y, 0f));
                MenusOnTop.Put(list.gameObject);
            }
        }
    }

    private void Update()
    {
        using var __t = ModTime.Measure("WeaponBench");
        var on = OpenXRBootstrap.IsRunning && Plugin.WeaponBenchOnTop.Value && Plugin.MenusOnTop.Value;
        if (!on)
        {
            // VR off: everything back. Drawn as part of the room: only the on-top bits go, the stick hold stays (a full
            // release every frame undid it, and it was put back again every second)
            if (!OpenXRBootstrap.IsRunning)
                ReleaseAll();
            else if (_onTop.Count > 0 || _seeThrough.Count > 0)
            {
                foreach (var go in _onTop.Values)
                    try { if (go != null) MenusOnTop.Release(go); } catch { }
                _onTop.Clear();
                RestorePresets();
            }
            // Drawn as part of the room: still keep the bench view from sliding with the stick
            if (OpenXRBootstrap.IsRunning && Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + 1f;
                try { HoldBenchCamera(); } catch { }
            }
            return;
        }
        try { ShowDropdownLists(); } catch (Exception e) { Plugin.Logger.LogError($"[Bench] drop-down: {e.GetType().Name}: {e.Message}"); }
        if (_layoutCanvas != null && Time.unscaledTime >= _layoutAt)
        {
            var lc = _layoutCanvas;
            _layoutCanvas = null;
            LogLayout(lc);
            LogNearbyPanels(lc);
        }
        if (Time.unscaledTime < _nextScan)
            return;
        _nextScan = Time.unscaledTime + 1f;
        try
        {
            var seen = new HashSet<IntPtr>();
            foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<Canvas>(), FindObjectsSortMode.None))
            {
                var c = o.TryCast<Canvas>();
                if (c == null || !c.isActiveAndEnabled || !c.name.StartsWith("WeaponModdingCanvas"))
                    continue;
                seen.Add(c.gameObject.Pointer);
                if (_onTop.ContainsKey(c.gameObject.Pointer))
                    continue;
                MenusOnTop.Put(c.gameObject);
                _onTop[c.gameObject.Pointer] = c.gameObject;
                SeeThroughPresets(c);
                PullWeaponOut(c);
                Plugin.Logger.LogInfo($"[Bench] Weapon modding page drawn on top: '{LaptopDiagnostics.Path(c.transform)}'");
                // The page grows from zero size as it opens (logged at once, every part read as one point): wait
                _layoutCanvas = c;
                _layoutAt = Time.unscaledTime + 1.5f;
            }
            WatchMagManager();
            HoldBenchCamera();
            // Pages that closed go back to normal
            List<IntPtr> gone = null;
            foreach (var kv in _onTop)
                if (!seen.Contains(kv.Key))
                    (gone ??= new List<IntPtr>()).Add(kv.Key);
            if (gone != null)
                foreach (var p in gone)
                {
                    try { if (_onTop[p] != null) MenusOnTop.Release(_onTop[p]); } catch { }
                    _onTop.Remove(p);
                    PutWeaponBack();
                    RestorePresets();
                    Plugin.Logger.LogInfo("[Bench] Weapon modding page closed");
                }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Bench] {e.GetType().Name}: {e.Message}");
            _nextScan = Time.unscaledTime + 10f;
        }
    }

    // The presets list's backdrop ('Presets/Scroll View/Viewport/Panel', 82% black, 21 cm square) is drawn on top with
    // the page in VR and covered the front of the gun as a black box (2026-11-03). While the page is open it's made
    // mostly see-through; the preset buttons on it stay as they are
    private readonly List<(UnityEngine.UI.Image image, Color colour)> _seeThrough = new();

    private void SeeThroughPresets(Canvas page)
    {
        try
        {
            // Every dark backing in the presets column: with only the Viewport's panel lightened the box still showed
            // (2026-11-03), there are more layers behind it
            var presets = page.transform.Find("Presets");
            if (presets == null)
                return;
            var n = 0;
            foreach (var img in presets.GetComponentsInChildren<UnityEngine.UI.Image>(true))
            {
                if (img == null)
                    continue;
                var c = img.color;
                if (c.a <= 0.3f || Mathf.Max(c.r, Mathf.Max(c.g, c.b)) > 0.3f)
                    continue;
                _seeThrough.Add((img, c));
                img.color = new Color(c.r, c.g, c.b, 0.15f);
                n++;
                Plugin.Logger.LogInfo($"[Bench] '{LaptopDiagnostics.Path(img.transform)}' made see-through (was alpha {c.a:0.00})");
            }
            Plugin.Logger.LogInfo($"[Bench] Presets column: {n} dark backing(s) made see-through so they don't hide the gun");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[Bench] presets backdrop: {e.Message}");
        }
    }

    private void RestorePresets()
    {
        foreach (var (img, c) in _seeThrough)
            try { if (img != null) img.color = c; } catch { }
        _seeThrough.Clear();
    }

    // Temporary: part of the list (a white column) was out of the pointer's reach, which covers the canvas's own
    // rectangle. Log where the page's parts are, in the canvas's coordinates, and which lie outside it
    // Temporary (2026-11-03): a solid black box covered the front of the gun on the bench in VR, and it isn't part of
    // the bench page. Every picture (Image / RawImage) at least 10 cm across within 3 m of the page, on any canvas,
    // is logged once with where it hangs, its colour, texture and layer, so the box can be named
    private static bool s_nearbyLogged;

    private static void LogNearbyPanels(Canvas page)
    {
        if (s_nearbyLogged || page == null)
            return;
        s_nearbyLogged = true;
        try
        {
            var centre = page.transform.position;
            var n = 0;
            foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<UnityEngine.UI.Graphic>(), FindObjectsSortMode.None))
            {
                var g = o.TryCast<UnityEngine.UI.Graphic>();
                if (g == null || !g.isActiveAndEnabled || g.TryCast<TMPro.TMP_Text>() != null)
                    continue;
                var rt = g.rectTransform;
                var size = Vector3.Scale(rt.rect.size, rt.lossyScale);
                if (Mathf.Max(size.x, size.y) < 0.1f || Vector3.Distance(rt.position, centre) > 3f)
                    continue;
                var raw = g.TryCast<UnityEngine.UI.RawImage>();
                var tex = raw != null ? (raw.texture != null ? raw.texture.name : "none") : "-";
                var canvas = g.canvas;
                Plugin.Logger.LogInfo($"[Bench] nearby picture '{LaptopDiagnostics.Path(g.transform)}' {(raw != null ? "RawImage" : g.GetIl2CppType().Name)} " +
                                      $"{size.x:0.00}x{size.y:0.00} m at {Vector3.Distance(rt.position, centre):0.00} m, colour {g.color}, texture {tex}, " +
                                      $"layer {g.gameObject.layer}, canvas '{(canvas != null ? canvas.name : "-")}' {(canvas != null ? canvas.renderMode.ToString() : "")}");
                if (++n >= 40)
                    break;
            }
            Plugin.Logger.LogInfo($"[Bench] {n} nearby pictures listed");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[Bench] nearby pictures: {e.Message}");
        }
    }

    private static void LogLayout(Canvas c)
    {
        try
        {
            var crt = c.GetComponent<RectTransform>();
            var rect = crt.rect;
            Plugin.Logger.LogInfo($"[Bench] canvas rect {rect} scale {c.transform.lossyScale} {c.renderMode}");
            // An IL2CPP array: a managed Vector3[] passed to GetWorldCorners came back unfilled (every part one point)
            var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
            var lines = 0;
            // (a capturing local function: hidden from the IL2CPP class injector, which can't expose it)
            [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
            void Walk(Transform t, int depth)
            {
                if (depth > 5 || lines > 80)
                    return;
                for (var i = 0; i < t.childCount; i++)
                {
                    var k = t.GetChild(i);
                    if (!k.gameObject.activeInHierarchy)
                        continue;
                    var rt = k.GetComponent<RectTransform>();
                    if (rt != null)
                    {
                        rt.GetWorldCorners(corners);
                        var a = crt.InverseTransformPoint(corners[0]);
                        var b = crt.InverseTransformPoint(corners[2]);
                        var outside = a.x < rect.xMin - 1 || a.y < rect.yMin - 1 || b.x > rect.xMax + 1 || b.y > rect.yMax + 1;
                        Plugin.Logger.LogInfo($"[Bench] {new string(' ', depth * 2)}{k.name}: ({a.x:0},{a.y:0})-({b.x:0},{b.y:0}){(outside ? " OUTSIDE" : "")}" +
                                              $"{(k.GetComponent<Canvas>() != null ? " [own canvas]" : "")}");
                        lines++;
                    }
                    Walk(k, depth + 1);
                }
            }
            Walk(c.transform, 1);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Bench] layout: {e.GetType().Name}: {e.Message}");
        }
    }

    // Temporary: the bench's magazine manager ('MagManagerCanvas', a floating menu) showed only its dark panels and
    // a few small buttons ("black") after a magazine was deleted or the mags refilled. Each time it opens or its
    // contents change, log (a second later) what's on it: layer, visibility, transparency, text, and whether the
    // bench is between the eye and the panel. Up to 8 times a session
    private int _magSig = -1, _magLogs;
    private float _magLogAt = -1f;
    private Canvas _magCanvas;

    private void WatchMagManager()
    {
        if (_magCanvas == null || !_magCanvas.isActiveAndEnabled)
        {
            _magCanvas = null;
            foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<Canvas>(), FindObjectsSortMode.None))
            {
                var c = o.TryCast<Canvas>();
                if (c != null && c.isActiveAndEnabled && c.name.StartsWith("MagManagerCanvas"))
                {
                    _magCanvas = c;
                    break;
                }
            }
            if (_magCanvas == null)
            {
                _magSig = -1;
                return;
            }
        }
        LiftMagEntries(_magCanvas);
        var sig = 0;
        foreach (var g in _magCanvas.GetComponentsInChildren<UnityEngine.UI.Graphic>(false))
            sig = sig * 31 + (g.enabled ? 1 : 2) + g.gameObject.layer;
        if (sig != _magSig)
        {
            _magSig = sig;
            if (_magLogs < 8)
                _magLogAt = Time.unscaledTime + 1f;
        }
        if (_magLogAt > 0f && Time.unscaledTime >= _magLogAt)
        {
            _magLogAt = -1f;
            _magLogs++;
            LogPanel(_magCanvas);
        }
    }

    // The log showed every part of the mag manager on the on-top layer, visible, with its text, yet the user still saw
    // only dark boxes: the on-top custom pass sorts what it draws by distance, not by the menu's order, so the list's
    // dark background ('PRIMARY MAGS', 81% opaque) and the page's '(Panel)' can end up drawn over the entries in them.
    // The magazine entries get their own nested canvas sorted above the page (with a raycaster so their buttons still
    // click), and '(Panel)' one sorted below it. New entries (after a delete/refill) are picked up each second
    private readonly HashSet<IntPtr> _lifted = new();
    private bool _liftLogged;

    private void LiftMagEntries(Canvas root)
    {
        var lifted = 0;
        for (var i = 0; i < root.transform.childCount; i++)
        {
            var k = root.transform.GetChild(i);
            if (k.name == "(Panel)")
                lifted += Sort(k.gameObject, root.sortingOrder - 1, false);
            else if (k.name == "PRIMARY MAGS" || k.name.EndsWith("MAGS"))
                for (var j = 0; j < k.childCount; j++)
                {
                    lifted += Sort(k.GetChild(j).gameObject, root.sortingOrder + 1, true);
                    FlattenEntry(k.GetChild(j), root.transform);
                }
        }
        if (lifted > 0 && !_liftLogged)
        {
            _liftLogged = true;
            Plugin.Logger.LogInfo($"[Bench] Mag manager: {lifted} part(s) given their own drawing order (list entries above the dark boxes)");
        }
    }

    // The log then showed the list box, BACK/REFILL and the key hints drawn, but nothing inside the list box (each
    // entry's picture, name, CLONE/REMOVE), though all of it was enabled and opaque. If an entry (or anything in it)
    // is shrunk to nothing or set off the page's surface (behind the box), put it back on the page at full size
    private static int s_flattenLogs;

    private static void FlattenEntry(Transform entry, Transform page)
    {
        foreach (var t in entry.GetComponentsInChildren<Transform>(true))
        {
            var fixedWhat = "";
            var s = t.localScale;
            if (Mathf.Abs(s.x) < 0.01f || Mathf.Abs(s.y) < 0.01f || Mathf.Abs(s.z) < 0.01f)
            {
                t.localScale = Vector3.one;
                fixedWhat += $" scale ({s.x:0.00},{s.y:0.00},{s.z:0.00}) -> 1";
            }
            var z = page.InverseTransformPoint(t.position).z;
            if (Mathf.Abs(z) > 1f)
            {
                var lp = t.localPosition;
                t.localPosition = new Vector3(lp.x, lp.y, lp.z - z * (t.parent != null ? 1f : 0f));
                if (Mathf.Abs(page.InverseTransformPoint(t.position).z) > 1f)
                    t.localPosition = new Vector3(lp.x, lp.y, 0f);
                fixedWhat += $" off the page by {z:0} -> on it";
            }
            if (fixedWhat != "" && s_flattenLogs++ < 30)
                Plugin.Logger.LogInfo($"[Bench] Mag list part '{t.name}':{fixedWhat}");
        }
    }

    private int Sort(GameObject go, int order, bool clickable)
    {
        if (go == null || _lifted.Contains(go.Pointer))
            return 0;
        _lifted.Add(go.Pointer);
        var c = go.GetComponent<Canvas>() ?? go.AddComponent<Canvas>();
        c.overrideSorting = true;
        c.sortingOrder = order;
        if (clickable && go.GetComponent<UnityEngine.UI.GraphicRaycaster>() == null)
            go.AddComponent<UnityEngine.UI.GraphicRaycaster>();
        return 1;
    }

    private static void LogPanel(Canvas c)
    {
        try
        {
            var cam = Camera.main;
            var center = c.transform.position;
            var dist = cam != null ? Vector3.Distance(cam.transform.position, center) : -1f;
            var blocked = "-";
            if (cam != null && Physics.Raycast(cam.transform.position, (center - cam.transform.position).normalized, out var hit, dist))
                blocked = $"'{hit.collider.name}' at {hit.distance:0.00} m";
            Plugin.Logger.LogInfo($"[Bench] Mag manager: {c.renderMode}, layer {c.gameObject.layer} (on top = {MenusOnTop.Layer}), " +
                                  $"sort {c.sortingOrder}, {dist:0.00} m away, in the way: {blocked}, scale {c.transform.lossyScale.x:0.0000}");
            var lines = 0;
            foreach (var t in c.GetComponentsInChildren<Transform>(false))
            {
                if (lines++ > 120)
                    break;
                if (t.Pointer == c.transform.Pointer)
                    continue;
                var info = new List<string> { $"layer {t.gameObject.layer}" };
                var rt = t.TryCast<RectTransform>();
                if (rt != null)
                {
                    // Where it sits, in the page's own pixels (centre and size)
                    var p = c.transform.InverseTransformPoint(rt.TransformPoint(rt.rect.center));
                    info.Add($"at ({p.x:0},{p.y:0},{p.z:0}) size {rt.rect.width:0}x{rt.rect.height:0}");
                    var s = rt.localScale;
                    if (Mathf.Abs(s.x - 1f) > 0.01f || Mathf.Abs(s.y - 1f) > 0.01f || Mathf.Abs(s.z - 1f) > 0.01f)
                        info.Add($"SCALE ({s.x:0.00},{s.y:0.00},{s.z:0.00})");
                }
                var g = t.GetComponent<UnityEngine.UI.Graphic>();
                if (g != null)
                {
                    info.Add($"{g.GetIl2CppType().Name} {(g.enabled ? "on" : "OFF")} alpha {g.color.a:0.00}/{g.canvasRenderer.GetAlpha():0.00}" +
                             $"{(g.canvasRenderer.cull ? " CULLED" : "")}");
                    var tmp = g.TryCast<TMPro.TMP_Text>();
                    if (tmp != null)
                        info.Add($"text '{(tmp.text ?? "").Replace("\n", " ").Substring(0, Math.Min(30, (tmp.text ?? "").Length))}'");
                    var raw = g.TryCast<UnityEngine.UI.RawImage>();
                    if (raw != null)
                        info.Add($"texture {(raw.texture != null ? raw.texture.name : "none")}");
                }
                var grp = t.GetComponent<CanvasGroup>();
                if (grp != null)
                    info.Add($"group alpha {grp.alpha:0.00}");
                if (t.GetComponent<UnityEngine.UI.Mask>() != null || t.GetComponent<UnityEngine.UI.RectMask2D>() != null)
                    info.Add("mask");
                if (t.GetComponent<Canvas>() != null)
                    info.Add("own canvas");
                Plugin.Logger.LogInfo($"[Bench]   {LaptopDiagnostics.Path(t).Replace(LaptopDiagnostics.Path(c.transform) + "/", "")}: {string.Join(", ", info)}");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Bench] mag manager log: {e.GetType().Name}: {e.Message}");
        }
    }

    // ---- The game's bench crashed with the grenade launcher ----
    // Using the bench with the grenade launcher in hand threw a NullReferenceException in
    // WeaponModding.CenterWeaponPosition every frame (6,500+ in a few minutes): it centres the gun on the table from
    // the weapon's WeaponAnimation.centerPoint (and GunFunction.MOD_Magazine), which the launcher doesn't have. The
    // rest of WeaponModding.Update never ran, so the bench never opened its page and couldn't be left: the player
    // sat crouched at the table until the game was quit. A game bug, likely on a flat screen too. If those parts are
    // missing, the centring is skipped (the gun just isn't centred) and the bench carries on.
    private static bool s_centreSkipLogged;

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    internal static void Apply(HarmonyLib.Harmony harmony)
    {
        try
        {
            var m = HarmonyLib.AccessTools.Method(typeof(WeaponModding), nameof(WeaponModding.CenterWeaponPosition));
            harmony.Patch(m, prefix: new HarmonyLib.HarmonyMethod(typeof(WeaponBench), nameof(BeforeCenterWeapon)));
            Plugin.Logger.LogInfo("[Bench] hooked WeaponModding.CenterWeaponPosition (guard for weapons without a centre point)");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Bench] CenterWeaponPosition hook failed: {e.GetType().Name}: {e.Message}");
        }

        // ODG Quartermaster's Gunsmith (ODG Ops modpack) shows the bench gun on a dark backdrop: it moves the gun onto
        // the highest unnamed layer from 29 down and has the camera draw only that. 29 is the only unnamed layer our
        // MenusOnTop finds too, so in VR the gun fell into the on-top drawing and mostly vanished, the rest of the view
        // a dark void (F3 shots 2026-11-03 07:12). While VR runs the backdrop isn't raised: the gun stays in the room
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type t;
            try { t = asm.GetType("Quartermaster.Gunsmith", false); }
            catch { continue; }
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var up = t?.GetMethod("StudioUp", any);
            if (up == null)
                continue;
            try
            {
                harmony.Patch(up, prefix: new HarmonyLib.HarmonyMethod(typeof(WeaponBench), nameof(BeforeGunsmithBackdrop)));
                Plugin.Logger.LogInfo($"[Bench] hooked Quartermaster.Gunsmith.StudioUp in {asm.GetName().Name} (no dark backdrop in VR)");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"[Bench] hooking Quartermaster.Gunsmith.StudioUp failed: {e.Message}");
            }
            // The Gunsmith also slides and zooms the game camera (movement keys and wheel, MoveCam). In VR that moved
            // the view and the floating page with it while the gun stayed ("the menu moves up and down" with the left
            // stick, 2026-11-03), so it's skipped while VR runs
            var move = t.GetMethod("MoveCam", any);
            if (move != null)
                try
                {
                    harmony.Patch(move, prefix: new HarmonyLib.HarmonyMethod(typeof(WeaponBench), nameof(BeforeGunsmithMoveCam)));
                    Plugin.Logger.LogInfo($"[Bench] hooked Quartermaster.Gunsmith.MoveCam (no camera panning in VR)");
                }
                catch (Exception e)
                {
                    Plugin.Logger.LogWarning($"[Bench] hooking Quartermaster.Gunsmith.MoveCam failed: {e.Message}");
                }
        }
    }

    private static bool s_backdropSkipLogged, s_panSkipLogged;

    private static bool BeforeGunsmithMoveCam()
    {
        if (!OpenXRBootstrap.IsRunning)
            return true;
        if (!s_panSkipLogged)
        {
            s_panSkipLogged = true;
            Plugin.Logger.LogInfo("[Bench] Gunsmith camera pan/zoom skipped in VR");
        }
        return false;
    }

    private static bool BeforeGunsmithBackdrop()
    {
        if (!OpenXRBootstrap.IsRunning)
            return true;
        if (!s_backdropSkipLogged)
        {
            s_backdropSkipLogged = true;
            Plugin.Logger.LogInfo("[Bench] Gunsmith's dark backdrop skipped in VR (the gun stays in the room, on its own layers)");
        }
        return false;
    }

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private static bool BeforeCenterWeapon(WeaponModding __instance)
    {
        try
        {
            var mods = __instance != null ? __instance.Weapon : null;
            var gun = mods != null ? mods.Weapon : null;
            var ok = gun != null && gun.weaponAnimation != null && gun.weaponAnimation.centerPoint != null &&
                     gun.gunFunction != null;
            if (ok)
                return true;
            if (!s_centreSkipLogged)
            {
                s_centreSkipLogged = true;
                Plugin.Logger.LogInfo($"[Bench] '{(gun != null ? gun.name : mods != null ? mods.name : "?")}' has no centre point: " +
                                      "skipping the bench's centring (the game's own code would fail every frame and lock the bench)");
            }
            return false;
        }
        catch
        {
            return true;
        }
    }

    // ---- Room to turn the weapon ----
    // Turning a long weapon on the bench (the M72) swung it through the wall behind the table and back. While the
    // modding page is open in VR, the weapon's holders (WeaponModding.WeaponParent_Left/_Right, the gun turns around
    // them) are moved Menus.BenchWeaponForward metres towards the camera, level, and put back when the page closes
    private readonly List<(Transform t, Vector3 pos)> _pulled = new();
    private const float MinBenchDistance = 0.75f;

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private void PullWeaponOut(Canvas page)
    {
        var amount = Mathf.Clamp(Plugin.BenchWeaponForward.Value, 0f, 0.6f);
        var cam = Camera.main;
        if (_pulled.Count > 0 || amount <= 0f || cam == null)
            return;
        WeaponModding bench = null;
        try { bench = page.GetComponentInParent<WeaponModding>(true); } catch { }
        if (bench == null)
            return;
        foreach (var t in new[] { bench.WeaponParent_Left, bench.WeaponParent_Right })
        {
            if (t == null)
                continue;
            var toCam = cam.transform.position - t.position;
            toCam.y = 0f;
            if (toCam.sqrMagnitude < 1e-4f)
                continue;
            // Never closer than 75 cm to the eye: the outdoor bench's view is already close and not against a wall,
            // and the full 30 cm put the rifle in the player's face there
            var move = Math.Min(amount, toCam.magnitude - MinBenchDistance);
            if (move <= 0.01f)
                continue;
            _pulled.Add((t, t.position));
            t.position += toCam.normalized * move;
        }
        var dist = Vector3.Distance(cam.transform.position, bench.WeaponParent_Left != null ? bench.WeaponParent_Left.position : page.transform.position);
        Plugin.Logger.LogInfo(_pulled.Count > 0
            ? $"[Bench] Weapon moved out from the wall so it can turn ({_pulled.Count} holder(s)), now {dist:0.00} m from the eye"
            : $"[Bench] Weapon left where the game puts it ({dist:0.00} m from the eye, too close to move nearer)");
    }

    private void PutWeaponBack()
    {
        // Our turning-point moves first, so the weapon goes back exactly where the bench had it
        PutTurnerBack();
        foreach (var (t, pos) in _pulled)
            try { if (t != null) t.position = pos; } catch { }
        _pulled.Clear();
    }

    private void ReleaseAll()
    {
        PutWeaponBack();
        RestorePresets();
        foreach (var (drag, max) in _benchCams.Values)
            try { if (drag != null) drag.MaxCameraOffset = max; } catch { }
        _benchCams.Clear();
        if (_onTop.Count == 0)
            return;
        foreach (var go in _onTop.Values)
            try { if (go != null) MenusOnTop.Release(go); } catch { }
        _onTop.Clear();
    }
}
