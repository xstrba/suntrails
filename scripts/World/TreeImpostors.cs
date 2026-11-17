using Godot;

namespace World;

/// <summary>
/// Far trees as impostors: at startup every species variant is rendered once, from the side,
/// into one texture atlas. Beyond the mesh LOD range each tree is then a single camera-facing
/// quad showing its picture. All species share the atlas, so a chunk's far trees are one draw
/// call and two triangles per tree, instead of a few hundred triangles and several draws.
/// </summary>
public partial class TreeImpostors : Node
{
    public const int Cols = 6;     // atlas is Cols x Cols cells, one per species/model pair (27 now)
    const int CellPx = 256;

    public ShaderMaterial Material { get; private set; }
    public Mesh Quad { get; private set; }

    /// <summary>Atlas cell of a species variant, as (column, row).</summary>
    public static Vector2 Cell(int index) => new(index % Cols, index / Cols);

    public override void _Ready()
    {
        Material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/tree_impostor.gdshader") };
        Material.SetShaderParameter("cells", (float)Cols);

        // Unit quad: x in [-0.5, 0.5], y in [0, 1] (base at the tree's foot). UV2 keeps those
        // local coordinates for the billboard maths, since the shader works in world space.
        var q = new MeshBuilder();
        var corners = new[] { new Vector2(-0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 1), new Vector2(-0.5f, 1) };
        foreach (var c in corners)
        {
            q.Positions.Add(new Vector3(c.X, c.Y, 0));
            q.Normals.Add(Vector3.Back);
            q.UVs.Add(new Vector2(c.X + 0.5f, 1 - c.Y));
            q.UV2s.Add(c);
        }
        q.Indices.AddRange(new[] { 0, 1, 2, 0, 2, 3 });
        Quad = q.ToArrayMesh();
    }

    /// <summary>Renders every species/model pair into the atlas (a couple of frames each).</summary>
    public async void Capture()
    {
        var viewport = new SubViewport
        {
            Size = new Vector2I(CellPx, CellPx),
            TransparentBg = true,
            OwnWorld3D = true,
            Msaa3D = Viewport.Msaa.Msaa4X,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        // Flat white ambient light only: the picture holds the tree's own colours, and the
        // impostor shader lights it in the scene like any other surface.
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.ClearColor,
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = Colors.White,
            AmbientLightEnergy = 1.0f,
            ReflectedLightSource = Godot.Environment.ReflectionSource.Disabled,
            TonemapMode = Godot.Environment.ToneMapper.Linear,
        };
        var camera = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, Environment = env, Near = 1, Far = 400 };
        // A one-tree MultiMesh, so the tree shaders get their per-instance tint like in the world.
        var holder = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseColors = true,
                UseCustomData = true,
                InstanceCount = 0,
            },
        };
        viewport.AddChild(camera);
        viewport.AddChild(holder);
        AddChild(viewport);

        var atlas = Image.CreateEmpty(CellPx * Cols, CellPx * Cols, false, Image.Format.Rgba8);
        for (int index = 0; index < TreeKind.Combos.Count; index++)
        {
            var (kind, model) = TreeKind.Combos[index];
            var k = TreeKind.All[kind];
            var asset = TreeAssets.Get(k.Models[model]);
            float frame = asset.FrameSize;
            var mm = holder.Multimesh;
            mm.InstanceCount = 0;
            mm.Mesh = asset.Mesh;
            mm.InstanceCount = 1;
            mm.SetInstanceTransform(0, Transform3D.Identity);
            mm.SetInstanceColor(0, Colors.White);
            var leaves = k.Leaves.SrgbToLinear();
            mm.SetInstanceCustomData(0, new Color(leaves.R, leaves.G, leaves.B, k.Birch));
            camera.Size = frame;
            camera.Position = new Vector3(0, frame / 2, 150);
            // Two frames: one to render with the new mesh, one for the image to be readable.
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            var img = viewport.GetTexture().GetImage();
            img.Convert(Image.Format.Rgba8);
            var cell = Cell(index);
            atlas.BlitRect(img, new Rect2I(0, 0, CellPx, CellPx), new Vector2I((int)cell.X * CellPx, (int)cell.Y * CellPx));
        }
        viewport.QueueFree();

        // Spread leaf colours into the transparent gaps so mipmaps don't get dark halos.
        atlas.FixAlphaEdges();
        atlas.GenerateMipmaps();
        Material.SetShaderParameter("atlas", ImageTexture.CreateFromImage(atlas));
        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--save-impostors") >= 0)
            atlas.SavePng("user://tree_impostors.png");
    }
}
