using System;
using System.Globalization;
using Godot;

namespace World;

/// <summary>
/// Port of SkyAndLighting (src/world/skyAndLighting.js) and the cloud tinting from clouds.js.
/// Moves the sun/moon light, blends sky, fog and ambient colours between keyframes, and
/// feeds the sky shader. Keys: 1-4 dawn/day/dusk/night, T pause, - / = slower/faster.
/// Debug: `-- --time=0.74` starts at that time of day, paused.
/// </summary>
public partial class DayNight : Node
{
    public static readonly (string Name, float Time)[] Presets =
    {
        ("Dawn", 0.26f), ("Day", 0.50f), ("Dusk", 0.74f), ("Night", 0.02f),
    };

    [Export(PropertyHint.Range, "0,1")] public float TimeOfDay = 0.5f;
    [Export] public float BaseCycleSeconds = 180f; // a full day at 1x speed
    [Export(PropertyHint.Range, "0,6,0.25")] public float Speed = 1f; // 0 = paused

    record Keyframe(float T, Color Zenith, Color Horizon, Color HemiSky, float HemiIntensity, float FogDensity);

    static readonly Keyframe[] Keys =
    {
        new(0.00f, new Color("04091a"), new Color("0d1c3a"), new Color("1f458a"), 0.48f, 0.0055f), // midnight
        new(0.22f, new Color("132042"), new Color("523b59"), new Color("4b4668"), 0.50f, 0.0050f), // pre-dawn
        new(0.27f, new Color("2d5894"), new Color("f49862"), new Color("ffb58a"), 0.72f, 0.0042f), // dawn
        new(0.36f, new Color("286ec8"), new Color("9bd0f5"), new Color("aad8ff"), 0.85f, 0.0036f), // morning
        new(0.50f, new Color("1e64c8"), new Color("a4d7fa"), new Color("bce0ff"), 0.95f, 0.0034f), // noon
        new(0.68f, new Color("285ba8"), new Color("9bc4e8"), new Color("9fc7f0"), 0.80f, 0.0038f), // late afternoon
        new(0.75f, new Color("1f2f66"), new Color("e86b42"), new Color("f58e62"), 0.65f, 0.0046f), // sunset
        new(0.82f, new Color("091126"), new Color("252a48"), new Color("2b3b66"), 0.45f, 0.0052f), // twilight
    };

    static readonly Color CloudNight = new("354158");
    static readonly Color CloudDay = new("f8fafc");
    static readonly Color CloudWarm = new("ffbe8a");
    static readonly Color MoonLight = new("b9c9e2");

    DirectionalLight3D _light;
    Godot.Environment _env;
    ShaderMaterial _sky;
    Water _water;
    ChunkManager _chunks;
    float _fogDistanceScale = 1;
    float _lastSpeed = 1f;
    double _cloudTime;

    /// <summary>Unit direction toward the sun (y &lt; 0 when it is below the horizon).</summary>
    public Vector3 SunDirection { get; private set; } = Vector3.Up;

    public string Clock
    {
        get
        {
            int minutes = (int)(TimeOfDay * 24 * 60) % 1440;
            return $"{minutes / 60:00}:{minutes % 60:00}";
        }
    }

    public string Phase => TimeOfDay switch
    {
        >= 0.22f and < 0.32f => "Dawn",
        >= 0.32f and < 0.70f => "Day",
        >= 0.70f and < 0.80f => "Dusk",
        _ => "Night",
    };

    public override void _Ready()
    {
        _light = GetNode<DirectionalLight3D>("../Sun");
        _env = GetNode<WorldEnvironment>("../WorldEnvironment").Environment;
        _sky = (ShaderMaterial)_env.Sky.SkyMaterial;
        _water = GetNodeOrNull<Water>("../Water");
        _chunks = GetNodeOrNull<ChunkManager>("../ChunkManager");
        foreach (var arg in OS.GetCmdlineUserArgs())
            if (arg.StartsWith("--time="))
            {
                TimeOfDay = float.Parse(arg.Substring(7), CultureInfo.InvariantCulture);
                Speed = 0;
            }
        Apply();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } key) return;
        switch (key.PhysicalKeycode)
        {
            case Key.Key1: TimeOfDay = Presets[0].Time; break;
            case Key.Key2: TimeOfDay = Presets[1].Time; break;
            case Key.Key3: TimeOfDay = Presets[2].Time; break;
            case Key.Key4: TimeOfDay = Presets[3].Time; break;
            case Key.T:
                if (Speed > 0) { _lastSpeed = Speed; Speed = 0; }
                else Speed = _lastSpeed;
                break;
            case Key.Minus: Speed = Mathf.Max(0, Speed - 0.25f); break;
            case Key.Equal: Speed = Mathf.Min(6, Speed + 0.25f); break;
        }
    }

    public override void _Process(double delta)
    {
        float dt = Mathf.Min((float)delta, 0.1f);
        if (Speed > 0) TimeOfDay = Mathf.PosMod(TimeOfDay + dt * Speed / BaseCycleSeconds, 1f);
        _cloudTime += dt; // clouds keep drifting even while the clock is paused
        _fogDistanceScale = Mathf.Lerp(_fogDistanceScale, _chunks?.AtmosphereDistanceScale ?? 1,
            1 - Mathf.Exp(-4 * dt));
        Apply();
    }

    void Apply()
    {
        // Sun and moon on opposite sides of a slightly tilted orbit, for scenic shadows.
        float angle = (TimeOfDay - 0.25f) * Mathf.Tau;
        var sunDir = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), Mathf.Sin(angle) * 0.32f).Normalized();
        var moonDir = -sunDir;
        SunDirection = sunDir;

        var (zenith, horizon, hemiSky, hemi, fog) = Sample(TimeOfDay);

        // The single shadow-casting light follows the sun by day and the moon by night.
        bool day = sunDir.Y >= -0.02f;
        var active = day ? sunDir : moonDir;
        float elevation = Mathf.Max(0, active.Y);
        float horizonFade = Mathf.SmoothStep(0, 0.12f, elevation);
        Color lightColor;
        float energy;
        if (day)
        {
            float warm = Mathf.Clamp(sunDir.Y / 0.35f, 0, 1);
            lightColor = new Color(1f, 0.62f + 0.34f * warm, 0.38f + 0.54f * warm);
            energy = (0.6f + 1.8f * Mathf.Sqrt(elevation)) * horizonFade;
        }
        else
        {
            lightColor = MoonLight;
            energy = (0.08f + 0.16f * Mathf.Sqrt(elevation)) * horizonFade;
        }
        // three.js intensities are roughly twice Godot's light energy for the same look.
        _light.LightColor = lightColor;
        _light.LightEnergy = energy * 0.55f;
        _light.Visible = energy > 0.001f;
        var up = Mathf.Abs(active.Y) > 0.99f ? Vector3.Right : Vector3.Up;
        _light.GlobalBasis = Basis.LookingAt(-active, up);

        _env.FogLightColor = horizon;
        // Toned down from the web values: Godot's sky also lights the scene (ambient + reflections)
        // on top of these, which washed out the noon colours.
        _env.FogDensity = fog * 0.8f / _fogDistanceScale;
        _env.AmbientLightColor = hemiSky;
        _env.AmbientLightEnergy = hemi * 0.65f;

        // Clouds: dark blue-grey at night, white by day, warm near sunrise and sunset.
        float daylight = Mathf.SmoothStep(-0.16f, 0.28f, sunDir.Y);
        float warmth = (1 - Mathf.SmoothStep(0.05f, 0.4f, Mathf.Abs(sunDir.Y))) * daylight;
        var cloud = CloudNight.Lerp(CloudDay, daylight).Lerp(CloudWarm, warmth * 0.65f);
        float night = Mathf.Clamp((-sunDir.Y - 0.02f) / 0.25f, 0, 1);

        _sky.SetShaderParameter("zenith_color", zenith);
        _sky.SetShaderParameter("horizon_color", horizon);
        _sky.SetShaderParameter("sun_dir", sunDir);
        _sky.SetShaderParameter("sun_color", day ? lightColor : new Color(1, 0.6f, 0.38f));
        _sky.SetShaderParameter("sun_visibility", Mathf.SmoothStep(-0.12f, 0.02f, sunDir.Y));
        _sky.SetShaderParameter("night", night);
        _sky.SetShaderParameter("star_rotation", TimeOfDay * Mathf.Pi * 0.25f);
        _sky.SetShaderParameter("cloud_color", cloud);
        _sky.SetShaderParameter("cloud_opacity", 0.5f + 0.28f * daylight);
        _sky.SetShaderParameter("cloud_time", (float)(_cloudTime % 100000.0));

        // The water's sun/moon glint follows whichever body is lighting the scene.
        var water = _water?.Material;
        if (water != null)
        {
            water.SetShaderParameter("sun_dir", active);
            water.SetShaderParameter("sky_fallback", horizon.Lerp(zenith, 0.35f));
            water.SetShaderParameter("sun_color", lightColor);
            water.SetShaderParameter("sun_intensity", day ? horizonFade : horizonFade * 0.35f);
            water.SetShaderParameter("light_energy", _light.Visible ? _light.LightEnergy : 0f);
            water.SetShaderParameter("ambient_light", hemiSky.SrgbToLinear() * _env.AmbientLightEnergy * 0.5f);
        }
    }

    static (Color zenith, Color horizon, Color hemiSky, float hemi, float fog) Sample(float t)
    {
        Keyframe k0 = Keys[^1], k1 = Keys[0];
        for (int i = 0; i < Keys.Length; i++)
            if (Keys[i].T <= t)
            {
                k0 = Keys[i];
                k1 = Keys[(i + 1) % Keys.Length];
            }
        float span = k1.T > k0.T ? k1.T - k0.T : 1 - k0.T + k1.T;
        float dist = t >= k0.T ? t - k0.T : 1 - k0.T + t;
        float a = span > 0 ? Math.Clamp(dist / span, 0, 1) : 0;
        return (k0.Zenith.Lerp(k1.Zenith, a), k0.Horizon.Lerp(k1.Horizon, a), k0.HemiSky.Lerp(k1.HemiSky, a),
            Mathf.Lerp(k0.HemiIntensity, k1.HemiIntensity, a), Mathf.Lerp(k0.FogDensity, k1.FogDensity, a));
    }
}
