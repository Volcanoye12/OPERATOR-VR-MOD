using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;

namespace OperatorVR;

/// <summary>
/// Stand-in ground for a Terrain while VR runs: Unity can't draw Terrain in multi-pass XR, so the Terrain component
/// is switched off and this draws an ordinary mesh built from its heightmap instead. Its TerrainCollider stays, so
/// you still walk on the real ground.
///
/// The mesh is a GridSize x GridSize grid over the whole terrain, split into chunks (16-bit indices). Each quad
/// gets the terrain layer with the most weight there (from the terrain's splat map), one sub-mesh per layer, so
/// paths and grass show where the terrain puts them (with blocky borders, as layers aren't blended). Each
/// layer's material is a copy of a plain opaque HDRP/Lit material already in the scene, so the shader variants it
/// needs are in the build.
/// </summary>
internal static class TerrainMesh
{
    private const int GridSize = 1024;
    private const int ChunkQuads = 128;
    // The splat map holds 4 layers per texture; only the first texture is used
    private const int MaxLayers = 4;

    internal static GameObject Build(Terrain terrain)
    {
        var data = terrain.terrainData;
        if (data == null)
            return null;

        var size = data.size;
        var layers = data.terrainLayers;
        Material source = null;
        Material[] materials;
        int layerCount;
        Texture2D splat;

        // Preferred: the terrain's own material, which blends its layers smoothly per pixel like the real terrain
        var terrainMat = Plugin.GroundUsesTerrainMaterial.Value ? MakeTerrainMaterial(terrain, data, size) : null;
        if (terrainMat != null)
        {
            layerCount = 1;
            materials = new[] { terrainMat };
            splat = null;
        }
        else
        {
            source = FindSourceMaterial();
            layerCount = Math.Max(1, Math.Min(MaxLayers, layers == null ? 0 : layers.Length));
            materials = new Material[layerCount];
            for (var i = 0; i < layerCount; i++)
                materials[i] = MakeMaterial(source, layers != null && i < layers.Length ? layers[i] : null, size, i);
            splat = ReadSplat(data);
        }
        var root = new GameObject($"OperatorVR Ground ({terrain.name})");
        root.layer = terrain.gameObject.layer;
        root.transform.SetPositionAndRotation(terrain.transform.position, Quaternion.identity);

        var step = 1f / GridSize;
        var chunks = 0;
        var quadsPerLayer = new int[layerCount];
        var lod = Plugin.GroundLod.Value;
        // Outdoors ran at ~36 fps against 51-63 indoors (2026-11-01). The ground was 2 million triangles at 2 m spacing
        // out to 1 km, tessellated, and drawn again for shadows. Far chunks now switch to a coarse copy (8 m spacing),
        // and the ground casts no shadows (it still receives them)
        var shadows = Plugin.GroundShadows.Value ? ShadowCastingMode.On : ShadowCastingMode.Off;
        for (var cz = 0; cz < GridSize; cz += ChunkQuads)
            for (var cx = 0; cx < GridSize; cx += ChunkQuads)
            {
                var qx = Math.Min(ChunkQuads, GridSize - cx);
                var qz = Math.Min(ChunkQuads, GridSize - cz);
                var go = new GameObject($"Chunk {cx / ChunkQuads},{cz / ChunkQuads}");
                go.layer = root.layer;
                go.transform.SetParent(root.transform, false);
                var fine = AddChunkRenderer(go, "Fine", BuildChunk(data, size, splat, layerCount, cx, cz, qx, qz, 1, step, quadsPerLayer),
                    materials, shadows);
                if (lod)
                {
                    MeshRenderer coarse = null;
                    try
                    {
                        coarse = AddChunkRenderer(go, "Coarse", BuildChunk(data, size, splat, layerCount, cx, cz, qx, qz, CoarseStep, step, null),
                            materials, shadows);
                        var group = go.AddComponent<LODGroup>();
                        var lods = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<LOD>(2);
                        lods[0] = new LOD(FineUntil, new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Renderer>(new Renderer[] { fine }));
                        lods[1] = new LOD(0f, new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Renderer>(new Renderer[] { coarse }));
                        group.SetLODs(lods);
                        group.RecalculateBounds();
                    }
                    catch (Exception e)
                    {
                        // Keep the fine ground rather than lose the chunk
                        Plugin.Logger.LogWarning($"[Terrain] Far-detail switch failed ({e.GetType().Name}: {e.Message}); full detail everywhere");
                        if (coarse != null)
                            UnityEngine.Object.Destroy(coarse.gameObject);
                        var g = go.GetComponent<LODGroup>();
                        if (g != null)
                            UnityEngine.Object.Destroy(g);
                        lod = false;
                    }
                }
                chunks++;
            }
        var tess = Plugin.GroundTessellation.Value || terrainMat == null ? "" : LowerTessellation(terrainMat);

        var materialDesc = terrainMat != null ? $"the terrain's own material ({terrainMat.shader.name})" : $"layers on '{(source == null ? "new HDRP/Lit" : source.name)}'";
        Plugin.Logger.LogInfo($"[Terrain] Built stand-in ground for '{terrain.name}': {size.x:0}x{size.z:0} m, {GridSize}x{GridSize} grid " +
                              $"({size.x / GridSize:0.0} m spacing), {chunks} chunks, {materialDesc}, " +
                              $"splat map {(splat == null ? "unavailable (first layer everywhere)" : $"{splat.width}x{splat.height}")}, " +
                              $"quads per layer: {string.Join(", ", quadsPerLayer)}; " +
                              $"far chunks coarse ({size.x / GridSize * CoarseStep:0} m spacing): {lod}, shadows cast: {shadows == ShadowCastingMode.On}{tess}");
        return root;
    }

    // Coarse copies use every 4th grid point; a chunk shows its fine mesh while it's at least this tall on screen
    // (a 250 m chunk: within roughly 250 m, less with a lower LOD bias)
    private const int CoarseStep = 4;
    private const float FineUntil = 0.4f;

    private static MeshRenderer AddChunkRenderer(GameObject chunk, string name, Mesh mesh, Material[] materials, ShadowCastingMode shadows)
    {
        var go = new GameObject(name);
        go.layer = chunk.layer;
        go.transform.SetParent(chunk.transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterials = materials;
        mr.shadowCastingMode = shadows;
        mr.receiveShadows = true;
        return mr;
    }

    /// <summary>
    /// The terrain's material is InTerra's tessellation shader, which subdivides every triangle on the GPU. Its
    /// tessellation factors go to their minimum (1 = no extra triangles); returns a note for the log.
    /// </summary>
    private static string LowerTessellation(Material mat)
    {
        var changed = new List<string>();
        try
        {
            var shader = mat.shader;
            for (var i = 0; i < shader.GetPropertyCount(); i++)
            {
                var name = shader.GetPropertyName(i);
                var type = shader.GetPropertyType(i);
                if (!name.Contains("Tess") || (type != ShaderPropertyType.Float && type != ShaderPropertyType.Range))
                    continue;
                var before = mat.GetFloat(name);
                // Factors only; distances, displacement and the like stay
                if (!name.Contains("Factor") || name.Contains("Distance"))
                {
                    changed.Add($"{name}={before:0.##} (kept)");
                    continue;
                }
                mat.SetFloat(name, 1f);
                changed.Add($"{name} {before:0.##} -> 1");
            }
        }
        catch (Exception e)
        {
            return $"; tessellation not changed ({e.Message})";
        }
        return changed.Count == 0 ? "; no tessellation settings found" : $"; tessellation: {string.Join(", ", changed)}";
    }

    /// <param name="every">Grid points between vertices: 1 for the fine mesh, CoarseStep for the far copy.</param>
    /// <param name="quadsPerLayer">Counted for the log (fine mesh only); null to skip.</param>
    private static Mesh BuildChunk(TerrainData data, Vector3 size, Texture2D splat, int layerCount,
                                   int cx, int cz, int qx, int qz, int every, float step, int[] quadsPerLayer)
    {
        qx /= every;
        qz /= every;
        step *= every;
        cx /= every;
        cz /= every;
        var w = qx + 1;
        var h = qz + 1;
        var verts = new Vector3[w * h];
        var normals = new Vector3[w * h];
        var uvs = new Vector2[w * h];
        for (var z = 0; z < h; z++)
            for (var x = 0; x < w; x++)
            {
                var u = (cx + x) * step;
                var v = (cz + z) * step;
                var i = z * w + x;
                verts[i] = new Vector3(u * size.x, data.GetInterpolatedHeight(u, v), v * size.z);
                normals[i] = data.GetInterpolatedNormal(u, v);
                uvs[i] = new Vector2(u, v);
            }

        var perLayer = new List<int>[layerCount];
        for (var l = 0; l < layerCount; l++)
            perLayer[l] = new List<int>();
        for (var z = 0; z < qz; z++)
            for (var x = 0; x < qx; x++)
            {
                var layer = splat == null ? 0 : DominantLayer(splat, (cx + x + 0.5f) * step, (cz + z + 0.5f) * step, layerCount);
                if (quadsPerLayer != null)
                    quadsPerLayer[layer]++;
                var i = z * w + x;
                var tris = perLayer[layer];
                tris.Add(i);
                tris.Add(i + w);
                tris.Add(i + 1);
                tris.Add(i + 1);
                tris.Add(i + w);
                tris.Add(i + w + 1);
            }

        var mesh = new Mesh();
        mesh.vertices = verts;
        mesh.normals = normals;
        mesh.uv = uvs;
        mesh.subMeshCount = layerCount;
        for (var l = 0; l < layerCount; l++)
            mesh.SetTriangles(perLayer[l].ToArray(), l);
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        return mesh;
    }

    private static int DominantLayer(Texture2D splat, float u, float v, int layerCount)
    {
        var c = splat.GetPixelBilinear(u, v);
        var best = 0;
        var bestWeight = c.r;
        if (layerCount > 1 && c.g > bestWeight) { best = 1; bestWeight = c.g; }
        if (layerCount > 2 && c.b > bestWeight) { best = 2; bestWeight = c.b; }
        if (layerCount > 3 && c.a > bestWeight) best = 3;
        return best;
    }

    /// <summary>
    /// CPU-readable copy of the terrain's first splat (alphamap) texture: RGBA = weights of layers 0-3.
    /// Copied through a linear render texture, as it may not be readable and its values mustn't be gamma-converted.
    /// </summary>
    private static Texture2D ReadSplat(TerrainData data)
    {
        try
        {
            var maps = data.alphamapTextures;
            if (maps == null || maps.Length == 0 || maps[0] == null)
                return null;
            var src = maps[0];
            var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                var copy = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false, true);
                copy.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
                copy.Apply();
                return copy;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[Terrain] Couldn't read the splat map: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// A plain opaque HDRP/Lit material from the scene to copy: standard material type (not skin/SSS), opaque,
    /// with a normal map (so that variant exists). Picked by name so it's the same one every time.
    /// </summary>
    private static Material FindSourceMaterial()
    {
        Material best = null;
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<MeshRenderer>(), FindObjectsSortMode.None))
        {
            var r = o.TryCast<MeshRenderer>();
            var m = r == null ? null : r.sharedMaterial;
            if (m == null || m.shader == null || m.shader.name != "HDRP/Lit" || m.renderQueue >= 2500)
                continue;
            if (m.HasProperty("_MaterialID") && Mathf.RoundToInt(m.GetFloat("_MaterialID")) != 1)
                continue; // 1 = Standard; others are skin, hair, translucent...
            if (m.HasProperty("_SurfaceType") && m.GetFloat("_SurfaceType") != 0f)
                continue;
            if (m.IsKeywordEnabled("_EMISSIVE_COLOR_MAP") || !m.HasProperty("_NormalMap") || m.GetTexture("_NormalMap") == null)
                continue;
            if (best == null || string.CompareOrdinal(m.name, best.name) < 0)
                best = m;
        }
        return best;
    }

    /// <summary>
    /// A copy of the terrain's own material (HDRP/TerrainLit) set up the way the Terrain sets it: splat (control) map,
    /// and per layer its diffuse, normal and mask maps and tiling. The mesh's UVs run 0..1 over the terrain like the
    /// terrain's own, so the control map and tiling line up. Null if there's no usable terrain material.
    /// </summary>
    private static Material MakeTerrainMaterial(Terrain terrain, TerrainData data, Vector3 size)
    {
        try
        {
            var template = terrain.materialTemplate;
            if (template == null || template.shader == null || !template.shader.name.Contains("Terrain") || !template.shader.isSupported)
            {
                Plugin.Logger.LogInfo($"[Terrain] No usable terrain material ({(template == null ? "none" : template.shader?.name)}); using plain layers");
                return null;
            }
            var maps = data.alphamapTextures;
            var layers = data.terrainLayers;
            if (maps == null || maps.Length == 0 || layers == null || layers.Length == 0)
                return null;

            var mat = new Material(template) { name = "OperatorVR Ground (terrain material)" };
            // Drawn as a plain mesh, not by the Terrain's instancing path
            mat.DisableKeyword("_TERRAIN_INSTANCED_PERPIXEL_NORMAL");
            mat.SetTexture("_Control0", maps[0]);
            if (maps.Length > 1)
                mat.SetTexture("_Control1", maps[1]);

            var anyMask = false;
            var anyNormal = false;
            for (var i = 0; i < Math.Min(8, layers.Length); i++)
            {
                var l = layers[i];
                if (l == null)
                    continue;
                var tile = l.tileSize;
                mat.SetTexture($"_Splat{i}", l.diffuseTexture);
                mat.SetVector($"_Splat{i}_ST", new Vector4(size.x / Math.Max(0.01f, tile.x), size.z / Math.Max(0.01f, tile.y),
                                                           l.tileOffset.x / Math.Max(0.01f, tile.x), l.tileOffset.y / Math.Max(0.01f, tile.y)));
                if (l.normalMapTexture != null)
                {
                    mat.SetTexture($"_Normal{i}", l.normalMapTexture);
                    mat.SetFloat($"_NormalScale{i}", l.normalScale);
                    anyNormal = true;
                }
                if (l.maskMapTexture != null)
                {
                    mat.SetTexture($"_Mask{i}", l.maskMapTexture);
                    anyMask = true;
                }
                mat.SetFloat($"_LayerHasMask{i}", l.maskMapTexture != null ? 1f : 0f);
                mat.SetFloat($"_Metallic{i}", l.metallic);
                mat.SetFloat($"_Smoothness{i}", l.smoothness);
                mat.SetVector($"_DiffuseRemapScale{i}", l.diffuseRemapMax - l.diffuseRemapMin);
                mat.SetVector($"_MaskMapRemapOffset{i}", l.maskMapRemapMin);
                mat.SetVector($"_MaskMapRemapScale{i}", l.maskMapRemapMax - l.maskMapRemapMin);
            }
            if (anyNormal) mat.EnableKeyword("_NORMALMAP"); else mat.DisableKeyword("_NORMALMAP");
            if (anyMask) mat.EnableKeyword("_MASKMAP"); else mat.DisableKeyword("_MASKMAP");
            if (layers.Length > 4) mat.EnableKeyword("_TERRAIN_8_LAYERS");

            Plugin.Logger.LogInfo($"[Terrain] Using the terrain's own material '{template.name}' ({template.shader.name}), {layers.Length} layers, " +
                                  $"normal maps={anyNormal}, mask maps={anyMask}");
            return mat;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[Terrain] Terrain material failed ({e.GetType().Name}: {e.Message}); using plain layers");
            return null;
        }
    }

    private static void SetIfPresent(Material m, string name, float value)
    {
        if (m.HasProperty(name))
            m.SetFloat(name, value);
    }

    /// <summary>Copy of the source material with one terrain layer on it, tiled like the terrain tiles it.</summary>
    private static Material MakeMaterial(Material source, TerrainLayer layer, Vector3 size, int index)
    {
        Material mat;
        if (source != null)
        {
            mat = new Material(source);
        }
        else
        {
            var shader = Shader.Find("HDRP/Lit");
            if (shader == null)
                return null;
            mat = new Material(shader);
        }
        mat.name = $"OperatorVR Ground layer {index}";

        if (layer != null && layer.diffuseTexture != null)
        {
            mat.SetTexture("_BaseColorMap", layer.diffuseTexture);
            var tile = layer.tileSize;
            var scale = new Vector2(size.x / Math.Max(0.01f, tile.x), size.z / Math.Max(0.01f, tile.y));
            mat.SetTextureScale("_BaseColorMap", scale);
            mat.SetTextureOffset("_BaseColorMap", layer.tileOffset);
            if (mat.HasProperty("_NormalMap"))
                mat.SetTexture("_NormalMap", layer.normalMapTexture);
            Plugin.Logger.LogInfo($"[Terrain] Layer {index}: '{layer.diffuseTexture.name}' tiled every {tile.x:0.#} m");
        }
        mat.SetColor("_BaseColor", Color.white);
        // A borrowed mask map setting made the ground glitter (no map + keyword on = very smooth): use plain matte values
        if (mat.HasProperty("_MaskMap"))
            mat.SetTexture("_MaskMap", null);
        mat.DisableKeyword("_MASKMAP");
        SetIfPresent(mat, "_Metallic", 0f);
        SetIfPresent(mat, "_Smoothness", 0.1f);
        SetIfPresent(mat, "_SmoothnessRemapMin", 0f);
        SetIfPresent(mat, "_SmoothnessRemapMax", 0.1f);
        SetIfPresent(mat, "_MetallicRemapMax", 0f);
        SetIfPresent(mat, "_NormalScale", 0.6f);
        foreach (var tex in new[] { "_DetailMap", "_EmissiveColorMap", "_CoatMaskMap", "_BentNormalMap", "_HeightMap" })
            if (mat.HasProperty(tex))
                mat.SetTexture(tex, null);
        mat.DisableKeyword("_DETAIL_MAP");
        mat.DisableKeyword("_EMISSIVE_COLOR_MAP");
        if (mat.HasProperty("_EmissiveColor"))
            mat.SetColor("_EmissiveColor", Color.black);
        return mat;
    }
}
