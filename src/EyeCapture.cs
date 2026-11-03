using System;
using System.IO;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace OperatorVR;

/// <summary>
/// Temporary: saves what Unity rendered into each eye's swapchain images as PNGs, to tell whether the right
/// eye's images really hold the level (then the VR runtime isn't showing them) or not (then Unity isn't writing
/// them). Runs 20 s after VR first renders in each scene, and whenever F3 is pressed (F3, not F5: the ODG Third Person mod toggles its view on F5).
/// Files go to BepInEx\OperatorVR_eyes\.
/// </summary>
public class EyeCapture : MonoBehaviour
{
    public EyeCapture(IntPtr ptr) : base(ptr) { }

    private const int VK_F3 = 0x72;
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private bool _f3Down;
    private string _scene;
    private float _captureAt = -1f;
    private int _shot;

    // Captures asked for by other components (e.g. after each F6 step)
    private static string s_requestTag;
    private static float s_requestAt = -1f;

    internal static void Request(string tag, float delay = 0f)
    {
        s_requestTag = tag;
        s_requestAt = Time.unscaledTime + delay;
    }

    private void Update()
    {
        using var __t = ModTime.Measure("EyeCapture");
        var down = (GetAsyncKeyState(VK_F3) & 0x8000) != 0;
        var pressed = down && !_f3Down;
        _f3Down = down;

        if (!OpenXRBootstrap.IsRunning)
        {
            _scene = null;
            // Flat mode: F3 saves an ordinary screenshot (to compare menus like the customisation page with VR),
            // with every canvas and its layout logged alongside
            if (pressed)
                CaptureFlat();
            return;
        }

        // With every VR snapshot: where the view and the gun are (the gun sat close to the face after menus)
        if (pressed)
            HeadTracking.LogPose("F3");

        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        // The automatic capture 20 s into each scene only with General.Diagnostics: over the project it filled the
        // folder with ~2,400 images (1.2 GB). F3 always works
        if (scene != _scene)
        {
            _scene = scene;
            _captureAt = Plugin.Diagnostics.Value ? Time.unscaledTime + 20f : -1f;
        }

        if (pressed || (_captureAt > 0f && Time.unscaledTime >= _captureAt))
        {
            _captureAt = -1f;
            Capture(scene, pressed ? "F3" : "auto");
        }
        if (s_requestAt > 0f && Time.unscaledTime >= s_requestAt)
        {
            s_requestAt = -1f;
            Capture(scene, s_requestTag);
        }

        // Shift + F4: record the left eye, 5 frames a second for 6 seconds, to show what happens over time (F4 alone starts VR)
        var f4 = (GetAsyncKeyState(VK_F4) & 0x8000) != 0 && (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
        if (f4 && !_f4Down && _burstLeft == 0)
        {
            _burstLeft = BurstFrames;
            _burstNext = Time.unscaledTime;
            _burstStamp = DateTime.Now.ToString("HHmmss");
            Plugin.Logger.LogInfo($"[Eyes] Recording {BurstFrames} frames of the left eye (Shift+F4)");
        }
        _f4Down = f4;
        if (_burstLeft > 0 && Time.unscaledTime >= _burstNext)
        {
            _burstNext = Time.unscaledTime + 1f / BurstPerSecond;
            var n = BurstFrames - _burstLeft + 1;
            _burstLeft--;
            RecordFrame(n);
            if (_burstLeft == 0)
                Plugin.Logger.LogInfo($"[Eyes] Recording done: {_burstStamp}_rec_01..{BurstFrames:00}.png");
        }
    }

    private void Capture(string scene, string why)
    {
        try
        {
            var dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "OperatorVR_eyes");
            Directory.CreateDirectory(dir);
            _shot++;
            var stamp = DateTime.Now.ToString("HHmmss");

            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<RenderTexture>());
            var saved = 0;
            foreach (var kv in XRPassLog.EyeTargets)
            {
                var eye = kv.Key == 0 ? "left" : kv.Key == 1 ? "right" : $"pass{kv.Key}";
                var index = 0;
                foreach (var id in kv.Value)
                {
                    RenderTexture rt = null;
                    foreach (var o in all)
                        if (o.GetInstanceID() == id)
                        {
                            rt = o.TryCast<RenderTexture>();
                            break;
                        }
                    var name = $"{stamp}_{Safe(scene)}_{why}_{eye}{index++}.png";
                    if (rt == null)
                    {
                        Plugin.Logger.LogInfo($"[Eyes] {eye} texture {id} not found");
                        continue;
                    }
                    Save(rt, Path.Combine(dir, name));
                    saved++;
                }
            }
            // The customisation page's flat view, when open
            var flat = CustomisationScreen.Texture;
            if (flat != null)
            {
                Save(flat, Path.Combine(dir, $"{stamp}_{Safe(scene)}_{why}_customise.png"));
                saved++;
            }
            Plugin.Logger.LogInfo($"[Eyes] Saved {saved} eye images ({why}) for '{scene}' to {dir}");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Eyes] {e.GetType().Name}: {e.Message}");
        }
    }

    private void CaptureFlat()
    {
        try
        {
            var dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "OperatorVR_eyes");
            Directory.CreateDirectory(dir);
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            var path = Path.Combine(dir, $"{DateTime.Now:HHmmss}_{Safe(scene)}_flat.png");
            ScreenCapture.CaptureScreenshot(path);
            Plugin.Logger.LogInfo($"[Eyes] Flat screenshot (F3) -> {path}");

            // Where each visible canvas and its main parts sit on screen, and which cameras draw what
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Canvas>()))
            {
                var c = o.TryCast<Canvas>();
                if (c == null || !c.isRootCanvas || !c.isActiveAndEnabled || c.gameObject.scene.name == null)
                    continue;
                var rt = c.GetComponent<RectTransform>();
                var kids = new System.Collections.Generic.List<string>();
                for (var i = 0; i < c.transform.childCount && kids.Count < 25; i++)
                {
                    var k = c.transform.GetChild(i);
                    if (!k.gameObject.activeSelf)
                        continue;
                    var kr = k.GetComponent<RectTransform>();
                    kids.Add(kr != null
                        ? $"{k.name} [anchor {kr.anchorMin}-{kr.anchorMax} pos {kr.anchoredPosition} size {kr.rect.size}]"
                        : k.name);
                }
                Plugin.Logger.LogInfo($"[Eyes]   canvas '{c.name}' {c.renderMode} sort={c.sortingOrder} camera={(c.worldCamera != null ? c.worldCamera.name : "-")} " +
                                      $"size={(rt != null ? rt.rect.size.ToString() : "?")}: {string.Join("; ", kids)}");
            }
            foreach (var cam in Camera.allCameras)
                Plugin.Logger.LogInfo($"[Eyes]   camera '{cam.name}' depth={cam.depth} pos={cam.transform.position} fwd={cam.transform.forward} " +
                                      $"fov={cam.fieldOfView:0} target={(cam.targetTexture != null ? cam.targetTexture.name : "screen")} rect={cam.rect}");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Eyes] flat screenshot: {e.GetType().Name}: {e.Message}");
        }
    }

    private const int VK_F4 = 0x73;
    private const int VK_SHIFT = 0x10;
    private const int BurstFrames = 30, BurstPerSecond = 5;
    private bool _f4Down;
    private int _burstLeft;
    private float _burstNext;
    private string _burstStamp;

    private void RecordFrame(int n)
    {
        try
        {
            if (!XRPassLog.LastTarget.TryGetValue(0, out var id))
                return;
            RenderTexture rt = null;
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<RenderTexture>()))
                if (o.GetInstanceID() == id)
                {
                    rt = o.TryCast<RenderTexture>();
                    break;
                }
            if (rt == null)
                return;
            var dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "OperatorVR_eyes");
            Directory.CreateDirectory(dir);
            Save(rt, Path.Combine(dir, $"{_burstStamp}_rec_{n:00}.png"));
            Plugin.Logger.LogInfo($"[Eyes] rec frame {n:00}");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Eyes] recording: {e.GetType().Name}: {e.Message}");
            _burstLeft = 0;
        }
    }

    private static void Save(RenderTexture rt, string path)
    {
        // Quarter size is plenty to see what's in it
        var w = Math.Max(1, rt.width / 4);
        var h = Math.Max(1, rt.height / 4);
        var small = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
        var prev = RenderTexture.active;
        try
        {
            Graphics.Blit(rt, small);
            RenderTexture.active = small;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            byte[] png = ImageConversion.EncodeToPNG(tex);
            File.WriteAllBytes(path, png);
            UnityEngine.Object.Destroy(tex);
        }
        finally
        {
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(small);
        }
    }

    private static string Safe(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s.Replace(' ', '_');
    }
}
