using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace World;

/// <summary>
/// Streams terrain chunks and their vegetation around the player. Heights, mesh arrays,
/// tree/rock/grass placement and MultiMesh buffers are built on thread-pool workers;
/// the main thread only uploads finished chunks, a few per frame.
/// </summary>
public partial class ChunkManager : Node3D
{
    public const float ChunkSize = 48f;
    public const int Segments = 32;
    const int Side = Segments + 1;

    [Export] public int ViewRadius = 6;
    [Export] public int PropRadius = 4;
    [Export] public int GrassRadius = 2;
    [Export] public bool CastPropShadows = true;
    [Export] public int MaxUploadsPerFrame = 3;
    const int PhotoMarginChunks = 2;
    static readonly int MaxGenerationJobs = Math.Clamp(System.Environment.ProcessorCount / 2, 2, 6);

    // Grass draws every clump within Near metres, easing down to MinFraction of them at Far
    // (the grass shader widens the surviving blades to compensate).
    const float GrassLodNear = 12f, GrassLodFar = 55f, GrassLodMinFraction = 0.12f;

    sealed class ChunkData
    {
        public Vector2I Key;
        public bool Distant;
        public Vector3[] Vertices;
        public Vector3[] Normals;
        public Vector2[] TrailEdges;
        public List<InstanceBatch> Batches;
        public List<Collider> Colliders;
    }

    sealed class Chunk
    {
        public Node3D Root;
        public bool Distant;
        public readonly List<MultiMeshInstance3D> Props = new();   // rocks: shown within PropRadius
        public readonly List<MultiMeshInstance3D> Trees = new();   // switched by Godot visibility ranges
        public readonly List<(MultiMeshInstance3D Node, float Distance)> Plants = new();
        public readonly List<(MultiMeshInstance3D Node, InstanceBatch Batch)> Grass = new();
        public List<Collider> Colliders;
        public readonly List<MultiMeshInstance3D> Impostors = new();
    }

    sealed class GenerationJob
    {
        public Vector2I Key;
        public bool Distant;
        public int Revision;
        public readonly CancellationTokenSource Cancellation = new();
    }

    sealed record GenerationResult(GenerationJob Job, ChunkData Data, Exception Error);

    readonly Dictionary<Vector2I, Chunk> _chunks = new();
    readonly Dictionary<Vector2I, GenerationJob> _inFlight = new();
    readonly ConcurrentQueue<GenerationResult> _ready = new();
    readonly Dictionary<Vector2I, bool> _needed = new();
    readonly List<(Vector2I Key, int Priority)> _wanted = new();
    readonly HashSet<Vector2I> _failed = new();
    Vector2I _playerChunk = new(int.MinValue, int.MinValue);
    Vector2 _lodAt = new(float.MaxValue, float.MaxValue);
    int _revision;
    bool _photoActive;
    double _viewTimer;
    Transform3D _lastView;
    float _lastFov;
    Vector2 _lastViewportSize;
    bool _lastReflection;

    Node3D _player;
    Camera3D _camera;
    Water _water;
    ShaderMaterial _terrainMaterial;
    ShaderMaterial _grassMaterial;
    float _detailScale = 1;
    int[] _indices;
    Vector2[] _uvs;
    readonly Dictionary<(PropKind, int), (Mesh mesh, Material material)> _propAssets = new();
    TreeImpostors _impostors;

    public int LoadedChunks => _chunks.Count;
    public int PendingChunks
    {
        get
        {
            int count = 0;
            foreach (var (key, distant) in _needed)
                if (!_chunks.TryGetValue(key, out var chunk) || chunk.Distant != distant) count++;
            return count;
        }
    }
    public int FailedChunks => _failed.Count;
    // Extend atmospheric layers more gently than terrain: retain a hazy horizon while
    // keeping telephoto subjects readable inside the longer photo view.
    public float AtmosphereDistanceScale => _photoActive ? Mathf.Sqrt((float)PhotoRadius / ViewRadius) : 1;
    int PhotoRadius => Math.Clamp(ViewRadius * 3, 18, 27);
    int DetailRadius => Math.Min(ViewRadius, Math.Max(GrassRadius, (int)Math.Ceiling(_treeDetail / ChunkSize) + 1));
    float TreeDetailDistance => _photoActive
        ? Mathf.Min(_treeDetail * _detailScale, PhotoRadius * ChunkSize + ChunkSize * 0.5f) : _treeDetail;

    public override void _Ready()
    {
        _player = GetNode<Node3D>("../Player");
        _camera = GetNode<Camera3D>("../Player/Camera3D");
        _water = GetNodeOrNull<Water>("../Water");
        _terrainMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/terrain.gdshader") };
        _terrainMaterial.SetShaderParameter("grass_tex", GD.Load<Texture2D>("res://textures/grass.jpg"));
        _terrainMaterial.SetShaderParameter("sand_tex", GD.Load<Texture2D>("res://textures/sand.jpg"));
        foreach (var (param, name) in new[] { ("cliff", "rock_face_03"), ("scree", "aerial_rocks_02"), ("snow", "snow_02") })
        {
            _terrainMaterial.SetShaderParameter($"{param}_ah", GD.Load<Texture2D>($"res://textures/{name}_albedo_height.png"));
            _terrainMaterial.SetShaderParameter($"{param}_nr", GD.Load<Texture2D>($"res://textures/{name}_normal_rough.png"));
        }
        _terrainMaterial.SetShaderParameter("water_level", (float)Terrain.WaterLevel);
        _terrainMaterial.SetShaderParameter("scree_line", (float)Terrain.ScreeLine);
        _terrainMaterial.SetShaderParameter("grass_line", (float)Terrain.GrassLine);
        _terrainMaterial.SetShaderParameter("snow_line", (float)Terrain.SnowLine);
        BuildSharedGrid();
        BuildPropAssets();
    }

    void BuildPropAssets()
    {
        var rockMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/rock.gdshader") };
        // Same scanned rock as the terrain cliffs.
        rockMat.SetShaderParameter("rock_ah", GD.Load<Texture2D>("res://textures/rock_face_03_albedo_height.png"));
        rockMat.SetShaderParameter("rock_nr", GD.Load<Texture2D>("res://textures/rock_face_03_normal_rough.png"));
        var grassMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/grass.gdshader") };
        _grassMaterial = grassMat;
        grassMat.SetShaderParameter("lod_near", GrassLodNear);
        grassMat.SetShaderParameter("lod_far", GrassLodFar);

        BuildTreeMeshes();
        _propAssets[(PropKind.Rock, 0)] = (Vegetation.WeatheredRock(11, 0.85f).ToArrayMesh(), rockMat);
        _propAssets[(PropKind.Rock, 1)] = (Vegetation.WeatheredRock(23, 0.78f).ToArrayMesh(), rockMat);
        _propAssets[(PropKind.Rock, 2)] = (Vegetation.WeatheredRock(37, 0.52f).ToArrayMesh(), rockMat);
        _propAssets[(PropKind.Grass, 0)] = (Vegetation.GrassBlades().ToArrayMesh(), grassMat);
    }

    // Distance (m, from the chunk centre) where full trees hand over to impostors. Within it
    // Godot picks the models' own automatic detail levels. No cross-fade: Godot draws fading
    // objects through its transparency path, which measured ~2x slower here.
    float _treeDetail = 90f;      // full trees up to here, impostors beyond (settings: TreeDetail)
    float _treeShadowEnd = 40f;   // near trees cast shadows up to here (settings: Shadows)
    float _plantScale = 1f;       // multiplier on undergrowth draw distances (settings: Undergrowth)

    // Benchmark switches: `-- --no-trees`, `-- --no-tree-shadows`, `-- --tree-end=<m>`.
    static readonly bool DebugNoTrees = System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--no-trees") >= 0;
    static readonly bool DebugNoTreeShadows = System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--no-tree-shadows") >= 0;

    /// <summary>
    /// Applies the pause menu's distance and detail options. Distances are baked into each chunk
    /// when it is built, so a change rebuilds the world around you (takes a second or two).
    /// </summary>
    public void ApplySettings(GraphicsSettings g, bool rebuild)
    {
        ViewRadius = g.RenderDistance;
        PropRadius = Mathf.Max(2, g.RenderDistance - 2);
        GrassRadius = g.GrassDistance;
        _treeDetail = g.TreeDetail;
        CastPropShadows = g.Shadows > 0;
        _treeShadowEnd = g.Shadows switch { 0 => 0, 1 => 25, 2 => 40, _ => 60 };
        _plantScale = g.Undergrowth;
        UpdateCameraRange();
        if (!rebuild) return;
        _revision++;
        foreach (var job in _inFlight.Values) job.Cancellation.Cancel();
        foreach (var chunk in _chunks.Values) chunk.Root.QueueFree();
        _chunks.Clear();
        _playerChunk = new Vector2I(int.MinValue, int.MinValue);
        _lodAt = new Vector2(float.MaxValue, float.MaxValue);
    }

    public void SetPhotoMode(bool active)
    {
        _photoActive = active;
        UpdateCameraRange();
        _playerChunk = new Vector2I(int.MinValue, int.MinValue);
    }

    void UpdateCameraRange() => _camera.Far = (_photoActive ? PhotoRadius : ViewRadius) * ChunkSize + 120;

    public bool PreparePhotoCapture()
    {
        if (_photoActive && (_playerChunk.X == int.MinValue || ViewChanged())) RefreshLayout();
        return _needed.Count > 0 && PendingChunks == 0;
    }

    public override void _ExitTree()
    {
        foreach (var job in _inFlight.Values) job.Cancellation.Cancel();
    }

    void BuildTreeMeshes()
    {
        TreeAssets.Load();
        _impostors = new TreeImpostors { Name = "TreeImpostors" };
        AddChild(_impostors);
        _impostors.Capture();
    }

    void BuildSharedGrid()
    {
        _uvs = new Vector2[Side * Side];
        for (int z = 0; z < Side; z++)
            for (int x = 0; x < Side; x++)
                _uvs[z * Side + x] = new Vector2(x / (float)Segments, z / (float)Segments);

        // Godot treats clockwise triangles as front-facing (three.js uses counter-clockwise),
        // so each quad is wound a-b-c / b-d-c instead of a-c-b / b-c-d.
        _indices = new int[Segments * Segments * 6];
        int p = 0;
        for (int z = 0; z < Segments; z++)
            for (int x = 0; x < Segments; x++)
            {
                int a = z * Side + x, b = a + 1, c = a + Side, d = c + 1;
                _indices[p++] = a; _indices[p++] = b; _indices[p++] = c;
                _indices[p++] = b; _indices[p++] = d; _indices[p++] = c;
            }
    }

    public override void _Process(double delta)
    {
        _viewTimer -= delta;
        var pos = _player.GlobalPosition;
        var pc = new Vector2I(Mathf.FloorToInt(pos.X / ChunkSize), Mathf.FloorToInt(pos.Z / ChunkSize));
        if (pc != _playerChunk || (_photoActive && _viewTimer <= 0 && ViewChanged()))
        {
            _playerChunk = pc;
            RefreshLayout();
        }
        bool created = UploadReady();
        StartGeneration();

        // Grass density LOD: refresh for new chunks or once the player has walked 2 m.
        var p2 = new Vector2(pos.X, pos.Z);
        if (created || p2.DistanceTo(_lodAt) > 2)
        {
            _lodAt = p2;
            foreach (var chunk in _chunks.Values) ApplyGrassLod(chunk, p2);
        }
    }

    void RefreshLayout()
    {
        var position = _player.GlobalPosition;
        _playerChunk = new Vector2I(Mathf.FloorToInt(position.X / ChunkSize), Mathf.FloorToInt(position.Z / ChunkSize));
        _lastView = _camera.GlobalTransform;
        _lastFov = _camera.Fov;
        // A subject enlarged by the lens needs the detail it would have at the equivalent
        // distance through our 24 mm photo lens. Keep the two-chunk buffer on cheap impostors.
        _detailScale = _photoActive ? Mathf.Max(1, 0.5f / Mathf.Tan(Mathf.DegToRad(_lastFov) / 2)) : 1;
        _grassMaterial.SetShaderParameter("detail_scale", _detailScale);
        _lodAt = new Vector2(float.MaxValue, float.MaxValue);
        _lastViewportSize = GetViewport().GetVisibleRect().Size;
        _lastReflection = _water?.ReflectionsEnabled == true;
        _viewTimer = 0.2;
        _wanted.Clear();
        _needed.Clear();
        _failed.Clear();
        var frustum = _photoActive ? CopyFrustum(_camera) : Array.Empty<Plane>();
        var reflection = _photoActive && _lastReflection ? ReflectedFrustum(frustum) : Array.Empty<Plane>();
        int r = _photoActive ? PhotoRadius : ViewRadius;
        int detailRadius = DetailRadius;
        float photoDetailEnd = Mathf.Max(_treeDetail, Mathf.Max(PropRadius * ChunkSize,
            Mathf.Max(GrassRadius * ChunkSize, 75 * _plantScale))) * _detailScale + ChunkSize;
        for (int dz = -r; dz <= r; dz++)
            for (int dx = -r; dx <= r; dx++)
            {
                int distSq = dx * dx + dz * dz;
                if (distSq > r * r + 1) continue;
                var key = new Vector2I(_playerChunk.X + dx, _playerChunk.Y + dz);
                bool nearby = distSq <= PhotoMarginChunks * PhotoMarginChunks + 1;
                if (_photoActive && !nearby && !InPhotoFrame(key, frustum, PhotoMarginChunks * ChunkSize) &&
                    !InPhotoFrame(key, reflection, PhotoMarginChunks * ChunkSize)) continue;
                bool detailedFrame = InPhotoFrame(key, frustum, ChunkSize / 2) ||
                    InPhotoFrame(key, reflection, ChunkSize / 2);
                bool distant = _photoActive && distSq > detailRadius * detailRadius + 1 &&
                    (distSq * ChunkSize * ChunkSize > photoDetailEnd * photoDetailEnd || !detailedFrame);
                _needed.Add(key, distant);
                if (_chunks.TryGetValue(key, out var chunk)) SetVisibility(chunk, key);
                if (chunk != null && chunk.Distant == distant) continue;
                bool inFrame = !_photoActive || nearby || InPhotoFrame(key, frustum, 0);
                _wanted.Add((key, distSq + (inFrame ? 0 : r * r + 1)));
            }

        var stale = new List<Vector2I>();
        foreach (var key in _chunks.Keys)
            if (!_needed.ContainsKey(key)) stale.Add(key);
        foreach (var key in stale)
        {
            _chunks[key].Root.QueueFree();
            _chunks.Remove(key);
        }

        foreach (var job in _inFlight.Values)
            if (!_needed.TryGetValue(job.Key, out bool distant) || distant != job.Distant || job.Revision != _revision)
                job.Cancellation.Cancel();

        _wanted.Sort((a, b) => a.Priority.CompareTo(b.Priority));
    }

    bool ViewChanged() => _lastView.Origin.DistanceSquaredTo(_camera.GlobalPosition) > 1 ||
        _lastView.Basis.Z.Dot(_camera.GlobalBasis.Z) < Mathf.Cos(Mathf.DegToRad(Mathf.Min(2.5f, _camera.Fov * 0.1f))) ||
        Math.Abs(Mathf.Tan(Mathf.DegToRad(_lastFov) / 2) / Mathf.Tan(Mathf.DegToRad(_camera.Fov) / 2) - 1) > 0.03f ||
        _lastViewportSize != GetViewport().GetVisibleRect().Size ||
        _lastReflection != (_water?.ReflectionsEnabled == true);

    static Plane[] CopyFrustum(Camera3D camera)
    {
        var frustum = camera.GetFrustum();
        var planes = new Plane[frustum.Count];
        frustum.CopyTo(planes, 0);
        return planes;
    }

    static Plane[] ReflectedFrustum(Plane[] frustum)
    {
        // Reflect the current view directly, so loading never uses the mirror's previous frame.
        var planes = new Plane[frustum.Length];
        for (int i = 0; i < planes.Length; i++)
        {
            var plane = frustum[i];
            var normal = plane.Normal;
            planes[i] = new Plane(new Vector3(normal.X, -normal.Y, normal.Z),
                plane.D - 2 * (float)Terrain.WaterLevel * normal.Y);
        }
        return planes;
    }

    static bool InPhotoFrame(Vector2I key, Plane[] planes, float margin)
    {
        if (planes.Length == 0) return false;
        // Conservative bounds include this terrain generator's elevations and tree crowns.
        var center = new Vector3((key.X + 0.5f) * ChunkSize, 48, (key.Y + 0.5f) * ChunkSize);
        var halfSize = new Vector3(ChunkSize / 2, 80, ChunkSize / 2);
        foreach (var plane in planes)
        {
            float radius = plane.Normal.Abs().Dot(halfSize);
            if (plane.DistanceTo(center) - radius > margin) return false;
        }
        return true;
    }

    void StartGeneration()
    {
        foreach (var (key, _) in _wanted)
        {
            if (_inFlight.Count >= MaxGenerationJobs) break;
            if (_inFlight.ContainsKey(key) || _failed.Contains(key)) continue;
            bool distant = _needed[key];
            if (_chunks.TryGetValue(key, out var chunk) && chunk.Distant == distant) continue;
            var job = new GenerationJob { Key = key, Distant = distant, Revision = _revision };
            _inFlight.Add(key, job);
            Task.Run(() =>
            {
                ChunkData data = null;
                Exception error = null;
                try { data = Generate(job.Key, job.Distant, job.Cancellation.Token); }
                catch (OperationCanceledException) { }
                catch (Exception e) { error = e; }
                _ready.Enqueue(new GenerationResult(job, data, error));
            });
        }
    }

    static ChunkData Generate(Vector2I key, bool distant, CancellationToken cancellation)
    {
        double originX = key.X * ChunkSize, originZ = key.Y * ChunkSize;
        double step = ChunkSize / Segments;
        var verts = new Vector3[Side * Side];
        var normals = new Vector3[Side * Side];
        var trailEdges = new Vector2[Side * Side];
        var trails = TrailNetwork.ForChunk(key.X, key.Y);
        for (int gz = 0; gz < Side; gz++)
        {
            cancellation.ThrowIfCancellationRequested();
            double wz = originZ + gz * step;
            for (int gx = 0; gx < Side; gx++)
            {
                double wx = originX + gx * step;
                int i = gz * Side + gx;
                verts[i] = new Vector3((float)(gx * step), (float)Terrain.Height(wx, wz), (float)(gz * step));
                // World-space finite differences give identical normals on shared chunk borders.
                var (nx, ny, nz) = Terrain.Normal(wx, wz);
                normals[i] = new Vector3((float)nx, (float)ny, (float)nz);
                trailEdges[i] = new Vector2(trails.DistanceToEdge(wx, wz), 0);
            }
        }
        cancellation.ThrowIfCancellationRequested();
        var (batches, colliders) = distant ? ChunkProps.GenerateDistant(key.X, key.Y, trails)
            : ChunkProps.Generate(key.X, key.Y, trails);
        cancellation.ThrowIfCancellationRequested();
        return new ChunkData { Key = key, Distant = distant, Vertices = verts, Normals = normals, TrailEdges = trailEdges,
            Batches = batches, Colliders = colliders };
    }

    bool UploadReady()
    {
        bool created = false;
        for (int n = 0; n < MaxUploadsPerFrame && _ready.TryDequeue(out var result);)
        {
            var job = result.Job;
            _inFlight.Remove(job.Key);
            bool cancelled = job.Cancellation.IsCancellationRequested;
            job.Cancellation.Dispose();
            if (cancelled || job.Revision != _revision || !_needed.TryGetValue(job.Key, out bool distant) || distant != job.Distant) continue;
            if (result.Error != null)
            {
                _failed.Add(job.Key);
                GD.PushError($"Chunk {job.Key} failed: {result.Error}");
                continue;
            }
            var data = result.Data;
            if (data == null) continue;
            n++;
            if (_chunks.Remove(data.Key, out var old)) old.Root.QueueFree();

            var chunk = new Chunk { Root = new Node3D { Name = $"Chunk_{data.Key.X}_{data.Key.Y}" },
                Distant = data.Distant, Colliders = data.Colliders };
            chunk.Root.AddChild(BuildTerrain(data));
            foreach (var batch in data.Batches)
            {
                if (batch.Kind == PropKind.Plant)
                {
                    if (_plantScale > 0.05f) AddPlants(chunk, batch);
                    continue;
                }
                if (batch.Kind == PropKind.Tree || batch.Kind == PropKind.TreeImpostor)
                {
                    if (DebugNoTrees) continue;
                    if (batch.Kind == PropKind.Tree) AddTreeLods(chunk, batch);
                    else AddTreeImpostors(chunk, batch);
                    continue;
                }
                var (mesh, material) = _propAssets[(batch.Kind, batch.Variant)];
                var mm = new MultiMesh
                {
                    TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                    UseColors = true,
                    Mesh = mesh,
                    InstanceCount = batch.Count,
                    Buffer = batch.Buffer,
                };
                bool grass = batch.Kind == PropKind.Grass;
                var node = new MultiMeshInstance3D
                {
                    Multimesh = mm,
                    MaterialOverride = material,
                    CastShadow = !grass && CastPropShadows
                        ? GeometryInstance3D.ShadowCastingSetting.On
                        : GeometryInstance3D.ShadowCastingSetting.Off,
                    // Wind moves vertices up to ~0.5 m outside the static bounds.
                    ExtraCullMargin = 0.5f,
                    // Grass is left out of the water reflection (see Water.cs).
                    Layers = grass ? Water.GrassLayer : 1,
                };
                chunk.Root.AddChild(node);
                if (grass) chunk.Grass.Add((node, batch));
                else chunk.Props.Add(node);
            }
            SetVisibility(chunk, data.Key);
            AddChild(chunk.Root);
            _chunks[data.Key] = chunk;
            created = true;
        }
        return created;
    }

    /// <summary>Full trees of one species/model in the chunk; per-instance custom data = leaf tint, birch.</summary>
    void AddTreeLods(Chunk chunk, InstanceBatch batch)
    {
        var (kind, model) = TreeKind.Combos[batch.Variant];
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            UseCustomData = true,
            Mesh = TreeAssets.Get(TreeKind.All[kind].Models[model]).Mesh,
            InstanceCount = batch.Count,
            Buffer = batch.Buffer,
        };
        // Near trees cast shadows; from _treeShadowEnd on, the same trees are drawn without them.
        // Tree shadows were the single biggest cost (all those leaf cards redrawn into each
        // shadow cascade), and far ones are barely visible through the haze anyway.
        bool shadows = CastPropShadows && !DebugNoTreeShadows;
        float split = Mathf.Min(_treeShadowEnd, _treeDetail);
        if (split > 0) AddRanged(chunk, mm, batch.Center, 0, split, shadows);
        chunk.Trees.Add(AddRanged(chunk, mm, batch.Center, split, TreeDetailDistance, false));
    }

    /// <summary>One undergrowth model in the chunk: drawn only nearby, no shadows, not in the water mirror.</summary>
    void AddPlants(Chunk chunk, InstanceBatch batch)
    {
        var k = PlantKind.All[batch.Variant];
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            UseCustomData = true,
            Mesh = TreeAssets.Get(k.Model).Mesh,
            InstanceCount = batch.Count,
            Buffer = batch.Buffer,
        };
        var node = AddRanged(chunk, mm, batch.Center, 0, k.DrawDistance * _plantScale, false);
        node.Layers = Water.GrassLayer;
        chunk.Plants.Add((node, k.DrawDistance * _plantScale));
    }

    /// <summary>All of the chunk's far trees as camera-facing pictures, in one draw call.</summary>
    void AddTreeImpostors(Chunk chunk, InstanceBatch batch)
    {
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            UseCustomData = true,
            Mesh = _impostors.Quad,
            InstanceCount = batch.Count,
            Buffer = batch.Buffer,
        };
        var node = AddRanged(chunk, mm, batch.Center, chunk.Distant ? 0 : TreeDetailDistance, ImpostorDistance(), false);
        node.MaterialOverride = _impostors.Material;
        // Billboards turn to the camera, so their bounds must cover any rotation.
        node.ExtraCullMargin = 12f;
        chunk.Impostors.Add(node);
    }

    float ImpostorDistance()
    {
        float end = (_photoActive ? PhotoRadius : PropRadius) * ChunkSize + ChunkSize * 0.5f;
        foreach (var arg in OS.GetCmdlineUserArgs())
            if (arg.StartsWith("--tree-end=")) end = float.Parse(arg.Substring(11), System.Globalization.CultureInfo.InvariantCulture);
        return end;
    }

    MultiMeshInstance3D AddRanged(Chunk chunk, MultiMesh mm, Vector2 center, float begin, float end, bool shadows)
    {
        var node = new MultiMeshInstance3D
        {
            Multimesh = mm,
            Position = new Vector3(center.X, 0, center.Y),
            CastShadow = shadows ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 1f,
            VisibilityRangeBegin = begin,
            // No margins: with fading off they act as hysteresis, and a chunk built while it is
            // inside the margin band starts hidden in both its full and impostor forms.
            VisibilityRangeEnd = end,
            VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled,
        };
        chunk.Root.AddChild(node);
        return node;
    }

    MeshInstance3D BuildTerrain(ChunkData data)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = data.Vertices;
        arrays[(int)Mesh.ArrayType.Normal] = data.Normals;
        arrays[(int)Mesh.ArrayType.TexUV] = _uvs;
        arrays[(int)Mesh.ArrayType.TexUV2] = data.TrailEdges;
        arrays[(int)Mesh.ArrayType.Index] = _indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, _terrainMaterial);
        return new MeshInstance3D
        {
            Mesh = mesh,
            Position = new Vector3(data.Key.X * ChunkSize, 0, data.Key.Y * ChunkSize),
        };
    }

    void SetVisibility(Chunk chunk, Vector2I key)
    {
        int dx = key.X - _playerChunk.X, dz = key.Y - _playerChunk.Y;
        int distSq = dx * dx + dz * dz;
        bool props = distSq <= PropRadius * PropRadius * _detailScale * _detailScale + 1;
        bool grass = distSq <= GrassRadius * GrassRadius * _detailScale * _detailScale + 1;
        foreach (var node in chunk.Props)
        {
            node.Visible = props;
            node.CastShadow = CastPropShadows && distSq <= PropRadius * PropRadius + 1
                ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off;
        }
        foreach (var (node, _) in chunk.Grass) node.Visible = grass;
        float detailEnd = TreeDetailDistance;
        foreach (var node in chunk.Trees) node.VisibilityRangeEnd = detailEnd;
        foreach (var (node, distance) in chunk.Plants) node.VisibilityRangeEnd = distance * _detailScale;
        float treeEnd = ImpostorDistance();
        foreach (var node in chunk.Impostors)
        {
            node.VisibilityRangeBegin = chunk.Distant ? 0 : detailEnd;
            node.VisibilityRangeEnd = treeEnd;
        }
    }

    void ApplyGrassLod(Chunk chunk, Vector2 at)
    {
        foreach (var (node, batch) in chunk.Grass)
        {
            if (!node.Visible) continue;
            float distance = Mathf.Max(0, batch.Center.DistanceTo(at) - batch.Radius) / _detailScale;
            float t = Mathf.Clamp((distance - GrassLodNear) / (GrassLodFar - GrassLodNear), 0, 1);
            float fraction = 1 - (1 - GrassLodMinFraction) * t * t * (3 - 2 * t);
            node.Multimesh.VisibleInstanceCount = Mathf.Max(1, Mathf.RoundToInt(batch.Count * fraction));
        }
    }

    /// <summary>
    /// Distance along a ray to the first thing it hits: the ground, the water surface, or a
    /// tree trunk or boulder. Used by photo mode's autofocus. Returns <paramref name="max"/> on a miss.
    /// </summary>
    public float MeasureDistance(Vector3 origin, Vector3 dir, float max = 400f)
    {
        float best = max, previous = 0;
        bool Below(float d)
        {
            var p = origin + dir * d;
            return p.Y < Mathf.Max((float)Terrain.Height(p.X, p.Z), (float)Terrain.WaterLevel);
        }
        for (float t = 0.3f; t < max; t += Mathf.Max(0.25f, t * 0.04f))
        {
            if (Below(t))
            {
                float near = previous, far = t;
                for (int i = 0; i < 6; i++)
                {
                    float mid = (near + far) / 2;
                    if (Below(mid)) far = mid; else near = mid;
                }
                best = far;
                break;
            }
            previous = t;
        }
        // Vertical cylinders (trunks, boulders), within the distance found so far.
        float a = dir.X * dir.X + dir.Z * dir.Z;
        if (a > 1e-6f)
            foreach (var chunk in _chunks.Values)
                foreach (var c in chunk.Colliders)
                {
                    float ox = origin.X - c.X, oz = origin.Z - c.Z;
                    float b = ox * dir.X + oz * dir.Z;
                    float disc = b * b - a * (ox * ox + oz * oz - c.Radius * c.Radius);
                    if (disc < 0) continue;
                    float t = (-b - Mathf.Sqrt(disc)) / a;
                    if (t > 0.3f && t < best && origin.Y + dir.Y * t <= c.MaxY) best = t;
                }
        return best;
    }

    /// <summary>Pushes a cylinder of <paramref name="radius"/> out of tree trunks and boulders (slides around them).</summary>
    public Vector3 ResolveCollisions(Vector3 pos, float radius = 0.42f)
    {
        int pcx = Mathf.FloorToInt(pos.X / ChunkSize), pcz = Mathf.FloorToInt(pos.Z / ChunkSize);
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (!_chunks.TryGetValue(new Vector2I(pcx + dx, pcz + dz), out var chunk)) continue;
                foreach (var c in chunk.Colliders)
                {
                    if (pos.Y > c.MaxY) continue;
                    float vx = pos.X - c.X, vz = pos.Z - c.Z;
                    float minDist = c.Radius + radius;
                    float distSq = vx * vx + vz * vz;
                    if (distSq >= minDist * minDist) continue;
                    if (distSq > 1e-6f)
                    {
                        float dist = Mathf.Sqrt(distSq);
                        float push = (minDist - dist) / dist;
                        pos.X += vx * push;
                        pos.Z += vz * push;
                    }
                    else pos.X += minDist;
                }
            }
        return pos;
    }
}
