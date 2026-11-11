using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;

namespace OperatorVR;

/// <summary>
/// Keeps a flipped-up magnifier's picture live (2026-11-04: "the camera froze again. The magnifier, can we sort it out?
/// And keep it sorted").
///
/// The game only draws a scope's camera while it thinks you aim down that sight. In VR you look through the magnifier
/// without that, so the lens kept the last picture, frozen. The log showed it plainly: 'Scope Camera' drew 1 picture a
/// frame while aiming and 0 the moment you stopped, with the magnifier still up. Re-setting the game's own switch
/// (ScopeInputSetter.camEnabled) wasn't enough: that switch stayed on while the camera still didn't draw.
///
/// So this checks what actually happens: it counts the scope camera's renders (RenderPipelineManager), and while the
/// magnifier is up on the gun in hand and the camera hasn't drawn for a few frames, it first asks the game to run it
/// (cams on, effect on, camera on) and, if it still doesn't draw, draws it itself each frame. As soon as the game draws
/// it again (you aim), or the magnifier is flipped down, it stops. Rendering.KeepMagnifierLive.
/// </summary>
public class MagnifierLive : MonoBehaviour
{
    public MagnifierLive(IntPtr ptr) : base(ptr) { }

    // Frames without a render before stepping in: short enough not to see it stop, long enough to skip the scope's own
    // render interval
    private const int StallFrames = 3;

    private static readonly Dictionary<IntPtr, int> s_lastDrawn = new();
    private static bool s_ourRender, s_hooked;

    private readonly List<FlipSights> _flips = new();
    private float _nextFind;
    private bool _drawing;          // drawing the magnifier ourselves
    private int _nudges, _logs, _drawnByUs, _stalls;
    private float _reportAt;

    private static void Hook()
    {
        if (s_hooked)
            return;
        s_hooked = true;
        try
        {
            RenderPipelineManager.add_beginCameraRendering(
                DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ScriptableRenderContext, Camera>>(
                    new Action<ScriptableRenderContext, Camera>((_, c) =>
                    {
                        if (c != null && !s_ourRender)
                            s_lastDrawn[c.Pointer] = Time.frameCount;
                    })));
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Magnifier] counting renders: {e.GetType().Name}: {e.Message}");
        }
    }

    private void LateUpdate()
    {
        using var __t = ModTime.Measure("MagnifierLive");
        if (!OpenXRBootstrap.IsRunning || !Plugin.KeepMagnifierLive.Value)
        {
            _drawing = false;
            if (_darkLenses.Count > 0)
                AllLensesBack();
            return;
        }
        try
        {
            Hook();
            if (Time.unscaledTime >= _nextFind)
            {
                _nextFind = Time.unscaledTime + 1f;
                FindMagnifiers();
            }
            var any = false;
            foreach (var f in _flips)
                if (f != null && f.isFlipped && f.gameObject.activeInHierarchy)
                {
                    any = true;
                    Keep(f);
                }
            if (!any && _drawing)
                Stop("magnifier flipped down or put away");
            foreach (var sc in _gunScopes)
                if (sc != null)
                    KeepScope(sc);
            foreach (var r in _realScopes)
                if (r != null)
                    KeepRealScope(r);
            Report();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Magnifier] {e.GetType().Name}: {e.Message}");
            _nextFind = Time.unscaledTime + 5f;
        }
    }

    private void FindMagnifiers()
    {
        _flips.Clear();
        // Under the player only (was a whole-level search every second)
        var me = LocalPlayer.Get();
        if (me != null)
            foreach (var f in me.GetComponentsInChildren<FlipSights>(false))
                if (f != null)
                    _flips.Add(f);
        FindGunScopes();
    }

    // ---- Fixed scopes (ACOG, Elcan...) on the gun in hand ----
    // 2026-11-05: "the ACOG is all right aiming close to my face, but aimed down, away from my face, it was a still
    // picture". Same cause as the magnifier: the game draws a scope's camera only while you're aimed in ('Scope Camera'
    // +0 renders in the 10 s windows between aims). In VR you see the lens from the hip too, so while it isn't aimed
    // the picture is drawn here, every Rendering.ScopeAsideFrames frames to keep the cost down (each draw is a
    // whole extra view of the scene)
    private readonly List<UltimateScopes.ScopeController> _gunScopes = new();
    private readonly Dictionary<IntPtr, int> _scopeOurFrame = new();
    private readonly HashSet<IntPtr> _scopeDrawing = new();
    private PlayerNetworking _me;

    // Lenses blacked out while not aimed, with their own materials, so they can be put back (aimed in, weapon changed,
    // VR stopped). Switching the lens off showed the world through the empty scope tube instead of black ("I don't
    // think it went dark"); the user then asked for it black when down (2026-11-05): the lens gets a plain black
    // material instead, so it still costs nothing
    private readonly Dictionary<IntPtr, (Renderer lens, Material[] own)> _darkLenses = new();
    private Material _black;
    private bool _blackLogged;

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private Material Black(UltimateScopes.ScopeController sc) => Black(sc.transform, sc.LensRenderer);

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private Material Black(Transform scope, Renderer lensRenderer)
    {
        if (_black != null)
            return _black;
        // HDRP's unlit shader if the game shipped it; otherwise a copy of one of the scope's own (lit) materials with
        // its colour black and its texture removed
        var unlit = Shader.Find("HDRP/Unlit");
        if (unlit != null)
        {
            _black = new Material(unlit) { name = "OperatorVR black lens" };
            _black.SetColor("_UnlitColor", Color.black);
        }
        else
        {
            var root = scope.parent != null ? scope.parent : scope;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r.Pointer == lensRenderer?.Pointer || r.sharedMaterial == null || !r.sharedMaterial.HasProperty("_BaseColor"))
                    continue;
                _black = new Material(r.sharedMaterial) { name = "OperatorVR black lens" };
                _black.SetColor("_BaseColor", Color.black);
                if (_black.HasProperty("_BaseColorMap")) _black.SetTexture("_BaseColorMap", null);
                if (_black.HasProperty("_Smoothness")) _black.SetFloat("_Smoothness", 0.3f);
                if (_black.HasProperty("_EmissiveColor")) _black.SetColor("_EmissiveColor", Color.black);
                break;
            }
        }
        if (!_blackLogged)
        {
            _blackLogged = true;
            Plugin.Logger.LogInfo(_black == null ? "[Scope] No material found to black out the lens: switching it off instead"
                : $"[Scope] Lens black-out material made from {(unlit != null ? "HDRP/Unlit" : $"shader '{_black.shader.name}'")}");
        }
        return _black;
    }

    /// <summary>Shows the scope's picture, or blacks the lens out; true if that changed anything.</summary>
    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private bool LensOn(UltimateScopes.ScopeController sc, bool on) => LensOn(sc.LensRenderer, sc.transform, on);

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private bool LensOn(Renderer lens, Transform scope, bool on)
    {
        if (lens == null)
            return false;
        var dark = _darkLenses.TryGetValue(lens.Pointer, out var entry);
        if (!on && dark && _black != null && lens.sharedMaterial != null && lens.sharedMaterial.Pointer != _black.Pointer)
        {
            // The game put its own material back while we had it black: black again (its own is kept from before)
            var again = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Material>(entry.own.Length);
            for (var i = 0; i < entry.own.Length; i++)
                again[i] = _black;
            lens.sharedMaterials = again;
            return false;
        }
        if (on == !dark)
            return false;
        if (on)
        {
            Restore(entry);
            _darkLenses.Remove(lens.Pointer);
            return true;
        }
        var own = lens.sharedMaterials;
        var black = Black(scope, lens);
        if (black == null)
            lens.forceRenderingOff = true;
        else
        {
            var mats = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Material>(own.Length);
            for (var i = 0; i < own.Length; i++)
                mats[i] = black;
            lens.sharedMaterials = mats;
        }
        var copy = new Material[own.Length];
        for (var i = 0; i < own.Length; i++)
            copy[i] = own[i];
        _darkLenses[lens.Pointer] = (lens, copy);
        return true;
    }

    private static void Restore((Renderer lens, Material[] own) entry)
    {
        if (entry.lens == null)
            return;
        entry.lens.forceRenderingOff = false;
        var mats = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Material>(entry.own.Length);
        for (var i = 0; i < entry.own.Length; i++)
            mats[i] = entry.own[i];
        entry.lens.sharedMaterials = mats;
    }

    private void AllLensesBack()
    {
        foreach (var e in _darkLenses.Values)
            try { Restore(e); } catch { }
        _darkLenses.Clear();
    }

    private void FindGunScopes()
    {
        _gunScopes.Clear();
        _realScopes.Clear();
        try { FindGunScopesInHand(); }
        finally
        {
            // A scope left dark on a gun that was put away comes back on
            var keep = new HashSet<IntPtr>();
            foreach (var sc in _gunScopes)
                if (sc != null && sc.LensRenderer != null)
                    keep.Add(sc.LensRenderer.Pointer);
            foreach (var r in _realScopes)
            {
                Renderer mr = null;
                try { mr = r != null ? r._mr : null; } catch { }
                if (mr != null)
                    keep.Add(mr.Pointer);
            }
            // ...and the magnifiers on the player, whose lens is blacked out by Keep while not aimed
            foreach (var f in _flips)
            {
                var msc = f != null ? f.GetComponentInChildren<UltimateScopes.ScopeController>(true) : null;
                if (msc != null && msc.LensRenderer != null)
                    keep.Add(msc.LensRenderer.Pointer);
            }
            var back = new List<IntPtr>();
            foreach (var kv in _darkLenses)
                if (!keep.Contains(kv.Key))
                    back.Add(kv.Key);
            foreach (var p in back)
            {
                try { Restore(_darkLenses[p]); } catch { }
                _darkLenses.Remove(p);
            }
        }
    }

    private void FindGunScopesInHand()
    {
        _me = LocalPlayer.Get();
        var gun = _me != null ? _me.activeWeapon : null;
        if (gun == null)
            return;
        foreach (var sc in gun.GetComponentsInChildren<UltimateScopes.ScopeController>(false))
            if (sc != null && sc.GetComponentInParent<FlipSights>() == null)   // magnifiers are handled above
                _gunScopes.Add(sc);
        foreach (var r in gun.GetComponentsInChildren<RealisticScopeEffectV2>(false))
            if (r != null && r.GetComponentInParent<FlipSights>() == null &&
                r.GetComponentInChildren<UltimateScopes.ScopeController>(true) == null &&
                r.GetComponentInParent<UltimateScopes.ScopeController>() == null)   // those are handled as the ACOG kind
                _realScopes.Add(r);
    }

    // ---- The game's other scope kind (RealisticScopeEffectV2) ----
    // "Shall we make it for all scopes that until you put it to your eye they're all black?" (2026-11-06). The ACOG
    // kind (UltimateScopes) and the flip-up magnifier already were; scopes of this kind draw onto their lens (_mr) the
    // same way. Not aimed (the game's isADS), the lens is black; aimed, the game's own picture. Rendering.ScopeAsideFrames 0
    private readonly List<RealisticScopeEffectV2> _realScopes = new();

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private void KeepRealScope(RealisticScopeEffectV2 r)
    {
        Renderer lens = null;
        try { lens = r._mr; } catch { }
        if (lens == null || !r.gameObject.activeInHierarchy)
            return;
        if (Plugin.ScopeAsideFrames.Value > 0)
        {
            LensOn(lens, r.transform, true);
            return;
        }
        var ads = false;
        try { ads = _me != null && _me.isADS; } catch { }
        if (LensOn(lens, r.transform, ads) && !ads && _logs++ < 40)
            Plugin.Logger.LogInfo($"[Scope] '{r.name}' not aimed: lens dark until you aim in");
    }

    // Settled: the scope's camera turning slower than SettleSpeed degrees a second for SettleTime. Once shown it stays
    // shown for the rest of that aim (normal aiming about doesn't blank it), and it's shown after SettleGiveUp anyway
    // (13:50 play: the turning test said "settled" 0.06 s into every aim, yet the slice was still there: the camera
    // isn't turning in the world. It's a "Dual Render" scope, which may move or re-aim its camera inside the scope as
    // the gun comes to the eye. So now the camera must also be still relative to the scope itself, and the first 0.8 s
    // of the first aims are logged: where the camera sits in the scope, which way it looks, its field of view)
    private const float SettleSpeed = 20f, SettleMove = 0.03f, SettleTime = 0.08f, SettleGiveUp = 0.9f;
    private readonly Dictionary<IntPtr, (int frame, float start, Vector3 fwd, Vector3 lpos, Vector3 lfwd, float calm, bool shown, float nextLog)> _settle = new();
    private int _settleLogs;

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private bool Settled(UltimateScopes.ScopeController sc, Camera cam)
    {
        var key = cam.Pointer;
        var fwd = cam.transform.forward;
        var st = sc.transform;
        var lpos = st.InverseTransformPoint(cam.transform.position);
        var lfwd = st.InverseTransformDirection(fwd);
        var fresh = !_settle.TryGetValue(key, out var s) || Time.frameCount - s.frame > StallFrames + 2;
        if (fresh)
        {
            s = (Time.frameCount, Time.unscaledTime, fwd, lpos, lfwd, 0f, false, Time.unscaledTime);   // a new aim
            _settleLogs++;
        }
        var since = Time.unscaledTime - s.start;
        if (_settleLogs <= 6 && since <= 0.8f && Time.unscaledTime >= s.nextLog)
        {
            s.nextLog = Time.unscaledTime + 0.1f;
            Plugin.Logger.LogInfo($"[Scope] aim {_settleLogs} at {since:0.00} s: camera in the scope at ({lpos.x * 100f:0.0}, {lpos.y * 100f:0.0}, {lpos.z * 100f:0.0}) cm looking ({lfwd.x:0.000}, {lfwd.y:0.000}, {lfwd.z:0.000}), fov {cam.fieldOfView:0.0}, near {cam.nearClipPlane:0.000}, lens shown {s.shown}");
        }
        if (!s.shown)
        {
            var dt = Mathf.Max(1e-4f, Time.unscaledDeltaTime);
            var speed = Mathf.Max(Vector3.Angle(s.fwd, fwd), Vector3.Angle(s.lfwd, lfwd)) / dt;
            var move = Vector3.Distance(s.lpos, lpos) * st.lossyScale.x / dt;
            s.calm = speed < SettleSpeed && move < SettleMove ? s.calm + dt : 0f;
            var aimedFor = Time.unscaledTime - s.start;
            if (s.calm >= SettleTime || aimedFor >= SettleGiveUp)
            {
                s.shown = true;
                if (_logs++ < 60)
                    Plugin.Logger.LogInfo($"[Scope] '{cam.name}' picture shown {aimedFor:0.00} s into the aim ({(aimedFor >= SettleGiveUp ? "still turning, shown anyway" : "settled")})");
            }
        }
        s.frame = Time.frameCount;
        s.fwd = fwd;
        s.lpos = lpos;
        s.lfwd = lfwd;
        _settle[key] = s;
        return s.shown;
    }

    // The pale slice in the scope (15:26 footage: a pale band down the right of the Elcan's picture for the whole time
    // aimed, not just at the start). Its camera sits inside the scope 2.6 cm off its middle (the "Dual Render"
    // parallax, log) and draws everything from 1 cm in front of it: the scope's own insides, its front lens and the
    // gun's parts can end up in its picture. A real scope shows nothing that close, so with the Touch controllers the
    // scope camera now starts drawing 30 cm out. (PS5 pad: as the game has it)
    private const float ScopeNear = 0.30f;
    private readonly Dictionary<IntPtr, (Camera cam, float near)> _nearWas = new();

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private void NearClip(Camera cam)
    {
        var key = cam.Pointer;
        if (Plugin.TouchControls.Value)
        {
            if (cam.nearClipPlane < ScopeNear - 0.001f)
            {
                if (!_nearWas.ContainsKey(key))
                {
                    _nearWas[key] = (cam, cam.nearClipPlane);
                    if (_logs++ < 60)
                        Plugin.Logger.LogInfo($"[Scope] '{cam.name}': draws from {ScopeNear * 100f:0} cm out (was {cam.nearClipPlane * 100f:0.#} cm), so nothing of the scope or gun gets into its picture");
                }
                cam.nearClipPlane = ScopeNear;
            }
        }
        else if (_nearWas.TryGetValue(key, out var was))
        {
            try { cam.nearClipPlane = was.near; } catch { }
            _nearWas.Remove(key);
        }
    }

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private void KeepScope(UltimateScopes.ScopeController sc)
    {
        var cam = sc.DualRenderCamera;
        if (cam == null || cam.targetTexture == null || !sc.gameObject.activeInHierarchy)
            return;
        var key = cam.Pointer;
        s_lastDrawn.TryGetValue(key, out var last);
        if (Time.frameCount - last <= StallFrames)
        {
            // The game draws it (you're aimed in). (12:56-13:08 the lens was kept dark until the scope "lined up with
            // the eye": the measured angle was 28-38° every aim, so it stayed black 1.5 s each time — the user: "it's
            // all a mess". Taken out.) The wall slice sliding across the lens at the start of each aim (still there
            // 13:31) is the scope's camera still swinging in with the gun. Now the lens stays dark only while that
            // camera is turning quickly at the start of an aim, and shows as soon as it has settled
            NearClip(cam);
            // (Touch controllers only: with the PS5 pad the lens shows as it always did)
            if (Plugin.TouchControls.Value && !Settled(sc, cam))
            {
                LensOn(sc, false);
                return;
            }
            LensOn(sc, true);
            if (_scopeDrawing.Remove(key) && _logs++ < 40)
                Plugin.Logger.LogInfo($"[Scope] '{sc.name}': aimed in, the game draws it again");
            return;
        }
        // "When the ACOG is at your side, make it black; only when you lift it to your head can you see through it"
        // (2026-11-05, 120 Hz test: drawing it at the side cost 1.3-2.2 ms a frame). ScopeAsideFrames 0: the lens
        // isn't drawn at all while you're not aimed in, so it shows dark, and nothing extra is drawn
        // Aimed in but the camera isn't drawing yet (the game still on the gun's other sight until ScopeGuard switches
        // it): the lens stays black rather than showing its last, frozen picture ("the scope froze for a second",
        // 2026-11-06). The black lens is still drawn (only its material differs), so a scope that draws only while its
        // lens can be seen still does
        var ads = false;
        try { ads = _me != null && _me.isADS; } catch { }
        if (ads)
        {
            LensOn(sc, false);
            return;
        }
        if (Plugin.ScopeAsideFrames.Value <= 0)
        {
            if (LensOn(sc, false) && _logs++ < 40)
                Plugin.Logger.LogInfo($"[Scope] '{sc.name}' not aimed: lens dark until you aim in");
            _scopeDrawing.Remove(key);
            return;
        }
        LensOn(sc, true);
        if (_scopeDrawing.Add(key) && _logs++ < 40)
            Plugin.Logger.LogInfo($"[Scope] '{sc.name}' not aimed: its picture stopped, drawing it every " +
                                  $"{Plugin.ScopeAsideFrames.Value} frame(s) (camera {(cam.enabled ? "on" : "OFF")}" +
                                  $"{(cam.gameObject.activeInHierarchy ? "" : ", inactive")}, into {cam.targetTexture.width}x{cam.targetTexture.height})");
        if (!cam.gameObject.activeInHierarchy)
            return;
        _scopeOurFrame.TryGetValue(key, out var ours);
        if (Time.frameCount - ours < Math.Max(1, Plugin.ScopeAsideFrames.Value))
            return;
        _scopeOurFrame[key] = Time.frameCount;
        s_ourRender = true;
        try { cam.Render(); }
        finally { s_ourRender = false; }
        _drawnByUs++;
    }

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private void Keep(FlipSights f)
    {
        var sc = f.GetComponentInChildren<UltimateScopes.ScopeController>(true);
        var cam = sc != null ? sc.DualRenderCamera : null;
        if (cam == null || cam.targetTexture == null)
            return;

        s_lastDrawn.TryGetValue(cam.Pointer, out var last);
        var since = Time.frameCount - last;

        // "Make the magnifier work like the ACOG" (2026-11-06, Afghanistan V2 Day): flipped up but not aimed, the game
        // stops drawing it and drawing it ourselves gave a soft, low-resolution picture (the game's 512x512 not-aimed
        // target) and cost ~6 fps. With ScopeAsideFrames 0 its lens is black until you aim; aimed in, the game draws
        // its own full-resolution picture into the lens as usual. Nothing is re-pointed (that left the lens on the
        // low-resolution picture after aiming)
        if (Plugin.ScopeAsideFrames.Value <= 0)
        {
            var ads = false;
            try { ads = _me != null && _me.isADS; } catch { }
            if (since <= StallFrames)
            {
                LensOn(sc, true);
                if (_drawing)
                    Stop("aimed in");
                return;
            }
            if (LensOn(sc, false) && _logs++ < 40)
                Plugin.Logger.LogInfo($"[Magnifier] '{f.name}' up but not aimed: lens dark until you aim in");
            _drawing = false;
            return;
        }

        Watch(sc, cam);
        if (since <= StallFrames)
        {
            if (_drawing)
                Stop("the game draws it again");
            return;
        }

        if (!_drawing)
        {
            // Ask the game first: its own switches, the scope effect and the camera itself
            _stalls++;
            var input = f.GetComponentInChildren<ScopeInputSetter>(true);
            var before = $"cams {(input == null ? "-" : input.camEnabled ? "on" : "OFF")}, effect {(sc.effectActive ? "on" : "OFF")}, " +
                         $"camera {(cam.enabled ? "on" : "OFF")}{(cam.gameObject.activeInHierarchy ? "" : " (inactive)")}, " +
                         $"interval {sc.RenderInterval}, skip {sc.RenderSkip}, into {cam.targetTexture.width}x{cam.targetTexture.height}";
            if (input != null && !input.camEnabled)
                input.SetCamsEnabled();
            if (!sc.effectActive)
                sc.EnableEffect();
            if (_nudges++ % 30 == 0 && _logs++ < 40)
                Plugin.Logger.LogInfo($"[Magnifier] '{f.name}' is up but its picture stopped ({since} frames, {before}): drawing it myself");
            _drawing = true;
        }

        // Draw it ourselves, once a frame (the same rate the game draws it while aiming)
        if (!cam.gameObject.activeInHierarchy)
            return;
        s_ourRender = true;
        try { cam.Render(); }
        finally { s_ourRender = false; }
        _drawnByUs++;
    }

    // 2026-11-05, 90 Hz run: "as I turned my head it was still the same picture; moving more to the right it came
    // unstuck", while the log showed 'Scope Camera' drawing every frame. So the picture was made but either the camera
    // wasn't turning with the gun, or the lens showed another texture than the one drawn into. Checked every 10 frames
    // while the magnifier is up: the lens is pointed back at the camera's texture if it isn't, and moments where the
    // view turns but the scope camera doesn't are logged
    private int _watchFrame, _watchLogs, _texFixes;
    private Quaternion _lastCamRot, _lastViewRot;
    private bool _haveWatch;

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private void Watch(UltimateScopes.ScopeController sc, Camera cam)
    {
        if (Time.frameCount - _watchFrame < 10)
            return;
        _watchFrame = Time.frameCount;

        // 1. The lens shows the texture the camera draws into
        var target = cam.targetTexture;
        if (sc.LensRenderer != null)
            foreach (var m in sc.LensRenderer.sharedMaterials)
                PointLensAt(m, target, sc.name);
        PointLensAt(sc._scopeMaterial, target, sc.name);

        // 2. The scope camera turns when the view (head + gun) turns
        var view = Camera.main;
        if (view == null)
            return;
        var camRot = cam.transform.rotation;
        var viewRot = view.transform.rotation;
        if (_haveWatch)
        {
            var camTurn = Quaternion.Angle(_lastCamRot, camRot);
            var viewTurn = Quaternion.Angle(_lastViewRot, viewRot);
            if (viewTurn > 3f && camTurn < 0.3f && _watchLogs++ < 30)
            {
                var gun = sc.transform;
                Plugin.Logger.LogInfo($"[Magnifier] view turned {viewTurn:0.0}° in 10 frames but the scope camera only {camTurn:0.00}° " +
                                      $"(camera fwd {Fmt(cam.transform.forward)}, scope fwd {Fmt(gun.forward)}, view fwd {Fmt(view.transform.forward)}, " +
                                      $"camera under scope: {cam.transform.IsChildOf(gun)})");
            }
        }
        _lastCamRot = camRot;
        _lastViewRot = viewRot;
        _haveWatch = true;
    }

    private static string Fmt(Vector3 v) => $"({v.x:0.00},{v.y:0.00},{v.z:0.00})";

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private void PointLensAt(Material m, RenderTexture target, string scope)
    {
        if (m == null || target == null)
            return;
        foreach (var prop in m.GetTexturePropertyNames())
        {
            var tex = m.GetTexture(prop);
            if (tex == null || tex.Pointer == target.Pointer)
                continue;
            var rt = tex.TryCast<RenderTexture>();
            // Only another render texture of the scope's own kind (square, 2D): never the lens's regular textures
            if (rt == null || rt.width != rt.height || rt.dimension != UnityEngine.Rendering.TextureDimension.Tex2D)
                continue;
            m.SetTexture(prop, target);
            if (_texFixes++ < 20)
                Plugin.Logger.LogInfo($"[Magnifier] '{scope}' lens material '{m.name}' showed '{rt.name}' {rt.width}x{rt.height} in {prop}, " +
                                      $"not the {target.width}x{target.height} the camera draws into: pointed it at the camera's");
        }
    }

    private void Stop(string why)
    {
        _drawing = false;
        if (_logs++ < 40)
            Plugin.Logger.LogInfo($"[Magnifier] Stopped drawing the magnifier myself: {why}");
    }

    // Every 30 s while it had to step in: how often, so the log shows it's working
    private void Report()
    {
        if (Time.unscaledTime < _reportAt)
            return;
        _reportAt = Time.unscaledTime + 30f;
        if (_drawnByUs == 0 && _stalls == 0)
            return;
        Plugin.Logger.LogInfo($"[Magnifier] Last 30 s: picture stopped {_stalls}x, drew it myself {_drawnByUs} frames");
        _drawnByUs = _stalls = 0;
    }
}
