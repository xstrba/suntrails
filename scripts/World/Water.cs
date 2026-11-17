using Godot;

namespace World;

/// <summary>
/// Keeps the water plane centred on the player (waves are world-anchored in the shader) and
/// renders planar reflections: a second camera mirrored below the water surface draws the
/// scene into a half-resolution texture that the water shader samples.
/// All water sits at one height, so a single mirror is exact for every lake.
/// </summary>
public partial class Water : MeshInstance3D
{
    // Render layers: 1 = world, 2 = water, 3 = grass. The reflection skips water (it can't
    // reflect itself) and grass (tiny blades, expensive, invisible in a rippled reflection).
    public const uint WaterLayer = 1 << 1;
    public const uint GrassLayer = 1 << 2;

    [Export(PropertyHint.Range, "0.25,1")] public float ReflectionScale = 0.5f;

    /// <summary>Off: no mirror camera; the water reflects a plain sky colour instead (cheaper).</summary>
    public bool ReflectionsEnabled
    {
        get => _reflections;
        set
        {
            _reflections = value;
            if (_viewport != null)
                _viewport.RenderTargetUpdateMode = value ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
            _material?.SetShaderParameter("reflection_enabled", value);
        }
    }
    bool _reflections = true;

    Node3D _player;
    SubViewport _viewport;
    Camera3D _mirror;
    Godot.Environment _mainEnv, _mirrorEnv;
    ShaderMaterial _material;

    public ShaderMaterial Material => _material;

    public override void _Ready()
    {
        _player = GetNode<Node3D>("../Player");
        _material = (ShaderMaterial)((PlaneMesh)Mesh).Material;
        Layers = WaterLayer;

        // The mirror camera gets its own copy of the environment without tonemapping or
        // post effects: its image is linear HDR, and is tonemapped once as part of the water.
        _mainEnv = GetNode<WorldEnvironment>("../WorldEnvironment").Environment;
        _mirrorEnv = (Godot.Environment)_mainEnv.Duplicate();
        _mirrorEnv.TonemapMode = Godot.Environment.ToneMapper.Linear;
        _mirrorEnv.SsaoEnabled = false;
        _mirrorEnv.GlowEnabled = false;

        _viewport = new SubViewport
        {
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            UseHdr2D = true,
            Msaa3D = Viewport.Msaa.Disabled,
            PositionalShadowAtlasSize = 0,
        };
        _mirror = new Camera3D
        {
            CullMask = 0xFFFFF & ~(WaterLayer | GrassLayer),
            Environment = _mirrorEnv,
        };
        _viewport.AddChild(_mirror);
        AddChild(_viewport);
        // Benchmarking: `-- --no-reflection` turns the mirror camera off.
        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--no-reflection") >= 0)
            _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        _material.SetShaderParameter("reflection_tex", _viewport.GetTexture());
        ReflectionsEnabled = _reflections;
    }

    public override void _Process(double delta)
    {
        var p = _player.GlobalPosition;
        float level = (float)Terrain.WaterLevel;
        Position = new Vector3(p.X, level, p.Z);

        var main = GetViewport().GetCamera3D();
        if (main == null) return;

        var size = (Vector2I)((Vector2)GetViewport().GetVisibleRect().Size * ReflectionScale);
        if (_viewport.Size != size) _viewport.Size = size;

        // Mirror the camera across the water plane. Reflecting the basis flips handedness,
        // so its X axis is negated too; the shader undoes that by sampling with u flipped.
        var t = main.GlobalTransform;
        static Vector3 R(Vector3 v) => new(v.X, -v.Y, v.Z);
        var origin = t.Origin;
        origin.Y = 2 * level - origin.Y;
        _mirror.GlobalTransform = new Transform3D(new Basis(-R(t.Basis.Column0), R(t.Basis.Column1), R(t.Basis.Column2)), origin);
        _mirror.Fov = main.Fov;
        _mirror.Near = main.Near;
        _mirror.Far = main.Far;

        // DayNight animates the main environment; keep the mirror's lighting in step.
        _mirrorEnv.FogLightColor = _mainEnv.FogLightColor;
        _mirrorEnv.FogDensity = _mainEnv.FogDensity;
        _mirrorEnv.AmbientLightColor = _mainEnv.AmbientLightColor;
        _mirrorEnv.AmbientLightEnergy = _mainEnv.AmbientLightEnergy;
    }
}
