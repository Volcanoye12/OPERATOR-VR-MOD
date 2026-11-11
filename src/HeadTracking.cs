using System;
using System.Collections;
using System.Runtime.InteropServices;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR;

namespace OperatorVR;

/// <summary>
/// Makes the headset look around inside the game world. The game keeps driving the camera
/// (mouse/keyboard turn and move the body); just before each frame renders we add the head
/// pose on top, then put the game's own pose back after the frame so game logic never sees it.
/// F10 recenters.
/// </summary>
public class HeadTracking : MonoBehaviour
{
    public HeadTracking(IntPtr ptr) : base(ptr) { }

    private const int VK_F10 = 0x79;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private Camera _cam;
    private bool _applied;
    private Vector3 _gamePos;
    private Quaternion _gameRot;

    private bool _haveOrigin;
    private Vector3 _originPos;
    private Quaternion _originYawInv = Quaternion.identity;
    // Turns the runtime's tracking space the right way up if it reported it upside down (identity normally)
    private Quaternion _flip = Quaternion.identity;
    private float _upsideDownFor, _lastHeadYaw;
    private Vector3 _lastHeadPos;
    private bool _haveLastHead;
    private bool _f10Down;
    private string _scene = "";
    private float _sceneSince;
    private bool _sceneRecentered;
    private bool _loggedActive;
    private bool _restoreLoopSeen;
    private bool _loggedFallback;
    private bool _loggedWaiting;

    private static HeadTracking s_instance;

    // View yaw set by the Touch controls (hand aim): the game's camera yaw then follows the hand, so the view can't
    // use it. Used only if set this frame or last; otherwise the view follows the game's yaw as before
    private static float s_viewYaw;
    private static int s_viewYawFrame = -10;

    /// <summary>Metres the view steps back from the body (eased); the armoury bench uses it so the gun isn't in your face.</summary>
    internal static float ViewBack;
    private float _viewBackNow;

    /// <summary>Sets the yaw (degrees) the VR view faces this frame, independent of the game's camera yaw.</summary>
    internal static void SetViewYaw(float yaw)
    {
        s_viewYaw = yaw;
        s_viewYawFrame = Time.frameCount;
    }

    /// <summary>
    /// Puts the game camera where the player's head is in the world (as rendered) until RestoreGameCameraPose.
    /// For game code that should use where you look rather than where the gun points (e.g. finding interactables).
    /// </summary>
    /// <summary>The game camera and its own pose, without the head pose added for VR (e.g. the customisation view).</summary>
    internal static bool TryGetGameCameraPose(out Camera cam, out Vector3 pos, out Quaternion rot)
    {
        var self = s_instance;
        cam = self != null && self._cam != null ? self._cam : Camera.main;
        pos = default;
        rot = Quaternion.identity;
        if (cam == null)
            return false;
        var applied = self != null && self._applied && self._cam != null && cam.Pointer == self._cam.Pointer;
        pos = applied ? self._gamePos : cam.transform.position;
        rot = applied ? self._gameRot : cam.transform.rotation;
        return true;
    }

    internal static bool TryApplyHeadPoseToGameCamera(out Vector3 savedPos, out Quaternion savedRot)
    {
        savedPos = default;
        savedRot = Quaternion.identity;
        var self = s_instance;
        if (self == null || !self._haveOrigin || !OpenXRBootstrap.IsRunning)
            return false;
        var cam = self._cam != null ? self._cam : Camera.main;
        if (cam == null)
            return false;
        var headRot = InputTracking.GetLocalRotation(XRNode.CenterEye);
        var headPos = InputTracking.GetLocalPosition(XRNode.CenterEye);
        if (!TryTrackingToWorld(headPos, headRot, out var worldPos, out var worldRot))
            return false;
        var t = cam.transform;
        savedPos = t.position;
        savedRot = t.rotation;
        t.SetPositionAndRotation(Plugin.PositionalTracking.Value ? worldPos : savedPos, worldRot);
        return true;
    }

    internal static void RestoreGameCameraPose(Vector3 pos, Quaternion rot)
    {
        var self = s_instance;
        var cam = self != null && self._cam != null ? self._cam : Camera.main;
        if (cam != null)
            cam.transform.SetPositionAndRotation(pos, rot);
    }

    /// <summary>The yaw the VR view faces, given the game camera's rotation (its own yaw unless the Touch controls set one).</summary>
    internal static float ViewYaw(Quaternion gameRot) => BodyYaw(gameRot);

    private static float BodyYaw(Quaternion gameRot) =>
        s_viewYawFrame >= Time.frameCount - 1 ? s_viewYaw : gameRot.eulerAngles.y;

    /// <summary>
    /// The game's own camera pitch and yaw in degrees (pitch positive = looking down, Unity's convention), without
    /// the head pose. The view doesn't use them (head + view yaw), but the game aims the weapon with them.
    /// </summary>
    // A recenter asked for directly (the Touch controls), not through a pressed F10: the 22:03 play held the right
    // stick down and no recenter happened, the simulated F10 never seen
    private static bool s_recenterAsked;

    internal static void RequestRecenter() => s_recenterAsked = true;

    // A recenter a moment from now (any thread: the headset's own events come in on the runtime's thread). The user,
    // 2026-11-10: after a pause (headset off for the bathroom) the head comes back somewhere else, so wherever it is
    // then becomes the standing spot
    private static long s_recenterAtTick;
    private static string s_recenterWhy;

    internal static void RecenterAfter(int milliseconds, string why)
    {
        s_recenterWhy = why;
        System.Threading.Interlocked.Exchange(ref s_recenterAtTick, Environment.TickCount64 + milliseconds);
    }

    internal static bool TryGetGameAim(out float pitch, out float yaw)
    {
        pitch = yaw = 0f;
        var self = s_instance;
        var cam = self != null && self._cam != null ? self._cam : Camera.main;
        if (cam == null)
            return false;
        var rot = self != null && self._applied ? self._gameRot : cam.transform.rotation;
        pitch = Mathf.DeltaAngle(0f, rot.eulerAngles.x);
        yaw = rot.eulerAngles.y;
        return true;
    }

    /// <summary>
    /// A pose in the headset's tracking space (e.g. a controller) converted to the game world the same way the
    /// head is: relative to the recentered origin, turned with the view's yaw, placed at the game camera.
    /// False until the head has been recentered.
    /// </summary>
    internal static bool TryTrackingToWorld(Vector3 pos, Quaternion rot, out Vector3 worldPos, out Quaternion worldRot)
    {
        worldPos = default;
        worldRot = Quaternion.identity;
        var self = s_instance;
        if (self == null || !self._haveOrigin)
            return false;
        var cam = self._cam != null ? self._cam : Camera.main;
        if (cam == null)
            return false;

        // Same upside-down correction as the head (controllers live in the same tracking space)
        pos = self._flip * pos;
        rot = self._flip * rot;

        // The game's own camera pose, not the one with the head pose added for rendering
        var gamePos = self._applied ? self._gamePos : cam.transform.position;
        var gameRot = self._applied ? self._gameRot : cam.transform.rotation;
        var bodyYaw = Quaternion.Euler(0f, BodyYaw(gameRot), 0f);
        var bodyRot = Plugin.UseGamePitch.Value ? Quaternion.Euler(gameRot.eulerAngles.x, BodyYaw(gameRot), 0f) : bodyYaw;

        worldPos = gamePos + bodyYaw * (self._originYawInv * (pos - self._originPos));
        worldRot = bodyRot * (self._originYawInv * rot);
        return true;
    }

    private void Start()
    {
        s_instance = this;
        // Keep a strong reference to the Il2Cpp delegate for the lifetime of the component
        Application.add_onBeforeRender((UnityAction)new Action(OnBeforeRender));
        StartCoroutine(RestoreAfterFrame().WrapToIl2Cpp());
        Plugin.Logger.LogInfo("[Head] Head tracking ready (F10 recenters)");
    }

    // A letter or digit key name -> Windows virtual-key code (letters and digits share their ASCII codes)
    private static int RecenterKeyCode()
    {
        var name = Plugin.RecenterKey.Value;
        if (string.IsNullOrEmpty(name))
            return 0;
        name = name.Trim().ToUpperInvariant();
        return name.Length == 1 && char.IsLetterOrDigit(name[0]) ? name[0] : 0;
    }

    private void Update()
    {
        using var __t = ModTime.Measure("HeadTracking");
        // Safety net: if the end-of-frame restore didn't run, hand the game its camera back before it moves it
        if (_applied)
        {
            RestoreGamePose();
            if (!_loggedFallback)
            {
                _loggedFallback = true;
                Plugin.Logger.LogWarning("[Head] End-of-frame restore missed; restored in Update instead");
            }
        }

        // F10, or Head.RecenterKey (U by default: the user's pad sends it on L1 + Create, and the game doesn't use it),
        // so the view can be put right from the pad after stepping away
        var down = (GetAsyncKeyState(VK_F10) & 0x8000) != 0 || (RecenterKeyCode() != 0 && (GetAsyncKeyState(RecenterKeyCode()) & 0x8000) != 0);
        if ((down && !_f10Down) || s_recenterAsked)
        {
            _haveOrigin = false;
            Plugin.Logger.LogInfo($"[Head] Recenter requested{(s_recenterAsked ? " (controller)" : "")}");
        }
        s_recenterAsked = false;
        var at = System.Threading.Interlocked.Read(ref s_recenterAtTick);
        if (at > 0 && Environment.TickCount64 >= at)
        {
            System.Threading.Interlocked.Exchange(ref s_recenterAtTick, 0);
            _haveOrigin = false;
            Plugin.Logger.LogInfo($"[Head] Recenter: {s_recenterWhy}");
        }
        _f10Down = down;

        // A few seconds after a new level comes up, recenter by itself (Head.RecenterOnLoad; the user asked for it,
        // 2026-11-02, so F10 isn't needed after every load). Waits for the level to settle past its loading stall
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? "";
        if (scene != _scene)
        {
            _scene = scene;
            _sceneSince = Time.unscaledTime;
            _sceneRecentered = false;
        }
        if (!_sceneRecentered && OpenXRBootstrap.IsRunning && Plugin.RecenterOnLoad.Value && Time.unscaledTime - _sceneSince >= 4f &&
            Time.unscaledDeltaTime < 0.2f)
        {
            _sceneRecentered = true;
            _haveOrigin = false;
            Plugin.Logger.LogInfo($"[Head] Recenter after loading '{scene}'");
        }

        if (!OpenXRBootstrap.IsRunning)
        {
            // Recenter automatically the next time VR starts
            _haveOrigin = false;
            _loggedActive = false;
            _loggedWaiting = false;
        }
    }

    private void OnBeforeRender()
    {
        using var __t = ModTime.Measure("HeadTracking render");
        if (!OpenXRBootstrap.IsRunning)
            return;

        try
        {
            if (_cam == null)
                _cam = Camera.main;
            if (_cam == null)
                return;

            var headRot = InputTracking.GetLocalRotation(XRNode.CenterEye);
            var headPos = InputTracking.GetLocalPosition(XRNode.CenterEye);

            // Right after VR starts the runtime reports an all-zero pose; recentering on that put the
            // camera a full standing height above the player, so wait for real tracking first
            var tracking = headPos.sqrMagnitude > 1e-6f || Quaternion.Angle(headRot, Quaternion.identity) > 0.01f;
            if (!tracking)
            {
                if (!_loggedWaiting)
                {
                    _loggedWaiting = true;
                    Plugin.Logger.LogInfo("[Head] Waiting for headset tracking...");
                }
                return;
            }

            if (!_haveOrigin)
            {
                // Some runs the runtime reported the whole tracking space upside down (head at y = -1.36 instead
                // of +1.36, view upside down). If the headset's up points down when recentering, turn tracking
                // 180° about the facing direction so it's the right way up again. Normal head tilt is far below 90°
                var up = headRot * Vector3.up;
                _flip = Quaternion.identity;
                if (up.y < 0f && Plugin.FixUpsideDown.Value)
                {
                    var fwd = headRot * Vector3.forward;
                    var axis = new Vector3(fwd.x, 0f, fwd.z);
                    axis = axis.sqrMagnitude > 1e-4f ? axis.normalized : Vector3.forward;
                    _flip = Quaternion.AngleAxis(180f, axis);
                    Plugin.Logger.LogWarning($"[Head] Headset reported upside down (up = {up}, position {headPos}); turning tracking the right way up");
                }
                headRot = _flip * headRot;
                headPos = _flip * headPos;

                // Forward = wherever the headset faces now; only yaw so the horizon stays level
                _originPos = headPos;
                _originYawInv = Quaternion.Inverse(Quaternion.Euler(0f, headRot.eulerAngles.y, 0f));
                _haveOrigin = true;
                Plugin.Logger.LogInfo($"[Head] Recentered at {headPos}");
            }
            else
            {
                headRot = _flip * headRot;
                headPos = _flip * headPos;

                // The headset's own reset (holding the Oculus button) changes the tracking space under us: one run
                // started upside down (corrected), the reset put it the right way up, and our correction then turned
                // the world upside down. If the view stays upside down, or the head jumps, recenter (which also
                // decides again whether tracking needs turning over)
                var upsideDown = (headRot * Vector3.up).y < -0.3f;
                _upsideDownFor = upsideDown ? _upsideDownFor + Time.unscaledDeltaTime : 0f;
                // A real head can't move half a metre or turn 45° in one frame
                var yaw = headRot.eulerAngles.y;
                var jumped = _haveLastHead && (Vector3.Distance(headPos, _lastHeadPos) > 0.5f ||
                                               Mathf.Abs(Mathf.DeltaAngle(yaw, _lastHeadYaw)) > 45f);
                _lastHeadPos = headPos;
                _lastHeadYaw = yaw;
                _haveLastHead = true;
                if (_upsideDownFor > 0.3f || jumped)
                {
                    Plugin.Logger.LogWarning(jumped
                        ? "[Head] Head jumped (headset reset?): recentering"
                        : "[Head] View upside down (headset reset?): recentering");
                    _upsideDownFor = 0f;
                    _haveLastHead = false;
                    _haveOrigin = false;
                    return;
                }
            }

            var relRot = _originYawInv * headRot;
            var relPos = _originYawInv * (headPos - _originPos);
            if (!Plugin.PositionalTracking.Value)
                relPos = Vector3.zero;
            relPos *= 1f - AimLock();
            // With the Touch controllers, aiming in: the sideways and up/down part of where your head is (compared with
            // the last recenter) fades out, so your eye is on the sight line wherever your head is (2026-11-11 17:13, the
            // user: "the sights weren't perfectly central, a bit to the right" — the recenter just before caught the head
            // 7 cm to one side). Forward/back still moves. (LockEyeWhenAiming off; the PS5 pad unchanged)
            // (Switched off 17:25: straight after it went in the user said "the aiming is not right, I can't look down the
            // scope properly". Aiming is back as it was before 17:16)
            if (false && Plugin.TouchControls.Value && !Plugin.LockEyeWhenAiming.Value)
            {
                var side = SideLock();
                relPos.x *= 1f - side;
                relPos.y *= 1f - side;
            }

            var t = _cam.transform;
            // If onBeforeRender fires again before a restore, keep the game's pose from the first call
            // instead of saving our own modified pose (which would stack the offset every call)
            if (!_applied)
            {
                _gamePos = t.position;
                _gameRot = t.rotation;
                _applied = true;
            }

            // Mouse up/down would tilt the whole world, which is uncomfortable in VR, so by default keep only the game's yaw
            var bodyYaw = Quaternion.Euler(0f, BodyYaw(_gameRot), 0f);
            var bodyRot = Plugin.UseGamePitch.Value ? Quaternion.Euler(_gameRot.eulerAngles.x, BodyYaw(_gameRot), 0f) : bodyYaw;

            var steady = SteadyEyes(_gamePos, _gameRot);
            _lastSteadyShift = steady - _gamePos;
            _viewBackNow = Mathf.MoveTowards(_viewBackNow, Mathf.Clamp(ViewBack, 0f, 1f), Time.unscaledDeltaTime * 0.6f);
            relPos += Vector3.back * _viewBackNow;
            _lastHeadShift = bodyYaw * relPos;
            t.SetPositionAndRotation(steady + bodyYaw * relPos, bodyRot * relRot);

            if (!_loggedActive)
            {
                _loggedActive = true;
                Plugin.Logger.LogInfo($"[Head] Applying head pose to '{_cam.name}' (positional={Plugin.PositionalTracking.Value}, gamePitch={Plugin.UseGamePitch.Value})");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Head] {e.GetType().Name}: {e.Message}");
        }
    }

    // ---- Pose report (with F5 snapshots) ----
    // The gun sat close to the face after using menus, and recentering didn't help. Logged with each F5: the game's
    // camera relative to the player's feet, the steady-eye-height and head shifts the view adds, and where the gun
    // is in front of the game camera, so a "normal" and a "too close" snapshot can be compared
    private Vector3 _lastSteadyShift, _lastHeadShift;

    internal static void LogPose(string why)
    {
        var self = s_instance;
        if (self == null)
            return;
        try
        {
            self.FindPlayer();
            var cam = self._cam != null ? self._cam : Camera.main;
            if (cam == null)
                return;
            // Outside onBeforeRender the camera has the game's own pose
            var camPos = cam.transform.position;
            var camRot = cam.transform.rotation;
            var feet = self._playerRoot != null ? self._playerRoot.position.y : float.NaN;
            var line = $"[Head] Pose ({why}): game camera {camPos.y - feet:0.00} m above feet, pitch {Mathf.DeltaAngle(0f, camRot.eulerAngles.x):0}°, " +
                       $"fov {cam.fieldOfView:0}; view adds steady-height {self._lastSteadyShift.y:+0.00;-0.00} m and head " +
                       $"({self._lastHeadShift.x:+0.00;-0.00}, {self._lastHeadShift.y:+0.00;-0.00}, {self._lastHeadShift.z:+0.00;-0.00}) m; " +
                       $"held eye height {self._eyeAbove:0.00} m; aiming {(self._player != null && self._player.isADS)}";
            if (self._player != null)
            {
                foreach (var (name, t) in new[] { ("weapon parent", self._player.ActiveWeaponParent), ("active weapon", self._player.activeWeapon != null ? self._player.activeWeapon.transform : null) })
                    if (t != null)
                    {
                        var local = Quaternion.Inverse(camRot) * (t.position - camPos);
                        line += $"; {name} '{t.name}' at right {local.x:+0.00;-0.00}, up {local.y:+0.00;-0.00}, forward {local.z:+0.00;-0.00} m from the camera";
                    }
            }
            Plugin.Logger.LogInfo(line);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Head] pose report: {e.GetType().Name}: {e.Message}");
        }
    }

    // ---- Eye on the sight while aiming (Head.LockEyeWhenAiming) ----
    // Aiming down the sights, the game lines the sight up with its camera position, but the view also carried the
    // head's movement since the last recenter (a few cm of lean, or a recenter taken slightly off), so the eye sat
    // beside the magnifier and its picture was lost "quite a few times". While aiming, that movement fades out
    // (in ~0.12 s) so the eye sits exactly where the game expects; it fades back in when the gun comes down.
    // Turning the head still looks around
    private float _aimLock;
    private bool _aimLogged;

    private float _sideLock;
    private bool _sideLogged;

    private float SideLock()
    {
        var ads = false;
        FindPlayer();
        try { ads = _player != null && _player.isADS; } catch { }
        _sideLock = Mathf.MoveTowards(_sideLock, ads ? 1f : 0f, Time.unscaledDeltaTime / 0.15f);
        if (ads && !_sideLogged)
        {
            _sideLogged = true;
            Plugin.Logger.LogInfo("[Head] Aiming (Touch): eye brought onto the sight line (sideways/up-down head offset faded out)");
        }
        return _sideLock;
    }

    private float AimLock()
    {
        var ads = false;
        if (Plugin.LockEyeWhenAiming.Value)
        {
            FindPlayer();
            try { ads = _player != null && _player.isADS; } catch { }
        }
        _aimLock = Mathf.MoveTowards(_aimLock, ads ? 1f : 0f, Time.unscaledDeltaTime / 0.12f);
        if (ads && !_aimLogged)
        {
            _aimLogged = true;
            Plugin.Logger.LogInfo("[Head] Aiming: eye held on the sight (head movement faded out until the gun comes down)");
        }
        return _aimLock;
    }

    private void FindPlayer()
    {
        if (_playerRoot != null || Time.unscaledTime < _nextPlayerFind)
            return;
        _nextPlayerFind = Time.unscaledTime + 2f;
        _haveEyeAbove = false;
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppInterop.Runtime.Il2CppType.Of<PlayerNetworking>(), FindObjectsSortMode.None))
        {
            var p = o.TryCast<PlayerNetworking>();
            if (p != null && p.gameObject.name.Contains("MyPlayer"))
            {
                _playerRoot = p.transform;
                _player = p;
                break;
            }
        }
    }

    // ---- Steady eye height (Head.SteadyEyeHeight) ----
    // The game's camera sits in the character's head, and looking down with the stick bends the neck and upper body
    // forward, so the head (and the VR view, which doesn't tilt with the stick) dropped to about 3/4 height. The
    // height above the player's feet is learnt while the game's aim is roughly level (so crouching or going prone
    // still lowers it), and held while the stick aims further up or down; rising is always followed at once
    private Transform _playerRoot;
    private PlayerNetworking _player;
    private float _nextPlayerFind;
    private float _eyeAbove;
    private bool _haveEyeAbove;

    /// <summary>The game's aim pitch (degrees up or down) while the view is held at standing height, else 0. The
    /// character is bent over then, and BodyHider hides its torso so you don't see it from the held view.</summary>
    internal static float EyeHeld { get; private set; }

    private Vector3 SteadyEyes(Vector3 gamePos, Quaternion gameRot)
    {
        EyeHeld = 0f;
        if (!Plugin.SteadyEyeHeight.Value)
            return gamePos;
        FindPlayer();
        if (_playerRoot == null)
            return gamePos;
        var feet = _playerRoot.position.y;
        var above = gamePos.y - feet;
        var pitch = Mathf.Abs(Mathf.DeltaAngle(0f, gameRot.eulerAngles.x));
        // 0 while roughly level (follow the game), 1 from 20° up or down (hold the learnt height)
        var hold = Mathf.InverseLerp(10f, 20f, pitch);

        // Not while aiming down the sights: the game puts the sights at its camera, and a held height left the eye
        // above or below them. The learnt height is only updated as usual meanwhile: it used to be set to the
        // camera's height outright, so aiming while looking down learnt the bent-over height, and after lowering the
        // gun the view stayed ducked until you looked level again
        var ads = false;
        try { ads = _player != null && _player.isADS; } catch { }
        if (ads)
        {
            if (!_haveEyeAbove || above > _eyeAbove)
            {
                _eyeAbove = above;
                _haveEyeAbove = true;
            }
            else
                _eyeAbove = Mathf.Lerp(_eyeAbove, above, (1f - hold) * Mathf.Min(1f, Time.unscaledDeltaTime * 8f));
            return gamePos;
        }
        if (!_haveEyeAbove)
        {
            _eyeAbove = above;
            _haveEyeAbove = true;
        }
        else if (above > _eyeAbove)
            _eyeAbove = above;                                                    // standing up: at once
        else
            _eyeAbove = Mathf.Lerp(_eyeAbove, above, (1f - hold) * Mathf.Min(1f, Time.unscaledDeltaTime * 8f));
        gamePos.y = Mathf.Lerp(gamePos.y, feet + _eyeAbove, hold);
        // How steep, while held (not aiming down the sights: the view then follows the game's camera)
        EyeHeld = hold > 0f ? pitch : 0f;
        return gamePos;
    }

    // Managed-only coroutine; HideFromIl2Cpp stops the class injector trying (and failing) to expose it
    [HideFromIl2Cpp]
    private IEnumerator RestoreAfterFrame()
    {
        while (true)
        {
            yield return new WaitForEndOfFrame();
            if (!_restoreLoopSeen)
            {
                _restoreLoopSeen = true;
                Plugin.Logger.LogInfo("[Head] End-of-frame restore running");
            }
            if (_applied)
                RestoreGamePose();
        }
    }

    private void RestoreGamePose()
    {
        _applied = false;
        if (_cam != null)
            _cam.transform.SetPositionAndRotation(_gamePos, _gameRot);
    }
}
