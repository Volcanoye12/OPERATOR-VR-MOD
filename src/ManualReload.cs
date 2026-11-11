using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.XR;

namespace OperatorVR;

/// <summary>
/// Controls.ManualReload (the user's idea, 2026-11-10): reload by hand with the Touch controllers.
/// - Your spare magazines hang visibly at the front left of your belt (copies of your gun's magazine, one per spare
///   mag in the game's PlayerNetworking.MagPouch, up to MaxShown), following your body as you turn. Your own body is
///   hidden in VR, so without them you'd be reaching blind.
/// - Reach there with the left hand and squeeze the left grip: a magazine comes into your hand (a buzz).
/// - Bring it to the gun's magazine well: within InsertDistance it clicks in (a firm buzz), the copy goes, and the
///   game's own reload runs, sped up (Controls.ManualReloadSpeed) so it's quick. The game keeps doing the real work:
///   ammo counts, dropping or keeping the old magazine, chambering a round, multiplayer sync.
/// - Let go of the grip anywhere else and the magazine goes back to the belt. The X button still reloads as before.
/// While your hand is at the belt or holding a magazine, the left grip doesn't sprint (TouchKeys asks TakesLeftGrip).
/// The magazines and your hand are placed where your real head and controller are (not the drawn arm, which follows
/// the gun in a calmed way), so what you see lines up with where your hand really is.
/// </summary>
public class ManualReload : MonoBehaviour
{
    public ManualReload(IntPtr ptr) : base(ptr) { }

    private const int MaxShown = 4;
    private const float GrabDistance = 0.16f, InsertDistance = 0.20f, PullDistance = 0.16f, GripPressed = 0.6f, GripReleased = 0.35f;
    private const float BeltZone = 0.20f;
    private float _nextDistLog;

    // ---- The magazine well as a rail (2026-11-11) ----
    // The user, after the 11:02 footage: "as you're picking it up, it's going automatically into the gun. I want it
    // so you have to push it into the gun", and the same by hand the other way: pull the gun's magazine out, look at
    // it, push it back. So the magazine no longer loads when it comes near. It stops at the mouth of the well, lined
    // up with the gun, and from there it only moves along the well, as far as your hand moves along it: push it up
    // its travel and it clicks home (only then does the game reload); pull it down and it comes free in your hand.
    // The gun's own magazine starts fully in and slides out the same way. How far it has slid goes by the left
    // controller's own movement relative to the gun (1:1, LeftHand.HandOnGunAt), not by the drawn hand, which is
    // calmed and may sit a few cm off: the drawn hand is brought to the magazine instead (LeftHand.RailOn).
    private const float CaptureDistance = 0.20f;
    private bool _onRail, _everRail, _railArmed, _railGlide, _blockedBuzz, _haveRail;
    private float _railS, _travel = 0.09f, _railEnterAt, _leftRailAt = -10f, _axialVel, _magLen = 0.2f;
    private Vector3 _railPrevRel, _railEntryRel, _axisRel = Vector3.up, _prevRel, _railFromPos, _freeMid;
    private Quaternion _seatRotRel = Quaternion.identity, _railFromRot = Quaternion.identity;
    private int _freeMidFrame = -1, _oldAmmo, _oldMax;
    private Transform _railFrame;
    /// <summary>The gun in hand is a pistol (a short magazine): GunShift doesn't push it forward off the hand.</summary>
    internal static bool PistolInHand { get; private set; }
    /// <summary>The gun in hand (its object), so other parts can tell when it changes.</summary>
    internal static IntPtr GunId { get; private set; }
    // 11:15 footage (2026-11-11): the pulled-out magazine flicked between the hand and the well (it was caught from
    // 20 cm away and thrown to the mouth, then came off again 4 cm lower), and the pistol's magazine couldn't be pulled
    // at all (the support hand's grip is at the magazine, so "nearer the magazine than the grip" never held). Now:
    // - Pulling: squeeze with the hand at the gun's magazine and move it DOWN the well (1.5 cm; 4 cm where the
    //   grip and the magazine are in the same place, as on a pistol, so a sprint squeeze doesn't pull it).
    // - Going in: the magazine's TOP has to come to the mouth of the well (12 cm), and it goes onto the rail where
    //   it is along it, not thrown to the mouth. Once it has come off the rail it must move clear (16 cm) first.
    private const float PushLen = 0.03f, PullLen = 0.06f;
    private bool _railPull, _railPullAuto, _pullByPress, _atMag, _atMagLogged;
    // A stand-in for the gun's magazine: hung from what the magazine hangs from, copying it while it's in the gun, and
    // left where it was while the magazine is out (or the game's reload is moving it)
    private Transform _wellProxy;
    private float _nextProxyLog;

    private Transform WellFrame(Magazine mag, bool follow)
    {
        var mt = mag.transform;
        if (_wellProxy == null)
        {
            _wellProxy = new GameObject("OperatorVR Well").transform;
            follow = true;
        }
        if (follow || _wellProxy.parent == null)
        {
            if (_wellProxy.parent != mt.parent)
                _wellProxy.SetParent(mt.parent, false);
            _wellProxy.SetPositionAndRotation(mt.position, mt.rotation);
            _wellProxy.localScale = mt.localScale;
        }
        else if (Time.unscaledTime >= _nextProxyLog && _logs < 200)
        {
            // How far the game's hidden magazine has wandered from the well (the cause of the 16:35 report)
            var off = Vector3.Distance(mt.position, _wellProxy.position);
            if (off > 0.03f)
            {
                _nextProxyLog = Time.unscaledTime + 2f;
                _logs++;
                Plugin.Logger.LogInfo($"[Reload] The game's hidden magazine is {off * 100f:0} cm from the well while yours is out: the well stays put");
            }
        }
        return _wellProxy;
    }

    private bool _needGripRelease, _beltMagnet, _beltLogged, _dropUnseen;
    private int _beltTaken = -1;
    private GameObject _droppedOld;
    private IntPtr _droppedOldFor;
    private float _railStart, _railPullThr;
    private bool _pullCand, _mustClear, _pullLogged;
    private Vector3 _pullRel, _alongLocal = Vector3.up;
    private float _tipGap = 9f;
    // Side by side, close together like pouches on a belt (the user's Ready or Not VR reference: a tight row)
    private const float MagGap = 0.05f;

    /// <summary>The left grip belongs to the reload (not sprint) while the hand is at the belt or holding a magazine.</summary>
    internal static bool TakesLeftGrip { get; private set; }

    /// <summary>The gun's magazine has been pulled out by hand: the gun doesn't fire until one goes back in.</summary>
    internal static bool MagOut { get; private set; }

    /// <summary>A magazine is in the left hand (new from the belt, or the gun's own pulled out).</summary>
    internal static bool HoldingMagazine => s_me != null && (s_me._holding || s_me._holdingOld);

    // Pulling the magazine out (2026-11-10): grip at the magazine well with an empty hand takes the gun's magazine
    // out (the game's one is hidden; a copy is in your hand). Let go and it drops to the floor. A magazine from the
    // belt pushed in then does the game's emergency reload (R twice: the game drops the old magazine and its rounds),
    // and the hidden magazine stays hidden so there aren't two on the floor. Push the same one back instead and it's
    // simply back in. Chamber a round yourself afterwards if the chamber is empty (double-tap X).
    private bool _holdingOld, _oldLeftWell;
    private IntPtr _magOutFor, _hiddenFor;
    private float _emergencyAt = -10f;
    private float _pulledAt = -10f;
    private readonly List<Renderer> _hiddenParts = new();
    private float _showPartsAt = -1f;
    private readonly List<(GameObject go, float until)> _dropped = new();
    private readonly List<(float at, int key, bool down)> _keys = new();
    private Vector3 _lastHand;
    private Vector3 _handVel;

    private PlayerNetworking _me;
    private float _nextFind;
    private GameObject _root;
    private readonly List<GameObject> _beltMags = new();
    private GameObject _held;
    private IntPtr _builtFor;            // the magazine the copies were made from
    private Quaternion _magInGun = Quaternion.identity;   // the magazine's turn relative to a level forward frame
    private static readonly Dictionary<string, Quaternion> s_magTurn = new();
    private bool _gripDown, _holding;
    private float _speedUntil = -1f;
    private Animator _sped;
    private float _spedWas = 1f;
    private int _logs;
    private IntPtr _pouchLoggedFor;
    private int _pouchLoggedKey = -1, _pouchLogs;

    private static string BaseName(string n)
    {
        if (string.IsNullOrEmpty(n))
            return "";
        return n.Replace("(Clone)", "").Trim();
    }

    private void LateUpdate()
    {
        using var __t = ModTime.Measure("ManualReload");
        try
        {
            EndSpeedUp();
            RunKeys();
            TidyDropped();
            var on = Plugin.ManualReload.Value && Plugin.TouchControls.Value && OpenXRBootstrap.IsRunning &&
                     Cursor.lockState == CursorLockMode.Locked && !Cursor.visible;
            if (!on || !Find(out var weapon, out var mag, out var spares))
            {
                Hide();
                return;
            }
            // Another gun (or the game swapped the magazine itself): the pulled-out state doesn't carry over
            if (MagOut && _magOutFor != mag.Pointer)
                PutBack("the gun or its magazine changed");
            // After the emergency reload: if the game kept the same magazine object as the new one, show it again
            if (!MagOut && _hiddenParts.Count > 0 && mag.Pointer == _hiddenFor && Time.unscaledTime > _emergencyAt + 2f)
                ShowParts();
            if (!TryPose(XRNode.Head, out var headPos, out var headRot) || !TryPose(XRNode.LeftHand, out var handPos, out var handRot))
            {
                Hide();
                return;
            }
            _lastHeadY = headPos.y;

            Build(weapon, mag);
            if (_root == null)
                return;
            LearnTurn(weapon, mag);
            if (!_turnGood && !_holding && !_holdingOld)
                spares = 0;   // no belt until the magazine's turn is known (it would lie any old way)

            // The belt: below the eyes, a little forward and to the left, turned with the head's heading
            var fwd = Vector3.ProjectOnPlane(headRot * Vector3.forward, Vector3.up);
            if (fwd.sqrMagnitude < 1e-4f)
                fwd = Vector3.forward;
            var yaw = Quaternion.LookRotation(fwd.normalized, Vector3.up);
            var belt = headPos + Vector3.down * Mathf.Clamp(Plugin.WaistMagDrop.Value, 0.3f, 0.9f) +
                       yaw * new Vector3(-Mathf.Clamp(Plugin.WaistMagSide.Value, -0.4f, 0.4f), 0f,
                                         Mathf.Clamp(Plugin.WaistMagForward.Value, 0f, 0.6f));
            // Within reach of the drawn arm (16:35 footage, the user: "I want the hand to actually grab the magazine at
            // the waist ... make the arm smaller?"): placed from your real head the belt hung about a metre below your
            // eyes, lower than the character's arm reaches, so the drawn hand stopped in the air above it. Now it hangs
            // at the character's own left hip, measured from the drawn left shoulder: 42 cm down, 22 cm forward,
            // 5 cm in — about 48 cm away, inside the arm's reach
            // (16:52: "the magazines were to the left of me, you could hardly grab them" — moved 7 cm towards your middle
            // and 3 cm further forward)
            if (LeftHand.ShoulderFrame >= Time.frameCount - 3)
                belt = LeftHand.ShoulderAt + yaw * new Vector3(0.12f, -0.42f, 0.25f);
            var allShown = Mathf.Min(MaxShown, spares);
            var shownCount = Mathf.Min(MaxShown, spares - (_holding ? 1 : 0));
            for (var i = 0; i < _beltMags.Count; i++)
            {
                var go = _beltMags[i];
                // The one taken is the one that goes (not always the last)
                var show = i < allShown && !(_holding && i == _beltTaken);
                if (go.activeSelf != show)
                    go.SetActive(show);
                if (!show)
                    continue;
                // Side by side, going left from the belt point
                go.transform.SetPositionAndRotation(belt + yaw * new Vector3(-i * MagGap, 0f, 0f), yaw * _magInGun);
            }

            // Grip: raw from the controller (TouchKeys' sprint mapping is held off while we have it)
            var grip = Grip();
            // Measured from the hand as it's DRAWN (the belt is now placed for the drawn arm); the controller only when
            // the drawn hand isn't free (holding the gun)
            // (without the shift that brings it onto a magazine, or it would never let go)
            var drawnHand = LeftHand.HandAtFrame >= Time.frameCount - 2 ? LeftHand.HandAt - LeftHand.RailShiftUsed : handPos + Vector3.up * 10f;
            // (16:52 play: the squeezes came with the controller 4-10 cm from the belt, but the drawn hand was 22 cm off
            // and nothing was taken. By the controller again, with the hand drawn where the controller is near the belt)
            var nearBelt = Vector3.Distance(handPos, belt + yaw * new Vector3(-(Mathf.Max(1, allShown) - 1) * MagGap * 0.5f, 0f, 0f))
                           < 0.20f + (Mathf.Max(1, allShown) - 1) * MagGap * 0.5f;
            // The belt magazine nearest the drawn hand: the hand closes on that one, and that's the one taken
            _beltMagnet = false;
            var nearestBelt = -1;
            var nearestBeltMid = Vector3.zero;
            if (nearBelt && !_holding && !_holdingOld && allShown > 0)
            {
                var best = float.MaxValue;
                for (var i = 0; i < allShown && i < _beltMags.Count; i++)
                    if (_beltMags[i].activeSelf && Middle(_beltMags[i], out var bm))
                    {
                        var dd = Vector3.Distance(drawnHand, bm);
                        if (dd < best)
                        {
                            best = dd;
                            nearestBelt = i;
                            nearestBeltMid = bm;
                        }
                    }
                // Only when the drawn hand is already close (15 cm): 17:53 it was drawn onto a magazine from 23-48 cm
                // away, a visible jump. Further off, the squeeze still takes the nearest one
                _beltMagnet = nearestBelt >= 0 && best < 0.15f;
                if (nearestBelt >= 0)
                    _beltTaken = nearestBelt;
            }

            // Where a magazine in the hand would be: as if the controller were the gun (held level and forward, the
            // magazine hangs as in the gun)
            var heldRot = handRot * _magInGun;
            var heldPos = handPos + handRot * new Vector3(0f, -0.03f, 0.03f);
            // The middle of the gun's magazine as drawn (its pivot can be far from it: in the first play the magazine
            // was held right under the gun and never went in), kept relative to the gun while it's hidden
            var gameReloading = false;
            try { gameReloading = weapon.gunFunction.isReloading; } catch { }
            if (!MagOut && !_seating && (_wellLocal == Vector3.zero || (Time.frameCount % 10 == 0 && !gameReloading)) &&
                Middle(mag.gameObject, out var mid))
            {
                // In the game's own magazine's axes (2026-11-11, 11:33 play): it stays in its place in the gun while
                // hidden, wherever the gun is drawn. Kept relative to the weapon object before, which for the Glock
                // isn't what's drawn in the hand: its well was 35 cm from the pistol, nothing could reach it
                _wellLocal = mag.transform.InverseTransformPoint(mid);
                // The well's direction (the way a magazine goes in: along its long side, feed end first) and how the
                // magazine sits when home, both relative to the gun. Not during the game's reload (it's in a hand then)
                // and only once the magazine's turn is known (before that the gun may still be turned in its draw
                // animation: the 11:15 play logged the HK416D's well as pointing sideways)
                if (!gameReloading && _turnGood)
                {
                    var along = _magLongLocal;
                    if ((_magInGun * along).y < 0f)
                        along = -along;
                    _alongLocal = along;
                    _axisRel = along;
                    _seatRotRel = Quaternion.identity;
                    // A pistol's magazine goes all the way into the grip; a rifle's about half way into the well
                    _travel = _magLen < 0.15f ? _magLen * 0.8f : Mathf.Clamp(_magLen * 0.45f, 0.06f, 0.10f);
                    if (!_haveRail && _logs++ < 200)
                        Plugin.Logger.LogInfo($"[Reload] Well of '{_me.activeWeapon.name}': goes in along {_axisRel} of the gun, {_travel * 100f:0} cm of travel");
                    _haveRail = true;
                }
            }
            // Where the gun's magazine sits, kept where it was while that magazine is out of the gun (16:35 footage, the
            // user: "as it's pushed up, on the left-hand side of the rifle you see another magazine"): the hidden magazine
            // the game keeps hold of while yours is out gets moved about by its animation, and the well — worked out from
            // it — went with it, so the magazine slid in beside the rifle, not into it
            var wf = WellFrame(mag, !MagOut && !_seating && !gameReloading);
            _railFrame = wf;
            GunId = weapon.Pointer;
            // A pistol's magazine is inside its grip: that's where the right hand belongs (LeftHand.PistolGrip)
            if (PistolInHand && _wellLocal != Vector3.zero && !gameReloading && !_seating)
            {
                LeftHand.PistolGripAt = mag.transform.TransformPoint(_wellLocal);
                LeftHand.PistolMag = mag.transform;
                LeftHand.PistolMagLocal = _wellLocal;
                LeftHand.PistolGripFrame = Time.frameCount;
            }
            var well = _wellLocal != Vector3.zero ? wf.TransformPoint(_wellLocal) : wf.position;
            // and the middle of the one in the hand (placed last frame; it follows the hand closely)
            var heldMid = heldPos;
            if ((_holding || _holdingOld) && _held.activeSelf && Middle(_held, out var hm))
                heldMid = hm;
            var gap = Vector3.Distance(heldMid, well);
            // Also measured without the learnt grip offset (up to 15 cm, learnt while holding the gun's front): the
            // ninth play never got a pistol magazine closer than 24 cm though the hand went to the pistol. The nearer
            // of the two counts
            if (LeftHand.HandOnGunFrame >= Time.frameCount - 2 && (_holding || _holdingOld))
                gap = Mathf.Min(gap, Vector3.Distance(heldMid + (LeftHand.HandOnGunRawAt - LeftHand.HandOnGunAt), well));
            // Reaching for the gun's own magazine: the hand as drawn next to the gun (the gun isn't where the right
            // controller really is, so the real hand can't be compared with it)
            var onGun = LeftHand.HandOnGunFrame >= Time.frameCount - 2 ? LeftHand.HandOnGunAt : handPos;
            // At the gun's magazine, and nearer it than the front grip (on this rifle the front grip is just in front
            // of the magazine: squeezing while holding the front pulled the magazine out in the fourth play)
            var toMag = Vector3.Distance(onGun, well);
            var toGrip = LeftHand.GripAtFrame >= Time.frameCount - 2 ? Vector3.Distance(onGun, LeftHand.GripAt) : 1f;
            var atGunMag = toMag < PullDistance && toMag + 0.03f < toGrip;
            // or about as near it as the grip (a pistol: the support hand holds the gun where the magazine is)
            var maybeGunMag = toMag < PullDistance && toMag < toGrip + 0.02f;
            // The hand has come to the gun's magazine (2026-11-11, 12:30 play; the user: "I want my hand to go to the
            // magazine and pull it out and have the magazine in my hand"). Until now the drawn hand stayed on the
            // front grip until it was 26 cm away, so you couldn't see it reach the magazine, and the squeeze that
            // should have taken it missed the "at the magazine" test by a centimetre (11 cm from it, 13 from the
            // grip). Now: nearer the magazine than the front grip = the hand is DRAWN on the magazine, fingers round
            // it, and a squeeze there takes it. It stays so until the hand is clearly back at the grip or away
            // The belt or the gun's own magazine? (15:03 play, the MP7: its magazine is in the pistol grip, low, near the
            // belt; three squeezes meant for it took a belt magazine instead, the hand 22-25 cm from both.) While the
            // gun's magazine is still in, a new one can't go in anyway, so near the gun's magazine (27 cm) that one
            // wins; the belt only when the hand is clearly away from it, or once the gun's magazine is out
            var beltWins = nearBelt && spares > 0 && (MagOut || toMag > 0.27f);
            var canTake = !_holding && !_holdingOld && !MagOut && _haveRail && !_seating && !gameReloading && !beltWins;
            // Where you SEE your hand (12:48 play: the user squeezed nine times with the drawn hand on the rifle's
            // magazine, and the log measured the controller 19-27 cm from it). The free left hand is drawn moving only
            // Controls.LeftHandMovement (0.6) as far from the gun's grip as the controller really does, so a drawn hand
            // on the magazine is a controller well past it. Measured the same way as it's drawn now
            var move = Mathf.Clamp(Plugin.LeftHandMovement.Value, 0.1f, 1f);
            var seen = LeftHand.GripAtFrame >= Time.frameCount - 2 ? LeftHand.GripAt + (onGun - LeftHand.GripAt) * move : onGun;
            var seenToMag = Vector3.Distance(seen, well);
            var seenToGrip = toGrip * move;
            if (PistolInHand)
                _atMag = canTake && toMag < (_atMag ? 0.18f : 0.13f);
            else
                // (16:15 play: "very difficult to hold the front of the gun, kept falling off": eight times the hand
                // gripped the front and at once was taken off it to the magazine, 10 cm from both. The magazine is
                // taken by a squeeze anyway; the hand is only drawn on it when clearly nearer it than the front grip)
                _atMag = canTake && (_atMag ? seenToMag < 0.14f && seenToMag < seenToGrip - 0.02f
                                            : seenToMag < 0.10f && seenToMag < seenToGrip - 0.06f);
            if (Time.unscaledDeltaTime > 1e-4f)
                _handVel = Vector3.Lerp(_handVel, (handPos - _lastHand) / Time.unscaledDeltaTime, 0.5f);
            _lastHand = handPos;
            // The left controller relative to the gun (turned into the gun's axes), and how fast it moves along the well
            var rel = Quaternion.Inverse(wf.rotation) * (onGun - wf.position);
            if (Time.unscaledDeltaTime > 1e-4f)
                _axialVel = Mathf.Lerp(_axialVel, Mathf.Clamp(Vector3.Dot(rel - _prevRel, _axisRel) / Time.unscaledDeltaTime, -3f, 3f), 0.4f);
            _prevRel = rel;

            TakesLeftGrip = _holding || _holdingOld || nearBelt || _atMag ||
                            (!PistolInHand && canTake && toMag < 0.27f && toMag < toGrip + 0.06f);
            // Near the belt the hand is drawn where it really is (the belt is placed from the real head); near the gun
            // it's drawn relative to the gun (as the free hand always is), since the gun isn't where the right
            // controller really is: the third play never got a magazine closer than 20 cm to the gun
            var beltMid = belt + yaw * new Vector3(-(Mathf.Max(1, shownCount) - 1) * MagGap * 0.5f, 0f, 0f);
            // Only close to the belt and not above it: in the fifth play the hand holding the gun's front was within the
            // old 30 cm of the belt, so it kept being taken off the gun and the front was hard to grip
            // Near the belt the hand is drawn where the controller really is (the belt is within the arm's reach now)
            LeftHand.ReloadHand = Vector3.Distance(handPos, beltMid) < BeltZone + 0.05f && handPos.y < beltMid.y + 0.12f;
            // Reaching down to a dropped magazine: the hand is drawn where it really is, so it lines up with it
            if (!_holding && !_holdingOld && MagOut && _droppedOld != null && _droppedOldFor == _magOutFor &&
                Middle(_droppedOld, out var dropMid) && Vector3.Distance(handPos, dropMid) < 0.40f)
                LeftHand.ReloadHand = true;
            LeftHand.KeepOffGun = _holding || _holdingOld || LeftHand.ReloadHand || (_atMag && !PistolInHand) || _beltMagnet;
            LeftHand.RailShiftRate = 18f;   // (gentler only while closing on a belt magazine, set below)
            if (_logs < 200 && Time.unscaledTime >= _nextDistLog && (_holding || atGunMag || gap < 0.3f))
            {
                _nextDistLog = Time.unscaledTime + 1f;
                _logs++;
                Plugin.Logger.LogInfo($"[Reload] distances: held mag to gun mag {gap * 100f:0} cm, its top to the well's mouth {_tipGap * 100f:0} cm, hand-on-gun to gun mag {toMag * 100f:0} cm, to the grip {toGrip * 100f:0} cm (holding {_holding}, old in hand {_holdingOld}, out {MagOut}, on rail {_onRail} at {_railS * 100f:0} cm)");
            }

            s_canDrop = spares > 0 || MagOut;
            if (s_dropAsked)
            {
                s_dropAsked = false;
                DropByButton(weapon, mag, well, spares);
            }

            var pressed = !_gripDown && grip > GripPressed;
            var released = _gripDown && grip < GripReleased;
            if (pressed) _gripDown = true;
            if (released) _gripDown = false;

            // (12:45-12:48 a squeeze at the rifle's magazine ran the game's own magazine check instead. Not what the
            // user meant, taken out: on every gun the HAND takes the magazine out, you look at it, push it back)

            // Pulling the gun's magazine: squeezed with the empty hand at it (whether squeezed there or before
            // getting there), then moved down the well
            var pullDown = 0f;
            // Just pushed a magazine in: no new pull until the grip has been let go (15:28 play: after every push
            // back in, the grip was still held, and moving the hand away pulled the magazine straight out again;
            // letting go then slid it back — "still problems with the reloading")
            if (!_gripDown)
                _needGripRelease = false;
            if (!_gripDown || _needGripRelease || _holding || _holdingOld || MagOut || !_haveRail || _seating || gameReloading)
            {
                _pullCand = false;
                _pullLogged = false;
            }
            // On a rifle only with the hand clearly at the magazine, not at the front grip (12:14 play: the user
            // squeezed to take hold of the rifle's front, 7 cm from the grip and 9 from the magazine, and the magazine
            // came out instead: "can't grab the front of the rifle"). On a pistol the two are the same place
            // The rifle (13:08 play): every squeeze the user made "with the hand on the magazine" logged the hand
            // 13-22 cm from the magazine's middle and 16-21 cm from the front grip, never inside the 12 cm the test
            // asked for (what's measured is the controller, a wrist's length from the palm wrapped round the
            // magazine). The front grip is taken by itself now (no button), so on the rifle a fresh squeeze anywhere
            // round the magazine takes it out: within 24 cm of it and not clearly nearer the front grip (6 cm)
            else if (!_pullCand && !PistolInHand && pressed && canTake && toMag < 0.27f && toMag < toGrip + 0.06f)
            {
                _pullCand = true;
                _pullRel = rel;
                _pullByPress = true;
                if (_logs++ < 200)
                    Plugin.Logger.LogInfo($"[Reload] Squeezed by the rifle's magazine ({toMag * 100f:0} cm from it, {toGrip * 100f:0} cm from the front grip): it comes out now");
            }
            else if (!_pullCand && _atMag)
            {
                _pullCand = true;
                _pullRel = rel;
                // Squeezed right there (2026-11-11, the user: "the only button press needed is the one to remove the
                // magazine"): it comes out at once. Only a grip already held when the hand arrives still needs the
                // hand to move away first (so a sprint squeeze passing the magazine doesn't take it)
                _pullByPress = pressed;
                if (!_pullLogged && _logs++ < 200)
                {
                    _pullLogged = true;
                    Plugin.Logger.LogInfo($"[Reload] Squeezed at the gun's magazine ({toMag * 100f:0} cm from it, {toGrip * 100f:0} cm from the grip): {(pressed ? "it comes out now" : "grip was already held, it comes out when the hand pulls away 3 cm")}");
                }
            }
            // Squeezed with the hand on the gun somewhere else (holding the front, or near the magazine but not on it):
            // the magazine comes only if the hand then pulls away 3 cm while squeezed. Held still it's just a squeeze
            // (sprint), as before
            else if (!_pullCand && pressed && !beltWins && PistolInHand && (LeftHand.Gripped || toMag < 0.22f))
            {
                _pullCand = true;
                _pullRel = rel;
                _pullByPress = false;
                if (!_pullLogged && _logs++ < 200)
                {
                    _pullLogged = true;
                    Plugin.Logger.LogInfo($"[Reload] Squeezed with the hand on the gun ({toMag * 100f:0} cm from the magazine, {toGrip * 100f:0} cm from the grip): the magazine comes out if the hand pulls away 3 cm");
                }
            }
            else if (_pullCand && toMag > 0.45f)
                _pullCand = false;
            // (11:50 play: "down the well" was too strict. A squeeze at the magazine followed by moving the hand
            // away 25 cm never pulled it. Now any way but up counts: down, back towards you, or sideways)
            var pullThr = 0.03f;
            if (_pullCand)
            {
                var d0 = rel - _pullRel;
                pullDown = (d0 - _axisRel * Mathf.Max(0f, Vector3.Dot(d0, _axisRel))).magnitude;
            }
            var pullNow = _pullCand && (_pullByPress || pullDown > pullThr);
            // Every squeeze of the left grip that takes nothing: where the hand was, to tune the reach from one play
            if (pressed && !_holding && !_holdingOld && !_pullCand && !(nearBelt && spares > 0) && _logs++ < 200)
                Plugin.Logger.LogInfo($"[Reload] Left grip squeezed, nothing there to take: controller {toMag * 100f:0} cm from the gun's magazine, {toGrip * 100f:0} cm from the grip; hand as drawn {seenToMag * 100f:0} / {seenToGrip * 100f:0} cm;{Vector3.Distance(handPos, belt) * 100f:0} cm from the belt ({spares} spare(s) shown, magazine out {MagOut}, pistol {PistolInHand})");

            // A pulled-out magazine dropped on the floor (2026-11-11, the user: "if you accidentally drop the magazine
            // and it's still full or half empty, can we re-pick it up?"): squeeze with the hand at it (30 cm, nearer it
            // than the belt) and it's in your hand again, rounds and all, to push back in. Only while it's still the
            // gun's magazine, i.e. no other one has gone in since
            var pickUp = false;
            if (!_holding && !_holdingOld && pressed && MagOut && _droppedOld != null && _droppedOldFor == _magOutFor &&
                Middle(_droppedOld, out var droppedMid))
            {
                // Reaching down towards it is enough (15:28 play: three squeezes with the hand over it, 104-113 cm
                // above it, as far down as the arm goes standing): the hand within 35 cm of it sideways and lowered
                // (at least 35 cm below the eyes). Then it comes up into the hand
                var toDropped = Vector3.Distance(handPos, droppedMid);
                var sideways = Vector3.ProjectOnPlane(handPos - droppedMid, Vector3.up).magnitude;
                // The belt comes first (17:13 play, the user: going for the belt it "grabbed up the empty magazine that
                // was on the floor"): with the hand at the belt and spares on it, the floor magazine only if the hand is
                // right at it (25 cm)
                var beltFirst = nearBelt && spares > 0;
                pickUp = toDropped < 0.25f ||
                         (!beltFirst && (toDropped < 0.30f ||
                                         (sideways < 0.35f && handPos.y < headPos.y - 0.35f)));
                if (!pickUp && _logs++ < 200)
                    Plugin.Logger.LogInfo($"[Reload] Squeezed: the dropped magazine is {toDropped * 100f:0} cm from the hand, {sideways * 100f:0} cm to the side (picks up within 35 cm to the side, hand lowered)");
            }
            if (pickUp)
            {
                for (var i = _dropped.Count - 1; i >= 0; i--)
                    if (_dropped[i].go == _droppedOld)
                        _dropped.RemoveAt(i);
                UnityEngine.Object.Destroy(_droppedOld);
                _droppedOld = null;
                _holdingOld = true;
                _onRail = false;
                _mustClear = false;
                _leftRailAt = Time.unscaledTime;
                if (!_held.activeSelf)
                    _held.SetActive(true);
                Haptics.Pulse(XRNode.LeftHand, 0.5f, 0.06f);
                if (_logs++ < 200)
                    Plugin.Logger.LogInfo($"[Reload] Dropped magazine picked up again ({_oldAmmo}/{_oldMax} in it): push it up into the gun to put it back");
            }
            else if (!_holding && !_holdingOld && pressed && beltWins)
            {
                _holding = true;
                if (_beltTaken < 0 || _beltTaken >= allShown)
                    _beltTaken = allShown - 1;
                _onRail = false;
                _everRail = false;
                _mustClear = false;
                _blockedBuzz = false;
                Haptics.Pulse(XRNode.LeftHand, 0.4f, 0.06f);
                if (_logs++ < 200)
                    Plugin.Logger.LogInfo($"[Reload] Took a magazine from the belt ({spares} spare(s))");
            }
            else if (!_holding && !_holdingOld && pullNow)
            {
                // Gripped in the gun and pulled: it slides out as the hand goes down
                _pullCand = false;
                PullOut(weapon, mag);
                EnterRail(0f, _pullRel, false);
                _railPull = true;
                _railPullThr = _pullByPress ? 0f : pullThr;
                _railPullAuto = _pullByPress;
            }
            else if (_holding && released)
            {
                _holding = false;
                _onRail = false;
                if (_logs++ < 200)
                    Plugin.Logger.LogInfo("[Reload] Let go of the magazine: back to the belt");
            }
            else if (_holdingOld && released)
            {
                // Let go while it's still in the well (not yet clear, or back at it): it slides home. Only a magazine
                // that's free in the hand falls to the floor (11:50 play: one let go 5 cm out was dropped)
                if (_onRail)
                    Reseat("let go while in the well: it slid back in");
                else
                    DropOld();
            }

            if (_seating)
            {
                LeftHand.RailOn = false;
                Seat();
                UncurlFingers();
                return;
            }
            var inHand = _holding || _holdingOld;
            if (_held.activeSelf != inHand)
                _held.SetActive(inHand);
            if (!inHand)
            {
                _onRail = false;
                if (_atMag && !PistolInHand)
                {
                    // The empty hand drawn on the lower half of the gun's magazine, ready to take it
                    var holdAt = well - wf.rotation * _axisRel * (_magLen * 0.2f);
                    LeftHand.RailOn = true;
                    LeftHand.RailShiftWant = _freeMidFrame >= Time.frameCount - 2 ? holdAt - (_freeMid - LeftHand.RailShiftUsed) : Vector3.zero;
                    if (!_atMagLogged && _logs++ < 200)
                    {
                        _atMagLogged = true;
                        Plugin.Logger.LogInfo($"[Reload] Hand at the gun's magazine ({toMag * 100f:0} cm from it, {toGrip * 100f:0} cm from the grip): drawn on it, a squeeze takes it out");
                    }
                }
                else if (_beltMagnet)
                {
                    // At the belt: the empty hand drawn closing on the nearest magazine there, ready to take it
                    LeftHand.RailOn = true;
                    LeftHand.RailShiftRate = 8f;   // a gentle glide onto it, not a snap
                    LeftHand.RailShiftWant = _freeMidFrame >= Time.frameCount - 2 ? nearestBeltMid - (_freeMid - LeftHand.RailShiftUsed) : Vector3.zero;
                    _beltTaken = nearestBelt;
                    if (!_beltLogged && _logs++ < 200)
                    {
                        _beltLogged = true;
                        Plugin.Logger.LogInfo($"[Reload] Hand at the belt: drawn on magazine {nearestBelt + 1} ({Vector3.Distance(drawnHand, nearestBeltMid) * 100f:0} cm from the drawn hand), a squeeze takes it");
                    }
                }
                else
                {
                    _atMagLogged = false;
                    _beltLogged = false;
                    LeftHand.RailOn = false;
                    UncurlFingers();
                }
                return;
            }
            _handRot = handRot;
            // In the drawn hand (LeftHand calls PlaceInHand once it has put the hand); here only if it hasn't lately
            if (LeftHand.HandAtFrame < Time.frameCount - 2 && !_onRail)
                SetHeld(heldPos, heldRot);

            if (_onRail)
            {
                RailStep(weapon, mag, rel);
                LeftHand.RailOn = _onRail;
                return;
            }
            LeftHand.RailOn = false;

            // Free in the hand. The gun's own magazine, pulled out: hold it up and look at it to see what's left
            if (_holdingOld)
                ShowCount(heldMid, headPos, headRot);
            // How far the magazine's top is from the mouth of the well (the stretch from 2 cm below the mouth up to it),
            // and where along the well that is. Also tried without the hand's learnt grip offset; the nearer counts
            var atWell = false;
            var startS = -_travel;
            _tipGap = 9f;
            if (_haveRail)
            {
                // (11:33 play: the magazine was held where it sits in the gun, 7 cm from home, for 15 s and never
                // caught: only the mouth counted. Now anywhere along the well counts, right up to home)
                var axisW = wf.rotation * _axisRel;
                var mouthTip = well + axisW * (_magLen * 0.5f - _travel);
                var tip = heldMid + _held.transform.rotation * _alongLocal * (_magLen * 0.5f);
                var u = Mathf.Clamp(Vector3.Dot(tip - mouthTip, axisW), -0.02f, _travel);
                _tipGap = Vector3.Distance(tip, mouthTip + axisW * u);
                if (LeftHand.HandOnGunFrame >= Time.frameCount - 2)
                {
                    var tip2 = tip + (LeftHand.HandOnGunRawAt - LeftHand.HandOnGunAt);
                    var u2 = Mathf.Clamp(Vector3.Dot(tip2 - mouthTip, axisW), -0.02f, _travel);
                    var gap2 = Vector3.Distance(tip2, mouthTip + axisW * u2);
                    if (gap2 < _tipGap)
                    {
                        _tipGap = gap2;
                        u = u2;
                    }
                }
                // The gun's own magazine, just pulled out (14:54 play, the user: "once it's grabbed out you should be
                // able to move your hand with the magazine ... seconds later it goes back in the gun"): the log showed it
                // caught on the well again straight after coming out, while still 12-15 cm from it, and then the
                // slightest upward move pushed it home. Now it's free in your hand until you've taken it well away
                // (its top 25 cm from the gun) and bring it back close (10 cm) to put it in
                var oldMag = _holdingOld;
                if (_tipGap > 0.16f || Time.unscaledTime > _leftRailAt + 0.8f)
                    _mustClear = false;
                // 15 cm. (Cut to 10 at 12:45, which broke the pistol: its magazine's top never came nearer than 13 cm
                // in the 12:48 play; at 15 it went in at 12:40)
                atWell = _tipGap < 0.15f && !_mustClear;
                // The gun's own magazine, pulled out: it goes back on only when you PUSH it up into the gun (15:03
                // play: "couldn't get the magazine back in". The 14:55 rule wanted it taken 25 cm away first, and the
                // user held it 3-20 cm from the well for many seconds, never far enough). Now: held still or moved
                // about near the gun it stays in the hand (that was "seconds later it goes back in"); moved UP towards
                // the well (10 cm/s or more) within 15 cm of it, half a second after it came out, it goes on and in
                // (15:19 play, the MP7: moving the hand away to look at it, with a little upward in the move, put it back
                // twice — "it automatically kept putting it back into the magwell when I wanted to move out the way".
                // Now it must also be lined up UNDER the well: its top within 6 cm of the line the magazine goes in on,
                // and below the mouth, and pushed up a bit faster)
                if (oldMag)
                {
                    var dTip = tip - mouthTip;
                    var alongWell = Vector3.Dot(dTip, axisW);
                    var sideways = (dTip - axisW * alongWell).magnitude;
                    // (16:15: "trouble getting the magazine back into the well" — held 5-15 cm under it for many
                    // seconds; 6 cm sideways and 15 cm/s were too strict. Now 8 cm and 8 cm/s)
                    // (17:30, the MP7 — its magazine goes up into the pistol grip: held 13-18 cm under it for ~7 s
                    // before it went on. Now 10 cm sideways, -20..+6 cm along, and 6 cm/s up)
                    atWell = _tipGap < 0.15f && sideways < 0.10f && alongWell > -0.20f && alongWell < 0.06f &&
                             _axialVel > 0.06f && Time.unscaledTime > _leftRailAt + 0.5f;
                }
                // The pistol held out at arm's length (13:31 play, the user: "I want to be able to reload it when the
                // hands are outstretched"): the left hand was right at the pistol's grip (3-10 cm from it) but the new
                // magazine's top stayed 17-21 cm from the well, so it never went on. On a pistol the hand at its grip
                // is enough: the magazine goes onto the well from there
                if (PistolInHand && !_mustClear && toGrip < 0.12f)
                    atWell = true;
                // Wherever it's caught, there are always at least 5 cm left to push
                startS = Mathf.Min(-_travel + u, -0.05f);
            }
            if (_holding && !MagOut)
            {
                // The gun still has its magazine in: nothing can go in until that's out (X, or pull it by hand first)
                if (atWell && !_blockedBuzz)
                {
                    _blockedBuzz = true;
                    Haptics.Pulse(XRNode.LeftHand, 0.25f, 0.03f);
                    StatusHud.ShowNotice("MAGAZINE STILL IN THE GUN - PULL IT OUT OR PRESS X", 3f);
                    if (_logs++ < 200)
                        Plugin.Logger.LogInfo("[Reload] New magazine at the gun, but the old one is still in: take that out first (X, or pull it)");
                }
                return;
            }
            // At the well: onto the rail, its top at the mouth. After it has come off the rail once it goes back on
            // only when the hand moves up towards the gun again (so it doesn't flick on and off at the edge)
            if (atWell && Time.unscaledTime > _leftRailAt + 0.3f)
            {
                EnterRail(startS, rel, true);
                LeftHand.RailOn = true;
                Haptics.Pulse(XRNode.LeftHand, 0.35f, 0.03f);
                if (_logs++ < 200)
                    Plugin.Logger.LogInfo($"[Reload] {(_holdingOld ? "Old" : "New")} magazine in the well (its top {_tipGap * 100f:0} cm from it, {-startS * 100f:0} cm from home): a 3 cm push up seats it");
            }
        }
        catch (Exception e)
        {
            if (_logs++ < 200)
                Plugin.Logger.LogError($"[Reload] {e.GetType().Name}: {e.Message}");
            Hide();
        }
    }

    // ---- On the rail: sliding in and out of the well ----

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private void EnterRail(float s, Vector3 rel, bool glide)
    {
        _onRail = true;
        _everRail = true;
        _railS = s;
        _railStart = s;
        _railPull = false;
        _railPrevRel = _railEntryRel = rel;
        _railEnterAt = Time.unscaledTime;
        // From the hand onto the rail in a tenth of a second, not in one jump
        _railGlide = glide && _held != null && _held.activeSelf && Middle(_held, out _railFromPos);
        if (_railGlide)
            _railFromRot = _held.transform.rotation;
    }

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private void RailStep(WeaponV3 weapon, Magazine mag, Vector3 rel)
    {
        // 11:50 play: the magazine went on and off the well a dozen times and seated once. It had to be pushed its
        // whole travel (10-12 cm) with the left controller, towards the right one, and came off 2 cm below where it
        // was caught. Now the slide is geared: a SHORT hand movement (4 cm up to seat, 6 cm away to pull out) moves
        // the magazine its whole travel, and it only comes off the well when the hand goes 12 cm away
        var d = rel - _railEntryRel;
        var up = Vector3.Dot(d, _axisRel);
        var away = (d - _axisRel * Mathf.Max(0f, up)).magnitude;   // down, back or sideways
        var before = _railS;
        if (_railPull)
        {
            // The gun's own magazine coming out: all the way out once the hand is 6 cm on from where it started to move
            var p = Mathf.Clamp01((away - _railPullThr) / PullLen);
            // Taken out by the squeeze itself: it slides out by itself in a quarter of a second
            if (_railPullAuto)
                p = Mathf.Max(p, Mathf.Clamp01((Time.unscaledTime - _railEnterAt) / 0.25f));
            _railS = -_travel * p;
            if (p >= 1f)
            {
                _onRail = false;
                _mustClear = true;
                _leftRailAt = Time.unscaledTime;
                Haptics.Pulse(XRNode.LeftHand, 0.5f, 0.05f);
                if (_logs++ < 200)
                    Plugin.Logger.LogInfo($"[Reload] Magazine out of the gun and in the hand ({_oldAmmo}/{_oldMax} in it)");
                return;
            }
        }
        else
        {
            // Going in: home once the hand has pushed up 4 cm from where it was caught
            var p = Mathf.Clamp01(up / PushLen);
            _railS = Mathf.Lerp(_railStart, 0f, p);
            if (p >= 1f)
            {
                _onRail = false;
                if (_holdingOld)
                    Reseat("pushed back in by hand");
                else
                {
                    _railS = 0f;
                    ApplyRail(_railFrame);
                    Insert(weapon, mag, 0f);
                }
                return;
            }
            // (A pistol held out lets the hand wander more: 20 cm before it comes off; 13:31 it came off at 13 cm)
            // (rifle 12 -> 16 cm, 17:53: a new magazine came off twice while being lined up, the hand drifting 12-13 cm)
            if (away > (PistolInHand ? 0.20f : 0.16f))
            {
                _onRail = false;
                _mustClear = true;
                _leftRailAt = Time.unscaledTime;
                Haptics.Pulse(XRNode.LeftHand, 0.3f, 0.03f);
                if (_logs++ < 200)
                    Plugin.Logger.LogInfo($"[Reload] {(_holdingOld ? "Old" : "New")} magazine taken back off the well (hand {away * 100f:0} cm away)");
                return;
            }
        }
        // A light tick every 2 cm of slide
        if (Mathf.FloorToInt(before / 0.02f) != Mathf.FloorToInt(_railS / 0.02f))
            Haptics.Pulse(XRNode.LeftHand, 0.18f, 0.012f);
        ApplyRail(_railFrame);
    }

    // The magazine where it is on the rail, and the drawn hand brought to it
    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private void ApplyRail(Transform gun)
    {
        if (gun == null || _held == null)
            return;
        var s = Mathf.Clamp(_railS, -_travel - 0.04f, 0f);
        var pos = gun.TransformPoint(_wellLocal) + gun.rotation * _axisRel * s;
        var rot = gun.rotation * _seatRotRel;
        if (_railGlide)
        {
            var k = Mathf.Clamp01((Time.unscaledTime - _railEnterAt) / 0.10f);
            if (k >= 1f)
                _railGlide = false;
            else
            {
                k = k * k * (3f - 2f * k);
                pos = Vector3.Lerp(_railFromPos, pos, k);
                rot = Quaternion.Slerp(_railFromRot, rot, k);
            }
        }
        SetHeld(pos, rot, true, gun);
        // The hand's hold point goes where the magazine is (what LeftHand already shifted it by is taken off first)
        LeftHand.RailShiftWant = _freeMidFrame >= Time.frameCount - 2 ? pos - (_freeMid - LeftHand.RailShiftUsed) : Vector3.zero;
    }

    // The gun's own magazine is home again (pushed up, or let go while still mostly in)
    private void Reseat(string why)
    {
        _holdingOld = false;
        _onRail = false;
        _needGripRelease = true;
        if (_held != null)
            _held.SetActive(false);
        Haptics.Pulse(XRNode.LeftHand, 1f, 0.1f);
        Haptics.Pulse(XRNode.RightHand, 0.5f, 0.06f);
        PutBack(why);
    }

    // The pulled-out magazine held up in view: its round count beside it (MagLabel draws it)
    private void ShowCount(Vector3 magMid, Vector3 headPos, Quaternion headRot)
    {
        var to = magMid - headPos;
        var far = to.magnitude;
        if (far < 0.05f || far > 0.9f || Vector3.Angle(headRot * Vector3.forward, to) > 40f)
            return;
        // The game's own magazine-check writing on the HUD (2026-11-11, the user: "can I have the nice writing like
        // it shows when you do the manual check of the magazine?"), instead of the little label beside it. The game's
        // OnScreenNotification.SetMagCheckString shows it; MagCount adds the exact count under it as for a manual
        // check. Set again every second while you look, so it stays up
        // (The little label beside the magazine stays too until the game's writing is seen working in the headset,
        // so the count never goes missing)
        MagLabel.ShowInHand(magMid, _oldAmmo, _oldMax);
        if (Time.unscaledTime < _nextCheckText)
            return;
        _nextCheckText = Time.unscaledTime + 1f;
        try
        {
            var share = _oldMax > 0 ? _oldAmmo / (float)_oldMax : 0f;
            var words = _oldAmmo <= 0 ? "Magazine empty"
                      : share >= 0.95f ? "Magazine full"
                      : share >= 0.6f ? "Magazine mostly full"
                      : share >= 0.35f ? "Magazine about half full"
                      : "Magazine nearly empty";
            OnScreenNotification.SetMagCheckString(words);
            if (!_checkTextLogged && _logs++ < 200)
            {
                _checkTextLogged = true;
                Plugin.Logger.LogInfo($"[Reload] Pulled-out magazine held up: the game's magazine-check text shown ('{words}', {_oldAmmo}/{_oldMax})");
            }
        }
        catch (Exception e)
        {
            // If the game's writing can't be shown, the label beside the magazine instead
            MagLabel.ShowInHand(magMid, _oldAmmo, _oldMax);
            if (!_checkTextLogged && _logs++ < 200)
            {
                _checkTextLogged = true;
                Plugin.Logger.LogInfo($"[Reload] Magazine-check text: {e.GetType().Name}: {e.Message} (label shown instead)");
            }
        }
    }

    private float _nextCheckText;
    private bool _checkTextLogged;

    // ---- The magazine goes in ----

    // The held magazine slides up into the gun (the user's reference, a Ready or Not VR clip, 2026-11-10: the
    // magazine visibly goes up into the well; ours vanished the moment it was near enough)
    private const float SeatTime = 0.10f;
    private bool _seating;
    private float _seatStart;
    private Vector3 _seatFrom, _seatToLocal;
    private Quaternion _seatFromRot, _seatRotLocal;
    private Transform _seatFrame;

    private void StartSeat(WeaponV3 weapon, Magazine mag)
    {
        if (_held == null || !_held.activeSelf || _wellLocal == Vector3.zero || !Middle(_held, out var from))
        {
            if (_held != null)
                _held.SetActive(false);
            return;
        }
        _seating = true;
        _seatStart = Time.unscaledTime;
        _seatFrom = from;
        _seatFromRot = _held.transform.rotation;
        // Home is where the game's magazine sits right now, kept relative to what it hangs from on the gun (the
        // game's reload is about to take the magazine itself away)
        // (Kept relative to the gun itself, not what the magazine hangs from: 16:05 footage, the pistol — the game's
        // reload animation moves the magazine's holder, and our magazine went with it, off to the right of the pistol)
        _seatFrame = weapon.transform;
        _seatToLocal = _seatFrame.InverseTransformPoint(_railFrame != null ? _railFrame.TransformPoint(_wellLocal) : mag.transform.TransformPoint(_wellLocal));
        _seatRotLocal = Quaternion.Inverse(_seatFrame.rotation) * (_railFrame != null ? _railFrame.rotation : mag.transform.rotation);
    }

    private void Seat()
    {
        var t = (Time.unscaledTime - _seatStart) / SeatTime;
        // It stays in the gun until the game's own (sped-up) reload has put its magazine there, so the well is never
        // seen empty in between
        var gameStillAtIt = (_sped != null || Time.unscaledTime < _hideMagsUntil) && Time.unscaledTime - _seatStart < 3.5f;
        if ((t >= 1f && !gameStillAtIt) || _held == null || _seatFrame == null)
        {
            _seating = false;
            if (_held != null)
                _held.SetActive(false);
            return;
        }
        t = Mathf.Clamp01(t);
        var k = 1f - (1f - t) * (1f - t);   // quick at first, settling at the end
        var gun = _seatFrame;
        SetHeld(Vector3.Lerp(_seatFrom, gun.TransformPoint(_seatToLocal), k),
                Quaternion.Slerp(_seatFromRot, gun.rotation * _seatRotLocal, k), true, gun);
    }

    private void Insert(WeaponV3 weapon, Magazine mag, float gap)
    {
        _holding = false;
        _onRail = false;
        _needGripRelease = true;
        StartSeat(weapon, mag);
        Haptics.Pulse(XRNode.LeftHand, 1f, 0.12f);
        Haptics.Pulse(XRNode.RightHand, 0.6f, 0.08f);
        var key = ReloadKey();
        if (key == 0)
        {
            if (_logs++ < 200)
                Plugin.Logger.LogWarning("[Reload] Magazine at the well, but the game's Reload key isn't known yet");
            return;
        }
        // The game's reload, by its own key: a short tap (as the X button does), or twice quickly for the emergency
        // reload when the old magazine was pulled out (the game then drops it, rounds and all)
        var now = Time.unscaledTime;
        var emergency = MagOut;
        _keys.Add((now, key, true));
        _keys.Add((now + 0.06f, key, false));
        if (emergency)
        {
            _keys.Add((now + 0.12f, key, true));
            _keys.Add((now + 0.18f, key, false));
            // The game's old magazine stays hidden while the game drops it (ours is already on the floor)
            MagOut = false;
            _magOutFor = IntPtr.Zero;
            _showPartsAt = now + 15f;
            _emergencyAt = now;
        }
        _spedGun = weapon;
        SpeedUp();
        if (_logs++ < 200)
            Plugin.Logger.LogInfo($"[Reload] Magazine in ({gap * 100f:0} cm from the well): game {(emergency ? "emergency reload (old one dropped)" : "reload")} started ({mag.ammoAmount}/{mag.ammoAmountMax} in the old one, chamber {(weapon.roundInChamber ? "loaded" : "empty")})");
    }

    // ---- Pulling the magazine out ----

    private void PullOut(WeaponV3 weapon, Magazine mag)
    {
        ShowParts();
        foreach (var r in mag.GetComponentsInChildren<Renderer>(false))
            if (r != null && r.enabled)
            {
                r.enabled = false;
                _hiddenParts.Add(r);
            }
        MagOut = true;
        _magOutFor = mag.Pointer;
        _hiddenFor = mag.Pointer;
        _holdingOld = true;
        _oldAmmo = mag.ammoAmount;
        _oldMax = mag.ammoAmountMax;
        _oldLeftWell = false;
        _pulledAt = Time.unscaledTime;
        _showPartsAt = -1f;
        Haptics.Pulse(XRNode.LeftHand, 0.7f, 0.08f);
        if (_logs++ < 200)
            Plugin.Logger.LogInfo($"[Reload] Magazine pulled out ({mag.ammoAmount}/{mag.ammoAmountMax}, chamber {(weapon.roundInChamber ? "loaded" : "empty")}; {_hiddenParts.Count} parts hidden)");
    }

    // Let go of the pulled-out magazine: it falls to the floor (a copy; the game's own is dropped by the emergency
    // reload when a new one goes in)
    private void DropOld()
    {
        _holdingOld = false;
        _onRail = false;
        try
        {
            var drop = UnityEngine.Object.Instantiate(_held);
            drop.name = "OperatorVR Dropped mag";
            drop.transform.SetParent(null, true);
            drop.SetActive(true);
            // It falls by our own hand to the floor found under it and lies there (15:19 play: with the game's physics
            // the dropped magazines fell straight through the floor — the log measured one 1,964 m below the hand —
            // so there was nothing to pick up)
            _falling.Add((drop, Vector3.ClampMagnitude(_handVel, 3f), FloorUnder(drop.transform.position)));
            // Dropped with X: not seen falling from the gun (16:52, the user: the pistol's magazine "drops to the right
            // of the pistol, I don't want that to be seen there"). It shows once it's lying on the floor
            if (_dropUnseen)
                foreach (var r in drop.GetComponentsInChildren<Renderer>(false))
                    if (r != null)
                        r.forceRenderingOff = true;
            // It can be picked up again (TryPickUp) as long as no other magazine has gone in: the game's own magazine,
            // rounds and all, is still on the gun, only hidden. So it stays on the floor 2 minutes, not 20 s
            _droppedOld = drop;
            _droppedOldFor = _magOutFor;
            _dropped.Add((drop, Time.unscaledTime + 120f));
        }
        catch (Exception e)
        {
            if (_logs++ < 200)
                Plugin.Logger.LogInfo($"[Reload] dropped magazine: {e.Message}");
        }
        if (_logs++ < 200)
            Plugin.Logger.LogInfo("[Reload] Pulled-out magazine dropped");
    }

    // The magazine is back in the gun as it was (same one pushed back, or the gun changed)
    private void PutBack(string why)
    {
        MagOut = false;
        _magOutFor = IntPtr.Zero;
        ShowParts();
        if (_logs++ < 200)
            Plugin.Logger.LogInfo($"[Reload] Magazine back in: {why}");
    }

    private void ShowParts()
    {
        foreach (var r in _hiddenParts)
            try { if (r != null) r.enabled = true; } catch { }
        _hiddenParts.Clear();
        _showPartsAt = -1f;
    }

    private readonly List<(GameObject go, Vector3 vel, float floor)> _falling = new();
    private float _lastHeadY;

    // The floor under a point: the first thing hit straight down that isn't the player (or one of our copies). None
    // found within 3 m: about where your feet are
    private float FloorUnder(Vector3 p)
    {
        var fallback = _lastHeadY != 0f ? _lastHeadY - 1.6f : p.y - 1f;
        try
        {
            var me = _me != null ? _me.transform.root : null;
            var best = float.NegativeInfinity;
            foreach (var h in Physics.RaycastAll(p + Vector3.up * 0.05f, Vector3.down, 3f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (h.collider == null)
                    continue;
                var t = h.collider.transform;
                if ((me != null && t.IsChildOf(me)) || t.name.StartsWith("OperatorVR"))
                    continue;
                if (h.point.y > best)
                    best = h.point.y;
            }
            if (!float.IsNegativeInfinity(best))
                return best;
        }
        catch { }
        return fallback;
    }

    // Falling magazines: down under gravity, then lying flat on the floor
    private void Fall()
    {
        var dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
        for (var i = _falling.Count - 1; i >= 0; i--)
        {
            var (go, vel, floor) = _falling[i];
            if (go == null)
            {
                _falling.RemoveAt(i);
                continue;
            }
            vel += Vector3.down * 9.81f * dt;
            vel.x *= 1f - Mathf.Min(1f, dt * 2f);
            vel.z *= 1f - Mathf.Min(1f, dt * 2f);
            var t = go.transform;
            t.position += vel * dt;
            t.Rotate(Vector3.right * (200f * dt), Space.Self);   // tumbling a little on the way down
            if (LowestY(go, out var low) && low > floor)
            {
                _falling[i] = (go, vel, floor);
                continue;
            }
            // Landed: lying on its side, its lowest point on the floor
            var longW = t.rotation * _alongLocal;
            var flat = Vector3.ProjectOnPlane(longW, Vector3.up);
            if (flat.sqrMagnitude < 1e-4f)
                flat = Vector3.forward;
            t.rotation = Quaternion.FromToRotation(longW, flat.normalized) * t.rotation;
            if (LowestY(go, out low))
                t.position += Vector3.up * (floor - low + 0.002f);
            // Unseen on its way down (an X drop) — shown now it's lying there
            foreach (var r in go.GetComponentsInChildren<Renderer>(false))
                if (r != null)
                    r.forceRenderingOff = false;
            _falling.RemoveAt(i);
            if (_logs++ < 200)
                Plugin.Logger.LogInfo($"[Reload] Dropped magazine is lying on the floor ({floor:0.00} m up)");
        }
    }

    private static bool LowestY(GameObject go, out float y)
    {
        y = float.PositiveInfinity;
        foreach (var r in go.GetComponentsInChildren<Renderer>(false))
            if (r != null && r.enabled)
                y = Mathf.Min(y, r.bounds.min.y);
        return !float.IsPositiveInfinity(y);
    }

    // ---- The game's own magazines out of sight during its (sped-up) reload ----
    // 15:37 footage, the user: "when the gun reloads, the magazine always passes the right of the gun ... remove that
    // animated part". After a magazine is pushed in by hand, the game's own reload still runs (it does the ammo): its
    // animation takes a magazine from the pouch past the right of the gun and drops the old one. Our copy is already
    // seated in the well, so from the push-in until a moment after the game's reload is over, every game magazine near
    // you is hidden; the one that ends up in the gun shows again after
    private float _hideMagsUntil = -1f;
    private readonly List<Renderer> _reloadHidden = new();
    private bool _reloadHideLogged;

    private void HideGameMagsDuringReload()
    {
        var active = _sped != null || Time.unscaledTime < _hideMagsUntil;
        if (!active)
        {
            if (_reloadHidden.Count > 0)
            {
                // Only what belongs to you comes back (the magazine now in the gun, the pouch's): the old magazine the
                // game threw on the floor stays hidden (16:15 footage: it "flashed up on the left" / "to the right of
                // the pistol" when it was shown again, our own dropped copy already lies there)
                var root = _me != null ? _me.transform.root : null;
                foreach (var r in _reloadHidden)
                    try { if (r != null && root != null && r.transform.IsChildOf(root)) r.enabled = true; } catch { }
                _reloadHidden.Clear();
            }
            return;
        }
        if (_sped != null)
            _hideMagsUntil = Time.unscaledTime + 0.25f;
        try
        {
            var centre = _me != null ? _me.transform.position : Vector3.zero;
            var n = 0;
            foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<Magazine>(), FindObjectsSortMode.None))
            {
                var m = o.TryCast<Magazine>();
                if (m == null || (m.transform.position - centre).sqrMagnitude > 4f)
                    continue;
                foreach (var r in m.GetComponentsInChildren<Renderer>(false))
                    if (r != null && r.enabled)
                    {
                        r.enabled = false;
                        _reloadHidden.Add(r);
                        n++;
                    }
            }
            if (n > 0 && !_reloadHideLogged && _logs++ < 200)
            {
                _reloadHideLogged = true;
                Plugin.Logger.LogInfo($"[Reload] The game's own magazines hidden while its reload runs ({n} parts); our magazine in the well shows instead");
            }
        }
        catch { }
    }

    private void TidyDropped()
    {
        HideGameMagsDuringReload();
        Fall();
        if (_showPartsAt > 0f && Time.unscaledTime >= _showPartsAt)
            ShowParts();
        for (var i = _dropped.Count - 1; i >= 0; i--)
            if (_dropped[i].go == null || Time.unscaledTime >= _dropped[i].until)
            {
                if (_dropped[i].go != null)
                    UnityEngine.Object.Destroy(_dropped[i].go);
                _dropped.RemoveAt(i);
            }
    }

    private void RunKeys()
    {
        var now = Time.unscaledTime;
        while (_keys.Count > 0 && now >= _keys[0].at)
        {
            TouchKeys.PressKey((KeyCode)_keys[0].key, _keys[0].down);
            _keys.RemoveAt(0);
        }
    }

    private static int ReloadKey()
    {
        try
        {
            var binds = InputLayer.Binds;
            var i = (int)InputKey.Reload;
            if (binds == null || i >= binds.Length || binds[i] == null)
                return 0;
            return (int)binds[i].KeyBind.PrimaryKey;
        }
        catch { return 0; }
    }

    // The game's reload animation drives the reload; played faster so it's over soon after the magazine clicks in
    private void SpeedUp()
    {
        var k = Mathf.Clamp(Plugin.ManualReloadSpeed.Value, 1f, 30f);
        if (k <= 1.01f || _me == null)
            return;
        try
        {
            // The reload's animation events land on FPSAnimationHelper, so its animator is the one playing the reload
            Animator anim = null;
            foreach (var h in _me.GetComponentsInChildren<FPSAnimationHelper>(false))
            {
                anim = h != null ? h.GetComponent<Animator>() ?? h.GetComponentInParent<Animator>() : null;
                if (anim != null)
                    break;
            }
            if (anim == null)
            {
                if (_logs++ < 200)
                    Plugin.Logger.LogInfo("[Reload] No first-person animator found: the reload plays at normal speed");
                return;
            }
            if (_sped == null)
            {
                _sped = anim;
                _spedWas = anim.speed;
            }
            anim.speed = _spedWas * k;
            _spedK = k;
            _sawReloading = false;
            _spedStart = Time.unscaledTime;
            // Until the game says the reload is over (at most 3 s)
            _speedUntil = Time.unscaledTime + 3f;
        }
        catch (Exception e)
        {
            if (_logs++ < 200)
                Plugin.Logger.LogInfo($"[Reload] speed-up: {e.Message}");
        }
    }

    // The sixth play (user: "the magazine has to be pushed in manually, get rid of the automatic reload"): the game's
    // own reload animation still played after the hand put the magazine in. The game's reload does the ammo, the old
    // magazine and the network through its animation events, so it still runs, but now as fast as it will go (about
    // 20x): over in a blink, and the speed-up ends as soon as the game says the reload is done
    private float _spedK = 1f, _spedStart;
    private WeaponV3 _spedGun;
    private bool _sawReloading;

    private float _keepHandUntil = -1f;

    private void EndSpeedUp()
    {
        // The left hand stays yours through the game's reload and a moment after (the game's hand pose settles back)
        LeftHand.KeepThroughGameReload = _sped != null || Time.unscaledTime < _keepHandUntil;
        if (_sped == null)
            return;
        _keepHandUntil = Time.unscaledTime + 0.3f;
        var reloading = false;
        try { reloading = _spedGun != null && _spedGun.gunFunction != null && _spedGun.gunFunction.isReloading; } catch { }
        if (reloading)
            _sawReloading = true;
        var done = Time.unscaledTime >= _speedUntil || (_sawReloading && !reloading) ||
                   (!_sawReloading && Time.unscaledTime > _spedStart + 1f);
        if (!done)
        {
            // The game may set its own speed during the reload: keep ours on
            try { if (Mathf.Abs(_sped.speed - _spedWas * _spedK) > 0.01f) _sped.speed = _spedWas * _spedK; } catch { }
            return;
        }
        try { _sped.speed = _spedWas; } catch { }
        if (_logs++ < 200)
            Plugin.Logger.LogInfo($"[Reload] Game reload over after {Time.unscaledTime - _spedStart:0.00} s (sped up x{_spedK:0}, saw it reloading: {_sawReloading})");
        _sped = null;
    }

    // ---- Finding the player, the gun's magazine and the spares ----

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private bool Find(out WeaponV3 weapon, out Magazine mag, out int spares)
    {
        weapon = null;
        mag = null;
        spares = 0;
        if (_me == null && Time.unscaledTime >= _nextFind)
        {
            _nextFind = Time.unscaledTime + 2f;
            foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<PlayerNetworking>(), FindObjectsSortMode.None))
            {
                var p = o.TryCast<PlayerNetworking>();
                if (p != null && p.gameObject.name.Contains("MyPlayer"))
                {
                    _me = p;
                    break;
                }
            }
        }
        if (_me == null || _me.activeWeapon == null)
            return false;
        weapon = _me.activeWeapon.GetComponent<WeaponV3>();
        mag = weapon != null && weapon.gunFunction != null ? weapon.gunFunction.currentMagazine : null;
        if (mag == null)
            return false;
        var pouchGo = _me.MagPouch;
        var pouch = pouchGo != null ? pouchGo.transform : null;
        var names = new List<string>();
        if (pouch != null)
            for (var i = 0; i < pouch.childCount; i++)
            {
                var c = pouch.GetChild(i);
                if (c == null)
                    continue;
                names.Add(c.gameObject.activeSelf ? c.name : c.name + " (hidden)");
                // The pouch holds every gun's magazines (rifle and pistol together): only this gun's count
                // (By the name without "(Clone)": 2026-11-11 the user refilled at the armoury bench and no magazines
                // came back on the belt. The game refills through AddMagToPouch(name, ...), whose new magazines may not
                // carry the same "(Clone)" ending as the first ones)
                if (c.gameObject.activeSelf && BaseName(c.name) == BaseName(mag.name))
                    spares++;
            }
        // Once per gun, and whenever what the pouch holds changes (a refill at the bench): everything in it
        var pouchKey = names.Count * 1000 + spares;
        // (its own allowance: the general 200 lines can be used up long before a refill late in a session)
        if ((mag.Pointer != _pouchLoggedFor || pouchKey != _pouchLoggedKey) && _pouchLogs++ < 60)
        {
            _pouchLoggedFor = mag.Pointer;
            _pouchLoggedKey = pouchKey;
            Plugin.Logger.LogInfo($"[Reload] Gun '{_me.activeWeapon.name}' magazine '{mag.name}': {spares} spare(s); the pouch holds {names.Count}: {string.Join(", ", names)}");
        }
        return true;
    }

    // Copies of the gun's magazine, real size: the belt's and the one in the hand. Made again for another gun
    private void Build(WeaponV3 weapon, Magazine mag)
    {
        // (Made again if the magazine in the hand was lost with a gun it hung from)
        if (_root != null && _held != null && (_builtFor == mag.Pointer || _seating))
            return;
        // Not while the game's reload has the magazine in the hand: it's turned any old way then (the belt magazines
        // once lay flat), so wait until it's back in the gun
        // (If the game never parents it to the gun, build anyway after a few seconds)
        if (!mag.transform.IsChildOf(weapon.transform))
        {
            if (_notInGunSince < 0f)
                _notInGunSince = Time.unscaledTime;
            if (Time.unscaledTime - _notInGunSince < 4f)
                return;
            if (_logs++ < 200)
                Plugin.Logger.LogInfo($"[Reload] '{mag.name}' isn't under the gun (parent '{mag.transform.parent?.name}'): belt made anyway");
        }
        _notInGunSince = -1f;
        Clear();
        _haveHeldMid = false;
        _wellLocal = Vector3.zero;
        _builtFor = mag.Pointer;
        _root = new GameObject("OperatorVR Waist Mags");
        UnityEngine.Object.DontDestroyOnLoad(_root);
        // How the magazine sits in the gun (so on the belt and in the hand it hangs the same way), relative to the
        // game's aim. Taken by LearnTurn once the gun is held normally (see there), kept per kind of magazine
        _turnGood = s_magTurn.TryGetValue(mag.name, out _magInGun);
        _magLongLocal = Vector3.up;
        for (var i = 0; i < MaxShown + 1; i++)
        {
            var copy = ModelCopy.Build(mag.transform, i < MaxShown ? $"Belt mag {i + 1}" : "Held mag", _root.transform, 1f, false, out var parts, out var across, out var shape);
            // Its long side (in the magazine's own axes; the copy keeps them)
            if (i == 0)
            {
                // A short magazine = a pistol (not the M79 launcher, whose 10 cm "magazine" isn't in a grip)
                var gunName = "";
                try { gunName = _me.activeWeapon.name; } catch { }
                PistolInHand = across < 0.15f && !gunName.Contains("M79");
                _magLen = across;
                _haveRail = false;
            }
            if (i == 0)
                _magLongLocal = shape.x >= shape.y && shape.x >= shape.z ? Vector3.right
                              : shape.y >= shape.z ? Vector3.up : Vector3.forward;
            // Real size: built 1 unit across, scaled back to its own size
            copy.transform.localScale = Vector3.one * Mathf.Max(0.01f, across);
            copy.SetActive(false);
            if (i < MaxShown)
                _beltMags.Add(copy);
            else
                _held = copy;
            if (i == 0 && _logs++ < 200)
                Plugin.Logger.LogInfo($"[Reload] Waist magazines made from '{mag.name}' ({parts} parts, {across * 100f:0} cm long)");
        }
    }

    // The magazine's turn, learnt from the gun while it's held normally. The eighth play: taken the moment the belt
    // was made, just after a weapon switch, the gun was still turned in its draw animation and the magazines (on the
    // belt and in the hand) lay on their side; earlier a reload's animation turned them upside down. Now it's taken
    // only when the magazine stands upright (its long side within ~35 degrees of the aim's up/down), the gun isn't
    // reloading and the magazine is in it. Until then the belt isn't shown
    private bool _turnGood;
    private Vector3 _magLongLocal = Vector3.up;

    private void LearnTurn(WeaponV3 weapon, Magazine mag)
    {
        if (_turnGood || MagOut || _holding || _holdingOld || !mag.transform.IsChildOf(weapon.transform))
            return;
        try { if (weapon.gunFunction != null && weapon.gunFunction.isReloading) return; } catch { }
        if (!HeadTracking.TryGetGameAim(out var pitch, out var aimYaw))
            return;
        var turn = Quaternion.Inverse(Quaternion.Euler(pitch, aimYaw, 0f)) * mag.transform.rotation;
        var upright = Mathf.Abs((turn * _magLongLocal).y);
        if (upright < 0.82f)
            return;
        // (A check that the magazine hangs below the gun's middle was tried 21:11 and removed 21:38: for the HK416D
        // it never passed, so the turn was never learnt and the belt stayed empty)
        _magInGun = turn;
        _turnGood = true;
        s_magTurn[mag.name] = turn;
        if (_logs++ < 200)
            Plugin.Logger.LogInfo($"[Reload] Magazine turn learnt for '{mag.name}' (upright {upright:0.00}, aim pitch {pitch:0})");
    }

    private void Hide()
    {
        TakesLeftGrip = false;
        s_canDrop = false;
        s_dropAsked = false;
        LeftHand.ReloadHand = false;
        LeftHand.KeepOffGun = false;
        LeftHand.RailOn = false;
        _onRail = false;
        _atMag = false;
        _holding = false;
        _holdingOld = false;   // a pulled-out magazine stays out (as if dropped)
        _seating = false;
        UncurlFingers();
        if (_root != null && _root.activeSelf)
            foreach (var go in _beltMags)
                if (go != null && go.activeSelf)
                    go.SetActive(false);
        if (_held != null && _held.activeSelf)
            _held.SetActive(false);
    }

    private void Clear()
    {
        // (the magazine in the hand may hang from the gun just now, not from our root)
        if (_held != null && (_root == null || !_held.transform.IsChildOf(_root.transform)))
            UnityEngine.Object.Destroy(_held);
        if (_root != null)
            UnityEngine.Object.Destroy(_root);
        _root = null;
        _beltMags.Clear();
        _held = null;
    }

    private Vector3 _wellLocal;
    private float _notInGunSince = -1f;
    private Quaternion _handRot = Quaternion.identity;
    private Vector3 _heldMidLocal;
    private bool _haveHeldMid;
    private static ManualReload s_me;

    // X (the Reload button) with hand reloading on (2026-11-10, the user: "a button that drops the magazine, then we
    // pick up a new one off the belt and put it in"): a tap drops the gun's magazine (even a full one) instead of the
    // game's automatic reload. Tapped again while the magazine is out: the game's reload after all (a way out if the
    // hand insert won't go).
    private static bool s_canDrop, s_dropAsked;

    /// <summary>The Reload button was tapped: true if the hand reload takes it (the game's key is then not pressed).</summary>
    internal static bool AskDrop()
    {
        if (!s_canDrop)
            return false;
        s_dropAsked = true;
        return true;
    }

    private void DropByButton(WeaponV3 weapon, Magazine mag, Vector3 well, float spareCount)
    {
        if (MagOut)
        {
            if (_logs++ < 200)
                Plugin.Logger.LogInfo("[Reload] X while the magazine is out: the game's reload");
            _holdingOld = false;
            _holding = false;
            Insert(weapon, mag, 0f);
            return;
        }
        if (_holdingOld)
            return;
        // Out of the gun and straight to the floor: the copy starts where the gun's magazine is, as it sits in the gun.
        // Also with a new magazine already in the left hand (that one stays where it is)
        var keep = _holding;
        var keepPos = _held.transform.position;
        var keepRot = _held.transform.rotation;
        _held.SetActive(true);
        SetHeld(well, mag.transform.rotation, middle: true);
        PullOut(weapon, mag);
        // Straight out of the bottom of the well and down (15:45 footage: dropped "down" in the weapon object's own
        // axes, which on the Glock point sideways, the magazine shot off to the right of the pistol)
        // (16:15: "out of the well" still went sideways on the Glock — 51 cm to the side. Now simply straight down)
        _handVel = Vector3.down * 0.8f;
        _dropUnseen = true;
        try { DropOld(); }
        finally { _dropUnseen = false; }
        if (keep)
            _held.transform.SetPositionAndRotation(keepPos, keepRot);
        else
            _held.SetActive(false);
        _blockedBuzz = false;
        Haptics.Pulse(XRNode.RightHand, 0.5f, 0.06f);
        if (_logs++ < 200)
            Plugin.Logger.LogInfo($"[Reload] X: magazine dropped ({spareCount} spare(s) on the belt)");
    }

    private void Awake()
    {
        s_me = this;
        LeftHand.OnHandPlaced = PlaceInHand;
    }

    // The magazine in the drawn hand's palm (the second play: it hung behind the wrist, out of sight, since it went
    // by the controller and the hand is drawn a little off from it)
    private static void PlaceInHand(Transform hand, Quaternion handRot)
    {
        var self = s_me;
        if (self == null || hand == null || self._held == null)
            return;
        var holding = self._held.activeSelf && (self._holding || self._holdingOld);
        if (!holding && !self._atMag && !self._beltMagnet)
            return;
        // The drawn hand's turn, back to the controller's (the hand may be drawn relative to the gun, turned with it)
        var ctrl = handRot * Quaternion.Inverse(LeftHand.RotOffset);
        // In the hand itself, between the palm and the thumb, found from the drawn hand's own finger bones (the
        // seventh play: worked out from the wrist with a guessed offset, the magazine stood behind the hand)
        var grip = self.GripPoint(hand, out var fromBones);
        if (!fromBones)
            grip = hand.position + ctrl * new Vector3(0f, -0.02f, 0.08f);
        // Held by its lower half: the top (feed end) sticks up out of the hand, ready to go in
        self._freeMid = grip + ctrl * new Vector3(0f, 0.04f, 0f);
        self._freeMidFrame = Time.frameCount;
        // Empty hand at the gun's magazine: only where the hand's hold point is (LateUpdate brings it onto the
        // magazine), and the fingers closed round it
        if (!holding)
        {
            if (fromBones)
                self.CurlFingers(hand, grip);
            return;
        }
        // In the well it stays on its rail (and the hand is brought to it); otherwise it's in the hand
        if (self._onRail)
            self.ApplyRail(self._railFrame);
        else
        {
            // 12:40 play (user: "numerous times it wasn't in the hand when it was pushing it back into the gun"):
            // the magazine turned with the controller, so with the hand rolled it lay flat, sticking sideways out of
            // the fist. Coming towards the gun (from 35 cm to 12 cm, top to the well) it now turns to line up with
            // the well, and it's held by its lower part, the top out of the hand, so it looks ready to go in
            var rot = ctrl * self._magInGun;
            if (self._railFrame != null && self._haveRail)
            {
                var t = Mathf.InverseLerp(0.35f, 0.12f, self._tipGap);
                rot = Quaternion.Slerp(rot, self._railFrame.rotation * self._seatRotRel, t * t * (3f - 2f * t));
            }
            self._freeMid = grip + rot * self._alongLocal * (self._magLen * 0.2f);
            self.SetHeld(self._freeMid, rot, true);
        }
        if (fromBones)
            self.CurlFingers(hand, grip);
    }

    // ---- Fingers closed round the magazine (the ninth play: "not holding it in the hand properly", the hand open) ----

    private static readonly string[] FingerNames = { "index", "middle", "ring", "pinky" };
    private static readonly float[] JointCurl = { 50f, 55f, 35f };   // knuckle, middle joint, tip
    private Transform _fingersFor;
    private readonly List<Transform[]> _fingers = new();
    private Transform[] _thumb;
    private readonly Dictionary<IntPtr, (Quaternion baseRot, Quaternion setRot)> _curlState = new();

    private void CurlFingers(Transform hand, Vector3 grip)
    {
        try
        {
            if (_fingersFor != hand)
            {
                _fingersFor = hand;
                _fingers.Clear();
                _curlState.Clear();
                var all = hand.GetComponentsInChildren<Transform>(true);
                Transform Bone(string finger, string joint)
                {
                    foreach (var t in all)
                        if (t != null && t.name.Equals($"{finger}_{joint}_l", StringComparison.OrdinalIgnoreCase))
                            return t;
                    return null;
                }
                foreach (var f in FingerNames)
                {
                    var j = new[] { Bone(f, "01"), Bone(f, "02"), Bone(f, "03") };
                    if (j[0] != null && j[1] != null)
                        _fingers.Add(j);
                }
                _thumb = new[] { Bone("thumb", "02"), Bone("thumb", "03") };
                if (_logs++ < 200)
                    Plugin.Logger.LogInfo($"[Reload] Fingers found to close round the magazine: {_fingers.Count} (thumb {(_thumb[0] != null ? "yes" : "no")})");
            }
            foreach (var j in _fingers)
            {
                // Bend towards the magazine: about the axis across the finger, on the magazine's side
                var along = j[1].position - j[0].position;
                var axis = Vector3.Cross(along, grip - j[0].position);
                if (axis.sqrMagnitude < 1e-10f)
                    continue;
                axis.Normalize();
                for (var i = 0; i < 3; i++)
                    Bend(j[i], axis, JointCurl[i]);
            }
            if (_thumb != null && _thumb[0] != null && _thumb[1] != null)
            {
                var along = _thumb[1].position - _thumb[0].position;
                var axis = Vector3.Cross(along, grip - _thumb[0].position);
                if (axis.sqrMagnitude > 1e-10f)
                {
                    axis.Normalize();
                    Bend(_thumb[0], axis, 20f);
                    Bend(_thumb[1], axis, 25f);
                }
            }
        }
        catch (Exception e)
        {
            if (_logs++ < 200)
                Plugin.Logger.LogInfo($"[Reload] fingers: {e.Message}");
        }
    }

    // Hand empty again: joints the animation hasn't set since go back as they were
    private void UncurlFingers()
    {
        if (_curlState.Count == 0)
            return;
        foreach (var j in _fingers)
            foreach (var b in j)
                Unbend(b);
        if (_thumb != null)
            foreach (var b in _thumb)
                Unbend(b);
        _curlState.Clear();
    }

    private void Unbend(Transform bone)
    {
        try
        {
            if (bone != null && _curlState.TryGetValue(bone.Pointer, out var s) && Quaternion.Angle(bone.localRotation, s.setRot) < 0.01f)
                bone.localRotation = s.baseRot;
        }
        catch { }
    }

    // Turns a joint by the given angle from its pose this frame. If the animation didn't set the joint again since
    // last frame, it starts from the pose before our turn (so the bend doesn't add up frame after frame)
    private void Bend(Transform bone, Vector3 worldAxis, float degrees)
    {
        if (bone == null)
            return;
        var key = bone.Pointer;
        var now = bone.localRotation;
        if (_curlState.TryGetValue(key, out var s) && Quaternion.Angle(now, s.setRot) < 0.01f)
            bone.localRotation = s.baseRot;
        var baseRot = bone.localRotation;
        bone.rotation = Quaternion.AngleAxis(degrees, worldAxis) * bone.rotation;
        _curlState[key] = (baseRot, bone.localRotation);
    }

    // The finger bones of the drawn left hand (found once per hand)
    private Transform _boneHand, _boneIndex, _bonePinky, _boneThumb;
    private bool _bonesLogged;

    private Vector3 GripPoint(Transform hand, out bool fromBones)
    {
        fromBones = false;
        if (_boneHand != hand)
        {
            _boneHand = hand;
            _boneIndex = _bonePinky = _boneThumb = null;
            Transform middle = null, ring = null, thumb1 = null;
            foreach (var t in hand.GetComponentsInChildren<Transform>(true))
            {
                var n = t.name.ToLowerInvariant();
                if (!n.EndsWith("_l") && !n.Contains("left"))
                    continue;
                if (n.Contains("index") && n.Contains("01") && _boneIndex == null) _boneIndex = t;
                else if (n.Contains("pinky") && n.Contains("01") && _bonePinky == null) _bonePinky = t;
                else if (n.Contains("ring") && n.Contains("01") && ring == null) ring = t;
                else if (n.Contains("middle") && n.Contains("01") && middle == null) middle = t;
                else if (n.Contains("thumb") && n.Contains("02") && _boneThumb == null) _boneThumb = t;
                else if (n.Contains("thumb") && n.Contains("01") && thumb1 == null) thumb1 = t;
            }
            _bonePinky ??= ring ?? middle;
            _boneThumb ??= thumb1;
            if (!_bonesLogged && _logs++ < 200)
            {
                _bonesLogged = true;
                Plugin.Logger.LogInfo($"[Reload] Hand bones for the magazine: index '{_boneIndex?.name}', little '{_bonePinky?.name}', thumb '{_boneThumb?.name}'");
            }
        }
        if (_boneIndex == null || _bonePinky == null || _boneThumb == null)
            return hand.position;
        // The middle of the palm (wrist and the knuckles), then half way to the thumb: where a held thing sits
        var palm = (hand.position + _boneIndex.position + _bonePinky.position) / 3f;
        fromBones = true;
        return Vector3.Lerp(palm, _boneThumb.position, 0.5f);
    }

    // Puts the held magazine at pos (its pivot, or with middle = true its middle there)
    // follow: while the magazine is in or on the gun's well it hangs from the gun itself, so it moves with the gun as
    // it's DRAWN (16:52 footage: going in, the magazine stood beside the rifle on the left — it was placed where the
    // game holds the gun, and the mod moves the drawn gun from there afterwards). In the hand: back under our own root
    private void SetHeld(Vector3 pos, Quaternion rot, bool middle = false, Transform follow = null)
    {
        var t = _held.transform;
        var parent = follow != null ? follow : (_root != null ? _root.transform : null);
        if (parent != null && t.parent != parent)
            t.SetParent(parent, true);
        t.rotation = rot;
        if (middle)
        {
            if (!_haveHeldMid)
            {
                t.position = Vector3.zero;
                if (Middle(_held, out var m))
                {
                    _heldMidLocal = t.InverseTransformPoint(m);
                    _haveHeldMid = true;
                }
            }
            if (_haveHeldMid)
                pos -= t.TransformVector(_heldMidLocal);
        }
        t.position = pos;
    }

    private static bool Middle(GameObject go, out Vector3 mid)
    {
        mid = default;
        var n = 0;
        var sum = Vector3.zero;
        foreach (var r in go.GetComponentsInChildren<Renderer>(false))
        {
            if (r == null || !r.enabled)
                continue;
            sum += r.bounds.center;
            n++;
        }
        if (n == 0)
            return false;
        mid = sum / n;
        return true;
    }

    // ---- Controllers and head ----

    private static float Grip()
    {
        var d = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
        return d.isValid && XRFeature.Float(d.deviceId, "Grip", out var v) ? v : 0f;
    }

    private static bool TryPose(XRNode node, out Vector3 pos, out Quaternion rot)
    {
        pos = default;
        rot = Quaternion.identity;
        var d = InputDevices.GetDeviceAtXRNode(node);
        if (!d.isValid)
            return false;
        var id = d.deviceId;
        Vector3 p;
        Quaternion r;
        if (node == XRNode.Head)
        {
            if (!XRFeature.Vector3(id, "CenterEyePosition", out p) && !XRFeature.Vector3(id, "DevicePosition", out p))
                return false;
            if (!XRFeature.Quaternion(id, "CenterEyeRotation", out r) && !XRFeature.Quaternion(id, "DeviceRotation", out r))
                return false;
        }
        else if (!XRFeature.Vector3(id, "PointerPosition", out p) || !XRFeature.Quaternion(id, "PointerRotation", out r))
        {
            if (!XRFeature.Vector3(id, "DevicePosition", out p) || !XRFeature.Quaternion(id, "DeviceRotation", out r))
                return false;
        }
        return HeadTracking.TryTrackingToWorld(p, r, out pos, out rot);
    }
}
