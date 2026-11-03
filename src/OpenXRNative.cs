using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx;

namespace OperatorVR;

/// <summary>
/// P/Invoke bindings into UnityOpenXR.dll (from com.unity.xr.openxr 1.19.0).
/// Signatures are copied from the package's OpenXRLoaderBase.cs / OpenXRFeatureInternal.cs /
/// OpenXRRenderSettings.cs, since the game ships none of the managed OpenXR code.
/// </summary>
internal static class OpenXRNative
{
    private const string Lib = "UnityOpenXR";

    /// <summary>OPERATOR_Data\Plugins\x86_64, where Unity keeps native plugins.</summary>
    internal static readonly string PluginDir =
        Path.Combine(Paths.GameRootPath, $"{Paths.ProcessName}_Data", "Plugins", "x86_64");

    /// <summary>
    /// .NET only probes the mod's folder and the game root for DllImport, so point
    /// "UnityOpenXR" at the copy in Plugins\x86_64. If Unity already loaded it, this returns the same module.
    /// </summary>
    internal static void RegisterResolver()
    {
        NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), Resolve);
    }

    // Named method rather than a lambda: the lambda form needs NullableAttribute, which this build lacks
    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Lib)
            return IntPtr.Zero;
        var path = Path.Combine(PluginDir, Lib + ".dll");
        if (NativeLibrary.TryLoad(path, out var handle))
            return handle;
        Plugin.Logger.LogError($"[OpenXR] Could not load {path}");
        return IntPtr.Zero;
    }

    /// <summary>Mirrors OpenXRFeature.NativeEvent; order matters.</summary>
    internal enum NativeEvent
    {
        XrSetupConfigValues,
        XrSystemIdChanged,
        XrInstanceChanged,
        XrSessionChanged,
        XrBeginSession,
        XrSessionStateChanged,
        XrChangedSpaceApp,
        XrEndSession,
        XrDestroySession,
        XrDestroyInstance,
        XrIdle,
        XrReady,
        XrSynchronized,
        XrVisible,
        XrFocused,
        XrStopping,
        XrExiting,
        XrLossPending,
        XrInstanceLossPending,
        XrRestartRequested,
        XrRequestRestartLoop,
        XrRequestGetSystemLoop,
        XrExtensionsReady,
    }

    /// <summary>Mirrors OpenXRSettings.RenderMode.</summary>
    internal enum RenderMode
    {
        MultiPass,
        SinglePassInstanced,
    }

    /// <summary>Mirrors OpenXRSettings.DepthSubmissionMode.</summary>
    internal enum DepthSubmissionMode
    {
        None,
        Depth16Bit,
        Depth24Bit,
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ReceiveNativeEventDelegate(NativeEvent e, ulong payload);

    [DllImport(Lib, EntryPoint = "main_LoadOpenXRLibrary")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool LoadOpenXRLibrary(byte[] loaderPath);

    [DllImport(Lib, EntryPoint = "main_UnloadOpenXRLibrary")]
    internal static extern void UnloadOpenXRLibrary();

    [DllImport(Lib, EntryPoint = "NativeConfig_SetCallbacks")]
    internal static extern void SetCallbacks(ReceiveNativeEventDelegate callback);

    [DllImport(Lib, EntryPoint = "NativeConfig_SetApplicationInfo", CharSet = CharSet.Ansi)]
    internal static extern void SetApplicationInfo(string applicationName, string applicationVersion, uint applicationVersionHash, string engineVersion);

    [DllImport(Lib, EntryPoint = "NativeConfig_GetProcAddressPtr")]
    internal static extern IntPtr GetProcAddressPtr([MarshalAs(UnmanagedType.I1)] bool loaderDefault);

    [DllImport(Lib, EntryPoint = "NativeConfig_SetProcAddressPtrAndLoadStage1")]
    internal static extern void SetProcAddressPtrAndLoadStage1(IntPtr func);

    [DllImport(Lib, EntryPoint = "NativeConfig_SetRenderMode")]
    internal static extern void SetRenderMode(RenderMode renderMode);

    [DllImport(Lib, EntryPoint = "NativeConfig_GetRenderMode")]
    internal static extern RenderMode GetRenderMode();

    [DllImport(Lib, EntryPoint = "NativeConfig_SetDepthSubmissionMode")]
    internal static extern void SetDepthSubmissionMode(DepthSubmissionMode mode);

    [DllImport(Lib, EntryPoint = "NativeConfig_GetDepthSubmissionMode")]
    internal static extern DepthSubmissionMode GetDepthSubmissionMode();

    [DllImport(Lib, EntryPoint = "NativeConfig_GetRuntimeName")]
    internal static extern IntPtr GetRuntimeName();

    [DllImport(Lib, EntryPoint = "NativeConfig_SetSymmetricProjection")]
    internal static extern void SetSymmetricProjection([MarshalAs(UnmanagedType.I1)] bool enabled);

    [DllImport(Lib, EntryPoint = "session_SetSuccessfullyInitialized")]
    internal static extern void SetSuccessfullyInitialized([MarshalAs(UnmanagedType.I1)] bool value);

    [DllImport(Lib, EntryPoint = "session_InitializeSession")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool InitializeSession();

    [DllImport(Lib, EntryPoint = "session_CreateSessionIfNeeded")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool CreateSessionIfNeeded();

    [DllImport(Lib, EntryPoint = "session_BeginSession")]
    internal static extern void BeginSession();

    [DllImport(Lib, EntryPoint = "session_EndSession")]
    internal static extern void EndSession();

    [DllImport(Lib, EntryPoint = "session_RequestExitSession")]
    internal static extern void RequestExitSession();

    [DllImport(Lib, EntryPoint = "session_DestroySession")]
    internal static extern void DestroySession();

    [DllImport(Lib, EntryPoint = "messagepump_PumpMessageLoop")]
    internal static extern void PumpMessageLoop();

    [DllImport(Lib, EntryPoint = "session_GetSessionState")]
    internal static extern void GetSessionState(out int oldState, out int newState);

    /// <summary>Same wide-string encoding the package uses for the loader path on Windows.</summary>
    internal static byte[] ToWideString(string s) => System.Text.Encoding.Unicode.GetBytes(s + '\0');
}
