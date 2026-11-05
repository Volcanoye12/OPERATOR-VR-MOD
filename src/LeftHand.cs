using System;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.XR;

namespace OperatorVR;

/// <summary>
/// Free left hand: while VR runs the character's left arm follows the left Touch controller, and goes back to the
/// game's grip on the gun when you bring the controller to where that grip is. The game places the hands with
/// FinalIK every frame (FullBodyBipedIK/ArmIK on the player, in LateUpdate); after it has, the left arm is bent
/// again with a simple two-bone IK towards the controller.
/// The gun isn't where the right controller really is (the game holds it in front of its camera, only aimed by the
/// hand), so the left hand is placed relative to the gun: the same offset from the character's right hand, in the
/// gun's aim, as the left controller has from the right controller, in the controller's aim. Controls.FreeLeftHand.
/// </summary>
public class LeftHand : MonoBehaviour
{
    public LeftHand(IntPtr ptr) : base(ptr) { }

    // Grab when the hand comes this close to the game's grip, let go when it is moved this far from it (metres)
    private const float GripDistance = 0.12f;
    private const float ReleaseDistance = 0.26f;
    // How quickly the arm blends between the game's grip and the free hand (per second)
    private const float BlendSpeed = 8f;

    private static LeftHand s_instance;

    private GameObject _player;
    private IntPtr _playerRoot;
    private Transform _upper, _lower, _hand, _rightHand;
    private float _nextScan;
    private bool _gripped = true;
    private float _free;            // 0 = the game's grip, 1 = following the controller
    private int _solvedFrame = -1;
    private bool _haveRotOffset;
    private Quaternion _rotOffset = Quaternion.identity;
    private Vector3 _posOffset;     // learnt while gripped: where the grip is compared with where the controller says
    private bool _loggedBones;
    // The game's left hand pose, relative to the character's right hand (so it follows the gun if something moves
    // the body after the game's hand IK)
    private bool _haveGamePose;
    private Vector3 _gameLocalPos;
    private Quaternion _gameLocalRot = Quaternion.identity;

    internal static void Apply(Harmony harmony)
    {
        // After every FinalIK solve on the local player the left arm is bent again, so ours is the last word.
        // First try: only IK.UpdateSolver/IKExecutionOrder were hooked, and something solved the arm again after them
        // (only the hand's turn survived: FullBodyBipedIK keeps the hand's rotation). Every solver goes through
        // IKSolver.Update, and the game's own hand scripts are hooked too
        TryPatch(harmony, AccessTools.Method(typeof(RootMotion.FinalIK.IKSolver), "Update"), nameof(AfterSolver));
        TryPatch(harmony, AccessTools.Method(typeof(RootMotion.FinalIK.IKExecutionOrder), "LateUpdate"), nameof(AfterExecutionOrder));
        TryPatch(harmony, AccessTools.Method(typeof(HandIkApplicator), "LateUpdate"), nameof(AfterHandIkApplicator));
        TryPatch(harmony, AccessTools.Method(typeof(FPSAnimationHelper), "LateUpdate"), nameof(AfterFPSAnimationHelper));
        TryPatch(harmony, AccessTools.Method(typeof(CharAnimationHelper), "LateUpdate"), nameof(AfterCharAnimationHelper));
    }

    private static void TryPatch(Harmony harmony, System.Reflection.MethodBase target, string postfix)
    {
        try
        {
            if (target == null)
            {
                Plugin.Logger.LogWarning($"[LeftHand] no method for {postfix}");
                return;
            }
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(LeftHand), postfix));
            Plugin.Logger.LogInfo($"[LeftHand] hooked {target.DeclaringType?.Name}.{target.Name}");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[LeftHand] patching {postfix} failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void AfterSolver(RootMotion.FinalIK.IKSolver __instance)
    {
        // Runs for every IK solver of every character each frame: check it's the player's before anything costly
        // (the type name is a string from IL2CPP)
        using var __t = ModTime.Measure("LeftHand IK hook");
        var self = s_instance;
        if (self == null || self._playerRoot == IntPtr.Zero)
            return;
        Transform root = null;
        try { root = __instance?.root; } catch { }
        if (root != null && root.root.Pointer == self._playerRoot)
            AfterSolve(root, __instance.GetIl2CppType().Name);
    }

    private static void AfterExecutionOrder(RootMotion.FinalIK.IKExecutionOrder __instance) => AfterSolve(__instance?.transform, "IKExecutionOrder");
    private static void AfterHandIkApplicator(HandIkApplicator __instance) => AfterSolve(__instance?.transform, "HandIkApplicator");
    private static void AfterFPSAnimationHelper(FPSAnimationHelper __instance) => AfterSolve(__instance?.transform, "FPSAnimationHelper");
    private static void AfterCharAnimationHelper(CharAnimationHelper __instance) => AfterSolve(__instance?.transform, "CharAnimationHelper");

    private static void AfterSolve(Transform t, string what)
    {
        using var __t = ModTime.Measure("LeftHand after solve");
        var self = s_instance;
        if (self == null || self._playerRoot == IntPtr.Zero || !OpenXRBootstrap.IsRunning || (!Enabled && !Plugin.HideLeftArm.Value && !Plugin.HideChest.Value))
            return;
        try
        {
            if (t == null || t.root.Pointer != self._playerRoot)
                return;
            if (what == "HandIkApplicator")
                self._sawLastHook = true;
            if (!Enabled)
            {
                if (what == "HandIkApplicator")
                {
                    self.CollapseArm();
                    self.CollapseChest();
                }
                return;
            }
            if (self._hooksSeen.Add(what))
                Plugin.Logger.LogInfo($"[LeftHand] Arm placed again after the game's {what} ('{t.name}')");
            self._lastHook = what;
            // The full-body solver is what puts the left hand where the game wants it (the gun's grip, a door
            // handle, a magazine pouch): read that before bending the arm back to the controller
            if (what == nameof(RootMotion.FinalIK.IKSolverFullBodyBiped))
            {
                self._sawHandSolver = true;
                self.ReadGamePose();
                self.SaveGameArm();
            }
            // Fourth try: after re-gripping, the body looked twisted and the gun no longer lined up when aiming
            // down the sights. The game's own scripts after the solver (and its weapon code next frame) may read the
            // hand: bend the arm only after the last of them, and put the game's arm back once the frame is drawn
            if (!self._sawLastHook || what == "HandIkApplicator")
                self.Solve();
            if (what == "HandIkApplicator")
            {
                self.CollapseArm();
                self.CollapseChest();
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[LeftHand] {e.GetType().Name}: {e.Message}");
            self._playerRoot = IntPtr.Zero;
        }
    }

    private bool _sawHandSolver, _sawLastHook;
    // The game's own arm (local rotations after its full-body solve), put back at the end of each frame we bent it
    private Quaternion _gameUpperLocal, _gameLowerLocal, _gameHandLocal;
    private bool _haveGameArm, _bentThisFrame;

    private void SaveGameArm()
    {
        if (_upper == null || _lower == null || _hand == null)
            return;
        _gameUpperLocal = _upper.localRotation;
        _gameLowerLocal = _lower.localRotation;
        _gameHandLocal = _hand.localRotation;
        _haveGameArm = true;
    }

    // Managed-only coroutine; HideFromIl2Cpp stops the class injector trying (and failing) to expose it
    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private System.Collections.IEnumerator RestoreAfterFrame()
    {
        while (true)
        {
            yield return new WaitForEndOfFrame();
            UncollapseArm();
            UncollapseChest();
            if (!_bentThisFrame || !_haveGameArm || _upper == null || _lower == null || _hand == null)
                continue;
            _bentThisFrame = false;
            try
            {
                _upper.localRotation = _gameUpperLocal;
                _lower.localRotation = _gameLowerLocal;
                _hand.localRotation = _gameHandLocal;
            }
            catch { }
        }
    }
    // ---- Hiding the left arm (Rendering.HideLeftArm) ----
    // The arms are one mesh with the rest (SK_Arms_*), so the left upper arm can't be switched off on its own. Just
    // for drawing, it's shrunk to nothing and the forearm (with the wrist GPS, glove and hand) scaled back up where it
    // was. Done after the game's last hand script and undone once the frame is drawn, so the game's IK never sees it
    private const float Shrink = 0.001f;
    private bool _collapsed;
    private int _collapsedFrame = -1;
    private Vector3 _cUpperScale, _cLowerPos, _cLowerScale;

    private void CollapseArm()
    {
        if (!Plugin.HideLeftArm.Value || CustomisationScreen.Open || _collapsed || _collapsedFrame == Time.frameCount ||
            _upper == null || _lower == null || _hand == null)
            return;
        _collapsedFrame = Time.frameCount;

        // Hiding the forearm too took the wrist off the glove (the user wanted it back): only the upper arm is shrunk
        // now, and the forearm (with the wrist, watch and hand) is scaled back up where it was
        var elbowPos = _lower.position;
        _cUpperScale = _upper.localScale;
        _cLowerPos = _lower.localPosition;
        _cLowerScale = _lower.localScale;
        _collapsed = true;

        _upper.localScale = _cUpperScale * Shrink;
        _lower.localScale = _cLowerScale / Shrink;
        _lower.position = elbowPos;
    }

    // ---- Hiding the shirt's chest (Rendering.HideChest) ----
    // The shirt is one mesh: chest, back and the sleeves that are the upper arms. Just for drawing, the body from the
    // pelvis up to spine_03 is shrunk to nothing, gathered at the lower back (spine_01), and everything hanging off
    // that chain (arms, neck/head, the gun holder, gear) is scaled back up where it was. Undone once the frame is
    // drawn, like the left arm
    private Transform[] _spine;            // pelvis, spine_01, spine_02, spine_03 (found with the arm)
    private bool _chestCollapsed;
    private int _chestFrame = -1;
    private Vector3 _cPelvisScale, _cPelvisPos;
    private readonly System.Collections.Generic.List<(Transform t, Vector3 scale, Vector3 pos)> _chestKids = new();
    private bool _loggedChest;

    private void CollapseChest()
    {
        if (!Plugin.HideChest.Value || CustomisationScreen.Open || _chestCollapsed || _chestFrame == Time.frameCount || _spine == null)
            return;
        foreach (var b in _spine)
            if (b == null)
                return;
        _chestFrame = Time.frameCount;

        var pelvis = _spine[0];
        // Gathered at spine_01 (the lower back), well below the eyes. At spine_03 (top of the chest, first try) the
        // shirt's collar and shoulders, partly on the clavicles, were dragged up into a dark shape in front of the eyes
        var top = _spine[1].position;
        // Everything hanging off the chain (other than the chain itself), with where it is now
        var kids = new System.Collections.Generic.List<(Transform t, Vector3 world)>();
        _chestKids.Clear();
        for (var i = 0; i < _spine.Length; i++)
        {
            var bone = _spine[i];
            var next = i + 1 < _spine.Length ? _spine[i + 1] : null;
            for (var c = 0; c < bone.childCount; c++)
            {
                var k = bone.GetChild(c);
                if (next != null && k.Pointer == next.Pointer)
                    continue;
                _chestKids.Add((k, k.localScale, k.localPosition));
                kids.Add((k, k.position));
            }
        }
        _cPelvisScale = pelvis.localScale;
        _cPelvisPos = pelvis.localPosition;
        _chestCollapsed = true;

        pelvis.localScale = _cPelvisScale * Shrink;
        pelvis.position = top;
        foreach (var (t, world) in kids)
        {
            t.localScale = t.localScale / Shrink;
            t.position = world;
        }
        if (!_loggedChest)
        {
            _loggedChest = true;
            Plugin.Logger.LogInfo($"[LeftHand] Chest hidden for drawing: {_spine[0].name}..{_spine[3].name} shrunk, {kids.Count} things off it kept " +
                                  $"({string.Join(", ", kids.ConvertAll(k => k.t.name))})");
        }
    }

    private void UncollapseChest()
    {
        if (!_chestCollapsed)
            return;
        _chestCollapsed = false;
        try
        {
            if (_spine?[0] != null)
            {
                _spine[0].localScale = _cPelvisScale;
                _spine[0].localPosition = _cPelvisPos;
            }
            foreach (var (t, scale, pos) in _chestKids)
                if (t != null)
                {
                    t.localScale = scale;
                    t.localPosition = pos;
                }
        }
        catch { }
    }

    private void UncollapseArm()
    {
        if (!_collapsed)
            return;
        _collapsed = false;
        try
        {
            if (_upper != null) _upper.localScale = _cUpperScale;
            if (_lower != null)
            {
                _lower.localScale = _cLowerScale;
                _lower.localPosition = _cLowerPos;
            }
        }
        catch { }
    }

    // Where the game usually holds the left hand (the gun's grip), relative to the right hand. When the game puts the
    // hand somewhere else (opening a door, reloading), it's using the hand: give it back until that's done.
    // Third try: the door moved the hand 0.8 m, that counted as letting go, and the door handle was then taken as
    // the grip, so the hand never gripped the gun again
    private bool _haveUsual;
    private Vector3 _usualLocalPos;
    private Quaternion _usualLocalRot = Quaternion.identity;
    private float _busyFor;
    private bool _gameBusy;
    private const float BusyDistance = 0.2f;

    private readonly System.Collections.Generic.HashSet<string> _hooksSeen = new();
    private string _lastHook;
    private Vector3 _lastSetPos, _lastTarget;
    private int _checkLogs;
    private float _nextCheck;

    private void Start()
    {
        s_instance = this;
        Application.add_onBeforeRender((UnityEngine.Events.UnityAction)new Action(CheckBeforeRender));
        StartCoroutine(BepInEx.Unity.IL2CPP.Utils.Collections.CollectionExtensions.WrapToIl2Cpp(RestoreAfterFrame()));
    }

    // Diagnostic: is the hand still where we put it when the frame is drawn? If not, something moved it after us
    private void CheckBeforeRender()
    {
        if (!OpenXRBootstrap.IsRunning || !Enabled || _playerRoot == IntPtr.Zero)
            return;
        try { FrameUpdate(); }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[LeftHand] {e.GetType().Name}: {e.Message}");
            _playerRoot = IntPtr.Zero;
            return;
        }
        if (_free < 0.99f || _hand == null || _checkLogs >= 8 || Time.unscaledTime < _nextCheck)
            return;
        _nextCheck = Time.unscaledTime + 2f;
        _checkLogs++;
        var off = Vector3.Distance(_hand.position, _lastSetPos);
        Plugin.Logger.LogInfo($"[LeftHand] Check before drawing: hand {off:0.000} m from where it was put, " +
                              $"{Vector3.Distance(_hand.position, _lastTarget):0.00} m short of the controller, " +
                              $"{Vector3.Distance(_hand.position, _rightHand.position):0.00} m from the right hand, " +
                              $"forearm end {Vector3.Distance(_lower.position, _hand.position):0.00} m from the hand " +
                              $"(last placed after {_lastHook ?? "LateUpdate"})");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    private const int VK_F3 = 0x72;
    private bool _f3Down;
    private static bool s_offByKey;
    // Only with the Touch controllers in your hands (Controls.TouchControls): with a gamepad they may be lying about
    private static bool Enabled => Plugin.FreeLeftHand.Value && Plugin.TouchControls.Value && !s_offByKey;

    private void LateUpdate()
    {
        using var __t = ModTime.Measure("LeftHand");
        // F3 switches the free left hand on/off for this session, to compare how the gun feels with and without it.
        // Only with the Touch controls: with a gamepad F3 is the headset screenshot (EyeCapture)
        var f3 = Plugin.TouchControls.Value && (GetAsyncKeyState(VK_F3) & 0x8000) != 0;
        if (f3 && !_f3Down)
        {
            s_offByKey = !s_offByKey;
            Plugin.Logger.LogInfo($"[LeftHand] Free left hand {(Enabled ? "on" : "off")} (F3)");
        }
        _f3Down = f3;

        if (!OpenXRBootstrap.IsRunning || !Enabled)
        {
            _free = 0f;
            _gripped = true;
        }
        if (!OpenXRBootstrap.IsRunning || (!Enabled && !Plugin.HideLeftArm.Value && !Plugin.HideChest.Value))
            return;
        // Every 2 s (once a second while there's no player yet): searching for the player every frame while there was
        // none (loading between levels) cost 2–3 ms a frame
        if (Time.unscaledTime >= _nextScan || (_player == null && Time.unscaledTime >= _nextScan - 1f))
        {
            _nextScan = Time.unscaledTime + 2f;
            try { FindArm(); }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[LeftHand] {e.GetType().Name}: {e.Message}");
                _nextScan = Time.unscaledTime + 10f;
            }
        }
        // Our own LateUpdate too, until the game's last hand script is seen (then only after that)
        if (_playerRoot != IntPtr.Zero && !_sawLastHook)
        {
            try
            {
                _lastHook = "LateUpdate";
                if (Enabled)
                    Solve();
                // (The arm is only hidden from the game's last hand script: here the game's IK may still be to come)
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[LeftHand] {e.GetType().Name}: {e.Message}");
                _playerRoot = IntPtr.Zero;
            }
        }
    }

    private void FindArm()
    {
        PlayerNetworking local = null;
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<PlayerNetworking>(), FindObjectsSortMode.None))
        {
            var p = o.TryCast<PlayerNetworking>();
            if (p != null && p.gameObject.name.Contains("MyPlayer"))
            {
                local = p;
                break;
            }
        }
        if (local == null)
        {
            _player = null;
            _playerRoot = IntPtr.Zero;
            return;
        }
        if (_player != null && _player.Pointer == local.gameObject.Pointer && _hand != null)
            return;

        _player = local.gameObject;
        _upper = _lower = _hand = _rightHand = null;
        _haveRotOffset = false;
        _haveGamePose = false;
        _haveUsual = false;
        _haveGameArm = false;
        _gameBusy = false;
        _busyFor = 0f;
        _posOffset = Vector3.zero;
        _gripped = true;
        _free = 0f;

        // The main skeleton's arm: clavicle_l > upperarm_l > lowerarm_l > hand_l, each the direct child of the last.
        // The rig also has helper copies with the same names inside the hand and forearm (hand_l < A_HandL < hand_l <
        // hand_l < lowerarm_l...), which the glove's wrist uses while its fingers hang off the main hand: moving a
        // copy (second and third tries) stretched the glove off while the fingers stayed on the gun
        string meshName = null;
        foreach (var t in _player.GetComponentsInChildren<Transform>(true))
        {
            if (t == null || t.name != "clavicle_l")
                continue;
            var upper = DirectChild(t, "upperarm_l");
            var lower = DirectChild(upper, "lowerarm_l");
            var hand = DirectChild(lower, "hand_l");
            if (hand == null)
                continue;
            _upper = upper;
            _lower = lower;
            _hand = hand;
            meshName = $"the skeleton under '{t.parent?.name}'";
            break;
        }
        foreach (var t in _player.GetComponentsInChildren<Transform>(true))
        {
            if (t == null || t.name != "clavicle_r")
                continue;
            var hand = DirectChild(DirectChild(DirectChild(t, "upperarm_r"), "lowerarm_r"), "hand_r");
            if (hand != null)
            {
                _rightHand = hand;
                break;
            }
        }
        // Otherwise the bones FinalIK uses
        if (_upper == null || _lower == null || _hand == null || _rightHand == null)
        {
            var fbik = _player.GetComponentInChildren<RootMotion.FinalIK.FullBodyBipedIK>(true);
            var refs = fbik != null ? fbik.references : null;
            if (refs != null)
            {
                _upper = refs.leftUpperArm;
                _lower = refs.leftForearm;
                _hand = refs.leftHand;
                _rightHand = refs.rightHand;
                meshName = $"FullBodyBipedIK '{fbik.name}'";
            }
        }

        if (_upper == null || _lower == null || _hand == null || _rightHand == null)
        {
            _playerRoot = IntPtr.Zero;
            if (!_loggedBones)
            {
                _loggedBones = true;
                Plugin.Logger.LogWarning($"[LeftHand] Left arm bones not found on '{_player.name}'");
            }
            return;
        }
        _playerRoot = _player.transform.root.Pointer;

        // The spine above the arm: clavicle_l < spine_03 < spine_02 < spine_01 < pelvis (seen in the log)
        _spine = null;
        var s3 = _upper.parent?.parent;
        var s2 = s3?.parent;
        var s1 = s2?.parent;
        var pelvis = s1?.parent;
        if (s3 != null && s3.name == "spine_03" && s2?.name == "spine_02" && s1?.name == "spine_01" && pelvis?.name == "pelvis")
            _spine = new[] { pelvis, s1, s2, s3 };
        else
            Plugin.Logger.LogWarning("[LeftHand] Spine (pelvis..spine_03) not found above the arm: the shirt's chest can't be hidden");
        Plugin.Logger.LogInfo($"[LeftHand] Left arm '{_upper.name}' > '{_lower.name}' > '{_hand.name}' (right hand '{_rightHand.name}') from {meshName}; " +
                              $"hand's parents: {ParentChain(_hand)}; forearm's parents: {ParentChain(_lower)}");
        // Every skinned mesh with a left hand bone, in case the visible hand uses another copy of the skeleton
        foreach (var smr in _player.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr == null) continue;
            foreach (var b in smr.bones)
                if (b != null && b.name == "hand_l")
                {
                    Plugin.Logger.LogInfo($"[LeftHand]   mesh '{smr.name}' uses hand_l {(b.Pointer == _hand.Pointer ? "(the one moved)" : "(ANOTHER copy)")}: {ParentChain(b)}");
                    break;
                }
        }
    }

    /// <summary>
    /// After each of the game's solves: while the hand is on the gun, remember where the game put it (the last one
    /// in the frame is the final grip); while it's free, bend the arm to the controller again.
    /// </summary>
    private void Solve()
    {
        if (_hand == null || _lower == null || _upper == null || _rightHand == null)
            return;
        if (_free <= 0.001f)
        {
            // Nothing to bend; without the full-body solver hook, read the game's pose here (last one in the frame wins)
            if (!_sawHandSolver)
                ReadGamePose();
            return;
        }
        if (!_haveGamePose || !Targets(out var gamePos, out var gameRot, out var freePos, out var freeRot, out _, out _, out var gameYaw))
            return;

        CalmFreeHand(gamePos, gameRot, gameYaw, ref freePos, ref freeRot);
        var targetPos = Vector3.Lerp(gamePos, freePos, _free);
        var targetRot = Quaternion.Slerp(gameRot, freeRot, _free);
        // Elbow down and out to the left of the body
        var bodyYaw = Quaternion.Euler(0f, gameYaw, 0f);
        var hint = (_upper.position + targetPos) * 0.5f + Vector3.down * 0.3f + bodyYaw * Vector3.left * 0.25f;
        SolveTwoBone(_upper, _lower, _hand, targetPos, hint);
        _hand.rotation = targetRot;
        _bentThisFrame = true;
        _lastSetPos = _hand.position;
        _lastTarget = targetPos;
    }

    // The free left hand waved about "way too quickly" (2026-11-04): it copied every move of the controller, full size
    // and at once. Now it moves less (Controls.LeftHandMovement: its offset from the gun's grip scaled down) and eases
    // after the controller (Controls.LeftHandSmoothing, how fast it catches up). Kept relative to the gun's aim, so
    // turning doesn't leave it behind; it starts from the grip each time it leaves the gun. Only what's drawn changes:
    // grabbing and letting go of the gun still go by where the controller really is
    private Vector3 _calmRel;
    private Quaternion _calmRot = Quaternion.identity;
    private bool _calmHave;

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private void CalmFreeHand(Vector3 gamePos, Quaternion gameRot, float gameYaw, ref Vector3 freePos, ref Quaternion freeRot)
    {
        if (!HeadTracking.TryGetGameAim(out var pitch, out var yaw))
            return;
        var aimRot = Quaternion.Euler(pitch, yaw, 0f);
        var inv = Quaternion.Inverse(aimRot);
        var scale = Mathf.Clamp(Plugin.LeftHandMovement.Value, 0.1f, 1f);
        var wantPos = gamePos + (freePos - gamePos) * scale;
        var wantRel = inv * (wantPos - _rightHand.position);
        var wantRot = Quaternion.Slerp(gameRot, freeRot, scale);
        var wantRotRel = inv * wantRot;

        // Leaving the gun (or the first time): start from the grip
        if (!_calmHave || _free <= 0.001f)
        {
            _calmHave = true;
            _calmRel = inv * (gamePos - _rightHand.position);
            _calmRot = inv * gameRot;
        }
        var k = 1f - Mathf.Exp(-Mathf.Max(0.5f, Plugin.LeftHandSmoothing.Value) * Time.unscaledDeltaTime);
        _calmRel = Vector3.Lerp(_calmRel, wantRel, k);
        _calmRot = Quaternion.Slerp(_calmRot, wantRotRel, k);
        freePos = _rightHand.position + aimRot * _calmRel;
        freeRot = aimRot * _calmRot;
    }

    /// <summary>Where the game has just put the left hand, relative to the right hand.</summary>
    private void ReadGamePose()
    {
        if (_hand == null || _rightHand == null)
            return;
        var inv = Quaternion.Inverse(_rightHand.rotation);
        _gameLocalPos = inv * (_hand.position - _rightHand.position);
        _gameLocalRot = inv * _hand.rotation;
        _haveGamePose = true;
    }

    /// <summary>
    /// Once a frame, just before it's drawn (after all the game's hand work): notice the game using the hand, grab
    /// or let go of the gun, learn how the controller lines up with the grip, and move the blend on.
    /// </summary>
    private void FrameUpdate()
    {
        if (_hand == null || _rightHand == null || !_haveGamePose || _solvedFrame == Time.frameCount)
            return;
        _solvedFrame = Time.frameCount;
        if (!Targets(out _, out _, out var freePos, out _, out var local, out var gamePitch, out var gameYaw))
            return;
        var aimRot = Quaternion.Euler(gamePitch, gameYaw, 0f);

        // Is the game's hand at its usual grip, or is the game using it for something (door, reload)?
        if (!_haveUsual)
        {
            _usualLocalPos = _gameLocalPos;
            _usualLocalRot = _gameLocalRot;
            _haveUsual = true;
        }
        var fromUsual = Vector3.Distance(_gameLocalPos, _usualLocalPos);
        if (fromUsual > BusyDistance)
        {
            _busyFor += Time.unscaledDeltaTime;
            // Still there after a while: it's a new grip (another weapon), not an action. 4 s took a slow door
            // opening as the grip
            if (_busyFor > 10f)
            {
                _usualLocalPos = _gameLocalPos;
                _usualLocalRot = _gameLocalRot;
                _busyFor = 0f;
                Plugin.Logger.LogInfo("[LeftHand] The game holds the hand somewhere new: that's the grip now");
            }
        }
        else
        {
            _busyFor = 0f;
            // Follow small changes of the grip (aiming down the sights, a different hold)
            var k = Time.unscaledDeltaTime * 2f;
            _usualLocalPos = Vector3.Lerp(_usualLocalPos, _gameLocalPos, k);
            _usualLocalRot = Quaternion.Slerp(_usualLocalRot, _gameLocalRot, k);
        }
        var busy = _busyFor > 0f;
        if (busy != _gameBusy)
        {
            _gameBusy = busy;
            Plugin.Logger.LogInfo(busy
                ? $"[LeftHand] The game is using the left hand ({fromUsual:0.00} m from the grip): it's the game's until done"
                : "[LeftHand] The game is done with the left hand");
        }
        if (_gameBusy)
        {
            _free = Mathf.MoveTowards(_free, 0f, Time.unscaledDeltaTime * BlendSpeed);
            return;
        }

        var gamePos = _rightHand.position + _rightHand.rotation * _usualLocalPos;
        var gameRot = _rightHand.rotation * _usualLocalRot;
        var gripGap = Vector3.Distance(freePos, gamePos);
        if (_gripped && gripGap > ReleaseDistance)
        {
            _gripped = false;
            Plugin.Logger.LogInfo($"[LeftHand] Let go of the gun ({gripGap:0.00} m from the grip; hand offset {local}, learnt {_posOffset})");
        }
        else if (!_gripped && gripGap < GripDistance)
        {
            _gripped = true;
            Plugin.Logger.LogInfo($"[LeftHand] Gripped the gun ({gripGap:0.00} m from the grip)");
        }

        // While holding the gun, slowly learn how the controller lines up with the game's grip, so letting go
        // and grabbing again happen where you'd expect (the controllers' origins aren't the character's wrists)
        if (_gripped && _free <= 0.001f && gripGap < ReleaseDistance)
        {
            var want = Quaternion.Inverse(aimRot) * (gamePos - _rightHand.position) - local;
            _posOffset = Vector3.Lerp(_posOffset, Vector3.ClampMagnitude(want, 0.15f), Time.unscaledDeltaTime * 0.5f);
            _rotOffset = Quaternion.Slerp(_rotOffset, Quaternion.Inverse(_lastRotFromCtrl) * gameRot, Time.unscaledDeltaTime * 0.5f);
        }

        _free = Mathf.MoveTowards(_free, _gripped ? 0f : 1f, Time.unscaledDeltaTime * BlendSpeed);
    }

    private Quaternion _lastRotFromCtrl = Quaternion.identity;

    /// <summary>
    /// Where the game holds the left hand, and where the left controller says it should be: the left controller's
    /// offset from the right one (in the right controller's aim, without roll) put on the character's right hand in
    /// the game's aim, since the gun isn't where the right controller really is.
    /// </summary>
    private bool Targets(out Vector3 gamePos, out Quaternion gameRot, out Vector3 freePos, out Quaternion freeRot,
                         out Vector3 local, out float gamePitch, out float gameYaw)
    {
        gamePos = freePos = local = default;
        gameRot = freeRot = Quaternion.identity;
        gamePitch = gameYaw = 0f;
        if (!TryGetControllers(out var leftPos, out var leftRot, out var rightPos, out var rightRot) ||
            !HeadTracking.TryGetGameAim(out gamePitch, out gameYaw))
            return false;

        var ctrlFrame = Quaternion.LookRotation(rightRot * Vector3.forward, Vector3.up);
        var aimRot = Quaternion.Euler(gamePitch, gameYaw, 0f);
        var toGame = aimRot * Quaternion.Inverse(ctrlFrame);

        gamePos = _rightHand.position + _rightHand.rotation * _gameLocalPos;
        gameRot = _rightHand.rotation * _gameLocalRot;

        local = Quaternion.Inverse(ctrlFrame) * (leftPos - rightPos);
        var handRotFromCtrl = toGame * leftRot;
        _lastRotFromCtrl = handRotFromCtrl;
        if (!_haveRotOffset)
        {
            _rotOffset = Quaternion.Inverse(handRotFromCtrl) * gameRot;
            _haveRotOffset = true;
        }
        freePos = _rightHand.position + aimRot * (local + _posOffset);
        freeRot = handRotFromCtrl * _rotOffset;
        return true;
    }

    private static Transform DirectChild(Transform t, string name)
    {
        if (t == null)
            return null;
        for (var i = 0; i < t.childCount; i++)
        {
            var c = t.GetChild(i);
            if (c.name == name)
                return c;
        }
        return null;
    }

    private static string ParentChain(Transform t)
    {
        var names = new System.Collections.Generic.List<string>();
        for (var p = t.parent; p != null && names.Count < 8; p = p.parent)
            names.Add(p.name);
        return string.Join(" < ", names);
    }

    /// <summary>Bends upper/lower so the hand reaches the target (as far as the arm allows), elbow towards the hint.</summary>
    private static void SolveTwoBone(Transform upper, Transform lower, Transform hand, Vector3 target, Vector3 hint)
    {
        var a = upper.position;
        var upperLen = Vector3.Distance(a, lower.position);
        var lowerLen = Vector3.Distance(lower.position, hand.position);
        var toTarget = target - a;
        var dist = Mathf.Clamp(toTarget.magnitude, 0.01f, (upperLen + lowerLen) * 0.999f);
        var dir = toTarget.sqrMagnitude > 1e-6f ? toTarget.normalized : upper.forward;

        // Elbow on the circle where both bone lengths fit, on the hint's side
        var cosA = Mathf.Clamp((upperLen * upperLen + dist * dist - lowerLen * lowerLen) / (2f * upperLen * dist), -1f, 1f);
        var sinA = Mathf.Sqrt(1f - cosA * cosA);
        var side = Vector3.ProjectOnPlane(hint - a, dir);
        if (side.sqrMagnitude < 1e-6f)
            side = Vector3.ProjectOnPlane(Vector3.down, dir);
        var elbow = a + dir * (upperLen * cosA) + side.normalized * (upperLen * sinA);

        upper.rotation = Quaternion.FromToRotation(lower.position - a, elbow - a) * upper.rotation;
        var handTarget = a + dir * dist;
        lower.rotation = Quaternion.FromToRotation(hand.position - lower.position, handTarget - lower.position) * lower.rotation;
    }

    private static bool TryGetControllers(out Vector3 leftPos, out Quaternion leftRot, out Vector3 rightPos, out Quaternion rightRot)
    {
        leftPos = rightPos = default;
        leftRot = rightRot = Quaternion.identity;
        return TryGetController(XRNode.LeftHand, out leftPos, out leftRot) &&
               TryGetController(XRNode.RightHand, out rightPos, out rightRot);
    }

    private static bool TryGetController(XRNode node, out Vector3 worldPos, out Quaternion worldRot)
    {
        worldPos = default;
        worldRot = Quaternion.identity;
        var device = InputDevices.GetDeviceAtXRNode(node);
        if (!device.isValid)
            return false;
        var id = device.deviceId;
        if (!XRFeature.Vector3(id, "PointerPosition", out var pos) || !XRFeature.Quaternion(id, "PointerRotation", out var rot))
        {
            if (!XRFeature.Vector3(id, "DevicePosition", out pos) || !XRFeature.Quaternion(id, "DeviceRotation", out rot))
                return false;
        }
        return HeadTracking.TryTrackingToWorld(pos, rot, out worldPos, out worldRot);
    }
}
