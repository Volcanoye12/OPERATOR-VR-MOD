using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
#if MELON
using Il2CppTMPro;
#else
using TMPro;
#endif
using UnityEngine;

namespace OperatorVR;

/// <summary>
/// The grenades you have left (the user's idea, 2026-11-01, after the magazine label). While you hold a grenade, one
/// line per kind you carry (frag, flashbang, ninebang...) shows in front of you, a little below the middle of the view:
/// small copies of that grenade (built from its own meshes and materials, one per grenade of that kind, up to
/// MaxCopies), turning slowly, with the number beside them. The kind in your hand is the top line, its number yellow;
/// the others are white. Counted from the grenades on the vest (PlayerNetworking.GrenadeV2Pouch's children, by name)
/// plus the one in hand: GrenadesList holds a different number per grenade, not per kind, and the game's weapon lists
/// gave no names for them. Controls.GrenadeCount.
/// </summary>
public class GrenadeLabel : MonoBehaviour
{
    public GrenadeLabel(IntPtr ptr) : base(ptr) { }

    private const int MaxCopies = 6;
    // Each copy is this big (largest side), spaced this far apart; lines this far apart
    private const float CopySize = 0.025f, CopyGap = 0.032f, LineGap = 0.034f;
    // Where the top line sits from the eye: 0.4 m ahead (the camera's near plane is 0.3 m), about 15° down. Above the
    // grenade it was hard to see: the arm holds it out about 0.9 m away
    private static readonly Vector3 InView = new(0f, -0.11f, 0.4f);
    private static readonly Color HeldColour = new(1f, 0.85f, 0.2f), OtherColour = new(0.9f, 0.9f, 0.9f);

    private class Line
    {
        public string Kind;
        public GameObject Template;   // one copy of this kind (inactive, cloned for the row)
        public GameObject Row;        // the copies and the number
        public TextMeshProUGUI Text;
        public int Count = -1;
        public bool Held;
    }

    private PlayerNetworking _me;
    private float _nextFind, _nextCount;
    private GameObject _root;
    private readonly Dictionary<string, Line> _lines = new();
    private string _shown = "";
    private TMP_FontAsset _font;
    private int _logs, _failLogs;
    private string _lastSpoken;

    private void LateUpdate()
    {
        using var __t = ModTime.Measure("GrenadeLabel");
        try
        {
            if (!OpenXRBootstrap.IsRunning || !Plugin.GrenadeCount.Value)
            {
                Hide();
                return;
            }
            FindMe();
            var grenade = HeldGrenade();
            var cam = Camera.main;
            GrenadeCommand(grenade);
            // Say the grenade's name when one comes into your hand or you switch to another kind (Controls.GrenadeVoice)
            var spoken = grenade != null ? DisplayName(BaseName(grenade.name)) : null;
            if (spoken != _lastSpoken)
            {
                _lastSpoken = spoken;
                if (spoken != null && Plugin.TouchControls.Value)
                    VoiceBridge.Grenade(spoken);
            }
            if (grenade == null || cam == null)
            {
                Hide();
                return;
            }
            Show(grenade, cam);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Grenade] label: {e.GetType().Name}: {e.Message}");
            Hide();
        }
    }

    // Spoken "frag grenade" / "nine bang" / "flashbang" (VoiceCommands, 2026-11-04): taps the game's grenade switch
    // (SwitchGrenades, G) until that kind is in hand, a tap every GrenadeTapGap seconds (time for the switch to
    // happen), up to 5 taps. Already in hand: the callout is said again. Not carried: nothing happens
    private string _wantGrenade;
    private int _grenadeTaps;
    private float _nextGrenadeTap;
    private bool _gDown;
    private const float GrenadeTapGap = 0.7f;

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private void GrenadeCommand(Transform grenade)
    {
        if (_gDown)
        {
            _gDown = false;
            TouchKeys.PressKey(KeyCode.G, false);
        }
        var held = grenade != null ? DisplayName(BaseName(grenade.name)) : null;
        var cmd = VoiceBridge.TakeGrenadeCommand();
        if (cmd != null && _me != null)
        {
            if (held == cmd)
            {
                VoiceBridge.Grenade(cmd);
                _wantGrenade = null;
            }
            else if (!Carries(cmd))
            {
                Plugin.Logger.LogInfo($"[VoiceCmd] No {cmd} carried");
                _wantGrenade = null;
            }
            else
            {
                _wantGrenade = cmd;
                _grenadeTaps = 0;
                _nextGrenadeTap = 0f;
                Plugin.Logger.LogInfo($"[VoiceCmd] Bringing up the {cmd} (in hand now: {held ?? "no grenade"})");
            }
        }
        if (_wantGrenade == null)
            return;
        if (held == _wantGrenade)
        {
            Plugin.Logger.LogInfo($"[VoiceCmd] {_wantGrenade} in hand after {_grenadeTaps} tap(s) of the grenade switch");
            _wantGrenade = null;
            return;
        }
        if (Time.unscaledTime < _nextGrenadeTap)
            return;
        if (_grenadeTaps >= 5)
        {
            Plugin.Logger.LogInfo($"[VoiceCmd] Gave up bringing up the {_wantGrenade} (in hand: {held ?? "no grenade"})");
            _wantGrenade = null;
            return;
        }
        _grenadeTaps++;
        _nextGrenadeTap = Time.unscaledTime + GrenadeTapGap;
        TouchKeys.PressKey(KeyCode.G, true);
        _gDown = true;
    }

    /// <summary>Whether a grenade of this kind (plain name) is in hand or on the vest.</summary>
    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private bool Carries(string kind)
    {
        var pouch = _me.GrenadeV2Pouch;
        if (pouch == null)
            return true;   // can't tell: try anyway
        for (var i = 0; i < pouch.childCount; i++)
        {
            var c = pouch.GetChild(i);
            if (c != null && c.gameObject.activeSelf && DisplayName(BaseName(c.name)) == kind)
                return true;
        }
        return false;
    }

    private void FindMe()
    {
        if (_me != null || Time.unscaledTime < _nextFind)
            return;
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

    /// <summary>The grenade in your hand, or null when you're not holding one.</summary>
    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private Transform HeldGrenade()
    {
        if (_me == null)
            return null;
        var g = _me.c_activeGrenade;
        if (g != null && g.gameObject.activeInHierarchy)
            return g.transform;
        return null;
    }

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private static string BaseName(string name) => name.Replace("(Clone)", "").Trim();

    /// <summary>A plain name for a grenade object's name (M67 = "Frag"); others as the game names them.</summary>
    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private static string DisplayName(string kind)
    {
        var k = kind.ToLowerInvariant();
        if (k.Contains("nine") || k.Contains("9bang") || k.Contains("9-bang") || k.Contains("9 bang"))
            return "9-Bang";
        if (k.Contains("flash"))
            return "Flashbang";
        if (k.Contains("m67") || k.Contains("frag"))
            return "Frag";
        if (k.Contains("smoke"))
            return "Smoke";
        return kind;
    }

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private void Show(Transform grenade, Camera cam)
    {
        if (_root == null)
        {
            // Made again after a level unloads (it goes with the camera it's on)
            _root = new GameObject("OperatorVR Grenade Count");
            _lines.Clear();
            _shown = "";
        }
        var t = _root.transform;
        // Fixed to the camera, which carries the head pose when the eyes are drawn, so it stays put in view
        if (t.parent == null || t.parent.Pointer != cam.transform.Pointer)
            t.SetParent(cam.transform, false);
        t.localPosition = InView;
        t.localRotation = Quaternion.identity;

        // Counting looks through the vest: twice a second is plenty
        if (Time.unscaledTime >= _nextCount)
        {
            _nextCount = Time.unscaledTime + 0.5f;
            Recount(grenade);
        }

        // Each copy turns slowly so you can see what it is
        var spin = Quaternion.Euler(0f, Time.unscaledTime * 60f % 360f, 0f);
        foreach (var line in _lines.Values)
            if (line.Row != null && line.Row.activeSelf)
                for (var i = 0; i < line.Row.transform.childCount; i++)
                {
                    var c = line.Row.transform.GetChild(i);
                    if (c.name.StartsWith("Grenade "))
                        c.localRotation = spin;
                }
        if (!_root.activeSelf)
            _root.SetActive(true);
    }

    /// <summary>Kinds carried and how many of each: the vest's grenades by name, plus the one in hand.</summary>
    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private void Recount(Transform grenade)
    {
        var held = BaseName(grenade.name);
        var counts = new Dictionary<string, int> { [held] = 1 };
        var sources = new Dictionary<string, Transform> { [held] = grenade };
        var pouch = _me.GrenadeV2Pouch;
        if (pouch != null)
            for (var i = 0; i < pouch.childCount; i++)
            {
                var c = pouch.GetChild(i);
                if (c == null || !c.gameObject.activeSelf)
                    continue;
                var kind = BaseName(c.name);
                counts[kind] = counts.TryGetValue(kind, out var n) ? n + 1 : 1;
                if (!sources.ContainsKey(kind))
                    sources[kind] = c;
            }

        // Held kind first, then the rest by name
        var order = new List<string>(counts.Keys);
        order.Sort((a, b) => a == held ? -1 : b == held ? 1 : string.CompareOrdinal(a, b));
        var summary = string.Join(", ", order.ConvertAll(k => $"{k} {counts[k]}"));
        // The held kind comes first, so a change of grenade in hand changes this too
        if (summary == _shown)
            return;
        _shown = summary;

        foreach (var line in _lines.Values)
            if (line.Row != null)
                line.Row.SetActive(false);
        for (var i = 0; i < order.Count; i++)
        {
            var kind = order[i];
            if (!_lines.TryGetValue(kind, out var line))
                _lines[kind] = line = new Line { Kind = kind };
            // In the armoury (2026-11-01) a rebuild failed halfway when a count changed: the row had its pictures but
            // no writing, and was taken as done. Each line is now checked whole, and a failed one is made again next time
            try
            {
                if (line.Template == null)
                    line.Template = BuildTemplate(sources[kind], kind);
                if (line.Row == null || line.Text == null || line.Count != counts[kind])
                    BuildRow(line, counts[kind]);
                line.Held = kind == held;
                line.Text.color = line.Held ? HeldColour : OtherColour;
                line.Row.transform.localPosition = new Vector3(0f, -i * LineGap, 0f);
                line.Row.SetActive(true);
            }
            catch (Exception e)
            {
                if (_failLogs++ < 10)
                    Plugin.Logger.LogWarning($"[Grenade] Line for {kind} failed, making it again next time: {e.GetType().Name}: " +
                                         $"{e.Message.Split('\n')[0]} (template {line.Template != null}, row {line.Row != null}, text {line.Text != null})");
                if (line.Row != null)
                    UnityEngine.Object.Destroy(line.Row);
                if (line.Template != null)
                    UnityEngine.Object.Destroy(line.Template);
                line.Row = null;
                line.Template = null;
                line.Text = null;
                line.Count = -1;
                _shown = "";
            }
        }
        if (_logs++ < 10)
            Plugin.Logger.LogInfo($"[Grenade] Holding {held}; grenades left: {summary}");
    }

    /// <summary>One copy of a grenade (ModelCopy), CopySize across. Inactive parts too: a grenade on the vest may have its
    /// parts switched off while it isn't drawn there.</summary>
    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private GameObject BuildTemplate(Transform source, string kind)
    {
        var template = ModelCopy.Build(source, $"{kind} copy", _root.transform, CopySize, true, out var parts, out var across);
        template.SetActive(false);
        Plugin.Logger.LogInfo($"[Grenade] Picture of {kind} from '{source.name}' ({parts} parts, {across * 100f:0.0} cm across, shown at {CopySize * 100f:0.0} cm)");
        return template;
    }

    /// <summary>One line: the copies, centred, with the number to their right.</summary>
    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private void BuildRow(Line line, int count)
    {
        if (line.Row != null)
            UnityEngine.Object.Destroy(line.Row);
        // Not done until the writing is on: the count is set last
        line.Count = -1;
        line.Text = null;
        line.Row = new GameObject($"{line.Kind} line");
        line.Row.transform.SetParent(_root.transform, false);
        var shown = Math.Min(count, MaxCopies);
        var width = (shown - 1) * CopyGap;
        for (var i = 0; i < shown; i++)
        {
            var c = UnityEngine.Object.Instantiate(line.Template, line.Row.transform);
            c.name = $"Grenade {i + 1}";
            c.transform.localPosition = new Vector3(-width / 2f + i * CopyGap, 0f, 0f);
            c.SetActive(true);
        }

        // The number, like the magazine label: a small world-space canvas drawn on top of everything, just right of
        // the row (left-aligned text, so its box starts there)
        var textGo = new GameObject("Count");
        textGo.transform.SetParent(line.Row.transform, false);
        var canvas = textGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = 32000;
        // Wide enough for the count and the name (left-aligned, so the box's left half reaches back to the row)
        const float boxWidth = 800f;
        textGo.GetComponent<RectTransform>().sizeDelta = new Vector2(boxWidth, 150f);
        textGo.transform.localScale = Vector3.one * 0.0002f;
        textGo.transform.localPosition = new Vector3(width / 2f + CopyGap * 0.6f + boxWidth * 0.0002f / 2f, 0f, 0f);
        var textChild = new GameObject("Text");
        textChild.transform.SetParent(textGo.transform, false);
        var text = textChild.AddComponent<TextMeshProUGUI>();
        textChild.GetComponent<RectTransform>().sizeDelta = new Vector2(boxWidth, 150f);
        if (_font == null)
            foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<TMP_Text>(), FindObjectsSortMode.None))
            {
                var existing = o.TryCast<TMP_Text>();
                if (existing != null && existing.font != null)
                {
                    _font = existing.font;
                    break;
                }
            }
        if (_font != null)
            text.font = _font;
        text.fontSize = 110f;
        text.alignment = TextAlignmentOptions.Left;
        text.outlineWidth = 0.2f;
        text.outlineColor = new Color32(0, 0, 0, 255);
        text.raycastTarget = false;
        // The flashbang and the ninebang look alike as pictures (2026-11-01), so each line says what it is
        text.text = $"{count} <size=65%>{DisplayName(line.Kind)}</size>";
        if (MenusOnTop.Layer >= 0)
            SetLayer(textGo.transform, MenusOnTop.Layer);
        line.Text = text;
        line.Count = count;
    }

    private void Hide()
    {
        if (_root != null && _root.activeSelf)
            _root.SetActive(false);
        // Shown again from scratch next time, so the held kind is on top
        _shown = "";
    }

    private static void SetLayer(Transform t, int layer)
    {
        t.gameObject.layer = layer;
        for (var i = 0; i < t.childCount; i++)
            SetLayer(t.GetChild(i), layer);
    }
}
