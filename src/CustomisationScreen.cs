using System;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.UI;

namespace OperatorVR;

/// <summary>
/// The character customisation page as a flat screen in VR. In flat mode the game points its camera at your
/// character from the side (narrow field of view) and shows the clothing panels on the right (an overlay canvas,
/// 'CharacterCustomisation V2/Canvas', which VRMenus already floats in front of you). In VR the camera is your head,
/// so the character view was lost. While that page is open, a camera of ours copies the game's camera (its own pose
/// and field of view, without the head) into a picture, and the picture is put behind the panels: the floating page
/// then looks like the flat screen. The pointer works on the panels as before. Menus.CustomisationScreen.
/// </summary>
public class CustomisationScreen : MonoBehaviour
{
    public CustomisationScreen(IntPtr ptr) : base(ptr) { }

    /// <summary>The page is open: show the whole character (BodyHider and the chest/arm shrinking pause).</summary>
    internal static bool Open { get; private set; }

    private Canvas _canvas;          // the customisation page's canvas, while open
    private float _nextFind;
    private Camera _flatCam;
    private RenderTexture _texture;
    private RawImage _view;
    private bool _logged, _copiedHdData;

    /// <summary>The flat view's picture while the page is open (EyeCapture saves it with F3).</summary>
    internal static RenderTexture Texture => s_instance != null && s_instance._flatCam != null && s_instance._flatCam.enabled ? s_instance._texture : null;
    private static CustomisationScreen s_instance;

    private void Start()
    {
        s_instance = this;
        // After HeadTracking's own onBeforeRender (added earlier), so the game's pose is known for this frame
        Application.add_onBeforeRender((UnityEngine.Events.UnityAction)new Action(BeforeRender));
    }

    private void Update()
    {
        using var __t = ModTime.Measure("CustomisationScreen");
        var on = OpenXRBootstrap.IsRunning && Plugin.CustomisationScreen.Value;
        if (on && Time.unscaledTime >= _nextFind)
        {
            _nextFind = Time.unscaledTime + 1f;
            try { _canvas = FindPage(); }
            catch { _canvas = null; }
        }
        var open = on && _canvas != null && _canvas.isActiveAndEnabled;
        Open = open;
        try
        {
            if (open)
                Show();
            else
                Hide();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Customise] {e.GetType().Name}: {e.Message}");
            _nextFind = Time.unscaledTime + 10f;
            _canvas = null;
        }
    }

    // The page's canvas: 'Canvas' under 'CharacterCustomisation V2' (from the flat screenshot's log), open when active
    private static Canvas FindPage()
    {
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<Canvas>(), FindObjectsSortMode.None))
        {
            var c = o.TryCast<Canvas>();
            if (c == null || !c.isRootCanvas || !c.isActiveAndEnabled)
                continue;
            for (var p = c.transform.parent; p != null; p = p.parent)
                if (p.name.StartsWith("CharacterCustomisation"))
                    return c;
        }
        return null;
    }

    // The picture is the screen's size, so a point the flat camera projects lands where it would on the flat screen:
    // ODG's cabinet panels (Loadout Presets etc.) measure the character with it (see AfterCabinetCamera)
    private void EnsureCamera()
    {
        var w = Mathf.Max(640, Screen.width);
        var h = Mathf.Max(360, Screen.height);
        if (_texture != null && (_texture.width != w || _texture.height != h))
        {
            if (_flatCam != null)
                _flatCam.targetTexture = null;
            if (_view != null)
                _view.texture = null;
            _texture.Release();
            UnityEngine.Object.Destroy(_texture);
            _texture = null;
        }
        if (_texture == null)
        {
            _texture = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { name = "OperatorVR Customise View" };
            _texture.Create();
            if (_flatCam != null)
                _flatCam.targetTexture = _texture;
            if (_view != null)
                _view.texture = _texture;
        }
        if (_flatCam == null)
        {
            var go = new GameObject("OperatorVR Customise Camera");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _flatCam = go.AddComponent<Camera>();
            _flatCam.stereoTargetEye = StereoTargetEyeMask.None;   // a plain camera, not a VR one
            _flatCam.targetTexture = _texture;
            _flatCam.enabled = false;
            _copiedHdData = false;
        }
    }

    private void Show()
    {
        EnsureCamera();
        // First try drew plain grey: with HDRP's default camera settings (exposure, volumes) the dark customisation
        // room washed out. Take the game camera's own HDRP settings
        if (!_copiedHdData && HeadTracking.TryGetGameCameraPose(out var gameCam, out _, out _))
        {
            var src = gameCam.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData>();
            if (src != null)
            {
                var dst = _flatCam.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData>() ??
                          _flatCam.gameObject.AddComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData>();
                src.CopyTo(dst);
                _copiedHdData = true;
                Plugin.Logger.LogInfo($"[Customise] Took the HDRP camera settings of '{gameCam.name}'");
            }
        }
        if (_view == null || _view.transform.parent == null || _view.transform.parent.Pointer != _canvas.transform.Pointer)
        {
            if (_view != null)
                UnityEngine.Object.Destroy(_view.gameObject);
            var go = new GameObject("OperatorVR Customise View");
            go.layer = _canvas.gameObject.layer;
            go.transform.SetParent(_canvas.transform, false);
            go.transform.SetAsFirstSibling();                       // behind the panels
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            _view = go.AddComponent<RawImage>();
            _view.texture = _texture;
            _view.raycastTarget = false;                            // clicks and drags go to the game as before
        }
        if (!_flatCam.enabled)
            _flatCam.enabled = true;
        if (!_view.gameObject.activeSelf)
            _view.gameObject.SetActive(true);
        // 3D character on the left: the flat picture then only fills the right half (behind the panels)
        var model3d = Plugin.Character3D.Value;
        var rtView = _view.rectTransform;
        var wantMinX = model3d ? 0.5f : 0f;
        if (Mathf.Abs(rtView.anchorMin.x - wantMinX) > 0.001f)
        {
            rtView.anchorMin = new Vector2(wantMinX, 0f);
            rtView.offsetMin = rtView.offsetMax = Vector2.zero;
            _view.uvRect = new Rect(wantMinX, 0f, 1f - wantMinX, 1f);
        }
        if (model3d)
            try { UpdateModel(); }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[Customise] 3D character: {e.GetType().Name}: {e.Message}");
                DropModel();
            }
        else
            DropModel();
        if (!_logged)
        {
            _logged = true;
            Plugin.Logger.LogInfo($"[Customise] Customisation page open: flat view of the game camera behind '{LaptopDiagnostics.Path(_canvas.transform)}'");
        }
    }

    private void Hide()
    {
        if (_flatCam != null && _flatCam.enabled)
        {
            _flatCam.enabled = false;
            Plugin.Logger.LogInfo("[Customise] Customisation page closed");
            _logged = false;
        }
        if (_view != null && _view.gameObject.activeSelf)
            _view.gameObject.SetActive(false);
        DropModel();
    }

    // ---- The character in 3D (Menus.Character3D) ----
    // "Can the character on the left be 3D instead of a 2D picture, but keep the right side 2D?" (2026-11-05). A copy
    // of your character (its clothing and gear as they are now, skinned parts frozen in their current pose) stands in
    // front of the left half of the page, placed, sized and turned as the flat view shows him: where he is in the game
    // camera's picture becomes where he is on the page, and his turn relative to that camera becomes his turn relative
    // to the page, so dragging him round in the game turns the copy too. Made again when his parts change (clothes
    // swapped). Hidden while our flat camera draws, so it doesn't film the copy
    private GameObject _model;
    private Transform _modelInner;
    private readonly System.Collections.Generic.List<Mesh> _bakedMeshes = new();
    private long _modelSig;
    private float _nextModelCheck;
    private Transform _character;
    private Bounds _charBounds;   // in the character's own space
    private bool _modelHooked;

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private Transform FindCharacter()
    {
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<PlayerNetworking>(), FindObjectsSortMode.None))
        {
            var p = o.TryCast<PlayerNetworking>();
            if (p == null || !p.gameObject.name.Contains("MyPlayer"))
                continue;
            foreach (var t in p.GetComponentsInChildren<Transform>(true))
                if (t != null && t.name == "TPCharacter")
                    return t;
            return p.transform;
        }
        return null;
    }

    // Only what the game's own customisation view shows: renderers on layers its camera draws (the first try copied
    // a gun that the flat view never shows, and it went through him). Ones we hid (the real character, below) count
    private bool Wanted(Renderer r) =>
        r != null && r.enabled && (!r.forceRenderingOff || _realHidden.ContainsKey(r.Pointer)) && r.gameObject.activeInHierarchy &&
        r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly &&    // (particles have no mesh: skipped when built)
        (_flatCam == null || (_flatCam.cullingMask & (1 << r.gameObject.layer)) != 0);

    // The real character stays where the game put him, further back in the room, and showed behind the copy as a
    // second, smaller figure ("the old model is still behind", 2026-11-05): hidden from view while the copy is up
    private readonly System.Collections.Generic.Dictionary<IntPtr, Renderer> _realHidden = new();

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private void HideReal(Transform ch)
    {
        foreach (var r in ch.GetComponentsInChildren<Renderer>(false))
            if (r != null && !r.forceRenderingOff)
            {
                r.forceRenderingOff = true;
                _realHidden[r.Pointer] = r;
            }
    }

    private void ShowReal()
    {
        foreach (var r in _realHidden.Values)
            try { if (r != null) r.forceRenderingOff = false; } catch { }
        _realHidden.Clear();
    }

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private long Signature(Transform ch)
    {
        long sig = 17;
        foreach (var r in ch.GetComponentsInChildren<Renderer>(false))
        {
            if (!Wanted(r))
                continue;
            var smr = r.TryCast<SkinnedMeshRenderer>();
            var mesh = smr != null ? smr.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
            sig = sig * 31 + r.Pointer.ToInt64() + (mesh != null ? mesh.Pointer.ToInt64() : 0);
        }
        return sig;
    }

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private void UpdateModel()
    {
        if (!_modelHooked)
        {
            _modelHooked = true;
            UnityEngine.Rendering.RenderPipelineManager.add_beginCameraRendering(
                DelegateSupport.ConvertDelegate<Il2CppSystem.Action<UnityEngine.Rendering.ScriptableRenderContext, Camera>>(
                    new Action<UnityEngine.Rendering.ScriptableRenderContext, Camera>((_, c) => ShowModelFor(c))));
        }
        if (Time.unscaledTime >= _nextModelCheck)
        {
            _nextModelCheck = Time.unscaledTime + 1f;
            if (_character == null)
                _character = FindCharacter();
            if (_character == null)
                return;
            var sig = Signature(_character);
            if (_model == null || sig != _modelSig)
            {
                BuildModel(_character);
                _modelSig = sig;
            }
        }
        if (_model == null || _character == null || !UpdatePose())
            return;
        PlaceModel();
    }

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private void BuildModel(Transform ch)
    {
        DropModel();
        _model = new GameObject("OperatorVR Customise Character");
        _model.transform.SetParent(_canvas.transform, false);
        var inner = new GameObject("Model");
        inner.transform.SetParent(_model.transform, false);
        _modelInner = inner.transform;

        var toLocal = ch.worldToLocalMatrix;
        var layer = ch.gameObject.layer;
        var any = false;
        var bounds = new Bounds();
        var parts = 0;
        var skipped = new System.Collections.Generic.List<string>();
        foreach (var r in ch.GetComponentsInChildren<Renderer>(false))
        {
            if (!Wanted(r))
            {
                if (r != null && skipped.Count < 60)
                    skipped.Add($"{r.name} (layer {r.gameObject.layer}{(r.enabled ? "" : ", off")}" +
                                $"{(r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly ? ", shadows only" : "")})");
                continue;
            }
            Mesh mesh;
            Matrix4x4 m;
            var smr = r.TryCast<SkinnedMeshRenderer>();
            if (smr != null)
            {
                if (smr.sharedMesh == null)
                    continue;
                // Frozen in the pose he has now. Baked into the renderer's own scaled space and placed with its full
                // matrix, so a mirrored part keeps its negative scale and isn't drawn inside out ("the guy's hollow").
                // (Before: baked with scale but placed without it, which loses the mirror)
                mesh = new Mesh { name = smr.name + " (pose)" };
                smr.BakeMesh(mesh, true);
                _bakedMeshes.Add(mesh);
                m = toLocal * smr.transform.localToWorldMatrix;
            }
            else
            {
                var mf = r.GetComponent<MeshFilter>();
                mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null)
                    continue;
                m = toLocal * r.transform.localToWorldMatrix;
            }
            layer = r.gameObject.layer;
            var part = new GameObject(r.name);
            part.transform.SetParent(inner.transform, false);
            ModelCopy.SetFromMatrix(part.transform, m);
            part.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = part.AddComponent<MeshRenderer>();
            mr.sharedMaterials = r.sharedMaterials;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            if (IsEye(r))
                _eyes.Add(mr);
            else if (smr != null)
                SplitEyes(r, mesh, part.transform, inner.transform);
            if (smr != null && IsArms(r))
                SplitSides(r, mesh, m, mr, part.transform, inner.transform);
            if (smr != null && _matLogs < 40)
            {
                _matLogs++;
                var names = new System.Collections.Generic.List<string>();
                foreach (var mat in r.sharedMaterials)
                    names.Add(mat != null ? mat.name : "-");
                _matNames.Add($"{r.name} [{string.Join(", ", names)}]");
            }
            // Bounds in the character's space from the body's own (skinned) meshes only: with every part counted he
            // measured 2 to 4.7 times his height (stray pieces around him), and was placed and sized from that
            if (smr != null)
            {
                var e = mesh.bounds.extents;
                for (var i = 0; i < 8; i++)
                {
                    var p = m.MultiplyPoint3x4(mesh.bounds.center + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z));
                    if (any) bounds.Encapsulate(p); else { bounds = new Bounds(p, Vector3.zero); any = true; }
                }
            }
            parts++;
        }
        _charBounds = bounds;
        ModelCopy.SetLayer(_model.transform, layer);
        HideReal(ch);
        Plugin.Logger.LogInfo($"[Customise] 3D character made from '{LaptopDiagnostics.Path(ch)}': {parts} parts, " +
                              $"{bounds.size.y:0.00} m tall; eye parts {_eyes.Count}" +
                              $"{(_eyes.Count > 0 ? $" ({string.Join(", ", _eyes.ConvertAll(e => e.name))})" : "")}" +
                              $"; left out {skipped.Count}: {string.Join(", ", skipped)}");
        if (_matNames.Count > 0)
        {
            Plugin.Logger.LogInfo($"[Customise] Body parts and their materials: {string.Join("; ", _matNames)}");
            _matNames.Clear();
        }
    }

    // "When he turns around you can see his eyes through the back of his head" (2026-11-05): the eye material draws
    // over the head. The eye parts (by renderer or material name) are hidden while he faces away from you
    private readonly System.Collections.Generic.List<MeshRenderer> _eyes = new();
    private bool _eyesHidden;

    private static readonly string[] EyeWords = { "eye", "cornea", "iris", "pupil" };

    private static bool EyeName(string name)
    {
        foreach (var w in EyeWords)
            if (name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0 && name.IndexOf("eyebrow", StringComparison.OrdinalIgnoreCase) < 0 &&
                name.IndexOf("eyelash", StringComparison.OrdinalIgnoreCase) < 0)
                return true;
        return false;
    }

    private static bool IsEye(Renderer r)
    {
        if (EyeName(r.name))
            return true;
        var mats = r.sharedMaterials;
        return mats.Length == 1 && mats[0] != null && EyeName(mats[0].name);
    }

    // The eyes were part of the head's mesh (log: "eye parts 0"): the eye sections (materials named like an eye) of the
    // baked mesh are moved into a piece of their own, which can then be hidden; the head keeps the rest
    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private void SplitEyes(Renderer r, Mesh baked, Transform headPart, Transform parent)
    {
        var mats = r.sharedMaterials;
        if (mats.Length < 2 || baked.subMeshCount < 2)
            return;
        var eyeSlots = new System.Collections.Generic.List<int>();
        for (var i = 0; i < Math.Min(mats.Length, baked.subMeshCount); i++)
            if (mats[i] != null && EyeName(mats[i].name))
                eyeSlots.Add(i);
        if (eyeSlots.Count == 0 || eyeSlots.Count == baked.subMeshCount)
            return;
        var eyeMesh = UnityEngine.Object.Instantiate(baked);
        eyeMesh.name = baked.name + " (eyes)";
        _bakedMeshes.Add(eyeMesh);
        var empty = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(0);
        for (var i = 0; i < baked.subMeshCount; i++)
        {
            if (eyeSlots.Contains(i))
                baked.SetTriangles(empty, i);       // the head without its eyes
            else
                eyeMesh.SetTriangles(empty, i);     // the eyes alone
        }
        var eyes = new GameObject(r.name + " (eyes)");
        eyes.transform.SetParent(parent, false);
        eyes.transform.localPosition = headPart.localPosition;
        eyes.transform.localRotation = headPart.localRotation;
        eyes.transform.localScale = headPart.localScale;
        eyes.AddComponent<MeshFilter>().sharedMesh = eyeMesh;
        var er = eyes.AddComponent<MeshRenderer>();
        er.sharedMaterials = mats;
        er.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _eyes.Add(er);
    }

    // "Hide the arm when he's turned, so you don't see it shining through the body" (2026-11-05). The arms and gloves
    // (log: SK_Arms_Short [M_Arms], SK_Gloves2B [Gloves 2, M_Arms]) draw over the body like the eyes did, so side-on
    // the far arm showed through his chest. Each such mesh is split into a left and a right half (by which side of
    // him each triangle lies); the half on his far side is hidden while you look at him from the side
    private readonly System.Collections.Generic.List<MeshRenderer> _leftArm = new(), _rightArm = new();
    private int _farSide;   // -1 left hidden, 1 right hidden, 0 none

    private static bool IsArms(Renderer r)
    {
        if (r.name.IndexOf("Arm", StringComparison.OrdinalIgnoreCase) >= 0 || r.name.IndexOf("Glove", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        foreach (var m in r.sharedMaterials)
            if (m != null && m.name.IndexOf("Arms", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        return false;
    }

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private void SplitSides(Renderer r, Mesh baked, Matrix4x4 toChar, MeshRenderer right, Transform part, Transform parent)
    {
        var verts = baked.vertices;
        var leftMesh = UnityEngine.Object.Instantiate(baked);
        leftMesh.name = baked.name + " (left)";
        _bakedMeshes.Add(leftMesh);
        int nl = 0, nr = 0;
        for (var s = 0; s < baked.subMeshCount; s++)
        {
            var tris = baked.GetTriangles(s);
            var l = new System.Collections.Generic.List<int>();
            var rt = new System.Collections.Generic.List<int>();
            for (var i = 0; i + 2 < tris.Length; i += 3)
            {
                var c = (verts[tris[i]] + verts[tris[i + 1]] + verts[tris[i + 2]]) / 3f;
                var list = toChar.MultiplyPoint3x4(c).x < 0f ? l : rt;   // his middle: the character's origin is between his feet
                list.Add(tris[i]);
                list.Add(tris[i + 1]);
                list.Add(tris[i + 2]);
            }
            nl += l.Count / 3;
            nr += rt.Count / 3;
            leftMesh.SetTriangles(new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(l.ToArray()), s);
            baked.SetTriangles(new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(rt.ToArray()), s);
        }
        var go = new GameObject(r.name + " (left)");
        go.transform.SetParent(parent, false);
        go.transform.localPosition = part.localPosition;
        go.transform.localRotation = part.localRotation;
        go.transform.localScale = part.localScale;
        go.AddComponent<MeshFilter>().sharedMesh = leftMesh;
        var lr = go.AddComponent<MeshRenderer>();
        lr.sharedMaterials = right.sharedMaterials;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _leftArm.Add(lr);
        _rightArm.Add(right);
        if (_matLogs < 40)
            _matNames.Add($"{r.name} split: {nl} triangles left, {nr} right");
    }

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private void ArmsForView(Camera head)
    {
        if (head == null || (_leftArm.Count == 0 && _rightArm.Count == 0))
            return;
        // Where you are, seen from him: +x is his right. Clearly to one side (more than ~35° off his front/back line)
        // = the other arm is behind his body
        var dir = Quaternion.Inverse(_model.transform.rotation) * (head.transform.position - _model.transform.position);
        var side = dir.normalized.x;
        var far = side > 0.6f ? -1 : side < -0.6f ? 1 : (Mathf.Abs(side) < 0.5f ? 0 : _farSide);
        if (far == _farSide)
            return;
        _farSide = far;
        foreach (var a in _leftArm)
            if (a != null) a.forceRenderingOff = far == -1;
        foreach (var a in _rightArm)
            if (a != null) a.forceRenderingOff = far == 1;
    }

    private int _matLogs;
    private readonly System.Collections.Generic.List<string> _matNames = new();

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private void EyesForView(Camera head)
    {
        if (_eyes.Count == 0 || head == null)
            return;
        // He faces his own +z; turned more than ~95° away from you = facing away (a little give against flicker)
        var toHead = (head.transform.position - _model.transform.position).normalized;
        var facing = Vector3.Dot(_model.transform.forward, toHead);
        var hide = _eyesHidden ? facing < 0.0f : facing < -0.1f;
        if (hide == _eyesHidden)
            return;
        _eyesHidden = hide;
        foreach (var e in _eyes)
            if (e != null)
                e.forceRenderingOff = hide;
    }

    // Where and how big the character is in the flat camera's picture, mapped onto the page (canvas units)
    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private void PlaceModel()
    {
        var page = _canvas.transform;
        var rect = _canvas.GetComponent<RectTransform>().rect;
        var top = _character.TransformPoint(new Vector3(_charBounds.center.x, _charBounds.max.y, _charBounds.center.z));
        var bottom = _character.TransformPoint(new Vector3(_charBounds.center.x, _charBounds.min.y, _charBounds.center.z));
        var centre = _character.TransformPoint(_charBounds.center);
        var vt = _flatCam.WorldToViewportPoint(top);
        var vb = _flatCam.WorldToViewportPoint(bottom);
        var vc = _flatCam.WorldToViewportPoint(centre);
        if (vc.z <= 0f)
            return;
        var heightUnits = Mathf.Abs(vt.y - vb.y) * rect.height;
        var scale = _charBounds.size.y > 1e-3f ? heightUnits / _charBounds.size.y : 1f;
        _modelInner.localScale = Vector3.one * scale;
        _modelInner.localPosition = -_charBounds.center * scale;
        // His turn as the game camera sees him, as seen through the page (the page faces you like the camera's picture)
        var rel = Quaternion.Inverse(_flatCam.transform.rotation) * _character.rotation;
        _model.transform.localRotation = rel;
        // In front of the page by half his depth, so he doesn't sink into it
        var depth = Mathf.Max(_charBounds.size.x, _charBounds.size.z) * scale;
        _model.transform.localPosition = new Vector3((vc.x - 0.5f) * rect.width + rect.center.x,
                                                     (vc.y - 0.5f) * rect.height + rect.center.y, -depth * 0.6f);
        // "He was further back than the panel; I want him a bit closer" (2026-11-05): brought towards your head
        // by Menus.Character3DCloser metres
        var head = Camera.main;
        var closer = Plugin.Character3DCloser.Value;
        if (head != null && Mathf.Abs(closer) > 0.001f)
        {
            var pos = _model.transform.position;
            var toHead = head.transform.position - pos;
            if (toHead.sqrMagnitude > 1e-4f)
                _model.transform.position = pos + toHead.normalized * Mathf.Min(closer, toHead.magnitude * 0.8f);
        }
        EyesForView(head);
        ArmsForView(head);
        if (_placeLogs++ < 2)
            Plugin.Logger.LogInfo($"[Customise] 3D character placed: {_charBounds.size.y:0.00} units tall in his space, " +
                                  $"{heightUnits:0} page units tall, {(head != null ? Vector3.Distance(head.transform.position, _model.transform.position) : -1f):0.00} m from your head " +
                                  $"(page {(head != null ? Vector3.Distance(head.transform.position, _canvas.transform.position) : -1f):0.00} m)");
    }

    private int _placeLogs;

    private bool _modelHiddenForFlat;

    private void ShowModelFor(Camera c)
    {
        try
        {
            if (_model == null)
                return;
            var flat = _flatCam != null && c != null && c.Pointer == _flatCam.Pointer;
            if (flat == _modelHiddenForFlat)
                return;
            _modelHiddenForFlat = flat;
            _model.SetActive(!flat);
        }
        catch { }
    }

    private void DropModel()
    {
        ShowReal();
        if (_model != null)
            UnityEngine.Object.Destroy(_model);
        _model = null;
        _modelInner = null;
        _modelSig = 0;
        _modelHiddenForFlat = false;
        foreach (var m in _bakedMeshes)
            if (m != null)
                UnityEngine.Object.Destroy(m);
        _bakedMeshes.Clear();
        _eyes.Clear();
        _eyesHidden = false;
        _leftArm.Clear();
        _rightArm.Clear();
        _farSide = 0;
        _character = null;
    }

    // ---- ODG cabinet panels ----
    // ODG's Loadout Presets (and NewSlots / Quartermaster, which share its OdgUi.Layout) place their panels beside the
    // character by projecting it through the cabinet camera (OdgUi.Layout.CabinetCamera). In VR that camera is the
    // headset's wide view, so the character measured tiny (68x165 px) and the panel was put over it in the flat view
    // shown here (2026-11-03). While VR runs, that helper gets the flat camera instead, posed as the game's camera
    internal static void Apply(HarmonyLib.Harmony harmony)
    {
        var postfix = new HarmonyLib.HarmonyMethod(typeof(CustomisationScreen), nameof(AfterCabinetCamera));
        var patched = 0;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type t;
            try { t = asm.GetType("OdgUi.Layout", false); }
            catch { continue; }
            var m = t?.GetMethod("CabinetCamera", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (m == null || m.ReturnType != typeof(Camera))
                continue;
            try
            {
                harmony.Patch(m, postfix: postfix);
                patched++;
                Plugin.Logger.LogInfo($"[Customise] hooked OdgUi.Layout.CabinetCamera in {asm.GetName().Name} (ODG panels measure the character in the flat view)");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"[Customise] hooking OdgUi.Layout.CabinetCamera in {asm.GetName().Name} failed: {e.Message}");
            }
        }
        if (patched == 0)
            Plugin.Logger.LogInfo("[Customise] no ODG cabinet panels to hook");
    }

    private static int s_cabinetLogs;

    private static void AfterCabinetCamera(ref Camera __result)
    {
        try
        {
            if (!OpenXRBootstrap.IsRunning || !Plugin.CustomisationScreen.Value || s_instance == null)
                return;
            var flat = s_instance.PrepareFlatCamera();
            if (flat == null)
                return;
            if (s_cabinetLogs++ < 3)
                Plugin.Logger.LogInfo($"[Customise] ODG panel measuring through the flat view ({flat.pixelWidth}x{flat.pixelHeight}, fov {flat.fieldOfView:0}) instead of '{(__result != null ? __result.name : "-")}'");
            __result = flat;
        }
        catch { }
    }

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private Camera PrepareFlatCamera()
    {
        EnsureCamera();
        return UpdatePose() ? _flatCam : null;
    }

    // The game camera's field of view as it is just before drawing, which is the game's own (the customisation view's
    // narrow one). At other times in a frame it reads as the headset's (100°): ODG's panel measured the character through
    // that and found it tiny in the middle, then sat over him on the flat view (2026-11-03)
    private static float s_drawFov = -1f;

    // Each frame, just before drawing: the flat camera takes the game camera's own view
    private void BeforeRender()
    {
        try
        {
            if (HeadTracking.TryGetGameCameraPose(out var cam, out _, out _))
                s_drawFov = cam.fieldOfView;
        }
        catch { }
        if (_flatCam == null || !_flatCam.enabled)
            return;
        UpdatePose();
    }

    private bool UpdatePose()
    {
        try
        {
            if (!HeadTracking.TryGetGameCameraPose(out var cam, out var pos, out var rot))
                return false;
            _flatCam.transform.SetPositionAndRotation(pos, rot);
            _flatCam.fieldOfView = s_drawFov > 0f ? s_drawFov : cam.fieldOfView;
            _flatCam.nearClipPlane = cam.nearClipPlane;
            _flatCam.farClipPlane = cam.farClipPlane;
            // Not the menus: in VR the floating page itself sits in front of the game camera, and the second try
            // filmed the back of it (grey, with bits of the panels) instead of the character
            var mask = cam.cullingMask & ~(1 << 5);                     // UI
            if (MenusOnTop.Layer >= 0)
                mask &= ~(1 << MenusOnTop.Layer);
            if (_canvas != null)
                mask &= ~(1 << _canvas.gameObject.layer);
            _flatCam.cullingMask = mask;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
