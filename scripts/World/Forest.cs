using System;
using World.Noise;

namespace World;

/// <summary>
/// Where trees grow and which ones. Thread-safe (read-only noise), called from chunk workers.
///  - Density: broad forest patches (hundreds of metres) with clearings inside them, fading to
///    scattered single trees on open meadows. Wetter ground is more forested.
///  - Species: each one prefers certain altitudes and moisture (willows on lake shores, spruce
///    and larch up toward the tree line, beech in dense lowland forest, birch and old twisted
///    oaks on lake shores, a few dead trees high up...).
///    On top of that the land is split into ~250 m cells, each with one dominant broadleaf and
///    one dominant conifer, so a forest is mostly one or two species instead of an even mix.
///    Some cells are autumn groves of orange maples, golden aspens and red maples.
/// </summary>
public static class Forest
{
    static readonly SimplexNoise2D PatchNoise = new(813127);
    static readonly SimplexNoise2D ClearingNoise = new(922081);

    const int Oak = TreeKind.Oak, Beech = TreeKind.Beech, Birch = TreeKind.Birch_, Maple = TreeKind.Maple,
        Aspen = TreeKind.Aspen, RedMaple = TreeKind.RedMaple, TwistedOak = TreeKind.TwistedOak,
        Spruce = TreeKind.Spruce, Fir = TreeKind.Fir, Larch = TreeKind.Larch, Dead = TreeKind.Dead;

    static readonly int[] Broadleaves = { Oak, Beech, Birch, TwistedOak };
    static readonly int[] Conifers = { Spruce, Fir, Larch };

    static double Smoothstep(double e0, double e1, double x)
    {
        double t = Math.Clamp((x - e0) / (e1 - e0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    // 1 inside [lo, hi], fading out over `soft` on either side.
    static double Band(double v, double lo, double hi, double soft) =>
        Smoothstep(lo - soft, lo, v) * (1 - Smoothstep(hi, hi + soft, v));

    /// <summary>How forested this spot is: 0 = open meadow, 1 = dense forest core.</summary>
    public static double Cover(double x, double z, double moisture)
    {
        double patches = PatchNoise.FbmRotated(x * 0.0035 + 4.2, z * 0.0035 - 1.7, 3, 2.0, 0.5) * 0.5 + 0.5;
        double clearings = ClearingNoise.FbmRotated(x * 0.018, z * 0.018, 2, 2.0, 0.5) * 0.5 + 0.5;
        double v = patches * 0.8 + Smoothstep(0.3, 0.75, moisture) * 0.3 + (clearings - 0.5) * 0.25;
        return Smoothstep(0.5, 0.72, v);
    }

    /// <summary>Trees per square metre for a given forest cover, height and slope.</summary>
    public static double Density(double cover, double height, double slope)
    {
        double d = Lerp(0.0012, 0.019, cover); // scattered meadow trees ... dense forest (~7 m apart)
        d *= 1 - Smoothstep(Terrain.TreeLine - 6, Terrain.TreeLine, height);
        d *= Smoothstep(Terrain.WaterLevel + 0.6, Terrain.WaterLevel + 1.2, height);
        d *= 1 - Smoothstep(0.55, 0.9, slope); // nothing on cliffs
        return d;
    }

    static double Lerp(double a, double b, double t) => a + (b - a) * t;

    static uint CellHash(int cx, int cz, uint salt)
    {
        unchecked
        {
            uint h = (uint)cx * 374761393u + (uint)cz * 668265263u + salt * 2246822519u;
            h = (h ^ (h >> 13)) * 1274126177u;
            return h ^ (h >> 16);
        }
    }

    /// <summary>Picks a species for a tree at this spot. `pick` is a uniform random number in [0, 1).</summary>
    public static int PickSpecies(double x, double z, double height, double moisture, double cover, double pick)
    {
        // Dominant species of this ~250 m cell (slightly warped so cells aren't square).
        double wx = x + PatchNoise.Noise(x * 0.004, z * 0.004) * 60, wz = z + PatchNoise.Noise(z * 0.004 + 9, x * 0.004) * 60;
        int cx = (int)Math.Floor(wx / 250), cz = (int)Math.Floor(wz / 250);
        uint h = CellHash(cx, cz, 7);
        int domBroad = Broadleaves[h % (uint)Broadleaves.Length];
        int domConifer = Conifers[(h >> 8) % (uint)Conifers.Length];
        bool autumnGrove = ((h >> 16) % 100) < 18;

        Span<double> w = stackalloc double[TreeKind.All.Length];
        double m = moisture, hh = height;
        double shore = Band(hh, 0.6, 3, 0.8);
        w[Oak] = Band(hh, 1.5, 16, 3) * Band(m, 0.25, 0.65, 0.12);
        w[Beech] = Band(hh, 2, 20, 3) * Band(m, 0.45, 0.9, 0.1) * (0.3 + cover);
        w[Birch] = Band(hh, 1, 24, 3) * (1.3 - cover) * 0.8 + shore * 1.5;
        w[Maple] = Band(hh, 2, 18, 3) * Band(m, 0.35, 0.75, 0.1) * 0.5;
        w[Aspen] = Band(hh, 1, 22, 3) * Band(m, 0.55, 1.0, 0.1) * 0.5;
        w[RedMaple] = Band(hh, 2, 16, 3) * 0.3;
        // Big old twisted oaks: drier lowland and lake shores.
        w[TwistedOak] = Band(hh, 1, 14, 3) * Band(m, 0.2, 0.6, 0.12) * 0.8 + shore * 1.2;
        w[Spruce] = Band(hh, 12, Terrain.TreeLine + 2, 4) * Band(m, 0.42, 1.0, 0.1);
        w[Fir] = Band(hh, 8, 28, 4) * Band(m, 0.38, 0.9, 0.1);
        w[Larch] = Band(hh, 14, Terrain.TreeLine + 2, 4) * 0.5;
        w[Dead] = Band(hh, 18, Terrain.TreeLine + 3, 4) * 0.15 + 0.015;

        w[domBroad] *= 4;
        w[domConifer] *= 4;
        // Autumn groves: the maples, aspens and red maples dominate and the greens step back.
        if (autumnGrove)
        {
            w[Maple] = w[Maple] * 6 + 0.4; w[Aspen] = w[Aspen] * 6 + 0.3; w[RedMaple] = w[RedMaple] * 6 + 0.2;
            w[Oak] *= 0.3; w[Beech] *= 0.3; w[TwistedOak] *= 0.3;
        }

        double total = 0;
        foreach (var v in w) total += v;
        if (total <= 1e-9) return hh > 15 ? Spruce : Birch;
        double r = pick * total;
        for (int i = 0; i < w.Length; i++)
        {
            r -= w[i];
            if (r <= 0) return i;
        }
        return w.Length - 1;
    }
}
