using System;
using UnityEngine;

namespace OperatorVR;

/// <summary>
/// Small stand-alone copies of things in the game (a grenade, a magazine) for the labels: the object's meshes and
/// materials as plain renderers, centred on the copy's origin and scaled so the largest side is a given size. Drawn on
/// the object's own layer, so the game camera lights them like the real thing.
/// </summary>
internal static class ModelCopy
{
    /// <param name="includeInactive">Also parts switched off (a grenade on the vest may not be drawn there). Off for
    /// a magazine, so only the rounds that are really in it show.</param>
    internal static GameObject Build(Transform source, string name, Transform parent, float size, bool includeInactive, out int parts, out float across) =>
        Build(source, name, parent, size, includeInactive, out parts, out across, out _);

    /// <param name="shape">The copy's size along its own x, y and z after scaling (its largest side is <paramref name="size"/>).</param>
    internal static GameObject Build(Transform source, string name, Transform parent, float size, bool includeInactive, out int parts, out float across, out Vector3 shape)
    {
        var copy = new GameObject(name);
        copy.transform.SetParent(parent, false);
        var inner = new GameObject("Model");
        inner.transform.SetParent(copy.transform, false);

        parts = 0;
        var layer = source.gameObject.layer;
        var bounds = new Bounds();
        var any = false;
        var toLocal = source.worldToLocalMatrix;
        foreach (var r in source.GetComponentsInChildren<Renderer>(includeInactive))
        {
            if (r == null || (!includeInactive && !r.enabled))
                continue;
            Mesh mesh = null;
            var mf = r.GetComponent<MeshFilter>();
            if (mf != null)
                mesh = mf.sharedMesh;
            var smr = r.TryCast<SkinnedMeshRenderer>();
            if (mesh == null && smr != null)
                mesh = smr.sharedMesh;
            // Hands and arms are skinned to a rig and bigger than the object; only its own parts
            if (mesh == null || (smr != null && smr.bones != null && smr.bones.Length > 1))
                continue;
            if (r.gameObject.activeInHierarchy)
                layer = r.gameObject.layer;
            var part = new GameObject(r.name);
            part.transform.SetParent(inner.transform, false);
            // The part's place relative to the object
            var m = toLocal * r.transform.localToWorldMatrix;
            SetFromMatrix(part.transform, m);
            part.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = part.AddComponent<MeshRenderer>();
            mr.sharedMaterials = r.sharedMaterials;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var b = TransformBounds(mesh.bounds, part.transform.localPosition, part.transform.localRotation, part.transform.localScale);
            if (any) bounds.Encapsulate(b); else { bounds = b; any = true; }
            parts++;
        }

        across = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
        var scale = across > 1e-5f ? size / across : 1f;
        inner.transform.localScale = Vector3.one * scale;
        inner.transform.localPosition = -bounds.center * scale;
        shape = bounds.size * scale;
        SetLayer(copy.transform, layer);
        return copy;
    }

    /// <summary>Position, rotation and scale from a matrix, keeping a mirror (negative determinant) as a negative x
    /// scale: with every scale positive a mirrored part came out inside out, so you saw through it.</summary>
    internal static void SetFromMatrix(Transform t, Matrix4x4 m)
    {
        var x = (Vector3)m.GetColumn(0);
        var y = (Vector3)m.GetColumn(1);
        var z = (Vector3)m.GetColumn(2);
        var mirrored = Vector3.Dot(Vector3.Cross(x, y), z) < 0f;
        t.localPosition = m.GetColumn(3);
        t.localRotation = Quaternion.LookRotation(z, y);
        t.localScale = new Vector3(mirrored ? -x.magnitude : x.magnitude, y.magnitude, z.magnitude);
    }

    private static Bounds TransformBounds(Bounds b, Vector3 pos, Quaternion rot, Vector3 scale)
    {
        var result = new Bounds(pos + rot * Vector3.Scale(b.center, scale), Vector3.zero);
        var e = b.extents;
        for (var i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
            result.Encapsulate(pos + rot * Vector3.Scale(b.center + corner, scale));
        }
        return result;
    }

    internal static void SetLayer(Transform t, int layer)
    {
        t.gameObject.layer = layer;
        for (var i = 0; i < t.childCount; i++)
            SetLayer(t.GetChild(i), layer);
    }
}
