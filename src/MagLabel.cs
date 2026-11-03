using System;
using Il2CppInterop.Runtime;
#if MELON
using Il2CppTMPro;
#else
using TMPro;
#endif
using UnityEngine;

namespace OperatorVR;

/// <summary>
/// The exact round count when checking a magazine. Adding it to the game's mag-check message didn't work: a normal
/// check never shows that message (the game expects you to judge it by eye). So while the check-mag key is held
/// (CheckMag = R held; Square held on the PS5 layout) for a moment, a small label floats just above the magazine in
/// your hand: "23 / 30 + 1", the + 1 when a round is chambered. Drawn on MenusOnTop's layer so nothing hides it, turned
/// to face you, and hidden a second and a half after the key is let go. Controls.ExactMagCount.
/// </summary>
public class MagLabel : MonoBehaviour
{
    public MagLabel(IntPtr ptr) : base(ptr) { }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    private const int VK_R = 0x52;

    private GameObject _go;
    private TextMeshProUGUI _text;
    private PlayerNetworking _me;
    private float _heldSince = -1f, _showUntil, _nextFind;
    private bool _logged;

    private void LateUpdate()
    {
        using var __t = ModTime.Measure("MagLabel");
        try
        {
            if (!OpenXRBootstrap.IsRunning || !Plugin.ExactMagCount.Value)
            {
                Hide();
                return;
            }
            var held = TouchKeys.GameHasFocus() && (GetAsyncKeyState(VK_R) & 0x8000) != 0;
            if (held)
            {
                if (_heldSince < 0f)
                    _heldSince = Time.unscaledTime;
                // A tap is a reload; the game's check starts on a hold
                if (Time.unscaledTime - _heldSince >= 0.35f)
                    _showUntil = Time.unscaledTime + 1.5f;
            }
            else
                _heldSince = -1f;
            if (Time.unscaledTime > _showUntil)
            {
                Hide();
                return;
            }
            Show();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Mag] label: {e.GetType().Name}: {e.Message}");
            Hide();
            _showUntil = 0f;
        }
    }

    private void Show()
    {
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
        var weapon = _me != null && _me.activeWeapon != null ? _me.activeWeapon.GetComponent<WeaponV3>() : null;
        var mag = weapon != null && weapon.gunFunction != null ? weapon.gunFunction.currentMagazine : null;
        var cam = Camera.main;
        if (mag == null || cam == null)
        {
            Hide();
            return;
        }
        if (_go == null)
            Create();
        if (_go == null)
            return;

        var count = $"{mag.ammoAmount}\n<size=60%>/ {mag.ammoAmountMax}{(weapon.roundInChamber ? "\n+ 1" : "")}</size>";
        if (_text.text != count)
            _text.text = count;
        // Just above the magazine, facing the eye
        var pos = mag.transform.position + Vector3.up * 0.10f;
        var t = _go.transform;
        t.position = pos;
        t.rotation = Quaternion.LookRotation(pos - cam.transform.position, Vector3.up);

        // The magazine itself, turning slowly where the drawing was (the user's idea, 2026-11-01, after the grenade
        // pictures). Made again when the rounds change, so the rounds you can see in it are the ones left. The drawing
        // with a line per round is the fallback for a magazine with nothing to copy
        var showModel = Plugin.MagModel.Value && UpdateModel(mag.transform, mag.ammoAmount);
        if (_outline != null && _outline.activeSelf == showModel)
            _outline.SetActive(!showModel);
        if (showModel)
        {
            var mt = _model.transform;
            mt.position = pos + t.right * -0.012f;
            mt.rotation = Quaternion.Euler(0f, Time.unscaledTime * 60f % 360f, 0f);
            if (!_model.activeSelf)
                _model.SetActive(true);
        }
        else
        {
            if (_model != null && _model.activeSelf)
                _model.SetActive(false);
            DrawRounds(mag.ammoAmount, mag.ammoAmountMax);
        }
        if (MenusOnTop.Layer >= 0 && _go.layer != MenusOnTop.Layer)
            SetLayer(t, MenusOnTop.Layer);
        if (!_go.activeSelf)
            _go.SetActive(true);
        if (!_logged)
        {
            _logged = true;
            Plugin.Logger.LogInfo($"[Mag] Magazine check: showing '{count}' above '{mag.name}'");
        }
    }

    // The copy of the magazine: its own object (not under the label's canvas, which is scaled for text), rebuilt when
    // the magazine or its round count changes
    private const float ModelSize = 0.07f;
    private GameObject _model, _outline;
    private IntPtr _modelOf;
    private int _modelAmmo = -1;
    private bool _modelOk;
    private int _modelLogs;

    /// <summary>Makes sure the copy matches this magazine and count; false if the magazine had nothing to copy.</summary>
    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private bool UpdateModel(Transform mag, int ammo)
    {
        if (_model != null && _modelOf == mag.Pointer && _modelAmmo == ammo)
            return _modelOk;
        if (_model != null)
            UnityEngine.Object.Destroy(_model);
        _modelOf = mag.Pointer;
        _modelAmmo = ammo;
        _model = ModelCopy.Build(mag, "OperatorVR Mag Copy", null, ModelSize, false, out var parts, out var across);
        UnityEngine.Object.DontDestroyOnLoad(_model);
        _model.SetActive(false);
        _modelOk = parts > 0;
        if (_modelLogs++ < 10)
            Plugin.Logger.LogInfo($"[Mag] Copy of '{mag.name}' with {ammo} rounds: {parts} parts, {across * 100f:0.0} cm across" +
                                  (_modelOk ? $", shown at {ModelSize * 100f:0} cm" : "; nothing to copy, the drawing is shown instead"));
        return _modelOk;
    }

    private void Create()
    {
        _go = new GameObject("OperatorVR Mag Count");
        UnityEngine.Object.DontDestroyOnLoad(_go);
        var canvas = _go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = 32000;
        var rt = _go.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(300f, 400f);
        // 400 units = 8 cm tall: a small magazine picture with the count beside it
        _go.transform.localScale = Vector3.one * 0.0002f;

        // The magazine: black outline, dark body, the rounds as lines (built in DrawRounds)
        var outline = NewBox("Outline", _go.transform, new Vector2(-60f, 0f), new Vector2(130f, 400f), Color.black);
        _outline = outline.gameObject;
        _magBody = NewBox("Body", outline, Vector2.zero, new Vector2(110f, 380f), new Color(0.12f, 0.12f, 0.12f)).GetComponent<RectTransform>();

        var textGo = new GameObject("Text");
        textGo.transform.SetParent(_go.transform, false);
        _text = textGo.AddComponent<TextMeshProUGUI>();
        var trt = textGo.GetComponent<RectTransform>();
        trt.sizeDelta = new Vector2(160f, 400f);
        trt.anchoredPosition = new Vector2(80f, 0f);
        // Borrow the game's own font (the build has no default TMP font of ours)
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<TMP_Text>(), FindObjectsSortMode.None))
        {
            var existing = o.TryCast<TMP_Text>();
            if (existing != null && existing.font != null)
            {
                _text.font = existing.font;
                break;
            }
        }
        _text.fontSize = 90f;
        _text.alignment = TextAlignmentOptions.Left;
        _text.color = new Color(1f, 0.85f, 0.2f);
        _text.outlineWidth = 0.2f;
        _text.outlineColor = new Color32(0, 0, 0, 255);
        _text.raycastTarget = false;
        _go.SetActive(false);
    }

    // ---- The magazine picture (the user asked for the label to look like a magazine, with a line per round) ----
    // One line per round, white while loaded and dark when spent, filling from the bottom; above 40 rounds (belts,
    // drums) each line stands for several rounds so the lines stay visible
    private const int MaxLines = 40;
    private RectTransform _magBody;
    private readonly System.Collections.Generic.List<UnityEngine.UI.Image> _lines = new();
    private int _linesFor = -1, _drawnAmmo = -1;
    private static readonly Color Loaded = Color.white, Spent = new(0.3f, 0.3f, 0.3f);

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private void DrawRounds(int ammo, int max)
    {
        if (_magBody == null || max <= 0)
            return;
        var lines = Math.Min(max, MaxLines);
        if (lines != _linesFor)
        {
            foreach (var l in _lines)
                if (l != null)
                    UnityEngine.Object.Destroy(l.gameObject);
            _lines.Clear();
            const float inner = 360f, gapShare = 0.35f;
            var pitch = inner / lines;
            var h = Math.Max(2f, pitch * (1f - gapShare));
            for (var i = 0; i < lines; i++)
            {
                // Bottom line first
                var y = -inner / 2f + pitch * (i + 0.5f);
                _lines.Add(NewBox($"Round {i + 1}", _magBody, new Vector2(0f, y), new Vector2(90f, h), Spent).GetComponent<UnityEngine.UI.Image>());
            }
            _linesFor = lines;
            _drawnAmmo = -1;
            // New lines start on the default layer: back onto the drawn-on-top one with the rest
            if (MenusOnTop.Layer >= 0)
                SetLayer(_go.transform, MenusOnTop.Layer);
        }
        if (ammo == _drawnAmmo)
            return;
        _drawnAmmo = ammo;
        // Lines that are loaded: rounded up, so a single round left still shows a line
        var full = ammo <= 0 ? 0 : (int)Math.Ceiling(ammo * (double)lines / max);
        for (var i = 0; i < _lines.Count; i++)
            if (_lines[i] != null)
                _lines[i].color = i < full ? Loaded : Spent;
    }

    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private static Transform NewBox(string name, Transform parent, Vector2 pos, Vector2 size, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<UnityEngine.UI.Image>();
        img.color = color;
        img.raycastTarget = false;
        var r = go.GetComponent<RectTransform>();
        r.sizeDelta = size;
        r.anchoredPosition = pos;
        return go.transform;
    }

    private void Hide()
    {
        if (_go != null && _go.activeSelf)
            _go.SetActive(false);
        if (_model != null && _model.activeSelf)
            _model.SetActive(false);
    }

    private static void SetLayer(Transform t, int layer)
    {
        t.gameObject.layer = layer;
        for (var i = 0; i < t.childCount; i++)
            SetLayer(t.GetChild(i), layer);
    }
}
