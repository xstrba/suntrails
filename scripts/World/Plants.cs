using System;
using Godot;

namespace World;

/// <summary>
/// Undergrowth from the MegaKit (CC0): bushes, ferns, flowers, clover, small plants, mushrooms
/// and wispy grass tufts. The pack's plants are modelled large (grass ~1.5 units tall), so each
/// has its own scale. Small plants are only drawn near the player and cast no shadows.
/// </summary>
public sealed record PlantKind(
    string Model,
    float ScaleMin, float ScaleMax,
    Color Tint,           // leaf tint for the pack's white leaf textures; white keeps painted colours
    float DrawDistance)   // metres from the chunk centre
{
    public const int Bush = 0, FlowerBush = 1, Fern = 2, BigLeaf = 3, Mushroom = 4,
        Flowers3 = 5, Flowers4 = 6, FlowerSingle = 7, Clover1 = 8, Clover2 = 9, SmallPlant = 10,
        WispyShort = 11, WispyTall = 12;

    public static readonly PlantKind[] All =
    {
        new("Bush_Common", 0.9f, 1.4f, new Color(0.42f, 0.6f, 0.22f), 75),
        new("Bush_Common_Flowers", 0.9f, 1.3f, new Color(0.5f, 0.68f, 0.25f), 70),
        new("Fern_1", 0.28f, 0.42f, Colors.White, 55),
        new("Plant_1_Big", 0.3f, 0.45f, Colors.White, 50),
        new("Mushroom_Common", 0.5f, 0.8f, Colors.White, 30),
        new("Flower_3_Group", 0.4f, 0.52f, Colors.White, 50),
        new("Flower_4_Group", 0.38f, 0.5f, Colors.White, 50),
        new("Flower_3_Single", 0.42f, 0.55f, Colors.White, 35),
        new("Clover_1", 0.25f, 0.35f, Colors.White, 35),
        new("Clover_2", 0.25f, 0.35f, Colors.White, 35),
        new("Plant_7", 0.7f, 1.0f, Colors.White, 35),
        new("Grass_Wispy_Short", 0.4f, 0.6f, Colors.White, 40),
        new("Grass_Wispy_Tall", 0.35f, 0.5f, Colors.White, 45),
    };

    static double Smoothstep(double e0, double e1, double x)
    {
        double t = Math.Clamp((x - e0) / (e1 - e0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>
    /// Chance of each plant in a ~1.7 m cell, given the spot. Forests get ferns, bushes, big
    /// leafy plants and mushrooms; open meadows get flower patches, clover and wispy grass.
    /// </summary>
    public static void Weights(Span<double> w, double height, double slope, double cover, double flowerPatch)
    {
        w.Clear();
        if (height < Terrain.WaterLevel + 0.7 || height > Terrain.GrassLine || slope > 0.5) return;
        double open = 1 - cover;
        double high = Smoothstep(Terrain.GrassLine - 8, Terrain.GrassLine, height); // thinner up high
        double ok = 1 - high * 0.7;
        w[Bush] = (0.02 + 0.05 * cover) * ok;
        w[FlowerBush] = 0.012 * open * ok;
        w[Fern] = 0.45 * cover * ok;
        w[BigLeaf] = 0.03 * cover * ok;
        w[Mushroom] = 0.035 * cover * ok;
        // Flowers come in patches (a noise picks where), like real meadows.
        w[Flowers3] = 0.2 * open * flowerPatch * flowerPatch * ok;
        w[Flowers4] = 0.16 * open * flowerPatch * flowerPatch * ok;
        w[FlowerSingle] = 0.03 * open * (0.3 + flowerPatch) * ok;
        w[Clover1] = 0.03 * open * ok;
        w[Clover2] = 0.02 * open * ok;
        w[SmallPlant] = 0.03 * ok;
        w[WispyShort] = 0.06 * open * ok;
        w[WispyTall] = 0.04 * open * ok;
    }
}
