using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using RaycastResults = Il2CppSystem.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>;

namespace OperatorVR;

/// <summary>
/// Temporary: works out why mission-laptop clicks don't land in VR. Logs the laptop canvases and
/// LaptopProjector setup once VR is on, then on every left click near a laptop logs what the
/// EventSystem's raycast actually hits at the mouse position.
/// </summary>
public class LaptopDiagnostics : MonoBehaviour
{
    public LaptopDiagnostics(IntPtr ptr) : base(ptr) { }

    private const int VK_LBUTTON = 0x01;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private bool _loggedSetup;
    private bool _mouseDown;
    private int _clicksLogged;

    private void Update()
    {
        using var __t = ModTime.Measure("LaptopDiagnostics");
        if (!OpenXRBootstrap.IsRunning)
        {
            _loggedSetup = false;
            return;
        }

        try
        {
            if (!_loggedSetup)
            {
                _loggedSetup = true;
                LogSetup();
            }

            var down = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
            if (down && !_mouseDown && _clicksLogged < 20 && NearLaptop())
            {
                _clicksLogged++;
                LogClick();
            }
            _mouseDown = down;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Laptop] {e.GetType().Name}: {e.Message}");
            _loggedSetup = true;
        }
    }

    private static bool NearLaptop()
    {
        var cam = Camera.main;
        if (cam == null)
            return false;
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<LaptopProjector>(), FindObjectsSortMode.None))
        {
            var lp = o.TryCast<LaptopProjector>();
            if (lp != null && Vector3.Distance(cam.transform.position, lp.transform.position) < 4f)
                return true;
        }
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<Canvas>(), FindObjectsSortMode.None))
        {
            var c = o.TryCast<Canvas>();
            if (c != null && c.name.Contains("DreamOS") && Vector3.Distance(cam.transform.position, c.transform.position) < 4f)
                return true;
        }
        return false;
    }

    private static void LogSetup()
    {
        var log = Plugin.Logger;
        var cam = Camera.main;
        log.LogInfo("===== [Laptop] setup =====");

        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<LaptopProjector>(), FindObjectsSortMode.None))
        {
            var lp = o.TryCast<LaptopProjector>();
            if (lp == null)
                continue;
            var src = lp.sourceCanvas;
            var ui = lp.uiCam;
            log.LogInfo($"[Laptop] projector '{Path(lp.transform)}' enabled={lp.enabled} sourceCanvas='{(src == null ? "-" : Path(src.transform))}' " +
                        $"uiCam='{(ui == null ? "-" : ui.name)}' uiCamEnabled={(ui != null && ui.enabled)} uiCamTarget={(ui == null || ui.targetTexture == null ? "screen" : ui.targetTexture.name)}");
        }

        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<Canvas>(), FindObjectsSortMode.None))
        {
            var c = o.TryCast<Canvas>();
            if (c == null || !c.name.Contains("DreamOS"))
                continue;
            var rt = c.GetComponent<RectTransform>();
            var gr = c.GetComponent<GraphicRaycaster>();
            var dist = cam == null ? -1f : Vector3.Distance(cam.transform.position, c.transform.position);
            log.LogInfo($"[Laptop] canvas '{Path(c.transform)}' mode={c.renderMode} active={c.isActiveAndEnabled} camera={(c.worldCamera == null ? "-" : c.worldCamera.name)} " +
                        $"size={rt.sizeDelta} scale={c.transform.lossyScale} dist={dist:0.00} raycaster={(gr == null ? "none" : gr.enabled.ToString())}");
        }

        var es = EventSystem.current;
        log.LogInfo($"[Laptop] EventSystem='{(es == null ? "-" : es.name)}' module='{(es == null || es.currentInputModule == null ? "-" : es.currentInputModule.GetIl2CppType().Name)}'");
        log.LogInfo("===== [Laptop] end =====");
    }

    private static void LogClick()
    {
        var es = EventSystem.current;
        if (es == null)
        {
            Plugin.Logger.LogInfo("[Laptop] click: no EventSystem");
            return;
        }

        var ped = new PointerEventData(es);
        // The input module reports the pointer in window pixels; use the same
        ped.position = MousePixels();
        var results = new RaycastResults();
        es.RaycastAll(ped, results);

        var hits = results.Count == 0 ? "nothing" : "";
        for (var i = 0; i < results.Count && i < 6; i++)
        {
            var r = results[i];
            var go = r.gameObject;
            var module = r.module == null ? "-" : $"{r.module.GetIl2CppType().Name}@{r.module.name}";
            hits += $"\n    {i}: '{(go == null ? "-" : Path(go.transform))}' via {module} cam={(r.module == null || r.module.eventCamera == null ? "-" : r.module.eventCamera.name)}";
        }
        Plugin.Logger.LogInfo($"[Laptop] click at {ped.position} hits {results.Count}: {hits}");
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hWnd, ref POINT p);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();

    private static Vector2 MousePixels()
    {
        var hwnd = GetActiveWindow();
        if (hwnd == IntPtr.Zero || !GetCursorPos(out var p) || !ScreenToClient(hwnd, ref p) || !GetClientRect(hwnd, out var r))
            return new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        var h = r.Bottom - r.Top;
        // Window pixels -> Unity screen pixels (bottom-left origin), scaled in case the window size differs
        var sx = Screen.width / (float)Math.Max(1, r.Right - r.Left);
        var sy = Screen.height / (float)Math.Max(1, h);
        return new Vector2(p.X * sx, (h - p.Y) * sy);
    }

    internal static string Path(Transform t)
    {
        var path = t.name;
        while (t.parent != null)
        {
            t = t.parent;
            path = t.name + "/" + path;
        }
        return path;
    }
}
