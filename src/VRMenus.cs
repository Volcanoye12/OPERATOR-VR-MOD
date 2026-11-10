using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace OperatorVR;

/// <summary>
/// All of OPERATOR's menus/HUD are uGUI canvases in ScreenSpaceOverlay, which never reach the headset.
/// While VR runs, turn each one into a world-space panel floating in front of the player's body
/// (follows walking and mouse turning, not head rotation, so the head can look at it).
/// Everything is put back when VR stops.
/// </summary>
public class VRMenus : MonoBehaviour
{
    public VRMenus(IntPtr ptr) : base(ptr) { }

    private class Saved
    {
        public Canvas Canvas;
        public RenderMode Mode;
        public Camera WorldCamera;
        public Vector3 LocalPos;
        public Quaternion LocalRot;
        public Vector3 LocalScale;
        public Vector2 SizeDelta;
        // Pinned where it first appeared (the bench page), until it closes
        public bool Pinned, PinnedToWall;
        public Vector3 PinPos;
        public Quaternion PinRot;
        public float PinnedAt;
        public bool DepthLogged, Hidden;
    }

    // The bench page in use (the nearest bench's, when the game has left another one open too); zero = none
    private static IntPtr s_currentBench;

    /// <summary>Whether this is the bench page in use (true too when only one is open or it isn't known yet).</summary>
    internal static bool IsCurrentBenchPage(Canvas c) =>
        c != null && (s_currentBench == IntPtr.Zero || c.Pointer == s_currentBench);

    // Keyed by native pointer: the same Il2Cpp object can come back as different managed wrappers
    private readonly Dictionary<IntPtr, Saved> _converted = new();

    private static VRMenus s_instance;

    /// <summary>Canvases currently floating as VR panels (for the mouse pointer to target).</summary>
    internal static IEnumerable<Canvas> FloatingCanvases()
    {
        if (s_instance == null)
            yield break;
        foreach (var s in s_instance._converted.Values)
            if (s.Canvas != null)
                yield return s.Canvas;
    }

    /// <summary>The ODG Ops modpack's mission menu, opened from the mission laptop over the laptop session.</summary>
    internal const string OdgOpsMenu = "ODG_OpsMenu";

    /// <summary>Whether a floating menu with this name is showing.</summary>
    internal static bool IsShowing(string name)
    {
        foreach (var c in FloatingCanvases())
            if (c.name == name && c.enabled && c.gameObject.activeInHierarchy)
                return true;
        return false;
    }

    private void Awake() => s_instance = this;
    private float _nextScan;
    private bool _wasRunning;

    // F7, not F11: the user's keyboard has no F11
    private const int VK_F7 = 0x76;
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    private bool _f7Down;
    private int _maskLogs;
    // F7 steps through these (metres); small rooms like character customisation put walls closer than 1.5 m
    private static readonly float[] DistanceSteps = { 1.5f, 1.0f, 0.6f };
    // Menus.Width applies at this distance; closer menus shrink so they look the same size
    private const float WidthReferenceDistance = 1.5f;

    private static void CycleDistance()
    {
        var current = Plugin.MenuDistance.Value;
        var next = DistanceSteps[0];
        for (var i = 0; i < DistanceSteps.Length; i++)
            if (current > DistanceSteps[i] + 0.01f)
            {
                next = DistanceSteps[i];
                break;
            }
        Plugin.MenuDistance.Value = next;
        Plugin.Logger.LogInfo($"[Menus] Distance now {next:0.0} m");
    }

    // The health and map screen's backing is a blurred picture of the view behind it (Kamgam BlurredBackgroundImage).
    // On a monitor it's frosted glass; in VR it's one flat copy of the scene hanging in front of the real 3D one,
    // which looked like the world poking through the menu and felt sickening (2026-11-03). In VR each one is switched
    // off and a plain dark backing put in its place (Menus.PlainBackdrops)
    private readonly HashSet<IntPtr> _plainDone = new();
    private float _nextBackdropScan;

    private void PlainBackdrops()
    {
        if (!Plugin.PlainBackdrops.Value || Time.unscaledTime < _nextBackdropScan)
            return;
        _nextBackdropScan = Time.unscaledTime + 1f;
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<Kamgam.UGUIBlurredBackground.BlurredBackgroundImage>(), FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var blur = o.TryCast<Kamgam.UGUIBlurredBackground.BlurredBackgroundImage>();
            if (blur == null)
                continue;
            if (_plainDone.Contains(blur.Pointer))
            {
                // The game may switch it back on with the screen
                if (blur.enabled)
                    blur.enabled = false;
                continue;
            }
            _plainDone.Add(blur.Pointer);
            var tint = blur.color;
            blur.enabled = false;
            var go = new GameObject("OperatorVR plain backdrop");
            go.layer = blur.gameObject.layer;
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(blur.transform, false);
            rt.SetAsFirstSibling();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            var img = go.AddComponent<UnityEngine.UI.Image>();
            img.color = new Color(0.04f, 0.045f, 0.05f, 0.88f);
            img.raycastTarget = false;
            Plugin.Logger.LogInfo($"[Menus] Blurred backing '{LaptopDiagnostics.Path(blur.transform)}' (tint {tint}) swapped for a plain dark one in VR");
        }
    }

    private void LateUpdate()
    {
        using var __t = ModTime.Measure("VRMenus");
        var running = OpenXRBootstrap.IsRunning;
        if (!running)
        {
            if (_wasRunning)
                RestoreAll();
            _wasRunning = false;
            return;
        }
        _wasRunning = true;

        var f7 = (GetAsyncKeyState(VK_F7) & 0x8000) != 0;
        if (f7 && !_f7Down)
            CycleDistance();
        _f7Down = f7;

        try
        {
            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + 0.25f;
                ConvertNewOverlays();
                try { PlainBackdrops(); }
                catch (Exception e) { Plugin.Logger.LogError($"[Menus] plain backdrops: {e.GetType().Name}: {e.Message}"); }
            }
            PlacePanels();
            FlattenMenus();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Menus] {e.GetType().Name}: {e.Message}");
            _nextScan = Time.unscaledTime + 5f;
        }
    }

    // Canvases already looked at and left alone (nested, world-space, or under a camera): skipped without asking the
    // game about each one four times a second. Forgotten every 10 s, in case the game switches one to overlay
    private readonly HashSet<IntPtr> _notOverlay = new();
    private float _forgetNotOverlayAt;

    private void ConvertNewOverlays()
    {
        if (Time.unscaledTime >= _forgetNotOverlayAt)
        {
            _forgetNotOverlayAt = Time.unscaledTime + 10f;
            _notOverlay.Clear();
        }
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<Canvas>(), FindObjectsSortMode.None))
        {
            if (o == null || _converted.ContainsKey(o.Pointer) || _notOverlay.Contains(o.Pointer))
                continue;
            var c = o.TryCast<Canvas>();
            if (c == null)
                continue;
            // Canvases parented under a camera belong to that view (e.g. the mission laptop's screen); leave them
            if (!c.isRootCanvas || c.renderMode != RenderMode.ScreenSpaceOverlay || c.GetComponentInParent<Camera>() != null)
            {
                _notOverlay.Add(c.Pointer);
                continue;
            }

            var rt = c.GetComponent<RectTransform>();
            var t = c.transform;
            var saved = new Saved
            {
                Canvas = c,
                Mode = c.renderMode,
                WorldCamera = c.worldCamera,
                LocalPos = t.localPosition,
                LocalRot = t.localRotation,
                LocalScale = t.localScale,
                SizeDelta = rt.sizeDelta,
            };
            _converted[c.Pointer] = saved;

            // Overlay canvases are sized to the screen; keep that pixel size on the panel
            c.renderMode = RenderMode.WorldSpace;
            rt.sizeDelta = saved.SizeDelta;

            Plugin.Logger.LogInfo($"[Menus] Floating '{c.name}' ({saved.SizeDelta.x:0}x{saved.SizeDelta.y:0}, sort={c.sortingOrder}) children: {ChildNames(t)}");
        }
    }

    private void PlacePanels()
    {
        var cam = Camera.main;
        if (cam == null)
            return;

        // LateUpdate runs with the game's own camera pose (head pose is only added at render time)
        var ct = cam.transform;
        // The view's yaw, not the game camera's: with hand aim the camera turns with the gun
        var bodyYaw = Quaternion.Euler(0f, HeadTracking.ViewYaw(ct.rotation), 0f);
        var forward = bodyYaw * Vector3.forward;
        var distance = Plugin.MenuDistance.Value;
        var width = Plugin.MenuWidth.Value * distance / WidthReferenceDistance;

        var tabOpen = TabMenuOpen();

        // With more than one bench page open, the one whose bench is nearest you
        var nearestBench = IntPtr.Zero;
        var benchPages = 0;
        var nearestD = float.MaxValue;
        foreach (var kv in _converted)
        {
            var c = kv.Value.Canvas;
            if (c == null || !c.isActiveAndEnabled || !c.name.StartsWith("WeaponModdingCanvas"))
                continue;
            benchPages++;
            Transform at = null;
            try { var wm = c.GetComponentInParent<WeaponModding>(true); at = wm != null ? wm.transform : null; } catch { }
            var d = at != null ? Vector3.Distance(ct.position, at.position) : float.MaxValue - 1f;
            if (d < nearestD)
            {
                nearestD = d;
                nearestBench = c.Pointer;
            }
        }
        s_currentBench = IntPtr.Zero;
        foreach (var kv in _converted)
        {
            var c = kv.Value.Canvas;
            if (c != null && c.isActiveAndEnabled && c.name.StartsWith("WeaponModdingCanvas") &&
                (benchPages < 2 || c.Pointer == nearestBench))
                s_currentBench = c.Pointer;
        }
        if (benchPages < 2)
            nearestBench = IntPtr.Zero;
        var dead = new List<IntPtr>();
        var neededLayers = 0;
        foreach (var kv in _converted)
        {
            var s = kv.Value;
            if (s.Canvas == null)
            {
                dead.Add(kv.Key);
                continue;
            }
            neededLayers |= 1 << s.Canvas.gameObject.layer;

            var t = s.Canvas.transform;
            var pixelsWide = Mathf.Max(1f, s.SizeDelta.x);
            // The game's infil map at mission start goes on the mission screen, as a page of it (the user's wish,
            // 2026-11-03), instead of floating in front
            if (MissionScreen.TryPagePose(s.Canvas, s.SizeDelta, out var pagePos, out var pageRot, out var pageWidth))
            {
                t.SetPositionAndRotation(pagePos, pageRot);
                t.localScale = Vector3.one * (pageWidth / pixelsWide);
                continue;
            }
            var scale = width * TabScreenScale(s.Canvas, tabOpen) / pixelsWide;
            // Higher sort orders sit slightly closer so stacked menus don't flicker through each other.
            // Rank rather than raw sortingOrder: sort=100 put a fade 20 cm in front of everything else
            var d = distance - SortRank(s.Canvas.sortingOrder) * 0.005f;

            // The bench page stays where it opened: drawn as part of the room (Menus.WeaponBenchOnTop off), following
            // the view made its boxes slide about behind the gun as the right stick turned (2026-11-03)
            if (s.Canvas.name.StartsWith("WeaponModdingCanvas"))
            {
                // Only the page of the bench you're at: the game leaves the last bench's page open, and pinned on the
                // wall behind this gun too, its empty list (in the game's brown) covered this page's list ("the dialog
                // boxes were blank" at every bench after the first, 2026-11-10). Any other bench's page is shrunk away
                if (s.Canvas.isActiveAndEnabled && nearestBench != IntPtr.Zero && s.Canvas.Pointer != nearestBench)
                {
                    if (!s.Hidden)
                    {
                        s.Hidden = true;
                        Plugin.Logger.LogInfo($"[Menus] Another bench's page was still open: hidden while you use the nearest one");
                    }
                    s.Pinned = s.PinnedToWall = false;
                    t.localScale = Vector3.zero;
                    continue;
                }
                s.Hidden = false;
                if (!s.Canvas.isActiveAndEnabled)
                    s.Pinned = s.PinnedToWall = false;
                else
                {
                    // On the wall behind the gun once the gun is known (it may take a moment after the page opens)
                    if (!Plugin.WeaponBenchOnTop.Value && !s.PinnedToWall && WeaponBench.TryWallPose(ct.position, out var wallPos, out var wallRot))
                    {
                        s.Pinned = s.PinnedToWall = true;
                        s.PinPos = wallPos;
                        s.PinRot = wallRot;
                        s.PinnedAt = Time.unscaledTime;
                        s.DepthLogged = false;
                        Plugin.Logger.LogInfo($"[Menus] Bench page on the wall behind the gun, {Vector3.Distance(ct.position, wallPos):0.00} m away");
                    }
                    if (!s.Pinned)
                    {
                        s.Pinned = true;
                        s.PinPos = ct.position + forward * d;
                        s.PinRot = bodyYaw;
                    }
                    // Same apparent size as a floating menu at that distance, times Menus.BenchPageSize: the writing is
                    // TextMeshPro (sharp at any size), so in the headset only a bigger page makes it easier to read
                    // (the user, 2026-11-10)
                    var pinScale = Plugin.MenuWidth.Value * Vector3.Distance(ct.position, s.PinPos) / WidthReferenceDistance / pixelsWide
                                   * Mathf.Clamp(Plugin.BenchPageSize.Value, 0.5f, 2.5f);
                    t.SetPositionAndRotation(s.PinPos, s.PinRot);
                    t.localScale = new Vector3(pinScale, pinScale, pinScale);
                    continue;
                }
            }

            t.SetPositionAndRotation(ct.position + forward * d, bodyYaw);
            t.localScale = new Vector3(scale, scale, scale);
            // worldCamera (the event camera) is owned by VRPointer while VR runs
        }
        foreach (var key in dead)
            _converted.Remove(key);

        // Overlay menus ignore the camera's culling mask, but as world-space panels they don't. Character
        // customisation narrows the mask to the character's layers, which hid its menu (and the dot) in VR.
        // The game sets the whole mask again when it changes views, so nothing needs putting back.
        if ((cam.cullingMask & neededLayers) != neededLayers)
        {
            // The game may reset the mask every frame; don't flood the log
            if (_maskLogs++ < 10)
                Plugin.Logger.LogInfo($"[Menus] Camera wasn't drawing the menu layers (mask 0x{cam.cullingMask:X}); adding 0x{neededLayers:X}");
            cam.cullingMask |= neededLayers;
        }
    }

    // The in-game Tab menu (health page, and the MAP page, which shows the infil map canvas over it) was small to
    // read. While it's open, the HUD canvas holding HealthUI and the map canvas (child 'Select Infil') are
    // Menus.TabScreenScale times wider; the rest of the time the HUD keeps its size so hints stay in view
    private HealthUI _healthUI;
    private Transform _healthInteractive;
    private float _nextHealthLook;

    private bool TabMenuOpen()
    {
        if ((_healthUI == null || _healthInteractive == null) && Time.unscaledTime >= _nextHealthLook)
        {
            _nextHealthLook = Time.unscaledTime + 2f;
            foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<HealthUI>(), FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var h = o.TryCast<HealthUI>();
                if (h == null)
                    continue;
                _healthUI = h;
                _healthInteractive = h.transform.Find("Interactive");
                break;
            }
        }
        if (_healthUI == null || !_healthUI.gameObject.activeInHierarchy)
            return false;
        return _healthInteractive != null
            ? _healthInteractive.gameObject.activeInHierarchy
            : Cursor.lockState != CursorLockMode.Locked || Cursor.visible;
    }

    private float TabScreenScale(Canvas c, bool tabOpen)
    {
        var mult = Mathf.Clamp(Plugin.TabScreenScale.Value, 0.5f, 3f);
        if (c.transform.Find("Select Infil") != null)
            return mult;
        if (tabOpen && _healthUI != null && _healthUI.transform.IsChildOf(c.transform))
            return mult;
        return 1f;
    }

    // The bench's magazine manager showed an empty list in VR: its entries (made by the game when the page opens) sat
    // 24,582 page units (~15 m) off the page (log, 2026-11-01). On a flat screen an overlay canvas ignores depth; as a
    // floating panel in VR, anything off its surface is somewhere else in the room, here behind the player. Once a
    // second, any part of a floating menu that's more than a unit off its surface is put back onto it
    private float _nextFlatten;
    private int _flattenLogs;

    private void FlattenMenus()
    {
        if (Time.unscaledTime < _nextFlatten)
            return;
        _nextFlatten = Time.unscaledTime + 1f;
        foreach (var s in _converted.Values)
        {
            var c = s.Canvas;
            if (c == null || !c.isActiveAndEnabled)
                continue;
            var page = c.transform;
            // Parents come before their children, so a moved parent takes its children with it
            foreach (var rt in c.GetComponentsInChildren<RectTransform>(false))
            {
                if (rt == null || rt.Pointer == page.Pointer)
                    continue;
                var local = page.InverseTransformPoint(rt.position);
                // Only parts well off the page: the bench's 'Collapse Toggle' sits 5 units off, the game puts it back
                // each time, and correcting it every second made it flicker. The broken ones were thousands off
                if (Mathf.Abs(local.z) <= 50f)
                    continue;
                rt.position = page.TransformPoint(new Vector3(local.x, local.y, 0f));
                if (_flattenLogs++ < 20)
                    Plugin.Logger.LogInfo($"[Menus] '{c.name}': '{rt.name}' was {local.z:0} units off the page; put back on it");
            }
        }
    }

    private int SortRank(int sortingOrder)
    {
        var rank = 0;
        var seen = new HashSet<int>();
        foreach (var s in _converted.Values)
        {
            if (s.Canvas == null)
                continue;
            var order = s.Canvas.sortingOrder;
            if (order < sortingOrder && seen.Add(order))
                rank++;
        }
        return rank;
    }

    private static string ChildNames(Transform t)
    {
        var names = new List<string>();
        for (var i = 0; i < t.childCount && names.Count < 12; i++)
        {
            var child = t.GetChild(i);
            names.Add(child.gameObject.activeSelf ? child.name : $"({child.name})");
        }
        return names.Count == 0 ? "none" : string.Join(", ", names);
    }

    private void RestoreAll()
    {
        foreach (var s in _converted.Values)
        {
            if (s.Canvas == null)
                continue;
            try
            {
                s.Canvas.renderMode = s.Mode;
                s.Canvas.worldCamera = s.WorldCamera;
                var t = s.Canvas.transform;
                t.localPosition = s.LocalPos;
                t.localRotation = s.LocalRot;
                t.localScale = s.LocalScale;
                s.Canvas.GetComponent<RectTransform>().sizeDelta = s.SizeDelta;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"[Menus] Could not restore a canvas: {e.Message}");
            }
        }
        Plugin.Logger.LogInfo($"[Menus] Restored {_converted.Count} menus to normal");
        _converted.Clear();
    }
}
