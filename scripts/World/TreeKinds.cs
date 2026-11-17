using System.Collections.Generic;
using Godot;

namespace World;

/// <summary>
/// The tree species, built from the Quaternius Stylized Nature MegaKit models (CC0,
/// assets/megakit). The pack's leaves are white textures made for tinting, so one model can
/// carry a summer-green, golden or autumn-red crown; species differ by model, colour, size,
/// bark and where they grow (see Forest.cs).
/// </summary>
public sealed record TreeKind(
    string Name,
    string[] Models,     // assets/megakit/<model>.gltf
    Color Leaves,        // leaf tint (sRGB); unused for leafless trees
    float Birch,         // 0 = normal bark, 1 = pale birch bark
    float ScaleMin, float ScaleMax)
{
    public const int Oak = 0, Beech = 1, Birch_ = 2, Maple = 3, Aspen = 4, RedMaple = 5, TwistedOak = 6,
        Spruce = 7, Fir = 8, Larch = 9, Dead = 10;

    public static readonly TreeKind[] All =
    {
        new("Oak", new[] { "CommonTree_1", "CommonTree_2" }, new Color(0.36f, 0.55f, 0.2f), 0, 1.48f, 1.87f),
        new("Beech", new[] { "CommonTree_3", "CommonTree_4" }, new Color(0.52f, 0.72f, 0.26f), 0, 1.56f, 1.95f),
        new("Birch", new[] { "CommonTree_5", "CommonTree_3" }, new Color(0.64f, 0.8f, 0.32f), 1, 1.56f, 2.03f),
        new("Autumn maple", new[] { "CommonTree_1", "CommonTree_4" }, new Color(0.96f, 0.5f, 0.15f), 0, 1.40f, 1.79f),
        new("Golden aspen", new[] { "CommonTree_3", "CommonTree_5" }, new Color(0.97f, 0.8f, 0.22f), 0.5f, 1.56f, 1.95f),
        new("Red maple", new[] { "TwistedTree_3", "TwistedTree_5" }, new Color(0.82f, 0.2f, 0.12f), 0, 0.55f, 0.70f),
        new("Twisted oak", new[] { "TwistedTree_1", "TwistedTree_2", "TwistedTree_4" }, new Color(0.42f, 0.6f, 0.22f), 0, 0.58f, 0.78f),
        new("Spruce", new[] { "Pine_1", "Pine_2", "Pine_3" }, new Color(0.22f, 0.4f, 0.28f), 0, 1.79f, 2.34f),
        new("Fir", new[] { "Pine_4", "Pine_5" }, new Color(0.3f, 0.5f, 0.24f), 0, 1.56f, 2.11f),
        new("Larch", new[] { "Pine_1", "Pine_3" }, new Color(0.88f, 0.66f, 0.2f), 0, 1.72f, 2.11f),
        new("Dead tree", new[] { "DeadTree_1", "DeadTree_2", "DeadTree_3", "DeadTree_4", "DeadTree_5" }, Colors.White, 0, 0.62f, 0.82f),
    };

    /// <summary>
    /// Every (species, model) pair gets an index: its MultiMesh batches and its impostor atlas
    /// cell are keyed by it.
    /// </summary>
    // Declared before Combos: static fields initialise in source order.
    static readonly Dictionary<(int, int), int> ComboIndex = new();

    public static readonly List<(int Kind, int Model)> Combos = BuildCombos();

    static List<(int, int)> BuildCombos()
    {
        var list = new List<(int, int)>();
        for (int k = 0; k < All.Length; k++)
            for (int m = 0; m < All[k].Models.Length; m++)
            {
                ComboIndex[(k, m)] = list.Count;
                list.Add((k, m));
            }
        return list;
    }

    public static int Combo(int kind, int model) => ComboIndex[(kind, model)];
}
