using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace OperatorVR;

/// <summary>
/// Unity can't draw Terrain in multi-pass XR. In 'Georgia 1 Day' it logs "The current XR provider does not
/// support rendering Terrain under the XR multipass rendering mode", and while a Terrain is drawn the right eye's
/// output never reaches its images (it kept showing the loading screen). Only switching the Terrain component off
/// fixes it; turning off its draw flags doesn't.
///
/// While VR runs: each Terrain component is switched off (its TerrainCollider stays, so you still walk on it) and a
/// stand-in ground mesh is drawn instead (TerrainMesh). F6 cycles modes for comparison, saving eye images 2 s later:
///   0 = Terrain off + stand-in ground (default)
///   1 = Terrain off, no ground
///   2 = Terrain shown as normal (right eye breaks)
/// Everything is put back when VR stops.
/// </summary>
public class VRTerrain : MonoBehaviour
{
    public VRTerrain(IntPtr ptr) : base(ptr) { }

    private const int VK_F6 = 0x75;
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static readonly string[] ModeNames = { "Terrain off + stand-in ground", "Terrain off, no ground", "Terrain shown (right eye breaks)" };

    private class Saved
    {
        public Terrain Terrain;
        public bool Enabled, DrawHeightmap;
        public GameObject Ground;
    }

    private readonly Dictionary<IntPtr, Saved> _saved = new();
    private int _mode;
    private bool _f6Down;
    private bool _wasRunning;
    private float _nextScan;

    private void Update()
    {
        using var __t = ModTime.Measure("VRTerrain");
        var running = OpenXRBootstrap.IsRunning;
        if (!running)
        {
            if (_wasRunning)
                RestoreAll();
            _wasRunning = false;
            return;
        }
        _wasRunning = true;

        // F6 now belongs to VRFoliage; terrain modes only change through Rendering config if ever needed
        var down = false;
        if (down && !_f6Down)
        {
            _mode = (_mode + 1) % ModeNames.Length;
            RestoreAll();
            _nextScan = 0f;
            Plugin.Logger.LogInfo($"[Terrain] F6: mode {_mode}, {ModeNames[_mode]}");
            EyeCapture.Request($"Terrain-{_mode}", 2f);
        }
        _f6Down = down;

        // Levels load terrains later, so keep checking
        if (Time.unscaledTime < _nextScan)
            return;
        _nextScan = Time.unscaledTime + 1f;

        if (_mode == 2)
            return;
        try
        {
            foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<Terrain>(), FindObjectsSortMode.None))
            {
                var t = o.TryCast<Terrain>();
                if (t == null || !t.enabled || _saved.ContainsKey(t.Pointer))
                    continue;
                var saved = new Saved { Terrain = t, Enabled = t.enabled, DrawHeightmap = t.drawHeightmap };
                _saved[t.Pointer] = saved;
                // Experimental (Rendering.TerrainGrass): the outdoor ground looked plain without its grass and small
                // details, which the Terrain draws. Keep the Terrain on with only its heightmap (the surface, the part
                // that broke the right eye) switched off, so it still draws grass and details on the stand-in ground
                if (_mode == 0 && Plugin.TerrainGrass.Value)
                {
                    t.drawHeightmap = false;
                    var details = 0;
                    var trees = 0;
                    try { details = t.terrainData.detailPrototypes.Length; trees = t.terrainData.treeInstanceCount; } catch { }
                    Plugin.Logger.LogInfo($"[Terrain] '{t.name}': surface off, grass and details kept (experimental; {details} grass/detail " +
                                          $"kinds, {trees} terrain trees, details drawn {t.drawTreesAndFoliage}), stand-in ground for the surface");
                }
                else
                {
                    t.enabled = false;
                    Plugin.Logger.LogInfo($"[Terrain] Switched off '{t.name}' ({ModeNames[_mode]}): Unity can't draw Terrain in multi-pass VR");
                }
                if (_mode == 0)
                {
                    try
                    {
                        saved.Ground = TerrainMesh.Build(t);
                    }
                    catch (Exception e)
                    {
                        Plugin.Logger.LogError($"[Terrain] Building stand-in ground failed: {e.GetType().Name}: {e.Message}");
                    }
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Terrain] {e.GetType().Name}: {e.Message}");
            _nextScan = Time.unscaledTime + 10f;
        }
    }

    private void RestoreAll()
    {
        foreach (var s in _saved.Values)
        {
            try
            {
                if (s.Ground != null)
                    UnityEngine.Object.Destroy(s.Ground);
                if (s.Terrain != null)
                {
                    s.Terrain.drawHeightmap = s.DrawHeightmap;
                    s.Terrain.enabled = s.Enabled;
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"[Terrain] Could not restore a terrain: {e.Message}");
            }
        }
        if (_saved.Count > 0)
            Plugin.Logger.LogInfo($"[Terrain] Restored {_saved.Count} terrains");
        _saved.Clear();
    }
}
