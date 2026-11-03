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
/// A small always-on display while playing (the user's request, 2026-11-02), fixed in view like the grenade count:
///   - lower left (Menus.StatusHud): the weapon in hand and its fire mode, health as a percentage of full, and red
///     warnings for bleeding and broken limbs (from the player's Health: TotalHP/initTotalHP, BleedingDamage_*,
///     Fractured_*);
///   - along the top (Menus.Compass): a compass strip for where you look, with N/NE/E... and bearings every 15°,
///     and a marker in the middle. North is the level's +Z.
/// Shown only in play (the game has the mouse locked), on MenusOnTop's layer so nothing hides it.
/// </summary>
public class StatusHud : MonoBehaviour
{
    public StatusHud(IntPtr ptr) : base(ptr) { }

    // Where they sit from the eye (metres: right, up, ahead) at ReferenceDistance; Menus.HudDistance moves them along
    // the same lines
    private const float ReferenceDistance = 0.5f;
    private static readonly Vector3 StatusAt = new(-0.15f, -0.16f, 0.5f);
    private static readonly Vector3 CompassAt = new(0f, 0.17f, 0.5f);
    // Canvas units per metre, and the compass's width (metres) and field (degrees each side)
    private const float UnitsPerMetre = 5000f, CompassWidth = 0.24f, CompassSpan = 60f;

    private PlayerNetworking _me;
    private float _nextFind, _nextText;
    private GameObject _root, _status, _compass;
    private TextMeshProUGUI _statusText;
    private readonly List<(TextMeshProUGUI text, float bearing)> _marks = new();
    private TMP_FontAsset _font;
    private bool _talkShown;

    private void LateUpdate()
    {
        using var __t = ModTime.Measure("StatusHud");
        try
        {
            var cam = Camera.main;
            var show = OpenXRBootstrap.IsRunning && (Plugin.StatusHud.Value || Plugin.Compass.Value) && cam != null &&
                       Cursor.lockState == CursorLockMode.Locked && !Cursor.visible;
            if (show)
                Find();
            if (!show || _me == null)
            {
                if (_root != null && _root.activeSelf)
                    _root.SetActive(false);
                return;
            }
            if (_root == null)
                Create();
            var t = _root.transform;
            if (t.parent == null || t.parent.Pointer != cam.transform.Pointer)
                t.SetParent(cam.transform, false);
            t.localPosition = Vector3.zero;
            t.localRotation = Quaternion.identity;
            if (!_root.activeSelf)
                _root.SetActive(true);

            _status.SetActive(Plugin.StatusHud.Value);
            _compass.SetActive(Plugin.Compass.Value);
            // Distance and size (Menus.HudDistance/HudSize; the user found the first version a bit big and close,
            // 2026-11-02). Same place in view at any distance; HudSize scales how big it looks
            var k = Mathf.Clamp(Plugin.HudDistance.Value, 0.35f, 3f) / ReferenceDistance;
            var look = Mathf.Clamp(Plugin.HudSize.Value, 0.3f, 3f);
            Place(_status, StatusAt, k, look);
            Place(_compass, CompassAt, k, look);
            // Quicker while talking starts or stops, so the indicator follows the button
            if (Plugin.StatusHud.Value && (Time.unscaledTime >= _nextText || AimLean.Talking != _talkShown))
            {
                _nextText = Time.unscaledTime + 0.25f;
                _talkShown = AimLean.Talking;
                var text = StatusText();
                if (_statusText.text != text)
                    _statusText.text = text;
            }
            if (Plugin.StatusHud.Value)
                UpdateWeaponPicture();
            if (Plugin.Compass.Value)
                UpdateCompass(cam);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Hud] {e.GetType().Name}: {e.Message}");
            _nextFind = Time.unscaledTime + 5f;
            _me = null;
        }
    }

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private static void Place(GameObject go, Vector3 at, float k, float look)
    {
        go.transform.localPosition = at * k;
        go.transform.localScale = Vector3.one * (k * look / UnitsPerMetre);
    }

    private void Find()
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

    private string StatusText()
    {
        var lines = new List<string>();
        // Push-to-talk held (Menus.TalkIndicator): the user couldn't tell whether the microphone was on
        if (Plugin.TalkIndicator.Value && AimLean.Talking)
            lines.Add("<color=#FF5050>● TALKING</color>");
        var w = _me.activeWeapon != null ? _me.activeWeapon.GetComponent<WeaponV3>() : null;
        if (w != null)
        {
            var name = string.IsNullOrEmpty(w.displayName) ? w.name.Replace("(Clone)", "").Trim() : w.displayName;
            var mode = "";
            try { if (w.gunStats != null) mode = $"  <color=#FFD933>{w.gunStats.fireMode.ToString().ToUpperInvariant()}</color>"; } catch { }
            lines.Add(name + mode);
        }
        var h = _me.health;
        if (h != null)
        {
            var pct = h.initTotalHP > 0f ? Mathf.Clamp(h.TotalHP / h.initTotalHP * 100f, 0f, 100f) : 0f;
            var colour = pct > 60f ? "#7CFC7C" : pct > 30f ? "#FFD933" : "#FF5050";
            var health = $"<color={colour}>HEALTH {pct:0}%</color>";
            var bleeding = h.BleedingDamage_Head + h.BleedingDamage_Chest + h.BleedingDamage_Stomach + h.BleedingDamage_LArm +
                           h.BleedingDamage_RArm + h.BleedingDamage_LLeg + h.BleedingDamage_RLeg > 0f;
            if (bleeding)
                health += "  <color=#FF5050>BLEEDING</color>";
            var broken = new List<string>();
            if (h.Fractured_LArm) broken.Add("L ARM");
            if (h.Fractured_RArm) broken.Add("R ARM");
            if (h.Fractured_LLeg) broken.Add("L LEG");
            if (h.Fractured_RLeg) broken.Add("R LEG");
            if (broken.Count > 0)
                health += $"  <color=#FF5050>BROKEN {string.Join(", ", broken)}</color>";
            lines.Add(health);
        }
        return string.Join("\n", lines);
    }

    // ---- Weapon picture (Menus.WeaponPicture) ----
    // A small copy of the weapon in hand, side on, left of its name (the user's request, 2026-11-03), made like the
    // grenade copies (ModelCopy) on the weapon's own layer so the room lights it. Made again when the weapon in hand
    // changes, and every few seconds if its parts change (a sight or magazine swapped). The text moves right to make room
    private const float PictureLength = 260f;                 // canvas units (1 unit = 0.2 mm at HudDistance 0.5, HudSize 1)
    private const float PictureLeft = -500f + 20f;            // the text box's left edge, plus a little
    private const float PictureY = 8f;                        // level with the weapon line (second from the bottom)
    private GameObject _weaponCopy;
    private IntPtr _weaponFor;
    private int _weaponParts;
    private float _nextWeaponCheck;
    private int _weaponLogs;

    private void UpdateWeaponPicture()
    {
        var want = Plugin.WeaponPicture.Value && _me.activeWeapon != null && _me.activeWeapon.gameObject.activeInHierarchy;
        if (!want)
        {
            DropWeaponCopy();
            return;
        }
        var source = _me.activeWeapon.transform;
        var changed = source.Pointer != _weaponFor;
        if (!changed && Time.unscaledTime >= _nextWeaponCheck)
        {
            _nextWeaponCheck = Time.unscaledTime + 3f;
            // Gone with a level change, or the parts changed
            changed = _weaponCopy == null || CountParts(source) != _weaponParts;
        }
        if (changed)
            MakeWeaponCopy(source);
    }

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private static int CountParts(Transform source)
    {
        var n = 0;
        foreach (var r in source.GetComponentsInChildren<Renderer>(false))
            if (r != null && r.enabled)
                n++;
        return n;
    }

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private void MakeWeaponCopy(Transform source)
    {
        DropWeaponCopy();
        _weaponFor = source.Pointer;
        _weaponParts = CountParts(source);
        _nextWeaponCheck = Time.unscaledTime + 3f;
        var copy = ModelCopy.Build(source, "Weapon picture", _status.transform, PictureLength, false, out var parts, out _, out var shape);
        if (parts == 0)
        {
            UnityEngine.Object.Destroy(copy);
            return;
        }
        // Side on: the longest side (the barrel) across, left to right. Tipped a little towards you so it reads as solid
        var side = shape.z >= shape.x && shape.z >= shape.y ? Quaternion.Euler(0f, 90f, 0f)
                 : shape.y >= shape.x ? Quaternion.Euler(0f, 0f, 90f)
                 : Quaternion.identity;
        copy.transform.localRotation = Quaternion.Euler(-12f, 0f, 0f) * side;
        copy.transform.localPosition = new Vector3(PictureLeft + PictureLength / 2f, PictureY, 0f);
        _weaponCopy = copy;
        _statusText.margin = new Vector4(PictureLength + 50f, 0f, 0f, 0f);
        if (_weaponLogs++ < 10)
            Plugin.Logger.LogInfo($"[Hud] Weapon picture of '{source.name}': {parts} parts, shape ({shape.x:0}, {shape.y:0}, {shape.z:0}) units");
    }

    private void DropWeaponCopy()
    {
        if (_weaponCopy != null)
            UnityEngine.Object.Destroy(_weaponCopy);
        _weaponCopy = null;
        _weaponFor = IntPtr.Zero;
        if (_statusText != null && _statusText.margin.x != 0f)
            _statusText.margin = Vector4.zero;
    }

    private void UpdateCompass(Camera cam)
    {
        // Where the head looks (the camera only carries the head pose while the eyes are drawn)
        var yaw = cam.transform.eulerAngles.y;
        if (HeadTracking.TryApplyHeadPoseToGameCamera(out var savedPos, out var savedRot))
        {
            yaw = cam.transform.eulerAngles.y;
            HeadTracking.RestoreGameCameraPose(savedPos, savedRot);
        }
        foreach (var (text, bearing) in _marks)
        {
            var delta = Mathf.DeltaAngle(yaw, bearing);
            var on = Mathf.Abs(delta) <= CompassSpan;
            if (text.gameObject.activeSelf != on)
                text.gameObject.SetActive(on);
            if (!on)
                continue;
            var rt = text.rectTransform;
            rt.anchoredPosition = new Vector2(delta / CompassSpan * CompassWidth / 2f * UnitsPerMetre, 0f);
            // Fade towards the ends of the strip
            var c = text.color;
            c.a = 1f - Mathf.Clamp01((Mathf.Abs(delta) - CompassSpan * 0.6f) / (CompassSpan * 0.4f));
            text.color = c;
        }
    }

    private void Create()
    {
        _root = new GameObject("OperatorVR Status HUD");
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<TMP_Text>(), FindObjectsSortMode.None))
        {
            var existing = o.TryCast<TMP_Text>();
            if (existing != null && existing.font != null)
            {
                _font = existing.font;
                break;
            }
        }

        _status = NewCanvas("Status", StatusAt, new Vector2(1000f, 200f));
        // 1400 wide, its left edge where the first version's 1000 was (-500): room for the weapon picture's margin
        _statusText = NewText(_status.transform, "Text", new Vector2(200f, 0f), new Vector2(1400f, 200f), 60f, TextAlignmentOptions.BottomLeft);

        _compass = NewCanvas("Compass", CompassAt, new Vector2(CompassWidth * UnitsPerMetre + 200f, 160f));
        var names = new Dictionary<int, string> { [0] = "N", [45] = "NE", [90] = "E", [135] = "SE", [180] = "S", [225] = "SW", [270] = "W", [315] = "NW" };
        for (var b = 0; b < 360; b += 15)
        {
            var main = names.TryGetValue(b, out var n);
            var mark = NewText(_compass.transform, $"Mark {b}", Vector2.zero, new Vector2(160f, 100f), main ? 64f : 40f, TextAlignmentOptions.Center);
            mark.text = main ? n : b.ToString("000");
            mark.color = b == 0 ? new Color(1f, 0.4f, 0.3f) : main ? Color.white : new Color(0.85f, 0.85f, 0.85f);
            _marks.Add((mark, b));
        }
        var pointer = NewText(_compass.transform, "Pointer", new Vector2(0f, -70f), new Vector2(100f, 60f), 50f, TextAlignmentOptions.Center);
        pointer.text = "^";
        pointer.color = new Color(1f, 0.85f, 0.2f);

        if (MenusOnTop.Layer >= 0)
            SetLayer(_root.transform, MenusOnTop.Layer);
        Plugin.Logger.LogInfo("[Hud] Status display and compass made");
    }

    private GameObject NewCanvas(string name, Vector3 at, Vector2 size)
    {
        var go = new GameObject(name);
        go.transform.SetParent(_root.transform, false);
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = 32000;
        go.GetComponent<RectTransform>().sizeDelta = size;
        go.transform.localPosition = at;
        go.transform.localScale = Vector3.one / UnitsPerMetre;
        return go;
    }

    private TextMeshProUGUI NewText(Transform parent, string name, Vector2 pos, Vector2 size, float fontSize, TextAlignmentOptions align)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var text = go.AddComponent<TextMeshProUGUI>();
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = size;
        rt.anchoredPosition = pos;
        if (_font != null)
            text.font = _font;
        text.fontSize = fontSize;
        text.alignment = align;
        text.color = Color.white;
        text.outlineWidth = 0.2f;
        text.outlineColor = new Color32(0, 0, 0, 255);
        text.raycastTarget = false;
        text.richText = true;
        return text;
    }

    private static void SetLayer(Transform t, int layer)
    {
        t.gameObject.layer = layer;
        for (var i = 0; i < t.childCount; i++)
            SetLayer(t.GetChild(i), layer);
    }
}
