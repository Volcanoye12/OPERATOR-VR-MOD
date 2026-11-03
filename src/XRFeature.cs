using UnityEngine;
using UnityEngine.Bindings;
using UnityEngine.XR;

namespace OperatorVR;

/// <summary>
/// Reads XR InputDevice features by usage name. Unity 6 passes the name to native code as a span, and the
/// interop's string overloads (InputDevices.TryGetFeatureValue_bool etc.) fail with "Method not found:
/// ReadOnlySpan.GetPinnableReference", so these call the *_Injected variants directly with the name pinned.
/// </summary>
internal static unsafe class XRFeature
{
    internal static bool Bool(ulong device, string usage, out bool value)
    {
        fixed (char* c = usage)
        {
            var name = new ManagedSpanWrapper(c, usage.Length);
            return InputDevices.TryGetFeatureValue_bool_Injected(device, ref name, out value);
        }
    }

    internal static bool Float(ulong device, string usage, out float value)
    {
        fixed (char* c = usage)
        {
            var name = new ManagedSpanWrapper(c, usage.Length);
            return InputDevices.TryGetFeatureValue_float_Injected(device, ref name, out value);
        }
    }

    internal static bool Vector2(ulong device, string usage, out Vector2 value)
    {
        fixed (char* c = usage)
        {
            var name = new ManagedSpanWrapper(c, usage.Length);
            return InputDevices.TryGetFeatureValue_Vector2f_Injected(device, ref name, out value);
        }
    }

    internal static bool Vector3(ulong device, string usage, out Vector3 value)
    {
        fixed (char* c = usage)
        {
            var name = new ManagedSpanWrapper(c, usage.Length);
            return InputDevices.TryGetFeatureValue_Vector3f_Injected(device, ref name, out value);
        }
    }

    internal static bool Quaternion(ulong device, string usage, out Quaternion value)
    {
        fixed (char* c = usage)
        {
            var name = new ManagedSpanWrapper(c, usage.Length);
            return InputDevices.TryGetFeatureValue_Quaternionf_Injected(device, ref name, out value);
        }
    }
}
