using System;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.UI;

namespace OperatorVR;

/// <summary>
/// The character customisation page as a flat screen in VR. In flat mode the game points its camera at your
/// character from the side (narrow field of view) and shows the clothing panels on the right (an overlay canvas,
/// 'CharacterCustomisation V2/Canvas', which VRMenus already floats in front of you). In VR the camera is your head,
/// so the character view was lost. While that page is open, a camera of ours copies the game's camera (its own pose
/// and field of view, without the head) into a picture, and the picture is put behind the panels: the floating page
/// then looks like the flat screen. The pointer works on the panels as before. Menus.CustomisationScreen.
/// </summary>
public class CustomisationScreen : MonoBehaviour
{
    public CustomisationScreen(IntPtr ptr) : base(ptr) { }

    /// <summary>The page is open: show the whole character (BodyHider and the chest/arm shrinking pause).</summary>
    internal static bool Open { get; private set; }

    private Canvas _canvas;          // the customisation page's canvas, while open
    private float _nextFind;
    private Camera _flatCam;
    private RenderTexture _texture;
    private RawImage _view;
    private bool _logged, _copiedHdData;

    /// <summary>The flat view's picture while the page is open (EyeCapture saves it with F3).</summary>
    internal static RenderTexture Texture => s_instance != null && s_instance._flatCam != null && s_instance._flatCam.enabled ? s_instance._texture : null;
    private static CustomisationScreen s_instance;

    private void Start()
    {
        s_instance = this;
        // After HeadTracking's own onBeforeRender (added earlier), so the game's pose is known for this frame
        Application.add_onBeforeRender((UnityEngine.Events.UnityAction)new Action(BeforeRender));
    }

    private void Update()
    {
        using var __t = ModTime.Measure("CustomisationScreen");
        var on = OpenXRBootstrap.IsRunning && Plugin.CustomisationScreen.Value;
        if (on && Time.unscaledTime >= _nextFind)
        {
            _nextFind = Time.unscaledTime + 1f;
            try { _canvas = FindPage(); }
            catch { _canvas = null; }
        }
        var open = on && _canvas != null && _canvas.isActiveAndEnabled;
        Open = open;
        try
        {
            if (open)
                Show();
            else
                Hide();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Customise] {e.GetType().Name}: {e.Message}");
            _nextFind = Time.unscaledTime + 10f;
            _canvas = null;
        }
    }

    // The page's canvas: 'Canvas' under 'CharacterCustomisation V2' (from the flat screenshot's log), open when active
    private static Canvas FindPage()
    {
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<Canvas>(), FindObjectsSortMode.None))
        {
            var c = o.TryCast<Canvas>();
            if (c == null || !c.isRootCanvas || !c.isActiveAndEnabled)
                continue;
            for (var p = c.transform.parent; p != null; p = p.parent)
                if (p.name.StartsWith("CharacterCustomisation"))
                    return c;
        }
        return null;
    }

    // The picture is the screen's size, so a point the flat camera projects lands where it would on the flat screen:
    // ODG's cabinet panels (Loadout Presets etc.) measure the character with it (see AfterCabinetCamera)
    private void EnsureCamera()
    {
        var w = Mathf.Max(640, Screen.width);
        var h = Mathf.Max(360, Screen.height);
        if (_texture != null && (_texture.width != w || _texture.height != h))
        {
            if (_flatCam != null)
                _flatCam.targetTexture = null;
            if (_view != null)
                _view.texture = null;
            _texture.Release();
            UnityEngine.Object.Destroy(_texture);
            _texture = null;
        }
        if (_texture == null)
        {
            _texture = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { name = "OperatorVR Customise View" };
            _texture.Create();
            if (_flatCam != null)
                _flatCam.targetTexture = _texture;
            if (_view != null)
                _view.texture = _texture;
        }
        if (_flatCam == null)
        {
            var go = new GameObject("OperatorVR Customise Camera");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _flatCam = go.AddComponent<Camera>();
            _flatCam.stereoTargetEye = StereoTargetEyeMask.None;   // a plain camera, not a VR one
            _flatCam.targetTexture = _texture;
            _flatCam.enabled = false;
            _copiedHdData = false;
        }
    }

    private void Show()
    {
        EnsureCamera();
        // First try drew plain grey: with HDRP's default camera settings (exposure, volumes) the dark customisation
        // room washed out. Take the game camera's own HDRP settings
        if (!_copiedHdData && HeadTracking.TryGetGameCameraPose(out var gameCam, out _, out _))
        {
            var src = gameCam.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData>();
            if (src != null)
            {
                var dst = _flatCam.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData>() ??
                          _flatCam.gameObject.AddComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData>();
                src.CopyTo(dst);
                _copiedHdData = true;
                Plugin.Logger.LogInfo($"[Customise] Took the HDRP camera settings of '{gameCam.name}'");
            }
        }
        if (_view == null || _view.transform.parent == null || _view.transform.parent.Pointer != _canvas.transform.Pointer)
        {
            if (_view != null)
                UnityEngine.Object.Destroy(_view.gameObject);
            var go = new GameObject("OperatorVR Customise View");
            go.layer = _canvas.gameObject.layer;
            go.transform.SetParent(_canvas.transform, false);
            go.transform.SetAsFirstSibling();                       // behind the panels
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            _view = go.AddComponent<RawImage>();
            _view.texture = _texture;
            _view.raycastTarget = false;                            // clicks and drags go to the game as before
        }
        if (!_flatCam.enabled)
            _flatCam.enabled = true;
        if (!_view.gameObject.activeSelf)
            _view.gameObject.SetActive(true);
        if (!_logged)
        {
            _logged = true;
            Plugin.Logger.LogInfo($"[Customise] Customisation page open: flat view of the game camera behind '{LaptopDiagnostics.Path(_canvas.transform)}'");
        }
    }

    private void Hide()
    {
        if (_flatCam != null && _flatCam.enabled)
        {
            _flatCam.enabled = false;
            Plugin.Logger.LogInfo("[Customise] Customisation page closed");
            _logged = false;
        }
        if (_view != null && _view.gameObject.activeSelf)
            _view.gameObject.SetActive(false);
    }

    // ---- ODG cabinet panels ----
    // ODG's Loadout Presets (and NewSlots / Quartermaster, which share its OdgUi.Layout) place their panels beside the
    // character by projecting it through the cabinet camera (OdgUi.Layout.CabinetCamera). In VR that camera is the
    // headset's wide view, so the character measured tiny (68x165 px) and the panel was put over it in the flat view
    // shown here (2026-11-03). While VR runs, that helper gets the flat camera instead, posed as the game's camera
    internal static void Apply(HarmonyLib.Harmony harmony)
    {
        var postfix = new HarmonyLib.HarmonyMethod(typeof(CustomisationScreen), nameof(AfterCabinetCamera));
        var patched = 0;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type t;
            try { t = asm.GetType("OdgUi.Layout", false); }
            catch { continue; }
            var m = t?.GetMethod("CabinetCamera", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (m == null || m.ReturnType != typeof(Camera))
                continue;
            try
            {
                harmony.Patch(m, postfix: postfix);
                patched++;
                Plugin.Logger.LogInfo($"[Customise] hooked OdgUi.Layout.CabinetCamera in {asm.GetName().Name} (ODG panels measure the character in the flat view)");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"[Customise] hooking OdgUi.Layout.CabinetCamera in {asm.GetName().Name} failed: {e.Message}");
            }
        }
        if (patched == 0)
            Plugin.Logger.LogInfo("[Customise] no ODG cabinet panels to hook");
    }

    private static int s_cabinetLogs;

    private static void AfterCabinetCamera(ref Camera __result)
    {
        try
        {
            if (!OpenXRBootstrap.IsRunning || !Plugin.CustomisationScreen.Value || s_instance == null)
                return;
            var flat = s_instance.PrepareFlatCamera();
            if (flat == null)
                return;
            if (s_cabinetLogs++ < 3)
                Plugin.Logger.LogInfo($"[Customise] ODG panel measuring through the flat view ({flat.pixelWidth}x{flat.pixelHeight}, fov {flat.fieldOfView:0}) instead of '{(__result != null ? __result.name : "-")}'");
            __result = flat;
        }
        catch { }
    }

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private Camera PrepareFlatCamera()
    {
        EnsureCamera();
        return UpdatePose() ? _flatCam : null;
    }

    // The game camera's field of view as it is just before drawing, which is the game's own (the customisation view's
    // narrow one). At other times in a frame it reads as the headset's (100°): ODG's panel measured the character through
    // that and found it tiny in the middle, then sat over him on the flat view (2026-11-03)
    private static float s_drawFov = -1f;

    // Each frame, just before drawing: the flat camera takes the game camera's own view
    private void BeforeRender()
    {
        try
        {
            if (HeadTracking.TryGetGameCameraPose(out var cam, out _, out _))
                s_drawFov = cam.fieldOfView;
        }
        catch { }
        if (_flatCam == null || !_flatCam.enabled)
            return;
        UpdatePose();
    }

    private bool UpdatePose()
    {
        try
        {
            if (!HeadTracking.TryGetGameCameraPose(out var cam, out var pos, out var rot))
                return false;
            _flatCam.transform.SetPositionAndRotation(pos, rot);
            _flatCam.fieldOfView = s_drawFov > 0f ? s_drawFov : cam.fieldOfView;
            _flatCam.nearClipPlane = cam.nearClipPlane;
            _flatCam.farClipPlane = cam.farClipPlane;
            // Not the menus: in VR the floating page itself sits in front of the game camera, and the second try
            // filmed the back of it (grey, with bits of the panels) instead of the character
            var mask = cam.cullingMask & ~(1 << 5);                     // UI
            if (MenusOnTop.Layer >= 0)
                mask &= ~(1 << MenusOnTop.Layer);
            if (_canvas != null)
                mask &= ~(1 << _canvas.gameObject.layer);
            _flatCam.cullingMask = mask;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
