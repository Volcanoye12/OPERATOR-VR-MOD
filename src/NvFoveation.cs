using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace OperatorVR;

/// <summary>
/// Fixed foveated rendering through NVIDIA's variable rate shading (NVAPI, Direct3D 11). Unity's own
/// foveation switch does nothing with HDRP on D3D11 (tested 2026-11-01), so the driver is told directly:
/// pixels in the middle of each lens are shaded at full rate, further out one shade covers 2 or 4 pixels.
///
/// Where: HDRP brackets each eye's own drawing with XRPass.StartSinglePass / StopSinglePass (shadow maps are
/// drawn outside those brackets). Postfixes on both put a plugin event into that eye's command buffer; on
/// Unity's render thread the event turns VRS on with that eye's pattern (the lens centre differs per eye)
/// or off again. Anything else (shadows, the scope camera, menus on the flat screen) is shaded as before.
/// </summary>
internal static unsafe class NvFoveation
{
    // ---- NVAPI ----
    private const uint IdInitialize = 0x0150e828;
    private const uint IdCreateShadingRateResourceView = 0x99ca2dff;
    private const uint IdRSSetShadingRateResourceView = 0x1b0c2f83;
    private const uint IdRSSetViewportsPixelShadingRates = 0x34f7938f;

    // NV_PIXEL_SHADING_RATE
    private const int Rate1x1 = 5, Rate2x1 = 6, Rate2x2 = 8, Rate4x4 = 11;

    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr*, int> s_createView;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int> s_setView;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int> s_setRates;

    private static bool s_initTried, s_ready;
    private static IntPtr s_device, s_context;
    private static IntPtr s_onDesc, s_offDesc; // NV_D3D11_VIEWPORTS_SHADING_RATE_DESC (+ its viewport array)

    // One shading-rate view per eye, for the eye size and strength they were made for
    private static readonly IntPtr[] s_views = new IntPtr[2];
    private static readonly int[] s_viewW = new int[2], s_viewH = new int[2];
    private static readonly float[] s_viewStrength = new float[2], s_viewCx = new float[2];

    // Render thread → main thread, for the log
    private static int s_onCount, s_offCount, s_lastOnStatus, s_lastOffStatus;
    private static float s_logAt;

    private const int EventBase = 0x4F56F000; // + 0 off, + 1 left eye on, + 2 right eye on
    private static IntPtr s_callback;

    internal static void Apply(Harmony harmony)
    {
        Patch(harmony, "StartSinglePass", nameof(Started));
        Patch(harmony, "StopSinglePass", nameof(Stopped));
    }

    private static void Patch(Harmony harmony, string name, string postfix)
    {
        try
        {
            var target = AccessTools.Method(typeof(XRPass), name, new[] { typeof(CommandBuffer) });
            if (target == null)
            {
                Plugin.Logger.LogWarning($"[Foveation] XRPass.{name} not found; foveated rendering won't work");
                return;
            }
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(NvFoveation), postfix));
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Foveation] hooking XRPass.{name} failed: {e.GetType().Name}: {e.Message}");
        }
    }

    // GpuAudit switches it off for a moment to measure what it saves
    internal static bool AuditPause;

    private static float Strength => AuditPause ? 0f : Mathf.Clamp01(Plugin.FoveatedRendering.Value);

    private static CommandBuffer FindCmd(object[] args)
    {
        foreach (var a in args)
            if (a is CommandBuffer c)
                return c;
        return null;
    }

    private static void Started(XRPass __instance, object[] __args)
    {
        try
        {
            if (!OpenXRBootstrap.IsRunning || Strength <= 0f || !__instance.enabled)
                return;
            var cmd = FindCmd(__args);
            if (cmd == null || !EnsureReady())
                return;

            var eye = Mathf.Clamp(__instance.multipassId, 0, 1);
            var desc = __instance.renderTargetDesc;
            // Where straight ahead lands in this eye's image: the projection is off-centre (more view outwards)
            var proj = __instance.GetProjMatrix(0);
            var cx = Mathf.Clamp(0.5f * (1f - proj.m02), 0.2f, 0.8f);
            if (!EnsureView(eye, desc.width, desc.height, cx))
                return;

            cmd.IssuePluginEvent(s_callback, EventBase + 1 + eye);
            LogNow();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Foveation] {e.GetType().Name}: {e.Message}");
            s_ready = false;
        }
    }

    private static void Stopped(XRPass __instance, object[] __args)
    {
        try
        {
            if (!s_ready || !__instance.enabled)
                return;
            FindCmd(__args)?.IssuePluginEvent(s_callback, EventBase);
        }
        catch { }
    }

    private static void LogNow()
    {
        var now = Time.unscaledTime;
        if (now < s_logAt)
            return;
        var first = s_logAt == 0f;
        s_logAt = now + 10f;
        if (first)
            return;
        Plugin.Logger.LogInfo($"[Foveation] strength {Strength:0.00}: VRS on {s_onCount}x / off {s_offCount}x in the last 10 s " +
                              $"(NVAPI status on {s_lastOnStatus}, off {s_lastOffStatus}; 0 = OK)");
        s_onCount = s_offCount = 0;
    }

    // ---- Render thread ----

    // A delegate rather than [UnmanagedCallersOnly]: Unity's CoreModule interop defines a clashing attribute type.
    // Kept in a static so the collector never frees the thunk Unity calls.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void RenderEvent(int id);
    private static readonly RenderEvent s_onRenderEvent = OnRenderEvent;

    private static void OnRenderEvent(int id)
    {
        var code = id - EventBase;
        if (code < 0 || code > 2 || s_context == IntPtr.Zero)
            return;
        if (code == 0)
        {
            s_lastOffStatus = s_setRates(s_context, s_offDesc);
            s_setView(s_context, IntPtr.Zero);
            s_offCount++;
        }
        else
        {
            var view = s_views[code - 1];
            if (view == IntPtr.Zero)
                return;
            s_setView(s_context, view);
            s_lastOnStatus = s_setRates(s_context, s_onDesc);
            s_onCount++;
        }
    }

    // ---- Set-up (main thread) ----

    private static bool EnsureReady()
    {
        if (s_ready)
            return true;
        if (s_initTried)
            return false;
        s_initTried = true;

        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11)
        {
            Plugin.Logger.LogWarning($"[Foveation] needs Direct3D 11 (the game runs {SystemInfo.graphicsDeviceType}); off");
            return false;
        }
        if (!NativeLibrary.TryLoad("nvapi64.dll", out var nvapi) ||
            !NativeLibrary.TryGetExport(nvapi, "nvapi_QueryInterface", out var qiPtr))
        {
            Plugin.Logger.LogWarning("[Foveation] NVIDIA driver interface (nvapi64.dll) not found; off (needs an NVIDIA card)");
            return false;
        }
        var qi = (delegate* unmanaged[Cdecl]<uint, IntPtr>)qiPtr;
        var init = (delegate* unmanaged[Cdecl]<int>)qi(IdInitialize);
        s_createView = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr*, int>)qi(IdCreateShadingRateResourceView);
        s_setView = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)qi(IdRSSetShadingRateResourceView);
        s_setRates = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)qi(IdRSSetViewportsPixelShadingRates);
        if (init == null || s_createView == null || s_setView == null || s_setRates == null)
        {
            Plugin.Logger.LogWarning("[Foveation] this NVIDIA driver lacks variable rate shading for D3D11; off");
            return false;
        }
        var st = init();
        if (st != 0)
        {
            Plugin.Logger.LogWarning($"[Foveation] NvAPI_Initialize failed ({st}); off");
            return false;
        }

        // The D3D11 device, from any texture Unity made, and its immediate context (what Unity draws with)
        var probe = new Texture2D(4, 4);
        var tex = probe.GetNativeTexturePtr();
        if (tex == IntPtr.Zero)
        {
            Plugin.Logger.LogWarning("[Foveation] no native texture to find the D3D11 device; off");
            return false;
        }
        IntPtr device, context;
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, void>)Vtbl(tex)[3])(tex, &device); // ID3D11DeviceChild::GetDevice
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, void>)Vtbl(device)[40])(device, &context); // ID3D11Device::GetImmediateContext
        UnityEngine.Object.Destroy(probe);
        if (device == IntPtr.Zero || context == IntPtr.Zero)
        {
            Plugin.Logger.LogWarning("[Foveation] D3D11 device/context not found; off");
            return false;
        }
        s_device = device;
        s_context = context;

        // On: one viewport (Unity uses one), surface value → rate. Off: zero viewports = VRS off everywhere
        s_onDesc = Marshal.AllocHGlobal(16 + 68);
        var vp = s_onDesc + 16;
        new Span<byte>((void*)vp, 68).Clear();
        *(byte*)vp = 1;
        var table = (int*)(vp + 4);
        for (var i = 0; i < 16; i++)
            table[i] = Rate1x1;
        table[1] = Rate2x1;
        table[2] = Rate2x2;
        table[3] = Rate4x4;
        *(uint*)s_onDesc = 16u | (1u << 16);
        *(uint*)(s_onDesc + 4) = 1;
        *(IntPtr*)(s_onDesc + 8) = vp;

        s_offDesc = Marshal.AllocHGlobal(16);
        *(uint*)s_offDesc = 16u | (1u << 16);
        *(uint*)(s_offDesc + 4) = 0;
        *(IntPtr*)(s_offDesc + 8) = IntPtr.Zero;

        s_callback = Marshal.GetFunctionPointerForDelegate(s_onRenderEvent);
        s_ready = true;
        Plugin.Logger.LogInfo("[Foveation] NVIDIA variable rate shading ready (D3D11)");
        return true;
    }

    private static IntPtr* Vtbl(IntPtr com) => *(IntPtr**)com;

    /// <summary>The shading-rate image for one eye: one byte per 16x16 pixel tile, an index into the rate table.</summary>
    private static bool EnsureView(int eye, int width, int height, float cx)
    {
        var s = Strength;
        if (s_views[eye] != IntPtr.Zero && s_viewW[eye] == width && s_viewH[eye] == height &&
            Mathf.Abs(s_viewStrength[eye] - s) < 0.001f && Mathf.Abs(s_viewCx[eye] - cx) < 0.01f)
            return true;
        if (width <= 0 || height <= 0)
            return false;

        // Ellipse radius 1 touches the middle of each image edge. Stronger = the sharp middle is smaller.
        var inner = 0.75f - 0.30f * s; // full rate inside (0.60 at 0.5, 0.45 at 1)
        var half = inner + 0.20f;      // 1 shade per 2 pixels
        var quarter = inner + 0.45f;   // 1 per 4 pixels; beyond (the far corners) 1 per 16

        int w = (width + 15) / 16, h = (height + 15) / 16;
        var data = new byte[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var dx = ((x + 0.5f) * 16f / width - cx) / 0.5f;
                var dy = ((y + 0.5f) * 16f / height - 0.5f) / 0.5f;
                var r = Mathf.Sqrt(dx * dx + dy * dy);
                data[y * w + x] = (byte)(r < inner ? 0 : r < half ? 1 : r < quarter ? 2 : 3);
            }

        // D3D11_TEXTURE2D_DESC: R8_UINT, default usage, shader resource
        var desc = stackalloc uint[10] { (uint)w, (uint)h, 1, 1, 62, 1, 0, 0, 8, 0 };
        IntPtr texture;
        int hr;
        fixed (byte* p = data)
        {
            var init = stackalloc byte[16];
            *(IntPtr*)init = (IntPtr)p;
            *(uint*)(init + 8) = (uint)w;
            *(uint*)(init + 12) = 0;
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint*, byte*, IntPtr*, int>)Vtbl(s_device)[5])(s_device, desc, init, &texture);
        }
        if (hr < 0 || texture == IntPtr.Zero)
        {
            Plugin.Logger.LogWarning($"[Foveation] couldn't make the {w}x{h} shading-rate image (0x{hr:X8}); off");
            s_ready = false;
            return false;
        }

        // NV_D3D11_SHADING_RATE_RESOURCE_VIEW_DESC_V1: version, R8_UINT, TEXTURE2D, MipSlice 0 (+ union padding)
        var vdesc = stackalloc uint[6] { 24u | (1u << 16), 62, 4, 0, 0, 0 };
        IntPtr view;
        var st = s_createView(s_device, texture, (IntPtr)vdesc, &view);
        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Vtbl(texture)[2])(texture); // the view keeps its own reference
        if (st != 0 || view == IntPtr.Zero)
        {
            Plugin.Logger.LogWarning($"[Foveation] NVIDIA refused the shading-rate view (status {st}); off");
            s_ready = false;
            return false;
        }

        // The old view may still be queued on the render thread, so it is kept rather than released (a few KB)
        s_views[eye] = view;
        s_viewW[eye] = width;
        s_viewH[eye] = height;
        s_viewStrength[eye] = s;
        s_viewCx[eye] = cx;
        Plugin.Logger.LogInfo($"[Foveation] {(eye == 0 ? "left" : "right")} eye {width}x{height}: lens centre at " +
                              $"{cx:0.000} of the width, strength {s:0.00} (full rate within {inner:0.00}, half to {half:0.00}, " +
                              $"quarter to {quarter:0.00})");
        return true;
    }
}
