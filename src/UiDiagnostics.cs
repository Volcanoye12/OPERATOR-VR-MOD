using System;
using System.Text;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.UIElements;

namespace OperatorVR;

/// <summary>
/// Logs every active uGUI Canvas and UI Toolkit UIDocument with how it renders, automatically
/// whenever the set of visible menus changes, so we can tell why menus don't reach the headset.
/// (Was an F7/F11 hotkey, but the user's F-keys send media keys without Fn.)
/// </summary>
public class UiDiagnostics : MonoBehaviour
{
    public UiDiagnostics(IntPtr ptr) : base(ptr) { }

    private float _nextCheck;
    private string _lastSignature = "";

    private void Update()
    {
        using var __t = ModTime.Measure("UiDiagnostics");
        if (Time.unscaledTime < _nextCheck)
            return;
        _nextCheck = Time.unscaledTime + 0.5f;

        try
        {
            var lines = Collect();
            var signature = string.Join("\n", lines);
            if (signature == _lastSignature)
                return;
            _lastSignature = signature;

            Plugin.Logger.LogInfo("===== [UI] menus changed =====");
            foreach (var line in lines)
                Plugin.Logger.LogInfo($"[UI] {line}");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[UI] dump failed: {e.GetType().Name}: {e.Message}");
            _nextCheck = Time.unscaledTime + 30f;
        }
    }

    private static System.Collections.Generic.List<string> Collect()
    {
        var lines = new System.Collections.Generic.List<string>();

        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<Canvas>(), FindObjectsSortMode.None))
        {
            var c = o.TryCast<Canvas>();
            if (c == null || !c.isRootCanvas || !c.enabled || !c.gameObject.activeInHierarchy)
                continue;
            var cam = c.worldCamera == null ? "-" : c.worldCamera.name;
            lines.Add($"canvas '{Path(c.transform)}' mode={c.renderMode} sort={c.sortingOrder} camera={cam} children={ActiveChildren(c.transform)}");
        }

        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<UIDocument>(), FindObjectsSortMode.None))
        {
            var d = o.TryCast<UIDocument>();
            if (d == null || !d.enabled || !d.gameObject.activeInHierarchy)
                continue;
            var root = d.rootVisualElement;
            if (root == null || root.resolvedStyle.display != DisplayStyle.Flex)
                continue;
            var ps = d.panelSettings;
            var target = ps == null || ps.targetTexture == null ? "screen" : $"texture {ps.targetTexture.width}x{ps.targetTexture.height}";
            lines.Add($"doc '{Path(d.transform)}' panel='{(ps == null ? "-" : ps.name)}' target={target} sort={d.sortingOrder} elements={root.childCount}");
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static int ActiveChildren(Transform t)
    {
        var n = 0;
        for (var i = 0; i < t.childCount; i++)
            if (t.GetChild(i).gameObject.activeSelf)
                n++;
        return n;
    }

    private static string Path(Transform t)
    {
        var sb = new StringBuilder(t.name);
        while (t.parent != null)
        {
            t = t.parent;
            sb.Insert(0, t.name + "/");
        }
        return sb.ToString();
    }
}
