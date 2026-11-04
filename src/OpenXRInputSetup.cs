using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OperatorVR;

/// <summary>
/// Registers the controllers with UnityOpenXR, the way the package's OpenXRInput.AttachActionSets does for the
/// Oculus Touch interaction profile (com.unity.xr.openxr 1.19, Runtime/input/OpenXRInput.cs and
/// Features/Interactions/OculusTouchControllerProfile.cs). Without this the runtime has no actions, so no
/// controllers appear. Must run after xrBeginSession and before the input subsystem starts, once per session.
/// Afterwards the controllers are ordinary XR InputDevices with the usages named below.
/// </summary>
internal static class OpenXRInputSetup
{
    private const string Lib = "UnityOpenXR";
    private const string Profile = "/interaction_profiles/oculus/touch_controller";
    private const string Left = "/user/hand/left";
    private const string Right = "/user/hand/right";

    // InputDeviceCharacteristics: HeldInHand 4 | TrackedDevice 32 | Controller 64 | Left 256 / Right 512
    private const uint LeftHand = 4 | 32 | 64 | 256;
    private const uint RightHand = 4 | 32 | 64 | 512;

    // OpenXRInteractionFeature.ActionType
    private const uint Binary = 0, Axis1D = 1, Axis2D = 2, Pose = 3, Vibrate = 4;

    [StructLayout(LayoutKind.Explicit)]
    private struct SerializedGuid
    {
        [FieldOffset(0)] public ulong ulong1;
        [FieldOffset(8)] public ulong ulong2;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct SerializedBinding
    {
        public ulong actionId;
        public string path;
    }

    [DllImport(Lib, EntryPoint = "OpenXRInputProvider_RegisterDeviceDefinition", CharSet = CharSet.Ansi)]
    private static extern ulong RegisterDeviceDefinition(string userPath, string interactionProfile, [MarshalAs(UnmanagedType.I1)] bool isAdditive,
                                                         uint characteristics, string name, string manufacturer, string serialNumber);

    [DllImport(Lib, EntryPoint = "OpenXRInputProvider_CreateActionSet", CharSet = CharSet.Ansi)]
    private static extern ulong CreateActionSet(string name, string localizedName, SerializedGuid guid);

    [DllImport(Lib, EntryPoint = "OpenXRInputProvider_CreateAction", CharSet = CharSet.Ansi)]
    private static extern ulong CreateAction(ulong actionSetId, string name, string localizedName, uint actionType, SerializedGuid guid,
                                             string[] userPaths, uint userPathCount, [MarshalAs(UnmanagedType.I1)] bool isAdditive,
                                             string[] usages, uint usageCount);

    [DllImport(Lib, EntryPoint = "OpenXRInputProvider_SuggestBindings", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SuggestBindings(string interactionProfile, SerializedBinding[] bindings, uint count);

    [DllImport(Lib, EntryPoint = "OpenXRInputProvider_AttachActionSets", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool AttachActionSets();

    // name, type, usage (the XR InputDevice feature name), input path on the left / right controller (null = not on that hand).
    // OpenXR action names must be lowercase path-safe (the package lowercases them); "devicePose" was rejected
    private static readonly (string name, uint type, string usage, string left, string right)[] Actions =
    {
        ("devicepose", Pose, "Device", "/input/grip/pose", "/input/grip/pose"),
        ("pointer", Pose, "Pointer", "/input/aim/pose", "/input/aim/pose"),
        ("trigger", Axis1D, "Trigger", "/input/trigger/value", "/input/trigger/value"),
        ("triggerpressed", Binary, "TriggerButton", "/input/trigger/value", "/input/trigger/value"),
        ("grip", Axis1D, "Grip", "/input/squeeze/value", "/input/squeeze/value"),
        ("grippressed", Binary, "GripButton", "/input/squeeze/value", "/input/squeeze/value"),
        ("thumbstick", Axis2D, "Primary2DAxis", "/input/thumbstick", "/input/thumbstick"),
        ("thumbstickclicked", Binary, "Primary2DAxisClick", "/input/thumbstick/click", "/input/thumbstick/click"),
        // Thumb resting on the thumbrest (touch only; the left one switches on the virtual D-pad)
        ("thumbresttouched", Binary, "ThumbrestTouch", "/input/thumbrest/touch", "/input/thumbrest/touch"),
        // X / A and Y / B
        ("primarybutton", Binary, "PrimaryButton", "/input/x/click", "/input/a/click"),
        ("secondarybutton", Binary, "SecondaryButton", "/input/y/click", "/input/b/click"),
        // Only the left controller has a menu button the app may use (the right one is the system button)
        ("menu", Binary, "MenuButton", "/input/menu/click", null),
        // Vibration (2026-11-04): without an output action the controllers reported no haptics, and every vibration
        // was refused ("didn't take a vibration"). The package's Oculus Touch profile binds it the same way
        ("haptic", Vibrate, "Haptic", "/output/haptic", "/output/haptic"),
    };

    /// <summary>Returns true if the action sets were attached; logs what failed otherwise.</summary>
    internal static bool Attach()
    {
        try
        {
            if (RegisterDeviceDefinition(Left, Profile, false, LeftHand, "Oculus Touch Controller OpenXR", "Oculus", "") == 0 ||
                RegisterDeviceDefinition(Right, Profile, false, RightHand, "Oculus Touch Controller OpenXR", "Oculus", "") == 0)
            {
                Plugin.Logger.LogWarning("[Controllers] Registering the controller devices failed");
                return false;
            }

            var set = CreateActionSet("operatorvr", "OperatorVR", new SerializedGuid());
            if (set == 0)
            {
                Plugin.Logger.LogWarning("[Controllers] Creating the action set failed");
                return false;
            }

            var bindings = new List<SerializedBinding>();
            foreach (var (name, type, usage, left, right) in Actions)
            {
                var hands = new List<string>();
                if (left != null) hands.Add(Left);
                if (right != null) hands.Add(Right);
                var id = CreateAction(set, name, name, type, new SerializedGuid(), hands.ToArray(), (uint)hands.Count, false, new[] { usage }, 1);
                if (id == 0)
                {
                    Plugin.Logger.LogWarning($"[Controllers] Creating action '{name}' failed");
                    return false;
                }
                if (left != null)
                    bindings.Add(new SerializedBinding { actionId = id, path = Left + left });
                if (right != null)
                    bindings.Add(new SerializedBinding { actionId = id, path = Right + right });
            }

            if (!SuggestBindings(Profile, bindings.ToArray(), (uint)bindings.Count))
            {
                Plugin.Logger.LogWarning("[Controllers] Suggesting bindings failed");
                return false;
            }
            if (!AttachActionSets())
            {
                Plugin.Logger.LogWarning("[Controllers] Attaching the action sets failed");
                return false;
            }
            Plugin.Logger.LogInfo($"[Controllers] Oculus Touch actions attached ({Actions.Length} actions, {bindings.Count} bindings)");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Controllers] Input setup failed: {e.GetType().Name}: {e.Message}");
            return false;
        }
    }
}
