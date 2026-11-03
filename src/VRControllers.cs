using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.XR;

namespace OperatorVR;

/// <summary>
/// Right-controller laser pointer for menus and laptop screens. While VRPointer has a target canvas, a yellow
/// beam comes out of the right controller's aim pose; where it meets the canvas the Windows cursor is moved
/// there (so the existing mouse pointer, arrow and UI clicks all follow it), and the trigger presses the left
/// mouse button. The beam only takes over the cursor when the controller moves, so the mouse still works when
/// the controller is put down. Controllers come from OpenXRInputSetup (Oculus Touch profile).
/// </summary>
public class VRControllers : MonoBehaviour
{
    public VRControllers(IntPtr ptr) : base(ptr) { }

    private const float BeamWidth = 0.004f;
    private const float MaxBeam = 10f;
    // Trigger travel (0..1) that counts as a click, with a lower release point so it doesn't chatter
    private const float PressAt = 0.6f, ReleaseAt = 0.4f;

    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;

    private GameObject _beam;
    private bool _pressed;
    private Vector2 _lastUv = new(-1f, -1f);
    private bool _loggedDevice, _loggedNoDevice;

    private void Update()
    {
        using var __t = ModTime.Measure("VRControllers");
        // Playing with a gamepad (Controls.TouchControls off): no laser, it would move the pointer the pad drives
        if (!OpenXRBootstrap.IsRunning || !Plugin.TouchControls.Value)
        {
            Hide();
            ReleaseButton();
            _loggedDevice = _loggedNoDevice = false;
            return;
        }

        try
        {
            var target = VRPointer.Target;
            if (target == null || !TryGetAim(out var origin, out var dir, out var trigger))
            {
                Hide();
                ReleaseButton();
                return;
            }

            // Where the beam meets the canvas plane (a world-space canvas faces along -forward)
            var t = target.transform;
            var normal = t.forward;
            var denom = Vector3.Dot(dir, normal);
            var hit = false;
            var hitPoint = origin + dir * MaxBeam;
            var uv = Vector2.zero;
            if (Mathf.Abs(denom) > 1e-4f)
            {
                var dist = Vector3.Dot(t.position - origin, normal) / denom;
                if (dist > 0f && dist < MaxBeam)
                {
                    var p = origin + dir * dist;
                    var rt = target.GetComponent<RectTransform>();
                    var local = rt.InverseTransformPoint(p);
                    var rect = rt.rect;
                    uv = new Vector2((local.x - rect.xMin) / rect.width, (local.y - rect.yMin) / rect.height);
                    if (uv.x >= 0f && uv.x <= 1f && uv.y >= 0f && uv.y <= 1f)
                    {
                        hit = true;
                        hitPoint = p;
                    }
                }
            }

            DrawBeam(origin, hitPoint, hit);

            if (hit)
            {
                // Only take over the cursor when the beam has actually moved, so the mouse keeps working
                if ((uv - _lastUv).sqrMagnitude > 0.0015f * 0.0015f)
                {
                    VRPointer.MoveMouseToViewport(uv.x, uv.y);
                    _lastUv = uv;
                }
                // With Touch controls on, the trigger already presses the Shoot key (left mouse), which clicks the UI;
                // a second click here would double up
                if (!_pressed && trigger >= PressAt && !TouchKeys.Active)
                {
                    _pressed = true;
                    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                }
            }
            if (_pressed && trigger <= ReleaseAt)
                ReleaseButton();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Controllers] {e.GetType().Name}: {e.Message}");
            Hide();
        }
    }

    private void ReleaseButton()
    {
        if (!_pressed)
            return;
        _pressed = false;
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }

    /// <summary>Right controller's aim ray in the game world, and its trigger (0..1).</summary>
    private bool TryGetAim(out Vector3 origin, out Vector3 dir, out float trigger)
    {
        origin = dir = default;
        trigger = 0f;
        var device = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        if (!device.isValid)
        {
            if (!_loggedNoDevice)
            {
                _loggedNoDevice = true;
                Plugin.Logger.LogInfo("[Controllers] No right controller found yet");
            }
            return false;
        }
        var id = device.deviceId;
        if (!_loggedDevice)
        {
            _loggedDevice = true;
            Plugin.Logger.LogInfo($"[Controllers] Right controller: '{device.name}' ({device.manufacturer}), characteristics {device.characteristics}");
        }

        // Aim pose (along the pointing direction), falling back to the grip pose
        if (!XRFeature.Vector3(id, "PointerPosition", out var pos) ||
            !XRFeature.Quaternion(id, "PointerRotation", out var rot))
        {
            if (!XRFeature.Vector3(id, "DevicePosition", out pos) ||
                !XRFeature.Quaternion(id, "DeviceRotation", out rot))
                return false;
        }
        XRFeature.Float(id, "Trigger", out trigger);

        if (!HeadTracking.TryTrackingToWorld(pos, rot, out origin, out var worldRot))
            return false;
        dir = worldRot * Vector3.forward;
        return true;
    }

    private void DrawBeam(Vector3 from, Vector3 to, bool hit)
    {
        if (_beam == null)
        {
            // Borrow a cube's mesh and the pipeline's default material, without the collider a primitive comes with
            // (it could block the game's own interaction raycasts)
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var mesh = tmp.GetComponent<MeshFilter>().sharedMesh;
            var material = new Material(tmp.GetComponent<Renderer>().sharedMaterial);
            UnityEngine.Object.Destroy(tmp);

            _beam = new GameObject("OperatorVR Laser");
            UnityEngine.Object.DontDestroyOnLoad(_beam);
            _beam.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = _beam.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }
        var mat = _beam.GetComponent<Renderer>().sharedMaterial;
        var color = hit ? new Color(1f, 0.85f, 0.2f) : new Color(0.6f, 0.6f, 0.6f);
        if (mat.HasProperty("_BaseColor"))
            mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_EmissiveColor"))
            mat.SetColor("_EmissiveColor", color * 2f);

        var length = Vector3.Distance(from, to);
        // Drawn over everything, like the menus it points at
        MenusOnTop.Put(_beam);
        _beam.SetActive(true);
        _beam.transform.SetPositionAndRotation((from + to) * 0.5f, Quaternion.LookRotation(to - from));
        _beam.transform.localScale = new Vector3(BeamWidth, BeamWidth, length);
    }

    private void Hide()
    {
        if (_beam != null && _beam.activeSelf)
            _beam.SetActive(false);
    }
}
