namespace World.Noise;

/// <summary>
/// Bit-exact port of src/world/noise.js: Mulberry32 PRNG, hash2D and 2D simplex noise.
/// Uses doubles and unsigned 32-bit math so the Godot world matches the web version.
/// Read-only after construction, so it is safe to call from worker threads.
/// </summary>
public static class Rng
{
    public static System.Func<double> Mulberry32(uint seed)
    {
        uint a = seed;
        return () =>
        {
            unchecked
            {
                a += 0x6D2B79F5;
                uint t = (a ^ (a >> 15)) * (1 | a);
                t = (t + ((t ^ (t >> 7)) * (61 | t))) ^ t;
                return (t ^ (t >> 14)) / 4294967296.0;
            }
        };
    }

    public static double Hash2D(int ix, int iz, int seed = 1337)
    {
        unchecked
        {
            uint h = (uint)ix * 374761393u + (uint)iz * 668265263u + (uint)seed * 1442695041u;
            h ^= h >> 13;
            h *= 1274126177u;
            return (h ^ (h >> 16)) / 4294967296.0;
        }
    }
}

public sealed class SimplexNoise2D
{
    static readonly double F2 = 0.5 * (System.Math.Sqrt(3) - 1);
    static readonly double G2 = (3 - System.Math.Sqrt(3)) / 6;
    static readonly double[] Grad2 = { 1, 1, -1, 1, 1, -1, -1, -1, 1, 0, -1, 0, 0, 1, 0, -1 };

    readonly byte[] _perm = new byte[512];
    readonly byte[] _permMod8 = new byte[512];

    public SimplexNoise2D(uint seed = 42)
    {
        var rand = Rng.Mulberry32(seed);
        var p = new byte[256];
        for (int i = 0; i < 256; i++) p[i] = (byte)i;
        for (int i = 255; i > 0; i--)
        {
            int j = (int)(rand() * (i + 1));
            (p[i], p[j]) = (p[j], p[i]);
        }
        for (int i = 0; i < 512; i++)
        {
            _perm[i] = p[i & 255];
            _permMod8[i] = (byte)(_perm[i] & 7);
        }
    }

    public double Noise(double xin, double yin)
    {
        double s = (xin + yin) * F2;
        int i = (int)System.Math.Floor(xin + s);
        int j = (int)System.Math.Floor(yin + s);
        double t = (i + j) * G2;
        double x0 = xin - (i - t);
        double y0 = yin - (j - t);

        int i1, j1;
        if (x0 > y0) { i1 = 1; j1 = 0; } else { i1 = 0; j1 = 1; }

        double x1 = x0 - i1 + G2, y1 = y0 - j1 + G2;
        double x2 = x0 - 1.0 + 2.0 * G2, y2 = y0 - 1.0 + 2.0 * G2;

        int ii = i & 255, jj = j & 255;
        int gi0 = _permMod8[ii + _perm[jj]] * 2;
        int gi1 = _permMod8[ii + i1 + _perm[jj + j1]] * 2;
        int gi2 = _permMod8[ii + 1 + _perm[jj + 1]] * 2;

        double n0 = 0, n1 = 0, n2 = 0;
        double t0 = 0.5 - x0 * x0 - y0 * y0;
        if (t0 >= 0) { t0 *= t0; n0 = t0 * t0 * (Grad2[gi0] * x0 + Grad2[gi0 + 1] * y0); }
        double t1 = 0.5 - x1 * x1 - y1 * y1;
        if (t1 >= 0) { t1 *= t1; n1 = t1 * t1 * (Grad2[gi1] * x1 + Grad2[gi1 + 1] * y1); }
        double t2 = 0.5 - x2 * x2 - y2 * y2;
        if (t2 >= 0) { t2 *= t2; n2 = t2 * t2 * (Grad2[gi2] * x2 + Grad2[gi2 + 1] * y2); }

        return 70.0 * (n0 + n1 + n2);
    }

    public double Fbm(double x, double y, int octaves = 4, double lacunarity = 2.0, double gain = 0.5)
    {
        double sum = 0, amp = 1, freq = 1, norm = 0;
        for (int o = 0; o < octaves; o++)
        {
            sum += amp * Noise(x * freq, y * freq);
            norm += amp;
            amp *= gain;
            freq *= lacunarity;
        }
        return sum / norm;
    }

    // Each octave is turned ~37° (a 3-4-5 rotation) so finer layers don't share the
    // coarse layer's lattice directions, which otherwise show up as straight features.
    const double RotC = 0.8, RotS = 0.6;

    public double FbmRotated(double x, double y, int octaves = 4, double lacunarity = 2.0, double gain = 0.5)
    {
        double sum = 0, amp = 1, norm = 0;
        for (int o = 0; o < octaves; o++)
        {
            sum += amp * Noise(x, y);
            norm += amp;
            amp *= gain;
            (x, y) = ((RotC * x - RotS * y) * lacunarity, (RotS * x + RotC * y) * lacunarity);
        }
        return sum / norm;
    }

    /// <summary>
    /// Ridged multifractal with rounded crests: |n| is replaced by sqrt(n² + crest²), so peaks
    /// are smooth instead of knife edges, and each octave is weighted by the previous ridge
    /// value so detail gathers on high ground while valleys stay smooth.
    /// Keep octaves low (2): crest sharpness grows with frequency², so fine ridged octaves
    /// turn back into metre-scale spikes however much they are rounded.
    /// </summary>
    public double SmoothRidgedFbm(double x, double y, int octaves, double lacunarity, double gain, double crest)
    {
        double sum = 0, amp = 1, norm = 0, weight = 1;
        for (int o = 0; o < octaves; o++)
        {
            double n = Noise(x, y);
            double r = System.Math.Max(0, (1 - System.Math.Sqrt(n * n + crest * crest)) / (1 - crest));
            r *= r;
            sum += amp * r * weight;
            norm += amp;
            weight = System.Math.Clamp(r * 1.4, 0, 1);
            amp *= gain;
            (x, y) = ((RotC * x - RotS * y) * lacunarity, (RotS * x + RotC * y) * lacunarity);
        }
        return sum / norm;
    }

    public double RidgedFbm(double x, double y, int octaves = 3, double lacunarity = 2.1, double gain = 0.5)
    {
        double sum = 0, amp = 1, freq = 1, norm = 0;
        for (int o = 0; o < octaves; o++)
        {
            double n = 1.0 - System.Math.Abs(Noise(x * freq, y * freq));
            sum += amp * n * n;
            norm += amp;
            amp *= gain;
            freq *= lacunarity;
        }
        return sum / norm;
    }
}
