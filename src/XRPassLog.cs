using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace OperatorVR;

/// <summary>
/// Temporary: in 'Georgia 1 Day' the right eye keeps the previous scene and the left eye flickers between
/// both views, although the XR display asks for 2 passes. Logs every XR pass HDRP builds (which render
/// target and array slice each eye goes to) for the first frames after VR starts and after each scene
/// change, so the two scenes can be compared.
/// </summary>
internal static class XRPassLog
{
    private static string _scene;
    private static int _loggedThisScene;

    internal static void Apply(Harmony harmony)
    {
        // Two hooks in case IL2CPP inlined one of them; each is patched separately so one failing doesn't stop the other
        TryPatch(harmony, AccessTools.Method(typeof(XRLayout), nameof(XRLayout.AddPass)), nameof(PassAdded));
        TryPatch(harmony, AccessTools.Method(typeof(XRSystem), nameof(XRSystem.ReconfigurePass)), nameof(PassReconfigured));
    }

    private static void TryPatch(Harmony harmony, System.Reflection.MethodBase target, string postfix)
    {
        try
        {
            if (target == null)
            {
                Plugin.Logger.LogWarning($"[XRPass] no method for {postfix}");
                return;
            }
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(XRPassLog), postfix));
            Plugin.Logger.LogInfo($"[XRPass] hooked {target.DeclaringType?.Name}.{target.Name}");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[XRPass] patching {postfix} failed: {e.GetType().Name}: {e.Message}");
        }
    }

    // Arguments found by type: the original parameter names aren't known for sure
    private static void PassReconfigured(object[] __args) => Log("reconfigured", __args);

    private static void PassAdded(object[] __args) => Log("added", __args);

    private static void Log(string what, object[] args)
    {
        Camera camera = null;
        XRPass pass = null;
        foreach (var a in args)
        {
            if (a is Camera c)
                camera = c;
            else if (a is XRPass p)
                pass = p;
        }
        if (what == "added")
            Count(camera);
        if (pass != null)
            Log(what, camera, pass);
    }

    // Renders per camera over 10 s, to see how often extra cameras (like Georgia's 'Camera') draw
    private static readonly System.Collections.Generic.Dictionary<string, int> s_counts = new();
    private static float s_countStart;

    private static void Count(Camera camera)
    {
        if (!OpenXRBootstrap.IsRunning)
            return;
        var name = camera == null ? "-" : camera.name;
        s_counts.TryGetValue(name, out var n);
        s_counts[name] = n + 1;

        var elapsed = Time.unscaledTime - s_countStart;
        if (elapsed < 10f)
            return;
        var parts = new System.Collections.Generic.List<string>();
        foreach (var kv in s_counts)
            parts.Add($"'{kv.Key}' {kv.Value}");
        Plugin.Logger.LogInfo($"[XRPass] passes per camera over {elapsed:0}s: {string.Join(", ", parts)}");
        s_counts.Clear();
        s_countStart = Time.unscaledTime;
    }

    /// <summary>Instance IDs of the textures each eye (multipassId) was rendered into, for EyeCapture.</summary>
    internal static readonly System.Collections.Generic.Dictionary<int, System.Collections.Generic.HashSet<int>> EyeTargets = new();

    private static readonly System.Text.RegularExpressions.Regex InstanceIdRx = new(@"InstanceID (-?\d+)");

    private static void RememberTarget(XRPass xrPass)
    {
        if (!xrPass.enabled)
            return;
        // RenderTargetIdentifier doesn't expose its instance ID, but its ToString() prints it
        var m = InstanceIdRx.Match(xrPass.renderTarget.ToString());
        if (!m.Success)
            return;
        if (!EyeTargets.TryGetValue(xrPass.multipassId, out var set))
            EyeTargets[xrPass.multipassId] = set = new System.Collections.Generic.HashSet<int>();
        var id = int.Parse(m.Groups[1].Value);
        if (set.Count < 8)
            set.Add(id);
        // The image this eye is being drawn into right now (the runtime cycles through three), for recordings
        LastTarget[xrPass.multipassId] = id;
    }

    /// <summary>Instance ID of the texture each eye (multipassId) was most recently rendered into.</summary>
    internal static readonly System.Collections.Generic.Dictionary<int, int> LastTarget = new();

    private static void Log(string what, Camera camera, XRPass xrPass)
    {
        try
        {
            if (OpenXRBootstrap.IsRunning)
                RememberTarget(xrPass);
            else
                EyeTargets.Clear();
        }
        catch { }

        try
        {
            if (!OpenXRBootstrap.IsRunning)
            {
                _scene = null;
                return;
            }
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (scene != _scene)
            {
                _scene = scene;
                _loggedThisScene = 0;
            }
            // A few frames' worth (2 passes per frame), a little while after the scene starts too
            if (_loggedThisScene++ >= 12)
                return;

            var desc = xrPass.renderTargetDesc;
            var slices = "";
            for (var v = 0; v < xrPass.viewCount; v++)
                slices += (v == 0 ? "" : ",") + xrPass.GetTextureArraySlice(v);
            Plugin.Logger.LogInfo($"[XRPass] {what} '{scene}' camera='{(camera == null ? "-" : camera.name)}' enabled={xrPass.enabled} " +
                                  $"multipassId={xrPass.multipassId} cullingPassId={xrPass.cullingPassId} views={xrPass.viewCount} " +
                                  $"singlePass={xrPass.singlePassEnabled} slices=[{slices}] target={xrPass.renderTarget} " +
                                  $"desc={desc.dimension} {desc.width}x{desc.height}x{desc.volumeDepth} scaled={xrPass.renderTargetScaledWidth}x{xrPass.renderTargetScaledHeight} " +
                                  $"firstCam={xrPass.isFirstCameraPass} lastCam={xrPass.isLastCameraPass}");
        }
        catch (Exception e)
        {
            if (_loggedThisScene < 20)
                Plugin.Logger.LogError($"[XRPass] log error {e.GetType().Name}: {e.Message}");
        }
    }
}
