using System;
using System.Collections.Generic;
using System.Threading;
using Godot;
using World.Noise;

namespace World;

/// <summary>
/// Deterministic routes between shared, jittered junctions. Planning runs on chunk workers.
/// A bounded cache shares routes across chunks without retaining the whole explored world.
/// </summary>
internal static class TrailNetwork
{
    const int JunctionSpacing = 240;
    const int NavigationStep = 12;
    const int SearchPadding = 8;
    const int CacheCapacity = 256;
    const float SampleRange = 16f;
    internal const float WalkwayHalfWidth = 0.65f;

    readonly record struct RouteKey(int X, int Z, bool East);
    internal readonly record struct Segment(Vector2 Start, Vector2 End, float HalfWidth);

    static readonly object CacheLock = new();
    static readonly Dictionary<RouteKey, Lazy<Segment[]>> Routes = new();
    static readonly Queue<RouteKey> CacheOrder = new();
    static readonly SimplexNoise2D RouteNoise = new(758921);

    /// <summary>A small, immutable local view used by both the ground mesh and its props.</summary>
    internal sealed class ChunkTrails
    {
        readonly Segment[] _segments;

        internal ChunkTrails(Segment[] segments) => _segments = segments;

        // Signed metres from the edge. Negative is inside the worn surface.
        // A width limit lets vegetation use the walking line inside the painted shoulders.
        public float DistanceToEdge(double x, double z, float maxHalfWidth = float.MaxValue)
        {
            var point = new Vector2((float)x, (float)z);
            float nearest = SampleRange;
            foreach (var segment in _segments)
            {
                var direction = segment.End - segment.Start;
                float along = Mathf.Clamp((point - segment.Start).Dot(direction) / direction.LengthSquared(), 0, 1);
                float halfWidth = Mathf.Min(segment.HalfWidth, maxHalfWidth);
                float limit = nearest + halfWidth;
                float distanceSquared = point.DistanceSquaredTo(segment.Start + direction * along);
                if (limit > 0 && distanceSquared < limit * limit)
                    nearest = Mathf.Sqrt(distanceSquared) - halfWidth;
            }
            return nearest;
        }
    }

    public static ChunkTrails ForChunk(int x, int z)
    {
        var origin = new Vector2(x * ChunkManager.ChunkSize, z * ChunkManager.ChunkSize);
        var bounds = new Rect2(origin, Vector2.One * ChunkManager.ChunkSize).Grow(SampleRange);
        int regionX = (int)Math.Floor(origin.X / JunctionSpacing);
        int regionZ = (int)Math.Floor(origin.Y / JunctionSpacing);
        var segments = new List<Segment>();

        // Junction displacement plus the search margin is less than one region. These
        // neighbouring edges include every route that can touch this chunk and its shoulders.
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                AddRoute(new RouteKey(regionX + dx, regionZ + dz, true));
                AddRoute(new RouteKey(regionX + dx, regionZ + dz, false));
            }
        return new ChunkTrails(segments.ToArray());

        void AddRoute(RouteKey key)
        {
            // Skip planning distant edges even within the neighbouring regions.
            const float reach = (SearchPadding + 7) * NavigationStep;
            var first = new Vector2(key.X * JunctionSpacing, key.Z * JunctionSpacing);
            var last = first + (key.East ? Vector2.Right : Vector2.Down) * JunctionSpacing;
            var possible = new Rect2(first, last - first).Grow(reach);
            if (!possible.Intersects(bounds, true)) return;

            foreach (var segment in GetRoute(key))
            {
                var extent = new Rect2(segment.Start, Vector2.Zero).Expand(segment.End).Grow(segment.HalfWidth);
                if (extent.Intersects(bounds, true)) segments.Add(segment);
            }
        }
    }

    static Segment[] GetRoute(RouteKey key)
    {
        Lazy<Segment[]> route;
        lock (CacheLock)
        {
            if (!Routes.TryGetValue(key, out route))
            {
                route = new Lazy<Segment[]>(() => BuildRoute(key), LazyThreadSafetyMode.ExecutionAndPublication);
                Routes.Add(key, route);
                CacheOrder.Enqueue(key);
                if (Routes.Count > CacheCapacity) Routes.Remove(CacheOrder.Dequeue());
            }
        }
        // Planning outside the lock lets different routes build in parallel.
        return route.Value;
    }

    static Vector2I? Junction(int x, int z)
    {
        int gridX = x * (JunctionSpacing / NavigationStep);
        int gridZ = z * (JunctionSpacing / NavigationStep);
        if (x != 0 || z != 0)
        {
            gridX += (int)(Rng.Hash2D(x, z, 7319) * 7) - 3;
            gridZ += (int)(Rng.Hash2D(x, z, 9923) * 7) - 3;
        }

        // Move wet or steep junctions onto nearby dry ground. Every incident route finds
        // exactly the same point, independent of chunk load order.
        for (int radius = 0; radius <= 4; radius++)
            for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != radius) continue;
                    double wx = (gridX + dx) * NavigationStep, wz = (gridZ + dz) * NavigationStep;
                    double height = Terrain.Height(wx, wz);
                    if (!DryGround(height)) continue;
                    var (_, normalY, _) = Terrain.Normal(wx, wz);
                    if (normalY > 0.94) return new Vector2I(gridX + dx, gridZ + dz);
                }
        return null;
    }

    static bool DryGround(double height) => height > Terrain.WaterLevel + 1.1 && height < Terrain.SnowLine - 2;

    static Segment[] BuildRoute(RouteKey key)
    {
        var start = Junction(key.X, key.Z);
        var end = Junction(key.X + (key.East ? 1 : 0), key.Z + (key.East ? 0 : 1));
        if (start == null || end == null) return Array.Empty<Segment>();

        bool road = key.East && key.Z % 3 == 0;
        float halfWidth = road ? 2.4f : 1.35f;
        float maxGrade = road ? 0.38f : 0.5f;
        var points = new RouteSearch(start.Value, end.Value).Find(maxGrade);
        if (points.Count < 2 || !SafeSurface(points, halfWidth, maxGrade)) return Array.Empty<Segment>();

        // Cut corners twice, retaining the shared endpoints. Keep the safe original if
        // rounding would push a bend into water or onto a steep bank.
        for (int pass = 0; pass < 2; pass++)
        {
            var rounded = new List<Vector2> { points[0] };
            for (int i = 1; i < points.Count; i++)
            {
                rounded.Add(points[i - 1].Lerp(points[i], 0.25f));
                rounded.Add(points[i - 1].Lerp(points[i], 0.75f));
            }
            rounded.Add(points[^1]);
            if (!SafeSurface(rounded, halfWidth, maxGrade)) break;
            points = rounded;
        }

        var segments = new Segment[points.Count - 1];
        for (int i = 0; i < segments.Length; i++)
            segments[i] = new Segment(points[i], points[i + 1], halfWidth);
        return segments;
    }

    static bool SafeSurface(List<Vector2> points, float halfWidth, float maxGrade)
    {
        for (int i = 1; i < points.Count; i++)
        {
            var start = points[i - 1];
            var direction = points[i] - start;
            float length = direction.Length();
            var side = new Vector2(-direction.Y, direction.X) * ((halfWidth + 0.4f) / length);
            int steps = Math.Max(1, (int)Math.Ceiling(length / 2));
            double previous = Terrain.Height(start.X, start.Y);
            for (int step = 0; step <= steps; step++)
            {
                var point = start + direction * ((float)step / steps);
                double height = Terrain.Height(point.X, point.Y);
                double left = Terrain.Height(point.X + side.X, point.Y + side.Y);
                double right = Terrain.Height(point.X - side.X, point.Y - side.Y);
                if (!DryGround(Math.Min(height, Math.Min(left, right))) ||
                    !DryGround(Math.Max(height, Math.Max(left, right))) ||
                    Math.Max(Math.Abs(left - height), Math.Abs(right - height)) > side.Length() * maxGrade ||
                    Math.Abs(height - previous) > length / steps * maxGrade)
                    return false;
                previous = height;
            }
        }
        return true;
    }

    // A* prefers gentle grades and routes away from shorelines. Half-grid height samples
    // also check each edge's midpoint without repeating terrain evaluations.
    sealed class RouteSearch
    {
        readonly Vector2I _origin, _start, _end;
        readonly int _width, _height, _sampleWidth;
        readonly double[] _heights;

        public RouteSearch(Vector2I start, Vector2I end)
        {
            _origin = start.Min(end) - Vector2I.One * SearchPadding;
            _start = start - _origin;
            _end = end - _origin;
            var size = (start - end).Abs() + Vector2I.One * (SearchPadding * 2 + 1);
            _width = size.X;
            _height = size.Y;
            _sampleWidth = _width * 2 - 1;
            _heights = new double[_sampleWidth * (_height * 2 - 1)];
            Array.Fill(_heights, double.NaN);
        }

        double Height(int x, int z)
        {
            ref double height = ref _heights[z * _sampleWidth + x];
            if (double.IsNaN(height))
                height = Terrain.Height((_origin.X + x * 0.5) * NavigationStep,
                    (_origin.Y + z * 0.5) * NavigationStep);
            return height;
        }

        public List<Vector2> Find(float maxGrade)
        {
            int startIndex = _start.Y * _width + _start.X;
            int endIndex = _end.Y * _width + _end.X;
            var costs = new double[_width * _height];
            var previous = new int[costs.Length];
            Array.Fill(costs, double.PositiveInfinity);
            Array.Fill(previous, -1);
            costs[startIndex] = 0;
            var open = new PriorityQueue<(int Index, double Cost), (double Estimate, int Index)>();
            open.Enqueue((startIndex, 0), (0, startIndex));

            while (open.TryDequeue(out var current, out _))
            {
                if (current.Cost > costs[current.Index]) continue;
                if (current.Index == endIndex) return Reconstruct(previous, endIndex);
                int x = current.Index % _width, z = current.Index / _width;
                double height = Height(x * 2, z * 2);
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, nz = z + dz;
                        if ((dx == 0 && dz == 0) || nx < 0 || nz < 0 || nx >= _width || nz >= _height) continue;
                        double nextHeight = Height(nx * 2, nz * 2);
                        double middleHeight = Height(x * 2 + dx, z * 2 + dz);
                        if (!DryGround(nextHeight) || !DryGround(middleHeight)) continue;
                        double distance = NavigationStep * Math.Sqrt(dx * dx + dz * dz);
                        double grade = Math.Max(Math.Abs(nextHeight - middleHeight), Math.Abs(middleHeight - height)) / (distance / 2);
                        if (grade > maxGrade) continue;
                        // Avoid cutting diagonally across a wet or steep corner.
                        if (dx != 0 && dz != 0 && (!DryGround(Height(nx * 2, z * 2)) || !DryGround(Height(x * 2, nz * 2))))
                            continue;

                        double variation = RouteNoise.Noise((_origin.X + nx) * 0.13, (_origin.Y + nz) * 0.13) * 0.5 + 0.5;
                        double cost = current.Cost + distance * (1 + grade * grade * 18 +
                            Math.Max(0, 2.5 - nextHeight) + variation * 0.35);
                        int next = nz * _width + nx;
                        if (cost >= costs[next]) continue;
                        costs[next] = cost;
                        previous[next] = current.Index;
                        double heuristic = Math.Sqrt((nx - _end.X) * (nx - _end.X) + (nz - _end.Y) * (nz - _end.Y)) * NavigationStep;
                        open.Enqueue((next, cost), (cost + heuristic, next));
                    }
            }
            return new List<Vector2>();
        }

        List<Vector2> Reconstruct(int[] previous, int index)
        {
            var points = new List<Vector2>();
            for (; index >= 0; index = previous[index])
                points.Add(new Vector2((_origin.X + index % _width) * NavigationStep,
                    (_origin.Y + index / _width) * NavigationStep));
            points.Reverse();
            return points;
        }
    }
}
