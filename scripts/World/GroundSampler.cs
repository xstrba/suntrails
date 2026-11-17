using System;
using System.Collections.Generic;
using Godot;

namespace World;

/// <summary>
/// Port of createGroundSampler / groundGrassMatrix (src/world/grounding.js).
/// Returns the height of the *rendered* triangle mesh, not the continuous noise,
/// so props sit exactly on the ground you see. One instance per chunk (not thread-shared).
/// </summary>
public sealed class GroundSampler
{
    const int Pad = 4;
    const int Size = ChunkManager.Segments + 2 * Pad + 1;
    static readonly double Step = ChunkManager.ChunkSize / ChunkManager.Segments;

    readonly int _gx0, _gz0;
    readonly float[] _grid = new float[Size * Size];
    readonly Dictionary<(int, int), float> _fallback = new();

    public GroundSampler(double originX, double originZ)
    {
        _gx0 = (int)Math.Round(originX / Step) - Pad;
        _gz0 = (int)Math.Round(originZ / Step) - Pad;
        Array.Fill(_grid, float.NaN);
    }

    float Vertex(int x, int z)
    {
        int ix = x - _gx0, iz = z - _gz0;
        if (ix >= 0 && ix < Size && iz >= 0 && iz < Size)
        {
            ref float h = ref _grid[iz * Size + ix];
            if (float.IsNaN(h)) h = (float)Terrain.Height(x * Step, z * Step);
            return h;
        }
        if (!_fallback.TryGetValue((x, z), out float v))
            _fallback[(x, z)] = v = (float)Terrain.Height(x * Step, z * Step);
        return v;
    }

    // Matches the rendered grid's triangles, split along the (x+1, z)-(x, z+1) diagonal.
    public double Sample(double x, double z)
    {
        int gx = (int)Math.Floor(x / Step), gz = (int)Math.Floor(z / Step);
        double u = x / Step - gx, v = z / Step - gz;
        double b = Vertex(gx + 1, gz), c = Vertex(gx, gz + 1);
        if (u + v <= 1)
        {
            double a = Vertex(gx, gz);
            return a + (b - a) * u + (c - a) * v;
        }
        double d = Vertex(gx + 1, gz + 1);
        return d + (c - d) * (1 - u) + (b - d) * (1 - v);
    }

    /// <summary>
    /// Shears a grass clump onto its local slope, then lowers it so every blade root is on or
    /// below the mesh. <paramref name="m"/> is a 3x4 row-major matrix (Godot MultiMesh layout).
    /// </summary>
    public void GroundGrass(float[] m, int o)
    {
        double x = m[o + 3], z = m[o + 11];
        double h = Sample(x, z);
        double dx = (Sample(x + 0.15, z) - Sample(x - 0.15, z)) / 0.3;
        double dz = (Sample(x, z + 0.15) - Sample(x, z - 0.15)) / 0.3;
        m[o + 4] = (float)(dx * m[o + 0] + dz * m[o + 8]);
        m[o + 6] = (float)(dx * m[o + 2] + dz * m[o + 10]);
        double correction = 0;
        foreach (var r in Vegetation.GrassRoots)
        {
            double wx = x + m[o + 0] * r.X + m[o + 2] * r.Y;
            double wz = z + m[o + 8] * r.X + m[o + 10] * r.Y;
            correction = Math.Min(correction, Sample(wx, wz) - (h + m[o + 4] * r.X + m[o + 6] * r.Y));
        }
        m[o + 7] = (float)(h + correction - 0.025);
    }
}
