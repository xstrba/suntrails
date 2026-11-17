using System;
using World.Noise;

namespace World;

/// <summary>Deterministic terrain height, moisture and biomes (started as a port of src/world/terrain.js).</summary>
public static class Terrain
{
    public const double WaterLevel = 0.0;

    // Altitude bands (metres), shared by the terrain shader, grass/tree placement and biome names.
    public const double ScreeLine = 22.0;  // loose rock starts showing through on high ground
    public const double GrassLine = 30.0;  // grass blades thin out and stop here
    public const double TreeLine = 32.0;   // no trees above this
    public const double SnowLine = 44.0;   // snow starts settling on flatter ground

    static readonly SimplexNoise2D HeightNoise = new(104729);
    static readonly SimplexNoise2D RidgeNoise = new(224737);
    static readonly SimplexNoise2D MoistureNoise = new(350377);
    static readonly SimplexNoise2D DetailNoise = new(479001);
    static readonly SimplexNoise2D WarpNoise = new(591161);
    static readonly SimplexNoise2D RegionNoise = new(611953);
    static readonly SimplexNoise2D SizeNoise = new(735419);

    static double Smoothstep(double e0, double e1, double x)
    {
        double t = Math.Clamp((x - e0) / (e1 - e0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>
    /// Landscape: broad gentle meadows with lakes, broken up by separate mountain regions.
    /// - Coordinates are domain-warped so landforms meander, and noise octaves are rotated so
    ///   layers don't share a grid (the web version's straight sawtooth crests came from that).
    /// - A very broad "region" noise decides where mountains rise, so meadows between them are large.
    /// - A second broad "size" noise makes each region's mountains either low and wide (~15-20 m,
    ///   long gentle ridges) or tall (~60-70 m, tighter ridges and more rugged detail).
    /// - Ridges use two rounded ridged octaves; finer rock detail is smooth noise weighted toward
    ///   the high ground, so valleys stay smooth (reads as erosion).
    /// </summary>
    public static double Height(double x, double z)
    {
        double qx = x + WarpNoise.FbmRotated(x * 0.0019 + 3.1, z * 0.0019 - 7.4, 3) * 45.0;
        double qz = z + WarpNoise.FbmRotated(x * 0.0019 - 11.6, z * 0.0019 + 5.9, 3) * 45.0;

        double region = RegionNoise.FbmRotated(qx * 0.0008 + 12.3, qz * 0.0008 - 4.7, 2, 2.0, 0.45) * 0.5 + 0.5;
        double mountainMask = Smoothstep(0.5, 0.75, region);
        double size = Smoothstep(0.25, 0.8, SizeNoise.Noise(qx * 0.0009 - 6.1, qz * 0.0009 + 2.9) * 0.5 + 0.5);

        double wide = RidgeNoise.SmoothRidgedFbm(qx * 0.0022, qz * 0.0022, 2, 2.1, 0.5, 0.3);
        double sharp = RidgeNoise.SmoothRidgedFbm(qx * 0.0036 + 17.7, qz * 0.0036 - 9.2, 2, 2.1, 0.5, 0.25);
        double ridges = Lerp(wide, sharp, size);
        double rugged = RidgeNoise.FbmRotated(qx * 0.016 + 41.3, qz * 0.016 - 8.8, 3, 2.0, 0.45) * ridges * Lerp(1.5, 5.0, size);
        double mountains = (ridges * Lerp(16.0, 68.0, size) + rugged) * mountainMask;

        // Rolling ground: gentle in the meadows, livelier in the foothills.
        double rolling = HeightNoise.FbmRotated(qx * 0.0028, qz * 0.0028, 5, 2.0, 0.5) * Lerp(7.0, 12.0, mountainMask);
        double micro = DetailNoise.FbmRotated(x * 0.018, z * 0.018, 3, 2.0, 0.45) * 1.1;

        double h = 2.2 + rolling + mountains + micro;

        // Carve gentle lake basins where the ground dips low (softplus: no crease at the shore).
        const double k = 0.6;
        double below = 1.2 - h;
        h -= (below > 8 * k ? below : k * Math.Log(1 + Math.Exp(below / k))) * 0.45;

        // Walkable meadow around spawn (0, 0), easing back into the landscape over ~100 m
        // so a nearby hill becomes a gentle rise instead of a wall around a pit.
        double distSq = x * x + z * z;
        if (distSq < 120 * 120)
        {
            double blend = Smoothstep(25, 120, Math.Sqrt(distSq));
            h = Lerp(4.5 + micro * 0.4, h, blend);
        }
        return h;
    }

    public static double Moisture(double x, double z) =>
        MoistureNoise.Fbm(x * 0.0022 + 100.0, z * 0.0022 - 50.0, 3, 2.0, 0.5) * 0.5 + 0.5;

    public static string BiomeName(double height, double moisture, double slope)
    {
        if (height < WaterLevel - 0.3) return "Lakebed";
        if (height < WaterLevel + 1.35) return "Shoreline";
        if (height > SnowLine) return "Alpine Peak";
        if (slope > 0.58) return "Rocky Cliff";
        if (height > ScreeLine) return "Highlands";
        if (moisture > 0.54) return "Forest";
        return "Meadow";
    }

    /// <summary>Unit surface normal from central differences (same as the JS chunk mesher).</summary>
    public static (double nx, double ny, double nz) Normal(double x, double z, double eps = 0.6)
    {
        double dx = (Height(x - eps, z) - Height(x + eps, z)) / (2 * eps);
        double dz = (Height(x, z - eps) - Height(x, z + eps)) / (2 * eps);
        double inv = 1 / Math.Sqrt(dx * dx + 1 + dz * dz);
        return (dx * inv, inv, dz * inv);
    }
}
