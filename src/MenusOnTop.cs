using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace OperatorVR;

/// <summary>
/// In flat mode the game's menus, HUD and prompts are drawn over everything; as floating panels in VR they sat
/// behind the player's own arms and gun (closer than 1 m) and were hard to see or use. While VR runs the floating
/// panels, the pointer arrow, the laser and the floated laptop screen go on a spare layer, and an HDRP custom pass
/// draws that layer again after post-processing with depth testing off, so they show on top like in flat mode.
/// (Rendering them in the Overlay queue made them vanish: HDRP doesn't draw that queue.) Menus.OnTop.
/// </summary>
public class MenusOnTop : MonoBehaviour
{
    public MenusOnTop(IntPtr ptr) : base(ptr) { }

    // Spare layers to try; the first one without a name is used. 29 comes last: the ODG Quartermaster's Gunsmith takes
    // the highest free layer from 29 down for its bench backdrop (the gun moved onto it, the camera drawing only that),
    // and sharing 29 put the bench gun into the on-top drawing and lost it (2026-11-03)
    private static readonly int[] Candidates = { 30, 26, 23, 21, 20, 19, 17, 16, 29 };

    /// <summary>The on-top layer, or -1 if not active.</summary>
    internal static int Layer { get; private set; } = -1;

    private static readonly Dictionary<IntPtr, (GameObject go, int layer)> s_moved = new();

    private GameObject _volumeGo;
    private CustomPassVolume _volume;
    private bool _wasRunning;
    private bool _failed;

    /// <summary>Puts a GameObject (and its children) on the on-top layer until VR stops or Release is called.</summary>
    internal static void Put(GameObject go)
    {
        if (Layer < 0 || go == null || go.layer == Layer)
            return;
        if (!s_moved.ContainsKey(go.Pointer))
            s_moved[go.Pointer] = (go, go.layer);
        SetLayer(go.transform, Layer);
    }

    /// <summary>Puts a GameObject back on its own layer.</summary>
    internal static void Release(GameObject go)
    {
        if (go == null || !s_moved.TryGetValue(go.Pointer, out var e))
            return;
        s_moved.Remove(go.Pointer);
        SetLayer(go.transform, e.layer);
        RestoreMasks(go.transform);
    }

    // ---- Lists clipped like in flat mode ----
    // Long lists (the customisation slot choices, the bench's parts list) spilled out of their boxes in VR. Two
    // reasons: entries the game makes after a page opens stayed on their own layer (drawn by the camera) while their
    // box was on ours (drawn later by the custom pass), and a stencil Mask can't clip across the two, nor inside a
    // custom pass that re-sorts by distance. So everything under a moved page is kept on our layer, and its Masks
    // are swapped for RectMask2D, which clips in the shader whatever the drawing order. Put back on Release/stop
    private static readonly Dictionary<IntPtr, (UnityEngine.UI.Mask mask, UnityEngine.UI.RectMask2D added, UnityEngine.UI.Graphic hidden)> s_masks = new();
    private float _nextResync;

    private static void ConvertMasks(Transform root)
    {
        foreach (var m in root.GetComponentsInChildren<UnityEngine.UI.Mask>(true))
        {
            if (m == null || !m.enabled || s_masks.ContainsKey(m.Pointer))
                continue;
            UnityEngine.UI.RectMask2D added = null;
            if (m.GetComponent<UnityEngine.UI.RectMask2D>() == null)
                added = m.gameObject.AddComponent<UnityEngine.UI.RectMask2D>();
            // A mask that hides its own picture: hide that picture now the Mask is off
            UnityEngine.UI.Graphic hidden = null;
            if (!m.showMaskGraphic)
            {
                var g = m.GetComponent<UnityEngine.UI.Graphic>();
                if (g != null && g.enabled)
                {
                    g.enabled = false;
                    hidden = g;
                }
            }
            m.enabled = false;
            s_masks[m.Pointer] = (m, added, hidden);
        }
    }

    private static void RestoreMasks(Transform root)
    {
        var done = new List<IntPtr>();
        foreach (var kv in s_masks)
        {
            var (m, added, hidden) = kv.Value;
            try
            {
                // (root null: everything)
                if (m != null && root != null && !m.transform.IsChildOf(root))
                    continue;
                if (added != null) UnityEngine.Object.Destroy(added);
                if (hidden != null) hidden.enabled = true;
                if (m != null) m.enabled = true;
            }
            catch { }
            done.Add(kv.Key);
        }
        foreach (var k in done)
            s_masks.Remove(k);
    }

    // Twice a second: entries added since go on our layer, and new masks are swapped
    private void Resync()
    {
        if (Time.unscaledTime < _nextResync)
            return;
        _nextResync = Time.unscaledTime + 1f;
        foreach (var (go, _) in s_moved.Values)
        {
            if (go == null)
                continue;
            try
            {
                SetLayer(go.transform, Layer);
                ConvertMasks(go.transform);
            }
            catch { }
        }
    }

    private static void SetLayer(Transform t, int layer)
    {
        t.gameObject.layer = layer;
        for (var i = 0; i < t.childCount; i++)
            SetLayer(t.GetChild(i), layer);
    }

    private void LateUpdate()
    {
        using var __t = ModTime.Measure("MenusOnTop");
        var running = OpenXRBootstrap.IsRunning && Plugin.MenusOnTop.Value && !_failed;
        if (!running)
        {
            if (_wasRunning)
                Stop();
            _wasRunning = false;
            return;
        }
        _wasRunning = true;

        try
        {
            if (_volumeGo == null)
                Create();
            if (Layer < 0)
                return;
            // Only the headset's camera: a global pass ran for every camera, and drew the menus into the
            // customisation page's flat view as well (it filmed its own page)
            var main = Camera.main;
            if (_volume != null && main != null && (_volume.targetCamera == null || _volume.targetCamera.Pointer != main.Pointer))
                _volume.targetCamera = main;
            foreach (var c in VRMenus.FloatingCanvases())
            {
                if (DrawnNormally(c))
                {
                    Release(c.gameObject);
                    continue;
                }
                Put(c.gameObject);
            }
            using (ModTime.Measure("MenusOnTop resync"))
                Resync();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[OnTop] {e.GetType().Name}: {e.Message}");
            _failed = true;
            Stop();
        }
    }

    // The bench's magazine manager showed only its dark boxes and two buttons through this pass (its list, names and
    // hints never appeared, although the log had every part on our layer, enabled and opaque, and a nested canvas
    // per entry didn't help). Nothing stands between the eye and it at the bench, so it's left to the camera's own
    // UI drawing, which keeps the menu's order
    // (Empty again: the real cause was the entries sitting ~15 m off the page, now fixed in VRMenus.FlattenMenus; drawn
    // normally, the bench's gun covered the page)
    private static readonly string[] NormalDrawNames = { };
    private static readonly HashSet<IntPtr> s_normalLogged = new();

    private static bool DrawnNormally(Canvas c)
    {
        // The bench page the old way with Menus.WeaponBenchOnTop off (the user's choice, 2026-11-03): drawn as part of
        // the room, so the gun in front of it shows over its panels
        if (!Plugin.WeaponBenchOnTop.Value && c.name.StartsWith("WeaponModdingCanvas"))
        {
            if (s_normalLogged.Add(c.Pointer))
                Plugin.Logger.LogInfo($"[OnTop] '{c.name}' drawn by the camera as normal, not on top (Menus.WeaponBenchOnTop off)");
            return true;
        }
        foreach (var n in NormalDrawNames)
            if (c.name.StartsWith(n))
            {
                if (s_normalLogged.Add(c.Pointer))
                    Plugin.Logger.LogInfo($"[OnTop] '{c.name}' drawn by the camera as normal, not on top");
                return true;
            }
        return false;
    }

    private void Create()
    {
        foreach (var l in Candidates)
            if (string.IsNullOrEmpty(LayerMask.LayerToName(l)))
            {
                Layer = l;
                break;
            }
        if (Layer < 0)
        {
            Plugin.Logger.LogWarning("[OnTop] No spare layer found; menus stay as they are");
            _failed = true;
            return;
        }

        _volumeGo = new GameObject("OperatorVR Menus On Top");
        UnityEngine.Object.DontDestroyOnLoad(_volumeGo);
        var volume = _volumeGo.AddComponent<CustomPassVolume>();
        _volume = volume;
        volume.isGlobal = true;
        volume.injectionPoint = CustomPassInjectionPoint.AfterPostProcess;

        var pass = new DrawRenderersCustomPass
        {
            name = "OperatorVR menus on top",
            layerMask = 1 << Layer,
            renderQueueType = CustomPass.RenderQueueType.All,
            sortingCriteria = SortingCriteria.CommonTransparent,
            overrideDepthState = true,
            depthCompareFunction = CompareFunction.Always,
            depthWrite = false,
            targetColorBuffer = CustomPass.TargetBuffer.Camera,
            targetDepthBuffer = CustomPass.TargetBuffer.Camera,
            clearFlags = ClearFlag.None,
        };
        volume.customPasses.Add(pass);
        Plugin.Logger.LogInfo($"[OnTop] Menus, pointer and laser drawn on top (layer {Layer})");
    }

    private void Stop()
    {
        foreach (var (go, layer) in s_moved.Values)
            try { if (go != null) SetLayer(go.transform, layer); } catch { }
        s_moved.Clear();
        RestoreMasks(null);
        if (_volumeGo != null)
            UnityEngine.Object.Destroy(_volumeGo);
        _volumeGo = null;
        _volume = null;
        Layer = -1;
    }
}
