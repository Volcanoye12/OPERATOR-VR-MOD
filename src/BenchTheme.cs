using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
#if MELON
using Il2CppTMPro;
#else
using TMPro;
#endif
using UnityEngine;
using UnityEngine.UI;

namespace OperatorVR;

/// <summary>
/// Menus.BenchTheme (the user's wish, 2026-11-10): the armoury bench page ('WeaponModdingCanvas') in the same look as
/// the VR mission screen. It's the game's own page, so it is restyled, not rebuilt (every button keeps its job):
/// plain backings go near-black, buttons get the dark card / gold highlight colours, writing goes off-white with
/// wider letter spacing, and gold corner brackets with an "ARMOURY · WEAPON BENCH" line frame the page. Pictures
/// (part icons, the gun's thumbnails) are left alone. Only backing colours change, never their see-through-ness
/// (WeaponBench.SeeThroughPresets makes one backing faint so the gun shows through). All put back when the page
/// closes or VR stops. The page's layout is logged once ("[BenchTheme] page:") to fine-tune the look.
/// </summary>
public class BenchTheme : MonoBehaviour
{
    public BenchTheme(IntPtr ptr) : base(ptr) { }

    private static readonly Color Gold = new(0.91f, 0.725f, 0.19f), Ink = new(0.04f, 0.05f, 0.06f),
        TextMain = new(0.91f, 0.9f, 0.88f);

    private Canvas _page;
    private float _nextScan;
    private bool _logged;
    private readonly Dictionary<IntPtr, (Image img, Color colour)> _images = new();
    private readonly Dictionary<IntPtr, (Selectable sel, ColorBlock colours, Color graphic)> _buttons = new();
    private readonly Dictionary<IntPtr, (TMP_Text text, Color colour, float spacing)> _texts = new();
    private GameObject _frame;

    private static readonly string[] PlainSprites = { "uisprite", "background", "inputfieldbackground", "uimask", "panel", "bg", "frame", "box", "rounded", "square", "white" };

    private void LateUpdate()
    {
        using var __t = ModTime.Measure("BenchTheme");
        try
        {
            var vr = Plugin.BenchTheme.Value && OpenXRBootstrap.IsRunning;
            if (!vr)
            {
                if (_mag != null || _magCards.Count > 0)
                    MagBack();
            }
            else if (Time.unscaledTime >= _nextMagScan)
            {
                _nextMagScan = Time.unscaledTime + 0.5f;
                MagManager();
            }
            // The empty panel over the slot list: fixed with or without the restyle
            if (vr || OpenXRBootstrap.IsRunning)
                ClearListOverlay();
            var on = vr && WeaponBench.Open;
            if (!on)
            {
                if (_page != null || _images.Count > 0 || _texts.Count > 0)
                    RestoreAll();
                return;
            }
            if (Time.unscaledTime < _nextScan)
                return;
            // Lists fill in as you pick slots, so look again every second
            _nextScan = Time.unscaledTime + 0.5f;
            // (Also when you've moved on to another bench and the game left this page open)
            if (_page == null || !_page.isActiveAndEnabled || !VRMenus.IsCurrentBenchPage(_page))
            {
                if (_page != null)
                    RestoreAll();
                _page = FindPage();
                if (_page == null)
                    return;
                Plugin.Logger.LogInfo($"[BenchTheme] Restyling '{_page.name}' in VR");
            }
            // (2026-11-10 release clean-up: the investigation's layout dumps, list-state / drawn-over reports, mask swap,
            // layout rebuild, view check and the bench eye capture are no longer called; the fixes that held are
            // ClearListOverlay and UnsquashRows)
            Theme(_page.transform);
            AddFrame();
            // The presets column (right of the gun): its dark backings came out weak and brown like the slot list's,
            // so they get solid cards too (the user chose that over seeing the gun through it, 2026-11-10)
            var presets = _page.transform.Find("Presets");
            if (presets != null)
                DarkCards(presets, _carded, _cards);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[BenchTheme] {e.GetType().Name}: {e.Message}");
            _nextScan = Time.unscaledTime + 5f;
        }
    }

    private static Canvas FindPage()
    {
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<Canvas>(), FindObjectsSortMode.None))
        {
            var c = o.TryCast<Canvas>();
            if (c != null && c.isActiveAndEnabled && c.isRootCanvas && c.name.StartsWith("WeaponModdingCanvas") &&
                VRMenus.IsCurrentBenchPage(c))
                return c;
        }
        return null;
    }

    // 2026-11-10, second go, from the page's layout and a picture of it: each slot row is a TMP_Dropdown whose own
    // backing is invisible (alpha 0) with a thin white outline ('Mod Section', sprite 'Cut Frame - 2px') and a white
    // arrow; the list sits on black backings at 55-82% (the wood showed through, looking brown). The first go turned
    // the white outlines dark (rows lost their edges) and widened the letter spacing (labels wrapped and overlapped).
    // Now: each row gets a solid dark card that turns dark gold under the pointer, grey edges, gold arrows, the list's
    // backing near-solid; writing untouched apart from dark writing made light. The presets panel is left alone
    private static readonly Color Card = new(0.07f, 0.085f, 0.1f, 0.95f), CardHover = new(0.3f, 0.24f, 0.08f, 0.97f),
        Edge = new(0.32f, 0.37f, 0.4f, 1f);

    private readonly List<GameObject> _cards = new();
    private bool _diagLogged, _rowLogged;
    private readonly HashSet<IntPtr> _carded = new();

    // A solid card inside each dark backing (as its first child, so it sits behind that backing's contents): the
    // game's own dark backings draw weak in VR, the mod's plain images draw solid
    private void DarkCards(Transform root, HashSet<IntPtr> carded, List<GameObject> cards)
    {
        foreach (var img in root.GetComponentsInChildren<Image>(true))
        {
            // Any dark-tinted backing, whatever its picture or whether it clips a scrolling list: the magazine
            // manager's top strip and middle box stayed brown when only plain, unclipped ones were done (2026-11-10).
            // Part pictures are tinted white, so a dark tint means a backing
            if (img == null || carded.Contains(img.Pointer) || img.name.StartsWith("OperatorVR"))
                continue;
            var c = img.color;
            if (c.a < 0.3f || Mathf.Max(c.r, c.g, c.b) > 0.3f)
                continue;
            carded.Add(img.Pointer);
            cards.Add(AddCard(img.transform));
        }
    }

    // Once: one slot row in full (every part, any depth), to name parts like the white bars
    private static void LogFirstRow(Transform root)
    {
        try
        {
            var dd = root.GetComponentInChildren<TMP_Dropdown>(true);
            if (dd == null)
                return;
            var lines = new List<string>();
            void Walk(Transform t, int depth)
            {
                if (lines.Count >= 60)
                    return;
                var img = t.GetComponent<Image>();
                var txt = t.GetComponent<TMP_Text>();
                lines.Add($"{new string(' ', depth * 2)}{t.name}{(t.gameObject.activeSelf ? "" : " (off)")}" +
                          $"{(img != null ? $": Image '{(img.sprite != null ? img.sprite.name : "-")}' {Hex(img.color)}" : "")}" +
                          $"{(txt != null ? $": Text '{Short(txt.text)}' {Hex(txt.color)}" : "")}");
                for (var i = 0; i < t.childCount; i++)
                    Walk(t.GetChild(i), depth + 1);
            }
            Walk(dd.transform, 0);
            Plugin.Logger.LogInfo("[BenchTheme] one slot row:\n" + string.Join("\n", lines));
        }
        catch (Exception e) { Plugin.Logger.LogInfo($"[BenchTheme] row log: {e.Message}"); }
    }

    // ---- The magazine manager ('MagManagerCanvas'), a separate page opened from the bench ----
    private Canvas _mag;
    private readonly List<GameObject> _magCards = new();
    private readonly List<(Mask mask, Image img, Color colour, RectMask2D added)> _magMasks = new();
    private readonly HashSet<IntPtr> _magCarded = new();
    private float _nextMagScan;
    private bool _magLogged;

    private void MagBack()
    {
        foreach (var go in _magCards)
            try { if (go != null) UnityEngine.Object.Destroy(go); } catch { }
        _magCards.Clear();
        _magCarded.Clear();
        MasksBack(_magMasks);
        _mag = null;
        ShowBenchGun();
    }

    private void MagManager()
    {
        Canvas found = null;
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<Canvas>(), FindObjectsSortMode.None))
        {
            var c = o.TryCast<Canvas>();
            if (c != null && c.isActiveAndEnabled && c.isRootCanvas && c.name.StartsWith("MagManagerCanvas"))
            {
                found = c;
                break;
            }
        }
        if (found == null)
        {
            if (_mag != null)
                Plugin.Logger.LogInfo("[BenchTheme] Magazine manager closed");
            MagBack();
            return;
        }
        if (_mag == null || _mag.Pointer != found.Pointer)
        {
            _mag = found;
            Plugin.Logger.LogInfo($"[BenchTheme] Restyling the magazine manager '{found.name}'");
        }
        DarkCards(found.transform, _magCarded, _magCards);
        HideBenchGun();
    }

    // The bench's gun hung in the middle of the magazine manager, half behind its panels ("just stuck on the menu",
    // the user, 2026-11-10): hidden while the magazine manager is open. Found as the bench does it: the turntable
    // ('DragMouse') nearest the eye, then up from it to the first object with parts drawn
    private readonly List<Renderer> _gunHidden = new();
    private bool _noGunLogged;

    private void HideBenchGun()
    {
        if (_gunHidden.Count > 0)
            return;
        var cam = Camera.main;
        if (cam == null)
            return;
        // Turned off by the game while the magazine manager is open, so not only enabled ones (the 21:16 visit: the
        // gun stayed behind the magazine manager's middle box and nothing was hidden)
        DragMouse best = null;
        var bestD = 6f;
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<DragMouse>(), FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var d = o.TryCast<DragMouse>();
            if (d == null)
                continue;
            var dist = Vector3.Distance(cam.transform.position, d.transform.position);
            if (dist < bestD)
            {
                best = d;
                bestD = dist;
            }
        }
        if (best == null)
        {
            if (!_noGunLogged)
            {
                _noGunLogged = true;
                Plugin.Logger.LogInfo("[BenchTheme] Magazine manager open, but no weapon turntable found to hide");
            }
            return;
        }
        for (var t = best.transform; t != null; t = t.parent)
        {
            foreach (var r in t.GetComponentsInChildren<Renderer>(false))
                if (r != null && r.enabled && r.gameObject.layer != MenusOnTop.Layer && r.GetComponentInParent<Canvas>() == null)
                    _gunHidden.Add(r);
            if (_gunHidden.Count > 0 || t.name.StartsWith("WeaponParent"))
                break;
        }
        foreach (var r in _gunHidden)
            r.enabled = false;
        if (_gunHidden.Count > 0)
            Plugin.Logger.LogInfo($"[BenchTheme] Gun hidden while the magazine manager is open ({_gunHidden.Count} parts, under '{best.name}')");
    }

    private void ShowBenchGun()
    {
        foreach (var r in _gunHidden)
            try { if (r != null) r.enabled = true; } catch { }
        if (_gunHidden.Count > 0)
            Plugin.Logger.LogInfo("[BenchTheme] Gun shown again");
        _gunHidden.Clear();
    }

    private GameObject AddCard(Transform row)
    {
        var go = new GameObject("OperatorVR Card");
        go.layer = row.gameObject.layer;
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(row, false);
        rt.SetAsFirstSibling();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(2f, 2f);
        rt.offsetMax = new Vector2(-2f, -2f);
        // Not an item of the row it's in: the magazine manager's strips line their contents up automatically, and the
        // card became an empty extra slot at the front instead of a background (2026-11-10 footage)
        go.AddComponent<LayoutElement>().ignoreLayout = true;
        var img = go.AddComponent<Image>();
        img.color = new Color(Card.r, Card.g, Card.b, 1f);
        img.raycastTarget = false;
        return go;
    }

    // Once: why a row might still look see-through (colours as drawn, fading groups, masks, materials)
    private void LogRowDiagnostics(Selectable s, Image img)
    {
        try
        {
            var cr = img.canvasRenderer;
            var parts = new List<string>
            {
                $"row image colour {Hex(img.color)}, drawn colour {Hex(cr.GetColor())}, alpha {cr.GetAlpha():0.00}, inherited alpha {cr.GetInheritedAlpha():0.00}",
                $"row material '{(img.material != null ? img.material.name : "-")}' shader '{(img.material != null && img.material.shader != null ? img.material.shader.name : "-")}'",
            };
            for (var p = s.transform; p != null; p = p.parent)
            {
                var cg = p.GetComponent<CanvasGroup>();
                var mask = p.GetComponent<Mask>();
                var rmask = p.GetComponent<RectMask2D>();
                var pi = p.GetComponent<Image>();
                if (cg != null || mask != null || rmask != null || (pi != null && p.Pointer != s.transform.Pointer))
                    parts.Add($"'{p.name}': {(cg != null ? $"CanvasGroup alpha {cg.alpha:0.00} " : "")}{(mask != null ? $"Mask(show {mask.showMaskGraphic}) " : "")}" +
                              $"{(rmask != null ? "RectMask2D " : "")}{(pi != null ? $"image {Hex(pi.color)} material '{(pi.material != null ? pi.material.name : "-")}' z {p.localPosition.z:0.##}" : "")}");
            }
            Plugin.Logger.LogInfo("[BenchTheme] row check: " + string.Join(" | ", parts));
        }
        catch (Exception e) { Plugin.Logger.LogInfo($"[BenchTheme] row check failed: {e.GetType().Name}: {e.Message}"); }
    }

    private Canvas _rowCanvas;
    private readonly HashSet<IntPtr> _listBackings = new();
    private float _nextListLog;
    private int _listLogs;

    // ---- Nothing of the room in front of the page ----
    // 2026-11-10 footage: at the main bench (beside the big window the mission screen hangs on) the slot list was
    // sometimes covered by a sharp-edged brown rectangle while the log showed the list filled in and showing; most
    // likely see-through room parts (the window glass) drawn over the page. Once a page has settled, every room part
    // between the eye and the page's list or presets is logged; see-through ones (transparent render queue, or named
    // glass/window) are hidden while the page is open
    private float _clearAt = -1f;
    private bool _cleared;
    private readonly List<Renderer> _viewHidden = new();

    private void ClearView(Transform root)
    {
        if (_cleared)
            return;
        if (_clearAt < 0f)
        {
            _clearAt = Time.unscaledTime + 1.5f;   // the page grows in and is pinned to the wall first
            return;
        }
        if (Time.unscaledTime < _clearAt)
            return;
        _cleared = true;
        try
        {
            var cam = Camera.main;
            if (cam == null)
                return;
            if (!HeadTracking.TryGetGameCameraPose(out _, out var eye, out _))
                eye = cam.transform.position;
            var targets = new List<Vector3>();
            var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
            foreach (var path in new[] { "Panel/Scroll View", "Presets" })
            {
                var rt = root.Find(path)?.GetComponent<RectTransform>();
                if (rt == null)
                    continue;
                rt.GetWorldCorners(corners);
                var mid = (corners[0] + corners[2]) * 0.5f;
                targets.Add(mid);
                for (var i = 0; i < 4; i++)
                    targets.Add(Vector3.Lerp(mid, corners[i], 0.8f));
            }
            var found = new Dictionary<IntPtr, (Renderer r, float dist)>();
            foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<Renderer>(), FindObjectsSortMode.None))
            {
                var r = o.TryCast<Renderer>();
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || r.GetComponentInParent<Canvas>() != null)
                    continue;
                var b = r.bounds;
                foreach (var target in targets)
                {
                    var to = target - eye;
                    var len = to.magnitude;
                    if (len < 0.05f)
                        continue;
                    if (b.IntersectRay(new Ray(eye, to / len), out var d) && d < len - 0.01f)
                    {
                        found[r.Pointer] = (r, d);
                        break;
                    }
                }
            }
            var lines = new List<string>();
            foreach (var (r, d) in found.Values)
            {
                var mat = r.sharedMaterial;
                var queue = mat != null ? mat.renderQueue : 0;
                var shader = mat != null && mat.shader != null ? mat.shader.name : "-";
                var n = (r.name + " " + (mat != null ? mat.name : "")).ToLowerInvariant();
                var seeThrough = queue >= 2450 || n.Contains("glass") || n.Contains("window");
                // Not the gun on the bench or the player's own body and gear
                var self = cam.transform.root;
                if (self != null && r.transform.root.Pointer == self.Pointer)
                    continue;
                if (r.GetComponentInParent<DragMouse>() != null)
                    continue;
                lines.Add($"'{r.name}' ({PathTo(r.transform, null)}) {d:0.00} m from the eye, material '{(mat != null ? mat.name : "-")}' " +
                          $"shader '{shader}' queue {queue}{(seeThrough ? " -> hidden while the page is open" : "")}");
                if (seeThrough)
                {
                    r.enabled = false;
                    _viewHidden.Add(r);
                }
            }
            Plugin.Logger.LogInfo($"[BenchTheme] Between the eye and the page: {(lines.Count == 0 ? "nothing" : string.Join(" | ", lines))}");
        }
        catch (Exception e) { Plugin.Logger.LogInfo($"[BenchTheme] view check: {e.GetType().Name}: {e.Message}"); }
    }

    // ---- Plain rectangle clipping for the scrolling lists ----
    // 2026-11-10 footage: the slot list sometimes showed as an empty brown box (main bench, then the caged one) while
    // the log had all 23 rows showing; glass in the way was ruled out. The lists are clipped by a Mask, which works
    // through the stencil buffer that HDRP also uses, and the masks' shared stencil materials are reused across the
    // benches and the magazine manager: when that goes wrong, everything inside the mask isn't drawn. In VR each
    // Mask is swapped for a RectMask2D (clips by rectangle, no stencil); its own picture is made invisible rather than
    // switched off, so the list can still be dragged. All put back when the page closes
    private readonly List<(Mask mask, Image img, Color colour, RectMask2D added)> _masks = new();

    private void SwapMasks(Transform root, List<(Mask mask, Image img, Color colour, RectMask2D added)> into)
    {
        foreach (var m in root.GetComponentsInChildren<Mask>(true))
        {
            if (m == null || !m.enabled)
                continue;
            var img = m.GetComponent<Image>();
            var colour = img != null ? img.color : Color.clear;
            RectMask2D added = null;
            if (m.GetComponent<RectMask2D>() == null)
                added = m.gameObject.AddComponent<RectMask2D>();
            var showed = m.showMaskGraphic;
            m.enabled = false;
            if (img != null && !showed)
                img.color = new Color(colour.r, colour.g, colour.b, 0f);
            into.Add((m, img, colour, added));
            if (_maskLogs++ < 6)
                Plugin.Logger.LogInfo($"[BenchTheme] '{PathTo(m.transform, root)}': clipped by rectangle instead of a stencil mask");
        }
    }

    private static void MasksBack(List<(Mask mask, Image img, Color colour, RectMask2D added)> list)
    {
        foreach (var (mask, img, colour, added) in list)
            try
            {
                if (added != null) UnityEngine.Object.Destroy(added);
                if (mask != null) mask.enabled = true;
                if (img != null) img.color = colour;
            }
            catch { }
        list.Clear();
    }

    private int _maskLogs;

    // ---- The slot list laid out again when the game didn't ----
    // 2026-11-10 (the "blank boxes" after visiting other benches): at the bench that showed an empty brown list, all
    // 23 rows were there but the list's Content was 419 tall instead of ~2700 and scrolled to the bottom: the game
    // hadn't redone the layout for that page, so the rows sat outside the visible window. Whenever the Content is
    // shorter than its rows need, its layout is rebuilt and the list scrolled to the top
    private int _layoutFixes;

    // ---- The empty panel over the slot list ----
    // 2026-11-10, found by LogWhatsDrawn after five wrong guesses: at every bench after the first, the list's window
    // ('Panel/Scroll View/Viewport') held a leftover 'Panel' (black at 82%, no rows in it) drawn on top of the real
    // list 'Content' — the empty brown box, with the restyle on or off. While VR runs and a bench page is open, any
    // child of the list's window that holds no slot rows is made invisible (put back when the page closes)
    private readonly List<(Graphic g, Color colour)> _overlays = new();
    private Canvas _overlayPage;
    private float _nextOverlayScan;
    private int _overlayLogs;

    // With the empty panel hidden the list area was simply empty at the later benches (2026-11-10 footage): the
    // rows sit in 'Viewport/Content' there, not in 'Viewport/Panel' as at the first bench. Once per page: where the
    // window, the rows' container and the first row sit (page units), and which container the scroll area moves; if
    // it's not the one with the rows, it's pointed at it and that container put at the top of the window
    private IntPtr _alignedFor;

    private void AlignRows(Transform page, Transform viewport)
    {
        if (_alignedFor == page.Pointer)
            return;
        var dd = viewport.GetComponentInChildren<TMP_Dropdown>(false);
        if (dd == null)
            return;   // not filled in yet
        _alignedFor = page.Pointer;
        try
        {
            Transform holder = dd.transform.parent;
            while (holder != null && holder.parent != null && holder.parent.Pointer != viewport.Pointer)
                holder = holder.parent;
            var sr = viewport.parent != null ? viewport.parent.GetComponent<ScrollRect>() : null;
            string Rect(Transform t)
            {
                if (t == null) return "-";
                var c = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
                t.GetComponent<RectTransform>().GetWorldCorners(c);
                var lo = page.InverseTransformPoint(c[0]);
                var hi = page.InverseTransformPoint(c[2]);
                return $"x {lo.x:0}..{hi.x:0} y {lo.y:0}..{hi.y:0}";
            }
            var holderRt = holder != null ? holder.GetComponent<RectTransform>() : null;
            Plugin.Logger.LogInfo($"[BenchTheme] rows: window {Rect(viewport)}; rows held by '{(holder != null ? holder.name : "-")}' {Rect(holder)} " +
                                  $"(anchors {holderRt?.anchorMin}..{holderRt?.anchorMax}, pivot {holderRt?.pivot}, at {holderRt?.anchoredPosition}); " +
                                  $"first row {Rect(dd.transform)}; scroll area moves '{(sr != null && sr.content != null ? sr.content.name : "-")}'");
            if (sr != null && holderRt != null && (sr.content == null || sr.content.Pointer != holderRt.Pointer))
            {
                sr.content = holderRt;
                holderRt.anchorMin = new Vector2(0f, 1f);
                holderRt.anchorMax = new Vector2(1f, 1f);
                holderRt.pivot = new Vector2(0.5f, 1f);
                holderRt.anchoredPosition = Vector2.zero;
                sr.verticalNormalizedPosition = 1f;
                Plugin.Logger.LogInfo($"[BenchTheme] Scroll area now moves '{holder.name}' (the rows), at the top of the window: {Rect(holder)}");
            }
        }
        catch (Exception e) { Plugin.Logger.LogInfo($"[BenchTheme] rows check: {e.GetType().Name}: {e.Message}"); }
    }

    // 2026-11-10, the actual cause of the empty list at every bench after the first: the rows were there and in
    // place, but squashed to a point (first row 0 x 0 page units, against 556 x 100 at the first bench), so nothing
    // showed and the pointer had nothing to land on. Any slot row (or part of one) shrunk to nothing is put back to
    // full size, checked twice a second while a page is open
    private int _unsquashLogs;

    private void UnsquashRows(Transform viewport)
    {
        var fixedScale = 0;
        foreach (var dd in viewport.GetComponentsInChildren<TMP_Dropdown>(false))
        {
            if (dd == null)
                continue;
            // The row and everything between it and the list's window
            for (var t = dd.transform; t != null && t.Pointer != viewport.Pointer; t = t.parent)
            {
                var s = t.localScale;
                if (Mathf.Abs(s.x) < 0.01f || Mathf.Abs(s.y) < 0.01f)
                {
                    if (_unsquashLogs < 6)
                        Plugin.Logger.LogInfo($"[BenchTheme] '{t.name}' was shrunk to {s}: back to full size");
                    _unsquashLogs++;
                    t.localScale = Vector3.one;
                    fixedScale++;
                }
            }
            var rt = dd.GetComponent<RectTransform>();
            if (rt != null && (rt.rect.width < 1f || rt.rect.height < 1f) && _unsquashLogs++ < 6)
                Plugin.Logger.LogInfo($"[BenchTheme] Row '{dd.name}' has no size: {rt.rect.size} (sizeDelta {rt.sizeDelta}, anchors {rt.anchorMin}..{rt.anchorMax}, scale {rt.localScale})");
        }
        if (fixedScale > 0)
            Canvas.ForceUpdateCanvases();
    }

    private void ClearListOverlay()
    {
        Canvas page = null;
        if (OpenXRBootstrap.IsRunning && WeaponBench.Open)
            page = FindPage();
        if (page == null || _overlayPage == null || page.Pointer != _overlayPage.Pointer)
        {
            foreach (var (g, colour) in _overlays)
                try { if (g != null) g.color = colour; } catch { }
            _overlays.Clear();
            _overlayPage = page;
            _alignedFor = IntPtr.Zero;
        }
        if (page == null || Time.unscaledTime < _nextOverlayScan)
            return;
        _nextOverlayScan = Time.unscaledTime + 0.5f;
        var viewport = page.transform.Find("Panel/Scroll View/Viewport");
        if (viewport == null)
            return;
        UnsquashRows(viewport);
        for (var i = 0; i < viewport.childCount; i++)
        {
            var child = viewport.GetChild(i);
            if (child == null || !child.gameObject.activeInHierarchy || child.name.StartsWith("OperatorVR") ||
                child.GetComponentInChildren<TMP_Dropdown>(false) != null)
                continue;
            foreach (var g in child.GetComponentsInChildren<Graphic>(false))
            {
                if (g == null || g.color.a < 0.01f || _overlays.Exists(o => o.g != null && o.g.Pointer == g.Pointer))
                    continue;
                _overlays.Add((g, g.color));
                if (_overlayLogs++ < 10)
                    Plugin.Logger.LogInfo($"[BenchTheme] Empty '{PathTo(g.transform, page.transform)}' ({Hex(g.color)}) was over the slot list: made invisible");
                g.color = new Color(g.color.r, g.color.g, g.color.b, 0f);
            }
        }
    }

    // Once per page, 2.5 s after it opens: everything of the page drawn over the slot list's area, with its colour,
    // fade, hidden-by-clipping state and depth; and the rows' own state. (2026-11-10: the list area showed as an
    // empty brown box at every bench after the first, with the theme on or off, while the list itself looked fine)
    private float _drawnAt = -1f;
    private bool _drawnLogged;

    private void LogWhatsDrawn(Transform root)
    {
        if (_drawnLogged)
            return;
        if (_drawnAt < 0f)
        {
            _drawnAt = Time.unscaledTime + 2.5f;
            return;
        }
        if (Time.unscaledTime < _drawnAt)
            return;
        _drawnLogged = true;
        try
        {
            var area = root.Find("Panel/Scroll View")?.GetComponent<RectTransform>();
            if (area == null)
                return;
            var c4 = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
            area.GetWorldCorners(c4);
            var mid = (c4[0] + c4[2]) * 0.5f;
            var lines = new List<string>();
            int rows = 0, rowsCulled = 0, rowsClipped = 0, rowsFaded = 0;
            foreach (var g in root.GetComponentsInChildren<Graphic>(false))
            {
                if (g == null || !g.enabled)
                    continue;
                var cr = g.canvasRenderer;
                var inRow = g.GetComponentInParent<TMP_Dropdown>() != null;
                if (inRow)
                {
                    rows++;
                    if (cr.cull) rowsCulled++;
                    if (cr.hasRectClipping) rowsClipped++;
                    if (cr.GetInheritedAlpha() < 0.05f) rowsFaded++;
                    continue;
                }
                var a = g.color.a * cr.GetInheritedAlpha();
                if (a < 0.05f || cr.cull)
                    continue;
                var rt = g.rectTransform;
                var gc = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
                rt.GetWorldCorners(gc);
                // Covers the middle of the list area (in the page's plane)
                var lo = root.InverseTransformPoint(gc[0]);
                var hi = root.InverseTransformPoint(gc[2]);
                var m = root.InverseTransformPoint(mid);
                if (m.x < Mathf.Min(lo.x, hi.x) || m.x > Mathf.Max(lo.x, hi.x) || m.y < Mathf.Min(lo.y, hi.y) || m.y > Mathf.Max(lo.y, hi.y))
                    continue;
                lines.Add($"'{PathTo(g.transform, root)}' {g.GetIl2CppType().Name} {Hex(g.color)} drawn alpha {a:0.00}, " +
                          $"z {Vector3.Dot(g.transform.position - root.position, root.forward) * 100f:0.0} cm, canvas '{(g.canvas != null ? g.canvas.name : "-")}'");
            }
            Plugin.Logger.LogInfo($"[BenchTheme] drawn over the list's middle: {(lines.Count == 0 ? "nothing" : string.Join(" | ", lines))}");
            Plugin.Logger.LogInfo($"[BenchTheme] slot-row parts: {rows}, culled {rowsCulled}, clipped by rectangle {rowsClipped}, faded out {rowsFaded}; " +
                                  $"page scale {root.lossyScale.x:0.00000}, page canvas enabled {(_page != null && _page.enabled)}, " +
                                  $"layer {root.gameObject.layer}, camera sees layer {((Camera.main != null && (Camera.main.cullingMask & (1 << root.gameObject.layer)) != 0) ? "yes" : "no")}");
        }
        catch (Exception e) { Plugin.Logger.LogInfo($"[BenchTheme] drawn check: {e.GetType().Name}: {e.Message}"); }
    }

    private void FixListLayout(Transform root)
    {
        try
        {
            var content = root.Find("Panel/Scroll View/Viewport/Content");
            var crt = content != null ? content.GetComponent<RectTransform>() : null;
            if (crt == null || crt.childCount == 0)
                return;
            var need = LayoutUtility.GetPreferredHeight(crt);
            var have = crt.rect.height;
            if (need <= have + 5f)
                return;
            LayoutRebuilder.ForceRebuildLayoutImmediate(crt);
            var sr = root.Find("Panel/Scroll View")?.GetComponent<ScrollRect>();
            if (sr != null)
                sr.verticalNormalizedPosition = 1f;
            if (_layoutFixes++ < 10)
                Plugin.Logger.LogInfo($"[BenchTheme] Slot list laid out again: it was {have:0} tall for rows needing {need:0} " +
                                      $"(now {crt.rect.height:0}), scrolled to the top");
        }
        catch (Exception e)
        {
            if (_layoutFixes++ < 10)
                Plugin.Logger.LogInfo($"[BenchTheme] list layout: {e.GetType().Name}: {e.Message}");
        }
    }

    private void ViewBack()
    {
        foreach (var r in _viewHidden)
            try { if (r != null) r.enabled = true; } catch { }
        _viewHidden.Clear();
        _cleared = false;
        _clearAt = -1f;
    }

    private static string PathTo(Transform t, Transform stop)
    {
        var parts = new List<string>();
        for (var p = t; p != null && (stop == null || p.Pointer != stop.Pointer) && parts.Count < 8; p = p.parent)
            parts.Insert(0, p.name);
        return string.Join("/", parts);
    }

    // 2026-11-10: after the range bench, the main bench's slot list showed as an empty brown box (rows gone, backings
    // not cleared). Every 2 s while a page is open (5 lines a page): how many rows are showing and where the list is
    // scrolled, to tell "not filled in", "scrolled away" and "hidden" apart
    private void LogListState(Transform root)
    {
        if (_listLogs >= 5 || Time.unscaledTime < _nextListLog)
            return;
        _nextListLog = Time.unscaledTime + 2f;
        _listLogs++;
        try
        {
            int all = 0, shown = 0;
            foreach (var dd in root.GetComponentsInChildren<TMP_Dropdown>(true))
            {
                if (dd == null || InPresets(dd.transform))
                    continue;
                all++;
                if (dd.gameObject.activeInHierarchy)
                    shown++;
            }
            var content = root.Find("Panel/Scroll View/Viewport/Content") ?? root.Find("Panel/Scroll View/Viewport/Panel");
            var sr = root.Find("Panel/Scroll View")?.GetComponent<ScrollRect>();
            var crt = content != null ? content.GetComponent<RectTransform>() : null;
            var panel = root.Find("Panel");
            var backs = new List<string>();
            foreach (var p in new[] { "Panel", "Panel/Scroll View", "Panel/Scroll View/Viewport" })
            {
                var img = root.Find(p)?.GetComponent<Image>();
                if (img != null)
                    backs.Add($"{p.Substring(p.LastIndexOf('/') + 1)} {Hex(img.color)}{(_images.ContainsKey(img.Pointer) ? " (ours)" : "")}");
            }
            Plugin.Logger.LogInfo($"[BenchTheme] list: {shown} of {all} slot rows showing; content " +
                                  $"{(crt != null ? $"'{crt.name}' at {crt.anchoredPosition} size {crt.rect.size}, {crt.childCount} children, active {crt.gameObject.activeInHierarchy}" : "not found")}; " +
                                  $"scroll {(sr != null ? sr.normalizedPosition.ToString() : "-")}; panel active {(panel != null && panel.gameObject.activeInHierarchy)}; " +
                                  $"backings {string.Join(", ", backs)}; cards {_cards.Count}");
        }
        catch (Exception e) { Plugin.Logger.LogInfo($"[BenchTheme] list state: {e.GetType().Name}: {e.Message}"); }
    }

    private void Theme(Transform root)
    {
        // The slot rows' canvas, and the backings between the page and the rows
        if (_rowCanvas == null)
        {
            _listBackings.Clear();
            foreach (var dd in root.GetComponentsInChildren<TMP_Dropdown>(true))
            {
                if (dd == null || dd.targetGraphic == null || InPresets(dd.transform))
                    continue;
                _rowCanvas = dd.targetGraphic.canvas;
                for (var p = dd.transform.parent; p != null && p.Pointer != root.Pointer; p = p.parent)
                {
                    var back = p.GetComponent<Image>();
                    if (back != null)
                        _listBackings.Add(back.Pointer);
                }
                Plugin.Logger.LogInfo($"[BenchTheme] Slot rows drawn by canvas '{(_rowCanvas != null ? _rowCanvas.name : "-")}'" +
                                      $"{(_rowCanvas != null && _rowCanvas.Pointer != _page.Pointer ? " (its own, not the page's)" : "")}; " +
                                      $"{_listBackings.Count} backing(s) round the list; first row '{PathTo(dd.transform, root)}'" +
                                      $"{(dd.gameObject.activeInHierarchy ? "" : " (hidden)")}");
                break;
            }
        }
        foreach (var s in root.GetComponentsInChildren<Selectable>(true))
        {
            if (s == null || _buttons.ContainsKey(s.Pointer) || s.transition != Selectable.Transition.ColorTint || InPresets(s.transform))
                continue;
            var g = s.targetGraphic;
            var img = g != null ? g.TryCast<Image>() : null;
            if (img == null)
                continue;
            var isSlot = s.TryCast<TMP_Dropdown>() != null;
            // The slot rows (invisible backing) and plain-card buttons / list items; a button that IS a picture
            // (a part's icon) and other invisible hit areas keep their look
            if (!isSlot && (!IsPlain(img) || g.color.a < 0.05f))
                continue;
            _buttons[s.Pointer] = (s, s.colors, g.color);
            if (isSlot)
            {
                // v3 footage: the slot rows stayed brown and see-through with their colour block set, so each row
                // gets its own solid card behind its contents instead (the game's hover tint left as it is)
                _cards.Add(AddCard(s.transform));
                continue;
            }
            g.color = Color.white;   // the colour block below gives the real colours (it multiplies)
            var cb = s.colors;
            cb.normalColor = Card;
            cb.highlightedColor = CardHover;
            cb.selectedColor = isSlot ? Card : CardHover;   // a picked slot doesn't stay lit after the pointer leaves
            cb.pressedColor = new Color(Gold.r, Gold.g, Gold.b, 0.85f);
            cb.disabledColor = new Color(0.12f, 0.13f, 0.14f, 0.8f);
            cb.colorMultiplier = 1f;
            cb.fadeDuration = 0.08f;
            s.colors = cb;
        }

        foreach (var img in root.GetComponentsInChildren<Image>(true))
        {
            if (img == null || _images.ContainsKey(img.Pointer) || img.name.StartsWith("OperatorVR") || InPresets(img.transform))
                continue;
            var sel = img.GetComponent<Selectable>();
            if (sel != null && _buttons.ContainsKey(sel.Pointer))
                continue;
            var c = img.color;
            if (c.a < 0.05f)
                continue;
            var sprite = img.sprite != null ? (img.sprite.name ?? "").ToLowerInvariant() : "";
            var grey = Mathf.Max(c.r, c.g, c.b) - Mathf.Min(c.r, c.g, c.b) < 0.12f;
            var bright = Mathf.Max(c.r, c.g, c.b) > 0.5f;
            Color? want = null;
            if (sprite.Contains("arrow") && grey && bright)
                want = Gold;
            else if (grey && bright && (sprite.Contains("fill") || sprite.Contains("bar") || sprite.Contains("glow") ||
                                        sprite.Contains("slider") || sprite.Contains("line")))
                want = new Color(Edge.r, Edge.g, Edge.b, 0.55f);              // glaring white bars: dimmed grey
            else if (sprite.Contains("frame") && grey && bright)
                want = Edge;                                                  // row outlines: grey edges
            else if (grey && !bright && IsPlain(img) && _listBackings.Contains(img.Pointer))
            {
                // The black backings round the slot list (Panel, Scroll View, the list's own Panel). In VR the rows
                // are drawn by a separate canvas, and a backing on the page's canvas was drawn OVER them: at 67% it
                // made the rows dim and brown, made solid it hid them (2026-11-10 footage). Backings in the rows'
                // canvas go near-solid behind them; the others go clear
                // (If the rows turn out to share the page's canvas, all backings go clear: the solid row cards are
                // enough, and nothing can cover them)
                var behind = img.canvas != null && _rowCanvas != null && img.canvas.Pointer == _rowCanvas.Pointer &&
                             _rowCanvas.Pointer != _page.Pointer;
                want = behind ? new Color(Ink.r, Ink.g, Ink.b, Mathf.Max(c.a, 0.92f)) : new Color(c.r, c.g, c.b, 0f);
            }
            if (want == null)
                continue;
            _images[img.Pointer] = (img, c);
            img.color = want.Value;
        }

        foreach (var t in root.GetComponentsInChildren<TMP_Text>(true))
        {
            if (t == null || _texts.ContainsKey(t.Pointer) || t.name.StartsWith("OperatorVR") || InPresets(t.transform))
                continue;
            var c = t.color;
            var grey = Mathf.Max(c.r, c.g, c.b) - Mathf.Min(c.r, c.g, c.b) < 0.12f;
            // Dark writing would vanish on the dark cards; everything else as the game has it
            if (!grey || Mathf.Max(c.r, c.g, c.b) > 0.5f)
                continue;
            _texts[t.Pointer] = (t, c, t.characterSpacing);
            t.color = new Color(TextMain.r, TextMain.g, TextMain.b, c.a);
        }
    }

    private static bool InPresets(Transform t)
    {
        for (var p = t; p != null; p = p.parent)
            if (p.name == "Presets")
                return true;
        return false;
    }

    // A plain backing (no sprite, or the stock UI square / rounded box), not a picture
    private static bool IsPlain(Image img)
    {
        var sp = img.sprite;
        if (sp == null)
            return true;
        var n = (sp.name ?? "").ToLowerInvariant();
        foreach (var p in PlainSprites)
            if (n.Contains(p))
                return true;
        return false;
    }

    // Gold corner brackets and a label, round the page's main panel (or the whole page)
    private void AddFrame()
    {
        if (_frame != null)
            return;
        // Round the slot list you see ('Panel/Scroll View'): 'Panel' itself reaches beyond it, and brackets on it
        // were out of sight (2026-11-10)
        var host = _page.transform.Find("Panel/Scroll View") ?? _page.transform.Find("Panel") ?? _page.transform;
        _frame = new GameObject("OperatorVR Bench Frame");
        _frame.layer = _page.gameObject.layer;
        var rt = _frame.AddComponent<RectTransform>();
        rt.SetParent(host, false);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        foreach (var (cx, cy) in new[] { (0f, 0f), (1f, 0f), (0f, 1f), (1f, 1f) })
        {
            var at = new Vector2(cx, cy);
            var sx = cx == 0f ? 1f : -1f;
            var sy = cy == 0f ? 1f : -1f;
            Bar(rt, at, new Vector2(Mathf.Min(0f, sx * 60f) - sx * 6f, Mathf.Min(0f, sy * 5f) - sy * 6f),
                new Vector2(Mathf.Max(0f, sx * 60f) - sx * 6f, Mathf.Max(0f, sy * 5f) - sy * 6f));
            Bar(rt, at, new Vector2(Mathf.Min(0f, sx * 5f) - sx * 6f, Mathf.Min(0f, sy * 60f) - sy * 6f),
                new Vector2(Mathf.Max(0f, sx * 5f) - sx * 6f, Mathf.Max(0f, sy * 60f) - sy * 6f));
        }
        var labelGo = new GameObject("OperatorVR Bench Label");
        labelGo.layer = _frame.layer;
        var lrt = labelGo.AddComponent<RectTransform>();
        lrt.SetParent(rt, false);
        lrt.anchorMin = lrt.anchorMax = new Vector2(0f, 1f);
        lrt.pivot = new Vector2(0f, 0f);
        lrt.anchoredPosition = new Vector2(0f, 10f);
        lrt.sizeDelta = new Vector2(900f, 40f);
        var label = labelGo.AddComponent<TextMeshProUGUI>();
        foreach (var t in _page.GetComponentsInChildren<TMP_Text>(true))
            if (t != null && t.font != null) { label.font = t.font; break; }
        label.text = "ARMOURY  ·  WEAPON BENCH";
        label.fontSize = 26f;
        label.characterSpacing = 14f;
        label.color = Gold;
        label.alignment = TextAlignmentOptions.BottomLeft;
        label.raycastTarget = false;
        SetLayer(_frame.transform, _page.gameObject.layer);
    }

    private void Bar(RectTransform parent, Vector2 anchor, Vector2 min, Vector2 max)
    {
        var go = new GameObject("OperatorVR Corner");
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = anchor;
        rt.offsetMin = min;
        rt.offsetMax = max;
        var img = go.AddComponent<Image>();
        img.color = Gold;
        img.raycastTarget = false;
    }

    private static void SetLayer(Transform t, int layer)
    {
        t.gameObject.layer = layer;
        for (var i = 0; i < t.childCount; i++)
            SetLayer(t.GetChild(i), layer);
    }

    private void RestoreAll()
    {
        foreach (var (s, colours, graphic) in _buttons.Values)
            try
            {
                if (s == null) continue;
                s.colors = colours;
                if (s.targetGraphic != null) s.targetGraphic.color = graphic;
            }
            catch { }
        foreach (var (img, colour) in _images.Values)
            try
            {
                // (The presets panel, which WeaponBench.SeeThroughPresets fades, is never touched)
                if (img != null) img.color = colour;
            }
            catch { }
        foreach (var (t, colour, spacing) in _texts.Values)
            try
            {
                if (t == null) continue;
                t.color = colour;
                t.characterSpacing = spacing;
            }
            catch { }
        try { if (_frame != null) UnityEngine.Object.Destroy(_frame); } catch { }
        var n = _buttons.Count + _images.Count + _texts.Count;
        _buttons.Clear();
        _images.Clear();
        _texts.Clear();
        _frame = null;
        foreach (var go in _cards)
            try { if (go != null) UnityEngine.Object.Destroy(go); } catch { }
        _cards.Clear();
        _carded.Clear();
        _page = null;
        _listLogs = 0;
        _drawnLogged = false;
        _drawnAt = -1f;
        ViewBack();
        MasksBack(_masks);
        _rowCanvas = null;
        _listBackings.Clear();
        if (n > 0)
            Plugin.Logger.LogInfo($"[BenchTheme] Page back to the game's look ({n} parts)");
    }

    // Once: the page's parts, to fine-tune the look later
    private static void LogLayout(Transform root)
    {
        var lines = new List<string>();
        void Walk(Transform t, int depth)
        {
            if (lines.Count >= 120 || depth > 6)
                return;
            var what = new List<string>();
            var img = t.GetComponent<Image>();
            if (img != null) what.Add($"Image {(img.sprite != null ? img.sprite.name : "-")} {Hex(img.color)}");
            if (t.GetComponent<RawImage>() != null) what.Add("RawImage");
            var txt = t.GetComponent<TMP_Text>();
            if (txt != null) what.Add($"Text '{Short(txt.text)}' {Hex(txt.color)}");
            var sel = t.GetComponent<Selectable>();
            if (sel != null) what.Add($"{sel.GetIl2CppType().Name} {sel.transition}");
            if (what.Count > 0 || depth < 3)
                lines.Add($"{new string(' ', depth * 2)}{t.name}{(t.gameObject.activeSelf ? "" : " (off)")}: {string.Join(", ", what)}");
            for (var i = 0; i < t.childCount; i++)
                Walk(t.GetChild(i), depth + 1);
        }
        Walk(root, 0);
        Plugin.Logger.LogInfo($"[BenchTheme] page: {lines.Count} lines\n" + string.Join("\n", lines));
    }

    // (ColorUtility.ToHtmlStringRGBA is stripped from this game: "Method unstripping failed", 2026-11-10)
    private static string Hex(Color c) =>
        $"#{(int)(c.r * 255):X2}{(int)(c.g * 255):X2}{(int)(c.b * 255):X2}{(int)(c.a * 255):X2}";

    private static string Short(string s)
    {
        s = (s ?? "").Replace("\n", " ");
        return s.Length > 30 ? s.Substring(0, 30) + "…" : s;
    }
}
