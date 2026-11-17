using System;
using System.Collections.Generic;
using Godot;
using World.Noise;

namespace World;

public enum PropKind { Tree, TreeImpostor, Plant, Rock, Grass }

/// <summary>A ready-to-upload MultiMesh buffer: 12 transform floats + 4 colour floats per instance.</summary>
public sealed class InstanceBatch
{
    public PropKind Kind;
    public int Variant;
    public int Count;
    public float[] Buffer;
    public Vector2 Center;   // world XZ centre (grass cells: for distance LOD; trees: buffer is relative to it)
    public float Radius;
}

public readonly record struct Collider(float X, float Z, float Radius, float MaxY);

/// <summary>
/// Places a chunk's trees, rocks and grass (rocks and grass started as a port of the web
/// version's ChunkManager._createChunkProps). Runs on worker threads.
/// </summary>
public static class ChunkProps
{
    public const float GrassCell = 12f;
    const int Stride = 16;

    sealed class Tree { public int Kind, Model; public double X, Y, Z, Scale, RotY, LeanX, LeanZ; public Color Leaves; public float Birch; }
    sealed class Rock { public double X, Y, Z, Sx, Sy, Sz, RotX, RotY, RotZ; }

    public static (List<InstanceBatch> batches, List<Collider> colliders) Generate(int cx, int cz)
        => Generate(cx, cz, TrailNetwork.ForChunk(cx, cz));

    internal static (List<InstanceBatch> batches, List<Collider> colliders) Generate(int cx, int cz,
        TrailNetwork.ChunkTrails trails)
    {
        double size = ChunkManager.ChunkSize;
        double originX = cx * size, originZ = cz * size;
        uint seed = (uint)Math.Floor(Rng.Hash2D(cx, cz, 9187) * 0xffffffff);
        var rand = Rng.Mulberry32(seed);
        // Shape variation draws from its own stream, so rock and grass layout is unchanged.
        var ground = new GroundSampler(originX, originZ);
        var rocks = new[] { new List<Rock>(), new List<Rock>(), new List<Rock>() };
        var trees = PlaceTrees(cx, cz, originX, originZ, ground, seed, trails);

        // Rock clusters: denser on rocky shorelines and mountain tops.
        void AddRock(double wx, double wz, double minScale, double maxScale)
        {
            if (wx * wx + wz * wz < 49) return;
            double h = Terrain.Height(wx, wz);
            if (h < Terrain.WaterLevel - 0.35 || h > Terrain.SnowLine + 30) return;
            double b = minScale + rand() * (maxScale - minScale);
            int variant = (int)(rand() * 3);
            var r = new Rock { X = wx, Y = h - 0.23 * b, Z = wz };
            r.Sx = b * (0.75 + rand() * 0.55);
            r.Sy = b * (0.65 + rand() * 0.55);
            r.Sz = b * (0.75 + rand() * 0.55);
            r.RotX = (rand() - 0.5) * 0.35;
            r.RotY = rand() * Math.PI * 2;
            r.RotZ = (rand() - 0.5) * 0.35;
            rocks[variant].Add(r);
        }

        for (int c = 0; c < 10; c++)
        {
            double rx = originX + rand() * size, rz = originZ + rand() * size;
            double hCenter = Terrain.Height(rx, rz);
            double moisture = Terrain.Moisture(rx, rz);
            bool isShore = hCenter >= Terrain.WaterLevel - 0.3 && hCenter < Terrain.WaterLevel + 2.6;
            bool isMountain = hCenter > Terrain.ScreeLine - 6;
            bool rockyShore = isShore && moisture < 0.56;

            double spawnChance = 0.32, anchorMin = 0.95, anchorMax = 1.95;
            int subBase = 3;
            if (isMountain) { spawnChance = 0.85; anchorMin = 1.25; anchorMax = 2.6; subBase = 4; }
            else if (rockyShore) { spawnChance = 0.78; anchorMin = 0.85; anchorMax = 2.0; subBase = 4; }
            if (rand() > spawnChance) continue;

            AddRock(rx, rz, anchorMin, anchorMax);
            int subCount = subBase + (int)(rand() * 3);
            for (int s = 0; s < subCount; s++)
            {
                double ang = rand() * Math.PI * 2;
                double dist = 0.9 + rand() * 2.6;
                AddRock(rx + Math.Cos(ang) * dist, rz + Math.Sin(ang) * dist, 0.3, 0.95);
            }
        }

        var center = new Vector2((float)(originX + size / 2), (float)(originZ + size / 2));
        var (batches, colliders) = BuildTreeBatches(trees, center, impostorsOnly: false);

        for (int v = 0; v < rocks.Length; v++)
        {
            var list = rocks[v];
            list.RemoveAll(rock => trails.DistanceToEdge(rock.X, rock.Z, TrailNetwork.WalkwayHalfWidth) <
                Math.Sqrt(rock.Sx * rock.Sx + rock.Sz * rock.Sz) + 0.25);
        }
        PlaceTrailStones(originX, originZ, ground, seed, trails, rocks);
        for (int v = 0; v < rocks.Length; v++)
        {
            var list = rocks[v];
            if (list.Count == 0) continue;
            var buf = new float[list.Count * Stride];
            for (int i = 0; i < list.Count; i++)
            {
                var r = list[i];
                // Varied stone hues: cool slate, warm granite, mossy grey.
                double shade = 0.62 + rand() * 0.42;
                double warm = (rand() - 0.5) * 0.08;
                Write(buf, i, MeshBuilder.EulerXYZ((float)r.RotX, (float)r.RotY, (float)r.RotZ),
                    new Vector3((float)r.Sx, (float)r.Sy, (float)r.Sz), new Vector3((float)r.X, (float)r.Y, (float)r.Z),
                    new Color((float)(shade + warm), (float)shade, (float)(shade - warm * 0.6)));
                double maxHoriz = Math.Max(r.Sx, r.Sz);
                if (maxHoriz >= 0.55)
                    colliders.Add(new Collider((float)r.X, (float)r.Z, (float)(0.58 * maxHoriz), (float)(r.Y + 0.65 * r.Sy)));
            }
            batches.Add(new InstanceBatch { Kind = PropKind.Rock, Variant = v, Count = list.Count, Buffer = buf });
        }

        batches.AddRange(PlacePlants(originX, originZ, ground, seed, center, trails));
        batches.AddRange(GenerateGrass(rand, originX, originZ, ground, trails));
        return (batches, colliders);
    }

    /// <summary>Far photo chunks retain the same trees as nearby chunks using only their impostors.</summary>
    internal static (List<InstanceBatch> batches, List<Collider> colliders) GenerateDistant(int cx, int cz,
        TrailNetwork.ChunkTrails trails)
    {
        double size = ChunkManager.ChunkSize;
        double originX = cx * size, originZ = cz * size;
        uint seed = (uint)Math.Floor(Rng.Hash2D(cx, cz, 9187) * 0xffffffff);
        var ground = new GroundSampler(originX, originZ);
        var trees = PlaceTrees(cx, cz, originX, originZ, ground, seed, trails);
        var center = new Vector2((float)(originX + size / 2), (float)(originZ + size / 2));
        return BuildTreeBatches(trees, center, impostorsOnly: true);
    }

    static (List<InstanceBatch> batches, List<Collider> colliders) BuildTreeBatches(List<Tree> trees,
        Vector2 center, bool impostorsOnly)
    {
        var batches = new List<InstanceBatch>();
        var colliders = new List<Collider>();

        // Trees: one batch per species variant, positions relative to the chunk centre
        // (the chunk's tree nodes sit there, so distance-based LOD measures from it).
        var groups = new Dictionary<int, List<Tree>>();
        foreach (var t in trees)
        {
            float radius = TreeAssets.Get(TreeKind.All[t.Kind].Models[t.Model]).TrunkRadius * (float)t.Scale;
            colliders.Add(new Collider((float)t.X, (float)t.Z, radius + 0.05f, float.MaxValue));
            if (impostorsOnly) continue;
            int key = TreeKind.Combo(t.Kind, t.Model);
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<Tree>();
            list.Add(t);
        }
        // Full trees: 12 transform floats, 4 colour (unused, white), 4 custom (leaf tint, birch).
        foreach (var (key, list) in groups)
        {
            var buf = new float[list.Count * 20];
            for (int i = 0; i < list.Count; i++)
            {
                var t = list[i];
                float sc = (float)t.Scale;
                WriteTree(buf, i, MeshBuilder.EulerXYZ((float)t.LeanX, (float)t.RotY, (float)t.LeanZ) * Basis.FromScale(new Vector3(sc, sc, sc)),
                    new Vector3((float)t.X - center.X, (float)t.Y, (float)t.Z - center.Y), t.Leaves, t.Birch);
            }
            batches.Add(new InstanceBatch { Kind = PropKind.Tree, Variant = key, Count = list.Count, Buffer = buf, Center = center });
        }
        // Far versions of all the chunk's trees, in one batch (see TreeImpostors); custom = atlas cell.
        if (trees.Count > 0)
        {
            var buf = new float[trees.Count * 20];
            for (int i = 0; i < trees.Count; i++)
            {
                var t = trees[i];
                int combo = TreeKind.Combo(t.Kind, t.Model);
                float frame = TreeAssets.Get(TreeKind.All[t.Kind].Models[t.Model]).FrameSize * (float)t.Scale;
                var cell = TreeImpostors.Cell(combo);
                WriteTree(buf, i, Basis.FromScale(new Vector3(frame, frame, frame)),
                    new Vector3((float)t.X - center.X, (float)t.Y, (float)t.Z - center.Y), new Color(cell.X, cell.Y, 0), 0);
            }
            batches.Add(new InstanceBatch { Kind = PropKind.TreeImpostor, Count = trees.Count, Buffer = buf, Center = center });
        }

        return (batches, colliders);
    }

    // Small, low stones sit in the same batches as the boulders and have no blocking collider.
    static void PlaceTrailStones(double originX, double originZ, GroundSampler ground, uint seed,
        TrailNetwork.ChunkTrails trails, List<Rock>[] rocks)
    {
        var rand = Rng.Mulberry32(seed ^ 0x7BC36E91u);
        const int count = 14;
        double cell = ChunkManager.ChunkSize / count;
        for (int z = 0; z < count; z++)
            for (int x = 0; x < count; x++)
            {
                double wx = originX + (x + rand()) * cell;
                double wz = originZ + (z + rand()) * cell;
                double edge = trails.DistanceToEdge(wx, wz, TrailNetwork.WalkwayHalfWidth);
                if (edge < 0.1 || edge > 2.2 || rand() > 0.38) continue;
                double height = ground.Sample(wx, wz);
                if (height < Terrain.WaterLevel + 0.9) continue;
                double size = 0.12 + rand() * 0.18;
                rocks[(int)(rand() * rocks.Length)].Add(new Rock
                {
                    X = wx, Y = height - size * 0.15, Z = wz,
                    Sx = size, Sy = size * 0.5, Sz = size * (0.7 + rand() * 0.4),
                    RotY = rand() * Math.PI * 2,
                });
            }
    }

    /// <summary>
    /// Trees on a jittered grid (~4.4 m cells): each cell holds a tree with a probability set by
    /// the local forest density, so dense forest, thinning edges and lone meadow trees all come
    /// from one rule. Species, shape variant, size, lean and tint are picked per tree.
    /// </summary>
    static List<Tree> PlaceTrees(int cx, int cz, double originX, double originZ, GroundSampler ground, uint seed,
        TrailNetwork.ChunkTrails trails)
    {
        var rand = Rng.Mulberry32(seed ^ 0x51ED27A3u);
        var trees = new List<Tree>();
        const int n = 11;
        double cell = ChunkManager.ChunkSize / n;
        for (int gz = 0; gz < n; gz++)
            for (int gx = 0; gx < n; gx++)
            {
                double x = originX + (gx + 0.5 + (rand() - 0.5) * 0.75) * cell;
                double z = originZ + (gz + 0.5 + (rand() - 0.5) * 0.75) * cell;
                double roll = rand(), pick = rand(), variantRoll = rand(), scaleRoll = rand(), rot = rand(), leanRoll = rand();
                if (x * x + z * z < 14 * 14) continue; // keep the spawn clearing open
                double h = Terrain.Height(x, z);
                double slope = Math.Max(Math.Abs(Terrain.Height(x + 1, z) - h), Math.Abs(Terrain.Height(x, z + 1) - h));
                double moisture = Terrain.Moisture(x, z);
                double cover = Forest.Cover(x, z, moisture);
                if (roll > Forest.Density(cover, h, slope) * cell * cell) continue;

                int kind = Forest.PickSpecies(x, z, h, moisture, cover, pick);
                var k = TreeKind.All[kind];
                int model = Math.Min(k.Models.Length - 1, (int)(variantRoll * k.Models.Length));
                var asset = TreeAssets.Get(k.Models[model]);
                // Forest trees grow taller competing for light; lone trees stay a bit smaller.
                double scale = (k.ScaleMin + scaleRoll * (k.ScaleMax - k.ScaleMin)) * (0.9 + 0.15 * cover);
                // Seat the trunk on the lowest ground under it so the downhill side never floats.
                double r = asset.TrunkRadius * scale * 1.3;
                if (trails.DistanceToEdge(x, z) < r + 0.8) continue;
                double g = ground.Sample(x, z);
                for (int i = 0; i < 4; i++)
                    g = Math.Min(g, ground.Sample(x + Math.Cos(i * Math.PI / 2) * r, z + Math.Sin(i * Math.PI / 2) * r));
                double leanDir = leanRoll * Math.PI * 2, lean = (leanRoll * 7919 % 1) * 0.04;
                // Per-tree variation of the species colour: lighter/darker, warmer/cooler.
                float b = 0.85f + (float)((pick * 4973) % 1) * 0.25f, warm = (float)((rot * 6151) % 1 - 0.5) * 0.12f;
                var c = k.Leaves;
                trees.Add(new Tree
                {
                    Kind = kind, Model = model, X = x, Y = g - 0.05, Z = z, Scale = scale,
                    RotY = rot * Math.PI * 2, LeanX = Math.Cos(leanDir) * lean, LeanZ = Math.Sin(leanDir) * lean,
                    Leaves = new Color(c.R * b * (1 + warm), c.G * b, c.B * b * (1 - warm)).SrgbToLinear(),
                    Birch = k.Birch,
                });
            }
        return trees;
    }

    static readonly SimplexNoise2D FlowerNoise = new(437113);

    /// <summary>
    /// Undergrowth on a jittered ~1.7 m grid: each cell may hold one plant, picked by the local
    /// environment (see PlantKind.Weights). One batch per plant model, relative to the chunk centre.
    /// </summary>
    static IEnumerable<InstanceBatch> PlacePlants(double originX, double originZ, GroundSampler ground, uint seed,
        Vector2 center, TrailNetwork.ChunkTrails trails)
    {
        var rand = Rng.Mulberry32(seed ^ 0x2C1B3C6Du);
        const int n = 28;
        double cell = ChunkManager.ChunkSize / n;
        var lists = new List<(Vector3 pos, float scale, float rot, Color tint)>[PlantKind.All.Length];
        Span<double> w = stackalloc double[PlantKind.All.Length];
        for (int gz = 0; gz < n; gz++)
            for (int gx = 0; gx < n; gx++)
            {
                double x = originX + (gx + rand()) * cell, z = originZ + (gz + rand()) * cell;
                double pick = rand(), scaleRoll = rand(), rot = rand(), shade = rand();
                double h = ground.Sample(x, z);
                double slope = Math.Max(Math.Abs(ground.Sample(x + 1, z) - h), Math.Abs(ground.Sample(x, z + 1) - h));
                double moisture = Terrain.Moisture(x, z);
                double cover = Forest.Cover(x, z, moisture);
                double flowers = Math.Clamp(FlowerNoise.FbmRotated(x * 0.03, z * 0.03, 2) * 1.6 + 0.2, 0, 1);
                PlantKind.Weights(w, h, slope, cover, flowers);
                double edge = trails.DistanceToEdge(x, z, TrailNetwork.WalkwayHalfWidth);
                if (edge > 0.1 && edge < 3.5 && h > Terrain.WaterLevel + 0.9 && h < Terrain.GrassLine && slope < 0.4)
                {
                    double verge = (1 - Mathf.SmoothStep(1, 3.5, edge)) * (0.3 + flowers * 0.7);
                    w[PlantKind.Flowers3] += verge * 0.14;
                    w[PlantKind.Flowers4] += verge * 0.1;
                    w[PlantKind.Clover1] += verge * 0.12;
                    w[PlantKind.Mushroom] += verge * 0.025;
                    w[PlantKind.FlowerBush] += verge * 0.025;
                }
                // One roll per cell: walk the cumulative chances.
                int kind = -1;
                double acc = 0;
                for (int i = 0; i < w.Length; i++)
                {
                    acc += w[i];
                    if (pick < acc) { kind = i; break; }
                }
                if (kind < 0) continue;
                var k = PlantKind.All[kind];
                float sc = (float)(k.ScaleMin + scaleRoll * (k.ScaleMax - k.ScaleMin));
                bool bush = kind is PlantKind.Bush or PlantKind.FlowerBush or PlantKind.BigLeaf;
                double clearance = bush ? sc * 1.2 + 0.15 : sc * 0.35;
                if (edge < clearance) continue;
                float b = 0.85f + (float)shade * 0.3f;
                var tint = new Color(k.Tint.R * b, k.Tint.G * b, k.Tint.B * b).SrgbToLinear();
                (lists[kind] ??= new()).Add((new Vector3((float)x - center.X, (float)h - 0.03f, (float)z - center.Y), sc, (float)(rot * Mathf.Tau), tint));
            }
        for (int kind = 0; kind < lists.Length; kind++)
        {
            var list = lists[kind];
            if (list == null) continue;
            var buf = new float[list.Count * 20];
            for (int i = 0; i < list.Count; i++)
            {
                var (pos, sc, rot, tint) = list[i];
                WriteTree(buf, i, new Basis(Vector3.Up, rot) * Basis.FromScale(new Vector3(sc, sc, sc)), pos, tint, 0);
            }
            yield return new InstanceBatch { Kind = PropKind.Plant, Variant = kind, Count = list.Count, Buffer = buf, Center = center };
        }
    }

    static IEnumerable<InstanceBatch> GenerateGrass(Func<double> rand, double originX, double originZ, GroundSampler ground,
        TrailNetwork.ChunkTrails trails)
    {
        // Variable-density clumps: sparse near water and mountains.
        var spots = new List<(double x, double y, double z, double s, double r)>();
        const int gridN = 80;
        double cell = ChunkManager.ChunkSize / gridN;
        static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        for (int gz = 0; gz < gridN; gz++)
            for (int gx = 0; gx < gridN; gx++)
            {
                double wx = originX + (gx + rand()) * cell;
                double wz = originZ + (gz + rand()) * cell;
                double h = Terrain.Height(wx, wz);
                if (h < Terrain.WaterLevel + 0.5 || h > Terrain.GrassLine) continue;
                double slope = Math.Max(Math.Abs(Terrain.Height(wx + 0.6, wz) - h), Math.Abs(Terrain.Height(wx, wz + 0.6) - h));
                // Steeper ground is drawn as bare scree/cliff by the terrain shader: no grass there.
                if (slope > 0.42) continue;

                double waterFactor = Clamp01((h - (Terrain.WaterLevel + 0.5)) / 1.6);
                double mountainFactor = Clamp01((Terrain.GrassLine - h) / 10.0);
                double slopeFactor = Clamp01((0.42 - slope) / 0.2);
                double patchBoost = Math.Sin(wx * 0.09) * Math.Cos(wz * 0.08) * 0.25 + 0.85;
                double density = Clamp01(waterFactor * mountainFactor * slopeFactor * patchBoost * 1.15);
                // Less grass in the shade of dense forest.
                if (density > 0) density *= 1 - 0.6 * Forest.Cover(wx, wz, Terrain.Moisture(wx, wz));
                if (rand() > density) continue;

                double sizeFactor = 0.65 + 0.55 * density;
                double s = (1.05 + rand() * 0.72) * sizeFactor;
                spots.Add((wx, h - 0.05, wz, s, rand() * Math.PI * 2));

                // Overlapping clumps in the densest meadow patches.
                if (density > 0.72 && rand() < (density - 0.55) * 1.4)
                {
                    double ox = wx + (rand() - 0.5) * 0.55;
                    double oz = wz + (rand() - 0.5) * 0.55;
                    double s2 = (1.15 + rand() * 0.75) * sizeFactor;
                    spots.Add((ox, Terrain.Height(ox, oz) - 0.05, oz, s2, rand() * Math.PI * 2));
                }
            }
        // Vegetation reclaims the green shoulders inside the painted road footprint.
        // Only the narrow walking line suppresses growth; the fringe keeps substantial tufts.
        int kept = 0;
        for (int i = 0; i < spots.Count; i++)
        {
            var spot = spots[i];
            double edge = trails.DistanceToEdge(spot.x, spot.z, TrailNetwork.WalkwayHalfWidth);
            if (edge < -0.1) continue;
            if (edge < 1.0)
            {
                double patch = FlowerNoise.Noise(spot.x * 0.7, spot.z * 0.7) * 0.5 + 0.5;
                double growth = Mathf.SmoothStep(-0.2, 0.9, edge + (patch - 0.5) * 0.8);
                if (Rng.Hash2D((int)Math.Floor(spot.x * 11), (int)Math.Floor(spot.z * 11), 6217) > growth) continue;
                spot.s *= 0.6 + growth * 0.4;
            }
            spots[kept++] = spot;
        }
        spots.RemoveRange(kept, spots.Count - kept);
        if (spots.Count == 0) yield break;

        // Build every clump, then split them into 12 m cells that can be culled and thinned separately.
        var all = new float[spots.Count * Stride];
        var cells = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < spots.Count; i++)
        {
            var g = spots[i];
            var basis = new Basis(Vector3.Up, (float)g.r);
            Write(all, i, basis, new Vector3((float)g.s, (float)g.s, (float)g.s), new Vector3((float)g.x, (float)g.y, (float)g.z),
                new Color((float)(0.78 + rand() * 0.22), (float)(0.82 + rand() * 0.18), (float)(0.72 + rand() * 0.22)));
            ground.GroundGrass(all, i * Stride);
            var key = ((int)Math.Floor(g.x / GrassCell), (int)Math.Floor(g.z / GrassCell));
            if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<int>();
            list.Add(i);
        }

        foreach (var (key, indices) in cells)
        {
            // Deterministic shuffle: any prefix of a cell covers it evenly, so distance LOD
            // can thin a cell just by lowering its visible instance count.
            uint state = unchecked((uint)indices[0] * 2654435761u + (uint)indices.Count);
            for (int i = indices.Count - 1; i > 0; i--)
            {
                state = unchecked(state * 1664525u + 1013904223u);
                int j = (int)(state / 4294967296.0 * (i + 1));
                (indices[i], indices[j]) = (indices[j], indices[i]);
            }
            var buf = new float[indices.Count * Stride];
            for (int i = 0; i < indices.Count; i++) Array.Copy(all, indices[i] * Stride, buf, i * Stride, Stride);
            yield return new InstanceBatch
            {
                Kind = PropKind.Grass, Count = indices.Count, Buffer = buf,
                Center = new Vector2((key.Item1 + 0.5f) * GrassCell, (key.Item2 + 0.5f) * GrassCell),
                Radius = GrassCell * 0.7072f,
            };
        }
    }

    /// <summary>Transform, white colour and custom data (rgb + a) for a 20-float MultiMesh instance.</summary>
    static void WriteTree(float[] buf, int index, Basis b, Vector3 origin, Color custom, float customA)
    {
        int o = index * 20;
        buf[o + 0] = b.Column0.X; buf[o + 1] = b.Column1.X; buf[o + 2] = b.Column2.X; buf[o + 3] = origin.X;
        buf[o + 4] = b.Column0.Y; buf[o + 5] = b.Column1.Y; buf[o + 6] = b.Column2.Y; buf[o + 7] = origin.Y;
        buf[o + 8] = b.Column0.Z; buf[o + 9] = b.Column1.Z; buf[o + 10] = b.Column2.Z; buf[o + 11] = origin.Z;
        buf[o + 12] = 1; buf[o + 13] = 1; buf[o + 14] = 1; buf[o + 15] = 1;
        buf[o + 16] = custom.R; buf[o + 17] = custom.G; buf[o + 18] = custom.B; buf[o + 19] = customA;
    }

    /// <summary>Writes rotation * scale + translation and a colour into Godot's MultiMesh buffer layout.</summary>
    static void Write(float[] buf, int index, Basis rotation, Vector3 scale, Vector3 origin, Color color)
    {
        var b = rotation * Basis.FromScale(scale);
        int o = index * Stride;
        buf[o + 0] = b.Column0.X; buf[o + 1] = b.Column1.X; buf[o + 2] = b.Column2.X; buf[o + 3] = origin.X;
        buf[o + 4] = b.Column0.Y; buf[o + 5] = b.Column1.Y; buf[o + 6] = b.Column2.Y; buf[o + 7] = origin.Y;
        buf[o + 8] = b.Column0.Z; buf[o + 9] = b.Column1.Z; buf[o + 10] = b.Column2.Z; buf[o + 11] = origin.Z;
        buf[o + 12] = color.R; buf[o + 13] = color.G; buf[o + 14] = color.B; buf[o + 15] = 1;
    }

}
