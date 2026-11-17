using System;
using System.Collections.Generic;
using Godot;
using World.Noise;

namespace World;

/// <summary>
/// Port of src/world/vegetationGeometry.js plus the trunk builders from chunkManager.js.
/// Every shape is seeded, so the trees, rocks and grass match the web version.
/// </summary>
public static class Vegetation
{
    /// <summary>
    /// 24 curved, tapered blades. UV.y runs 0 (root) to 1 (tip); UV2 holds each vertex's
    /// sideways half-width vector so the shader can widen thinned-out distant blades.
    /// </summary>
    public static MeshBuilder GrassBlades()
    {
        var rand = Rng.Mulberry32(202);
        var m = new MeshBuilder();
        for (int blade = 0; blade < 24; blade++)
        {
            float angle = (float)rand() * Mathf.Tau;
            float radius = Mathf.Sqrt((float)rand()) * 0.47f;
            float x = Mathf.Cos(angle) * radius, z = Mathf.Sin(angle) * radius;
            float dir = (float)rand() * Mathf.Tau;
            float width = 0.009f + (float)rand() * 0.012f;
            float height = 0.18f + (float)rand() * 0.35f;
            float lean = 0.08f + (float)rand() * 0.2f;
            int start = m.VertexCount;
            for (int row = 0; row < 4; row++)
            {
                float t = row / 3f;
                float half = width * (1 - t);
                foreach (int side in new[] { -1, 1 })
                {
                    m.Positions.Add(new Vector3(
                        x + Mathf.Cos(dir) * lean * t * t + Mathf.Sin(dir) * half * side,
                        height * t - lean * 0.25f * t * t,
                        z + Mathf.Sin(dir) * lean * t * t - Mathf.Cos(dir) * half * side));
                    m.UVs.Add(new Vector2(side == -1 ? 0 : 1, t));
                    m.UV2s.Add(new Vector2(Mathf.Sin(dir) * half * side, -Mathf.Cos(dir) * half * side));
                }
                if (row < 3)
                {
                    int a = start + row * 2;
                    m.Indices.AddRange(new[] { a, a + 1, a + 2 });
                    if (row < 2) m.Indices.AddRange(new[] { a + 1, a + 3, a + 2 });
                }
            }
        }
        return m.ComputeNormals();
    }

    /// <summary>Blade root positions (x, z) in the clump's local space, used to seat clumps on slopes.</summary>
    public static readonly Vector2[] GrassRoots = FindGrassRoots();

    static Vector2[] FindGrassRoots()
    {
        var roots = new List<Vector2>();
        foreach (var p in GrassBlades().Positions)
            if (p.Y == 0) roots.Add(new Vector2(p.X, p.Z));
        return roots.ToArray();
    }

    /// <summary>
    /// A boulder: a noise-deformed sphere, then sliced by a few random planes so it has the flat
    /// fracture faces and sharp edges of broken stone instead of a smooth pebble shape.
    /// Normals are smoothed only across gentle angles, so the fracture edges stay crisp.
    /// </summary>
    public static MeshBuilder WeatheredRock(int seed, float squash)
    {
        var m = MeshBuilder.Icosphere(0.8f, 4);
        float phase = seed * 0.71f;
        var rand = Rng.Mulberry32((uint)seed * 7919u);
        var cuts = new List<(Vector3 n, float d)>();
        int cutCount = 5 + (int)(rand() * 3);
        for (int c = 0; c < cutCount; c++)
        {
            // Mostly sideways and upward-facing cuts: rocks split along steep joints and flat tops.
            float az = (float)rand() * Mathf.Tau;
            float el = Mathf.Lerp(-0.25f, 1.1f, (float)rand());
            var n = new Vector3(Mathf.Cos(az) * Mathf.Cos(el), Mathf.Sin(el), Mathf.Sin(az) * Mathf.Cos(el)).Normalized();
            cuts.Add((n, 0.5f + (float)rand() * 0.22f));
        }
        for (int i = 0; i < m.VertexCount; i++)
        {
            var p = m.Positions[i];
            float d = 1 + Mathf.Sin(p.X * 4 + phase) * Mathf.Cos(p.Z * 3 - phase) * 0.14f
                        + Mathf.Sin(p.Y * 7 + p.Z * 5 + phase) * 0.05f;
            p *= d;
            foreach (var (n, dist) in cuts)
            {
                float over = p.Dot(n) - dist;
                if (over > 0) p -= n * over * 0.92f; // flatten onto the cut, leaving a little relief
            }
            m.Positions[i] = new Vector3(p.X, Math.Max(-0.46f, p.Y) * squash, p.Z);
        }
        return m.Unweld().ComputeNormalsCreased(35f);
    }
}
