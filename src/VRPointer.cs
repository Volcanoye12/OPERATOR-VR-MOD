using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.UI;

namespace OperatorVR;

/// <summary>
/// Lets the normal mouse drive whichever menu you're using in VR. The target canvas (a floating
/// menu, or a mission laptop screen) gets a hidden orthographic camera that frames it exactly, set as
/// its event camera, so the mouse's position on the monitor maps 1:1 onto the panel. A dot is drawn
/// on the panel where the mouse points.
/// </summary>
public class VRPointer : MonoBehaviour
{
    public VRPointer(IntPtr ptr) : base(ptr) { }

    private const float LaptopRange = 2.5f;
    // The bench's weapon sits about 0.7 m from the eye; the arrow is drawn nearer than that there
    private const float BenchArrowDistance = 0.45f;

    private Camera _uiCam;
    private Canvas _cursorCanvas;
    private RectTransform _cursorDot;

    private Canvas _target;
    private static VRPointer s_instance;

    private void Awake() => s_instance = this;
    private IntPtr _sizedFor, _sizeLogged;
    private float _arrowScale = 1f;
    // Original event cameras of canvases we took over, keyed by native pointer
    private readonly Dictionary<IntPtr, (Canvas canvas, Camera cam)> _origCams = new();
    private float _nextLaptopScan;
    private readonly List<Canvas> _laptops = new();
    private bool _wasRunning;
    // Click catchers of other floating menus that we switched off while the mouse drives another one
    private readonly Dictionary<IntPtr, GraphicRaycaster> _raycastersOff = new();
    private int _raycasterLogs;

    private void LateUpdate()
    {
        using var __t = ModTime.Measure("VRPointer");
        var running = OpenXRBootstrap.IsRunning;
        if (!running)
        {
            if (_wasRunning)
                ReleaseAll();
            _wasRunning = false;
            return;
        }
        _wasRunning = true;

        try
        {
            EnsureObjects();

            var target = PickTarget();
            if (target == null || !SameCanvas(target, _target))
            {
                if (target != null)
                {
                    Plugin.Logger.LogInfo($"[Pointer] Mouse now drives '{target.name}' (sort={target.sortingOrder}, because of '{FirstSelectable(target)}')");
                    // The hidden Windows cursor is often left at a window edge; start laptops in the middle
                    if (target.name.Contains("DreamOS"))
                        CenterMouse();
                }
                else if (_target != null)
                    Plugin.Logger.LogInfo($"[Pointer] Mouse no longer drives '{_target.name}'");
                _target = target;
            }

            if (_target == null)
            {
                RestoreRaycasters();
                _cursorCanvas.gameObject.SetActive(false);
                return;
            }

            FrameTarget(_target);
            PlaceCursor(_target);
            MagManagerBack(_target);
            if (Plugin.Diagnostics.Value) LogBenchDrag(_target);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Pointer] {e.GetType().Name}: {e.Message}");
        }
    }

    // The magazine manager's Back button (2026-11-11, the user: "when you press back, clicking on back isn't doing
    // anything"; the log had a click on 'refill mags button' but never one on Back). A press with the arrow on Back now
    // closes the manager itself if the game hasn't a moment later. Each press there is logged with what's under the arrow
    private bool _mmLmbWas;
    private float _mmCloseAt = -1f;
    private Canvas _mmCanvas;
    private int _mmLogs;

    private void MagManagerBack(Canvas target)
    {
        if (_mmCloseAt > 0f && Time.unscaledTime >= _mmCloseAt)
        {
            _mmCloseAt = -1f;
            try
            {
                if (_mmCanvas != null && _mmCanvas.gameObject.activeInHierarchy)
                {
                    var wm = _mmCanvas.GetComponentInParent<WeaponModding>();
                    if (wm != null)
                    {
                        wm.CloseMagManager();
                        Plugin.Logger.LogInfo("[Pointer] Mag manager Back: the game didn't close it, closed it directly");
                    }
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"[Pointer] Mag manager Back: {e.GetType().Name}: {e.Message}"); }
        }
        if (target == null || !target.name.Contains("MagManager"))
        {
            _mmLmbWas = false;
            return;
        }
        var lmb = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        if (lmb && !_mmLmbWas)
        {
            TryGetMouseViewport(out var nx, out var ny);
            var under = GraphicNameUnderMouse(target, nx, ny);
            if (_mmLogs++ < 30)
                Plugin.Logger.LogInfo($"[Pointer] Mag manager press at ({nx:0.00}, {ny:0.00}): under the arrow '{under}'");
            if (under.IndexOf("close", StringComparison.OrdinalIgnoreCase) >= 0 ||
                under.IndexOf("back", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _mmCanvas = target;
                _mmCloseAt = Time.unscaledTime + 0.3f;
            }
        }
        _mmLmbWas = lmb;
    }

    // Temporary (2026-11-01): turning the weapon on the bench stopped working again. The game's DragMouse turns it while
    // the left button is held, if, before the press, the game was told the arrow wasn't over a menu (its canDrag), using
    // the mouse's movement. Each press on the bench page logs what's under the arrow, canDrag, and on release how far
    // the weapon turned
    private bool _benchLmbWas;
    private float _benchYawAtPress;
    private int _benchLogs;
    private DragMouse _drag;

    private void LogBenchDrag(Canvas target)
    {
        if (!target.name.Contains("WeaponModding") || _benchLogs >= 20)
            return;
        var lmb = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        if (lmb == _benchLmbWas)
            return;
        _benchLmbWas = lmb;
        if (_drag == null)
            foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<DragMouse>(), FindObjectsSortMode.None))
            {
                _drag = o.TryCast<DragMouse>();
                if (_drag != null && _drag.isActiveAndEnabled)
                    break;
            }
        var yaw = _drag != null ? _drag._yRotation : float.NaN;
        _benchLogs++;
        if (lmb)
        {
            _benchYawAtPress = yaw;
            TryGetMouseViewport(out var nx, out var ny);
            Plugin.Logger.LogInfo($"[Pointer] Bench press at ({nx:0.00}, {ny:0.00}): under the arrow '{GraphicNameUnderMouse(target, nx, ny)}', " +
                                  $"game told over a menu {s_overResult}; DragMouse {(_drag == null ? "not found" : $"'{_drag.name}' enabled {_drag.isActiveAndEnabled}, canDrag {_drag.canDrag}, dragging off {_drag.callingDragOff}")}");
        }
        else
            Plugin.Logger.LogInfo($"[Pointer] Bench release: weapon turned {yaw - _benchYawAtPress:0.0}° (yaw now {yaw:0.0})");
    }

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private static string GraphicNameUnderMouse(Canvas c, float nx, float ny)
    {
        var rt = c.GetComponent<RectTransform>();
        var r = rt.rect;
        var world = rt.TransformPoint(new Vector3(r.xMin + nx * r.width, r.yMin + ny * r.height, 0f));
        string last = "nothing";
        foreach (var g in c.GetComponentsInChildren<Graphic>(false))
        {
            if (g == null || !g.enabled || !g.raycastTarget || g.canvasRenderer.cull || g.color.a < 0.01f)
                continue;
            var grt = g.rectTransform;
            var local = grt.InverseTransformPoint(world);
            if (grt.rect.Contains(new Vector2(local.x, local.y)) && GroupBlocks(g.transform))
                last = g.name;  // later in the hierarchy draws on top
        }
        return last;
    }

    private void EnsureObjects()
    {
        if (_uiCam == null)
        {
            var go = new GameObject("OperatorVR UI Camera");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _uiCam = go.AddComponent<Camera>();
            _uiCam.enabled = false; // never renders; only used for mouse raycasts
            _uiCam.orthographic = true;
            _uiCam.nearClipPlane = 0.01f;
            _uiCam.farClipPlane = 2f;
        }

        if (_cursorCanvas == null)
        {
            var go = new GameObject("OperatorVR Cursor");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _cursorCanvas = go.AddComponent<Canvas>();
            _cursorCanvas.renderMode = RenderMode.WorldSpace;
            _cursorCanvas.sortingOrder = 32000;
            go.GetComponent<RectTransform>().sizeDelta = new Vector2(ArrowW, ArrowH);

            var arrow = new GameObject("Arrow");
            arrow.transform.SetParent(go.transform, false);
            var img = arrow.AddComponent<Image>();
            img.sprite = MakeArrowSprite();
            img.raycastTarget = false;
            _cursorDot = arrow.GetComponent<RectTransform>();
            _cursorDot.sizeDelta = new Vector2(ArrowW, ArrowH);
            // Tip at the canvas origin, so the tip sits exactly where the mouse points
            _cursorDot.pivot = new Vector2(ArrowMargin / (float)ArrowW, 1f - ArrowMargin / (float)ArrowH);
            _cursorDot.anchoredPosition = Vector2.zero;
        }
    }

    // Arrow texture size in pixels (also the cursor canvas's size in its own units)
    private const int ArrowW = 50, ArrowH = 80, ArrowMargin = 2;

    // Classic pointer outline in a 12 x 19 grid, y measured down from the tip
    private static readonly Vector2[] ArrowShape =
    {
        new(0f, 0f), new(0f, 16f), new(4f, 12.5f), new(6.8f, 18.5f), new(9f, 17.5f), new(6.3f, 11.6f), new(11f, 11.6f),
    };

    /// <summary>Yellow arrow with a black outline, drawn into a texture once.</summary>
    private static Sprite MakeArrowSprite()
    {
        var inside = new bool[ArrowW, ArrowH];
        for (var x = 0; x < ArrowW; x++)
            for (var y = 0; y < ArrowH; y++)
            {
                // Texture rows go up from the bottom; the shape is measured down from the top
                // Shape is inset by the outline width so the outline isn't cut off at the edges
                var gx = (x + 0.5f - ArrowMargin) / 4f;
                var gy = (ArrowH - 1 - y + 0.5f - ArrowMargin) / 4f;
                inside[x, y] = InPolygon(gx, gy, ArrowShape);
            }

        var yellow = new Color32(255, 217, 51, 255);
        var black = new Color32(0, 0, 0, 255);
        var clear = new Color32(0, 0, 0, 0);
        var tex = new Texture2D(ArrowW, ArrowH, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        for (var x = 0; x < ArrowW; x++)
            for (var y = 0; y < ArrowH; y++)
            {
                // Fill = shrunk shape, outline = grown shape minus the fill
                var fill = AllWithin(inside, x, y, 2);
                var outline = AnyWithin(inside, x, y, 2);
                tex.SetPixel(x, y, fill ? yellow : outline ? black : clear);
            }
        tex.Apply();
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;

        var sprite = Sprite.Create(tex, new Rect(0f, 0f, ArrowW, ArrowH), new Vector2(0f, 1f), 100f);
        sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sprite;
    }

    private static bool InPolygon(float px, float py, Vector2[] poly)
    {
        var inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            var a = poly[i];
            var b = poly[j];
            if ((a.y > py) != (b.y > py) && px < (b.x - a.x) * (py - a.y) / (b.y - a.y) + a.x)
                inside = !inside;
        }
        return inside;
    }

    private static bool AllWithin(bool[,] m, int x, int y, int r)
    {
        for (var dx = -r; dx <= r; dx++)
            for (var dy = -r; dy <= r; dy++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= ArrowW || ny >= ArrowH || !m[nx, ny])
                    return false;
            }
        return true;
    }

    private static bool AnyWithin(bool[,] m, int x, int y, int r)
    {
        for (var dx = -r; dx <= r; dx++)
            for (var dy = -r; dy <= r; dy++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx >= 0 && ny >= 0 && nx < ArrowW && ny < ArrowH && m[nx, ny])
                    return true;
            }
        return false;
    }

    private bool _cursorWasFree = true;

    private Canvas PickTarget()
    {
        // 1) Topmost floating menu that has something to click, while the game has the mouse cursor free (menus).
        // In play the game locks and hides it; without this check the infil map's markers (still clickable after the
        // infil was picked) kept the pointer on that canvas all mission, which paused hand aim and turning
        Canvas best = null;
        var cursorFree = Cursor.lockState != CursorLockMode.Locked || Cursor.visible;
        if (cursorFree != _cursorWasFree)
        {
            _cursorWasFree = cursorFree;
            Plugin.Logger.LogInfo($"[Pointer] Game cursor {(cursorFree ? "free" : "locked")} ({Cursor.lockState}, visible {Cursor.visible})");
        }
        // Floating menus all sit in the same place, so with several clickable ones (the Tab menu's MAP page opens the
        // infil map canvas, sort 7, over the HealthUI tabs, sort 1) the topmost one with a button under the arrow wins;
        // otherwise the one already driven stays, else the topmost. Always taking the topmost left the HEALTH tab
        // unclickable once MAP was open
        Canvas under = null, current = null;
        var haveMouse = TryGetMouseViewport(out var nx, out var ny);
        foreach (var c in cursorFree ? VRMenus.FloatingCanvases() : Array.Empty<Canvas>())
        {
            if (!c.enabled || !c.gameObject.activeInHierarchy || !HasActiveSelectable(c))
                continue;
            if (best == null || c.sortingOrder > best.sortingOrder)
                best = c;
            if (SameCanvas(c, _target))
                current = c;
            if (haveMouse && (under == null || c.sortingOrder > under.sortingOrder) && SelectableUnderMouse(c, nx, ny))
                under = c;
        }
        // While the left button is held (dragging a bandage onto a limb on the health page) the screen being dragged
        // on keeps the mouse, so the drag isn't cut off by passing over another screen's button
        if (current != null && (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0)
            return current;
        if (under != null)
            return under;
        if (current != null)
            return current;
        if (best != null)
            return best;

        // 2) Nearest mission laptop screen within reach, once you're using it (AccessLaptop). Just walking up to one
        // made it the target, and with A as the menu click (and hand aim paused for the pointer) the laptop could
        // no longer be opened and the gun froze
        if (LaptopClickLog.CurrentLaptop == null)
            return null;
        if (Time.unscaledTime >= _nextLaptopScan)
        {
            _nextLaptopScan = Time.unscaledTime + 1f;
            _laptops.Clear();
            foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<Canvas>(), FindObjectsSortMode.None))
            {
                var c = o.TryCast<Canvas>();
                if (c != null && c.isRootCanvas && c.renderMode == RenderMode.WorldSpace && c.name.Contains("DreamOS"))
                    _laptops.Add(c);
            }
        }

        var cam = Camera.main;
        if (cam == null)
            return null;
        Canvas nearest = null;
        var nearestDist = LaptopRange;
        foreach (var c in _laptops)
        {
            if (c == null || !c.enabled || !c.gameObject.activeInHierarchy)
                continue;
            var d = Vector3.Distance(cam.transform.position, c.transform.position);
            if (d < nearestDist)
            {
                nearest = c;
                nearestDist = d;
            }
        }
        return nearest;
    }

    private static bool HasActiveChild(Transform t, string name)
    {
        for (var i = 0; i < t.childCount; i++)
        {
            var child = t.GetChild(i);
            if (child.name == name && child.gameObject.activeInHierarchy)
                return true;
        }
        return false;
    }

    private static bool HasActiveSelectable(Canvas c)
    {
        // The ODG Ops Menu (ODG Ops modpack) draws and hit-tests its own buttons from the mouse's screen position, so
        // it has no Selectables but is clickable all over
        if (c.name == VRMenus.OdgOpsMenu)
            return true;
        // Likewise ODG Quartermaster's Gunsmith on the bench page: it hides the game's buttons and draws its own
        // ('GS_Root'), mapping the mouse through the page's event camera (OdgUi.Skin.Bind), so the page must keep ours
        if (HasActiveChild(c.transform, "GS_Root"))
            return true;
        var selectables = c.GetComponentsInChildren<Selectable>(false);
        foreach (var s in selectables)
            if (s.IsInteractable() && NotHiddenByGroup(s))
                return true;
        return false;
    }

    /// <summary>
    /// Whether a clickable thing on this canvas is under the mouse, which maps onto the whole canvas
    /// (the same mapping FrameTarget gives the raycast camera).
    /// </summary>
    private static bool SelectableUnderMouse(Canvas c, float nx, float ny)
    {
        var rt = c.GetComponent<RectTransform>();
        var r = rt.rect;
        var world = rt.TransformPoint(new Vector3(r.xMin + nx * r.width, r.yMin + ny * r.height, 0f));
        foreach (var s in c.GetComponentsInChildren<Selectable>(false))
        {
            if (!s.IsInteractable() || !NotHiddenByGroup(s))
                continue;
            var srt = s.transform.TryCast<RectTransform>();
            if (srt == null)
                continue;
            var local = srt.InverseTransformPoint(world);
            if (srt.rect.Contains(new Vector2(local.x, local.y)))
                return true;
        }
        return false;
    }

    // ---- "Is the mouse over a menu?" for the game (EventSystem.IsPointerOverGameObject) ----
    // The bench's weapon (and the customisation character) rotates on a mouse drag only while that returns false
    // (DragMouse.Update). In VR it never did: with every menu a floating panel, it reported the pointer over UI even
    // over empty space, so the yellow cursor couldn't rotate the weapon. While the mouse drives a floating menu, the
    // answer is ours: over UI only if a visible raycast target of that menu is under the arrow
    private static int s_overFrame = -1;
    private static bool s_overResult;
    private static bool s_overLogged;

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    internal static void ApplyPointerOverPatch(HarmonyLib.Harmony harmony)
    {
        try
        {
            var m = HarmonyLib.AccessTools.Method(typeof(UnityEngine.EventSystems.EventSystem),
                nameof(UnityEngine.EventSystems.EventSystem.IsPointerOverGameObject), Type.EmptyTypes);
            harmony.Patch(m, postfix: new HarmonyLib.HarmonyMethod(typeof(VRPointer), nameof(AfterIsPointerOver)));
            Plugin.Logger.LogInfo("[Pointer] hooked EventSystem.IsPointerOverGameObject (drag to rotate on the bench)");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Pointer] IsPointerOverGameObject hook failed: {e.GetType().Name}: {e.Message}");
        }
    }

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private static void AfterIsPointerOver(ref bool __result)
    {
        try
        {
            var target = Target;
            if (!OpenXRBootstrap.IsRunning || target == null || target.name.Contains("DreamOS"))
                return;
            if (Time.frameCount != s_overFrame)
            {
                s_overFrame = Time.frameCount;
                s_overResult = TryGetMouseViewport(out var nx, out var ny) && GraphicUnderMouse(target, nx, ny);
            }
            if (__result && !s_overResult && !s_overLogged)
            {
                s_overLogged = true;
                Plugin.Logger.LogInfo($"[Pointer] Arrow over empty space on '{target.name}': the game now sees it as not over a menu (drag works)");
            }
            __result = s_overResult;
        }
        catch { }
    }

    /// <summary>A visible, raycast-target graphic of this canvas is under the mouse (mapped as FrameTarget does).</summary>
    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private static bool GraphicUnderMouse(Canvas c, float nx, float ny)
    {
        var rt = c.GetComponent<RectTransform>();
        var r = rt.rect;
        var world = rt.TransformPoint(new Vector3(r.xMin + nx * r.width, r.yMin + ny * r.height, 0f));
        foreach (var g in c.GetComponentsInChildren<Graphic>(false))
        {
            if (g == null || !g.enabled || !g.raycastTarget || g.canvasRenderer.cull || g.color.a < 0.01f)
                continue;
            var grt = g.rectTransform;
            var local = grt.InverseTransformPoint(world);
            if (!grt.rect.Contains(new Vector2(local.x, local.y)))
                continue;
            if (GroupBlocks(g.transform))
                return true;
        }
        return false;
    }

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private static bool GroupBlocks(Transform t)
    {
        foreach (var grp in t.GetComponentsInParent<CanvasGroup>())
        {
            if (!grp.blocksRaycasts || grp.alpha < 0.01f)
                return false;
            if (grp.ignoreParentGroups)
                break;
        }
        return true;
    }

    /// <summary>For the log: which clickable thing made a canvas count as a menu.</summary>
    private static string FirstSelectable(Canvas c)
    {
        foreach (var s in c.GetComponentsInChildren<Selectable>(false))
            if (s.IsInteractable() && NotHiddenByGroup(s))
            {
                var t = s.transform;
                var path = t.name;
                while (t.parent != null && t.parent != c.transform)
                {
                    t = t.parent;
                    path = t.name + "/" + path;
                }
                return path;
            }
        return "-";
    }

    /// <summary>Menus the game hides with a transparent / non-blocking CanvasGroup still count as active objects.</summary>
    private static bool NotHiddenByGroup(Selectable s)
    {
        foreach (var g in s.GetComponentsInParent<CanvasGroup>())
        {
            if (g.alpha < 0.01f || !g.blocksRaycasts)
                return false;
            if (g.ignoreParentGroups)
                break;
        }
        return true;
    }

    /// <summary>Point the hidden camera straight at the canvas so the whole screen maps onto it.</summary>
    private void FrameTarget(Canvas target)
    {
        var rt = target.GetComponent<RectTransform>();
        var t = target.transform;
        var size = rt.sizeDelta;
        var scale = t.lossyScale;
        var worldW = size.x * scale.x;
        var worldH = size.y * scale.y;
        var center = rt.TransformPoint(rt.rect.center);

        // A world-space canvas faces -forward, so view it from behind its forward vector
        _uiCam.transform.SetPositionAndRotation(center - t.forward * 0.5f, t.rotation);
        _uiCam.orthographicSize = worldH * 0.5f;
        _uiCam.aspect = worldW / Mathf.Max(0.0001f, worldH);

        if (!_origCams.ContainsKey(target.Pointer))
            _origCams[target.Pointer] = (target, target.worldCamera);
        target.worldCamera = _uiCam;

        // Hand any canvas we're no longer targeting back its own camera
        var release = new List<IntPtr>();
        foreach (var kv in _origCams)
            if (kv.Key != target.Pointer)
                release.Add(kv.Key);
        foreach (var key in release)
            Release(key);

        OnlyTargetTakesClicks(target);
    }

    // In the main menu you couldn't confirm leaving the lobby (2026-11-01): the warning's YES never got the click. The
    // other floating menus keep their click catchers, and with no event camera of their own they use the game camera,
    // whose higher depth puts their hits ahead of the driven menu's. The main menu's 'Flat Canvas' (lobby page, full
    // size, sitting behind) took the clicks in the middle of the view. While the mouse drives one menu, only that one
    // takes clicks
    private void OnlyTargetTakesClicks(Canvas target)
    {
        foreach (var c in VRMenus.FloatingCanvases())
        {
            if (SameCanvas(c, target))
                continue;
            var gr = c.GetComponent<GraphicRaycaster>();
            if (gr == null || !gr.enabled)
                continue;
            gr.enabled = false;
            _raycastersOff[c.Pointer] = gr;
            if (_raycasterLogs++ < 20)
                Plugin.Logger.LogInfo($"[Pointer] '{c.name}' stops taking clicks while the mouse drives '{target.name}'");
        }
        if (_raycastersOff.TryGetValue(target.Pointer, out var own))
        {
            _raycastersOff.Remove(target.Pointer);
            if (own != null)
                own.enabled = true;
        }
    }

    private void RestoreRaycasters()
    {
        foreach (var gr in _raycastersOff.Values)
            if (gr != null)
                gr.enabled = true;
        _raycastersOff.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hWnd, ref POINT p);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    private const int VK_LBUTTON = 0x01;

    /// <summary>The canvas the mouse currently drives (floating menu or laptop screen), or null.</summary>
    internal static Canvas Target => s_instance == null ? null : s_instance._target;

    /// <summary>Whether the arrow is over a visible part of the menu it drives now (false over empty space).</summary>
    internal static bool ArrowOverMenu()
    {
        var t = Target;
        return t != null && TryGetMouseViewport(out var nx, out var ny) && GraphicUnderMouse(t, nx, ny);
    }

    /// <summary>Puts the Windows cursor at a point given as 0..1 across the game window (bottom-left origin), the reverse of TryGetMouseViewport.</summary>
    internal static void MoveMouseToViewport(float nx, float ny)
    {
        var hwnd = GetActiveWindow();
        if (hwnd == IntPtr.Zero || !GetClientRect(hwnd, out var r))
            return;
        var p = new POINT
        {
            X = Mathf.RoundToInt(Mathf.Clamp01(nx) * (r.Right - r.Left)),
            Y = Mathf.RoundToInt((1f - Mathf.Clamp01(ny)) * (r.Bottom - r.Top)),
        };
        if (!ClientToScreen(hwnd, ref p))
            return;
        // While the click is held (dragging), move there as a real mouse movement first: placing the cursor alone
        // gives the game no mouse movement, and its drags read that (the bench's weapon turn, and the health screen's
        // medication couldn't be dragged onto the body, 2026-11-04). Then place it exactly (pointer speed settings
        // can scale the movement)
        // (Not on the weapon bench: the controller swing turns the weapon there, and both would turn it twice)
        if (!WeaponBench.Open && (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0 && GetCursorPos(out var now))
        {
            // Scaled up (Menus.LaserDragSpeed): at 1 the medication trailed behind the laser ("a bit slow", 2026-11-04)
            var gain = Mathf.Max(0.1f, Plugin.LaserDragSpeed.Value);
            var dx = Mathf.RoundToInt((p.X - now.X) * gain);
            var dy = Mathf.RoundToInt((p.Y - now.Y) * gain);
            if (dx != 0 || dy != 0)
            {
                TouchKeys.MoveMouse(dx, dy);
                if (!s_dragMoveLogged)
                {
                    s_dragMoveLogged = true;
                    Plugin.Logger.LogInfo("[Pointer] Dragging with the laser: sending real mouse movement while the click is held");
                }
            }
        }
        SetCursorPos(p.X, p.Y);
    }

    private static bool s_dragMoveLogged;

    internal static void CenterMouse()
    {
        var hwnd = GetActiveWindow();
        if (hwnd == IntPtr.Zero || !GetClientRect(hwnd, out var r))
            return;
        var p = new POINT { X = (r.Right - r.Left) / 2, Y = (r.Bottom - r.Top) / 2 };
        if (ClientToScreen(hwnd, ref p) && SetCursorPos(p.X, p.Y))
            Plugin.Logger.LogInfo($"[Pointer] Mouse moved to the middle of the window ({p.X}, {p.Y})");
    }

    /// <summary>
    /// Mouse position as 0..1 across the game window (bottom-left origin, like Unity). Read from Windows
    /// because the game runs on the new Input System, where the legacy Input.mousePosition may throw.
    /// </summary>
    private static bool TryGetMouseViewport(out float nx, out float ny)
    {
        nx = ny = 0.5f;
        var hwnd = GetActiveWindow();
        if (hwnd == IntPtr.Zero || !GetCursorPos(out var p) || !ScreenToClient(hwnd, ref p) || !GetClientRect(hwnd, out var r))
            return false;
        var w = Math.Max(1, r.Right - r.Left);
        var h = Math.Max(1, r.Bottom - r.Top);
        nx = Mathf.Clamp01(p.X / (float)w);
        ny = Mathf.Clamp01(1f - p.Y / (float)h);
        return true;
    }

    private void PlaceCursor(Canvas target)
    {
        if (!TryGetMouseViewport(out var nx, out var ny))
        {
            _cursorCanvas.gameObject.SetActive(false);
            return;
        }

        // Same mapping the raycast uses: viewport point on the ortho camera, on the canvas plane
        var p = _uiCam.ViewportToWorldPoint(new Vector3(nx, ny, 0.5f));
        var t = target.transform;
        var panelWidth = target.GetComponent<RectTransform>().sizeDelta.x * t.lossyScale.x;
        // Arrow height: 3% of the panel's width, 1-5 cm (a laptop screen is only ~40 cm wide).
        // Only the tip clicks, so it can be small without losing accuracy
        var arrowHeight = Mathf.Clamp(panelWidth * 0.03f, 0.01f, 0.05f);
        if (_sizedFor != target.Pointer)
        {
            _sizedFor = target.Pointer;
            // Half size on the health screen: the arrow covered the writing on the injured part (2026-11-03)
            _arrowScale = 1f;
            try
            {
                var health = target.GetComponentInChildren<HealthUI>(false);
                if (health != null && health.isActiveAndEnabled)
                    _arrowScale = Plugin.HealthArrowSize.Value;
            }
            catch { }
        }
        arrowHeight *= _arrowScale;
        if (_sizeLogged != target.Pointer)
        {
            _sizeLogged = target.Pointer;
            Plugin.Logger.LogInfo($"[Pointer] '{target.name}' is {panelWidth:0.00} m wide; arrow {arrowHeight * 100f:0.0} cm tall");
        }

        _cursorCanvas.gameObject.SetActive(true);
        // Same layer as the panel: the laptop's sit-down view culls layers the dot was on
        var layer = target.gameObject.layer;
        if (_cursorCanvas.gameObject.layer != layer)
        {
            SetLayer(_cursorCanvas.transform, layer);
            var cam = Camera.main;
            Plugin.Logger.LogInfo($"[Pointer] Dot on layer {layer} ({LayerMask.LayerToName(layer)}); " +
                                  $"main camera draws it: {(cam != null && (cam.cullingMask & (1 << layer)) != 0)}");
        }
        var ct = _cursorCanvas.transform;
        // In front of the panel, far enough to clear a laptop's screen glass
        var at = p - t.forward * 0.008f;
        var s = arrowHeight / ArrowH;
        // On the weapon bench the weapon is held out between you and the page, and the arrow on the page looked to be
        // behind it (2026-11-01: "the yellow cursor was behind the weapon"). There it's drawn nearer, on the same line
        // from the eye and scaled to look the same size, so it's in front of the weapon
        // Only over empty page (where you grab to turn the weapon): over a menu it stays on the page, so you can see
        // what it's on. Brought nearer everywhere, it floated in front of the lists and nothing could be picked out
        // (2026-11-02)
        if (target.name.Contains("WeaponModding") && Camera.main != null && !GraphicUnderMouse(target, nx, ny))
        {
            // From where the eyes really are (the head pose), not the game camera: in VR the head is off to one side
            // of it, and an arrow brought nearer along the game camera's line showed somewhere other than where it
            // pointed, so menu items couldn't be clicked (2026-11-02)
            var eye = Camera.main.transform.position;
            if (HeadTracking.TryApplyHeadPoseToGameCamera(out var savedPos, out var savedRot))
            {
                eye = Camera.main.transform.position;
                HeadTracking.RestoreGameCameraPose(savedPos, savedRot);
            }
            var dist = Vector3.Distance(eye, at);
            if (dist > BenchArrowDistance)
            {
                var k = BenchArrowDistance / dist;
                at = eye + (at - eye) * k;
                s *= k;
            }
        }
        ct.SetPositionAndRotation(at, t.rotation);
        ct.localScale = new Vector3(s, s, s);
    }

    private static void SetLayer(Transform t, int layer)
    {
        t.gameObject.layer = layer;
        for (var i = 0; i < t.childCount; i++)
            SetLayer(t.GetChild(i), layer);
    }

    private void Release(IntPtr key)
    {
        if (!_origCams.TryGetValue(key, out var entry))
            return;
        _origCams.Remove(key);
        if (entry.canvas != null)
            entry.canvas.worldCamera = entry.cam;
    }

    private void ReleaseAll()
    {
        foreach (var key in new List<IntPtr>(_origCams.Keys))
            Release(key);
        RestoreRaycasters();
        _target = null;
        if (_cursorCanvas != null)
            _cursorCanvas.gameObject.SetActive(false);
    }

    private static bool SameCanvas(Canvas a, Canvas b) =>
        a != null && b != null && a.Pointer == b.Pointer;
}
