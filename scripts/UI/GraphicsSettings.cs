using Godot;

namespace World;

/// <summary>
/// Graphics options from the pause menu, saved to user://settings.cfg. Presets fill in every
/// option; changing any single option switches the preset to Custom.
/// </summary>
public sealed class GraphicsSettings
{
    public static readonly string[] PresetNames = { "Low", "Medium", "High", "Ultra", "Custom" };
    public const int Custom = 4;
    public static readonly string[] ShadowNames = { "Off", "Low", "Medium", "High" };
    public static readonly string[] ReflectionNames = { "Off", "Half resolution", "Full resolution" };

    public int Preset = 2;
    public int RenderDistance = 6;     // chunks (48 m) of terrain around you
    public float TreeDetail = 90;      // metres of full 3D trees; beyond, trees are pictures
    public int GrassDistance = 2;      // chunks
    public float Undergrowth = 1;      // multiplier on bush/fern/flower draw distances (0 = none)
    public int Shadows = 2;
    public int Reflections = 1;
    public bool AmbientOcclusion = true;
    public float RenderScale = 1;      // < 1 renders fewer pixels and upscales with AMD FSR
    public bool VSync = true;

    const string Path = "user://settings.cfg";

    public void ApplyPreset(int preset)
    {
        Preset = preset;
        (RenderDistance, TreeDetail, GrassDistance, Undergrowth, Shadows, Reflections, AmbientOcclusion, RenderScale) = preset switch
        {
            0 => (4, 45f, 1, 0.6f, 1, 0, false, 0.7f),
            1 => (5, 70f, 2, 0.85f, 2, 1, true, 0.85f),
            2 => (6, 90f, 2, 1f, 2, 1, true, 1f),
            3 => (8, 130f, 3, 1.4f, 3, 2, true, 1f),
            _ => (RenderDistance, TreeDetail, GrassDistance, Undergrowth, Shadows, Reflections, AmbientOcclusion, RenderScale),
        };
    }

    public static GraphicsSettings Load()
    {
        var s = new GraphicsSettings();
        var cfg = new ConfigFile();
        if (cfg.Load(Path) != Error.Ok) return s;
        s.Preset = (int)cfg.GetValue("graphics", "preset", s.Preset);
        s.RenderDistance = (int)cfg.GetValue("graphics", "render_distance", s.RenderDistance);
        s.TreeDetail = (float)cfg.GetValue("graphics", "tree_detail", s.TreeDetail);
        s.GrassDistance = (int)cfg.GetValue("graphics", "grass_distance", s.GrassDistance);
        s.Undergrowth = (float)cfg.GetValue("graphics", "undergrowth", s.Undergrowth);
        s.Shadows = (int)cfg.GetValue("graphics", "shadows", s.Shadows);
        s.Reflections = (int)cfg.GetValue("graphics", "reflections", s.Reflections);
        s.AmbientOcclusion = (bool)cfg.GetValue("graphics", "ambient_occlusion", s.AmbientOcclusion);
        s.RenderScale = (float)cfg.GetValue("graphics", "render_scale", s.RenderScale);
        s.VSync = (bool)cfg.GetValue("graphics", "vsync", s.VSync);
        return s;
    }

    public void Save()
    {
        var cfg = new ConfigFile();
        cfg.SetValue("graphics", "preset", Preset);
        cfg.SetValue("graphics", "render_distance", RenderDistance);
        cfg.SetValue("graphics", "tree_detail", TreeDetail);
        cfg.SetValue("graphics", "grass_distance", GrassDistance);
        cfg.SetValue("graphics", "undergrowth", Undergrowth);
        cfg.SetValue("graphics", "shadows", Shadows);
        cfg.SetValue("graphics", "reflections", Reflections);
        cfg.SetValue("graphics", "ambient_occlusion", AmbientOcclusion);
        cfg.SetValue("graphics", "render_scale", RenderScale);
        cfg.SetValue("graphics", "vsync", VSync);
        cfg.Save(Path);
    }

    /// <summary>Options that only affect newly built chunks (the world is rebuilt when they change).</summary>
    public (int, float, int, float, int) WorldKey => (RenderDistance, TreeDetail, GrassDistance, Undergrowth, Shadows);
}
