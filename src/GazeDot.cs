using System;
using UnityEngine;
using UnityEngine.UI;

namespace OperatorVR;

/// <summary>
/// With a gamepad, what you can use is decided by where the game aims (its camera, turned by the right stick), but
/// in VR nothing showed where that was: picking one gun off the armoury wall was guesswork. A small dot at the aim
/// point shows it, white near something usable and green when the game's scan finds something to use there (the
/// moment to press Interact). Drawn on top of everything, in play only (not in menus). Menus.GazeDot.
/// </summary>
public class GazeDot : MonoBehaviour
{
    public GazeDot(IntPtr ptr) : base(ptr) { }

    private const float Distance = 1.2f;          // metres in front of the eyes (the game's reach)
    private const float Size = 0.012f;           // dot diameter in metres
    private static readonly Color Idle = new(1f, 1f, 1f, 0.55f);
    private static readonly Color Ready = new(0.3f, 1f, 0.3f, 0.95f);

    private GameObject _dot;
    private Image _image;
    private bool _logged;

    private void Start()
    {
        // After HeadTracking's own onBeforeRender, so the camera already carries the head pose
        Application.add_onBeforeRender((UnityEngine.Events.UnityAction)new Action(BeforeRender));
    }

    private bool ShouldShow() =>
        // With the Touch controllers too (2026-11-04: no dot at the armoury wall or weapon bench): the game's scan
        // follows its aim, which the right hand (or, holding Interact, the head) steers, so the dot is just as useful
        OpenXRBootstrap.IsRunning && Plugin.GazeDot.Value &&
        Cursor.lockState == CursorLockMode.Locked && VRPointer.Target == null && !CustomisationScreen.Open;

    private void BeforeRender()
    {
        try
        {
            if (!ShouldShow())
            {
                if (_dot != null && _dot.activeSelf)
                    _dot.SetActive(false);
                return;
            }
            // Only near something usable (the game's own nearby check, or a hit just now): shown all the time it
            // drifted about the view for nothing
            var ready = InteractScanLog.Found && Time.unscaledTime - InteractScanLog.ScannedAt < 0.5f;
            var near = ready || Time.unscaledTime - InteractScanLog.NearbyAt < 0.5f;
            var cam = Camera.main;
            if (cam == null || !near)
            {
                if (_dot != null && _dot.activeSelf)
                    _dot.SetActive(false);
                return;
            }
            if (_dot == null)
                Create();
            if (!_dot.activeSelf)
                _dot.SetActive(true);
            if (MenusOnTop.Layer >= 0 && _dot.layer != MenusOnTop.Layer)
                MenusOnTop.Put(_dot);

            // Where the game aims (its own camera, which the right stick turns): that's what its scan checks. Facing
            // the eyes, so it reads as a dot from wherever the head is
            if (!HeadTracking.TryGetGameCameraPose(out _, out var aimPos, out var aimRot))
                return;
            var dotPos = aimPos + aimRot * Vector3.forward * Distance;
            var eye = cam.transform.position;
            var look = dotPos - eye;
            _dot.transform.SetPositionAndRotation(dotPos, look.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(look) : aimRot);
            // Green while the latest scan (within the last half second) found something
            _image.color = ready ? Ready : Idle;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[GazeDot] {e.GetType().Name}: {e.Message}");
            Plugin.GazeDot.Value = false;
        }
    }

    private void Create()
    {
        _dot = new GameObject("OperatorVR Gaze Dot");
        UnityEngine.Object.DontDestroyOnLoad(_dot);
        var canvas = _dot.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        var rt = _dot.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(64f, 64f);
        _dot.transform.localScale = Vector3.one * (Size / 64f);

        var img = new GameObject("Dot");
        img.transform.SetParent(_dot.transform, false);
        var irt = img.AddComponent<RectTransform>();
        irt.anchorMin = Vector2.zero;
        irt.anchorMax = Vector2.one;
        irt.offsetMin = irt.offsetMax = Vector2.zero;
        _image = img.AddComponent<Image>();
        _image.sprite = MakeCircle();
        _image.raycastTarget = false;
        _image.color = Idle;
        if (!_logged)
        {
            _logged = true;
            Plugin.Logger.LogInfo("[GazeDot] Aiming dot on: white = nothing to use, green = press Interact");
        }
    }

    // A soft-edged white disc
    private static Sprite MakeCircle()
    {
        const int n = 64;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "OperatorVR Gaze Dot" };
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
            {
                var dx = (x + 0.5f) / n * 2f - 1f;
                var dy = (y + 0.5f) / n * 2f - 1f;
                var d = Mathf.Sqrt(dx * dx + dy * dy);
                var a = Mathf.Clamp01((1f - d) * 8f);           // solid disc with a soft edge
                var ring = d > 0.7f ? 1f : 0.85f;
                tex.SetPixel(x, y, new Color(ring, ring, ring, a));
            }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
    }
}
