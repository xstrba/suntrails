using Godot;

namespace World;

/// <summary>
/// FPS, position, biome and chunk stats. Run with `-- --screenshot=out.png` to save a
/// frame once the world has loaded and quit (handy for automated visual checks).
/// </summary>
public partial class Hud : CanvasLayer
{
    Label _label;
    Player _player;
    ChunkManager _chunks;
    DayNight _dayNight;
    PhotoMode _photo;
    string _screenshotPath;
    double _elapsed;
    double _shotDelay = 4;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always; // FPS/stats keep updating (and screenshots work) while paused
        _player = GetNode<Player>("../Player");
        _chunks = GetNode<ChunkManager>("../ChunkManager");
        _dayNight = GetNode<DayNight>("../DayNight");
        _photo = GetNodeOrNull<PhotoMode>("../PhotoMode");
        _label = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = Control.MouseFilterEnum.Ignore };
        _label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.7f));
        _label.AddThemeConstantOverride("shadow_offset_x", 1);
        _label.AddThemeConstantOverride("shadow_offset_y", 1);
        AddChild(_label);
        _label.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopWide);
        _label.OffsetTop = 10;
        _label.OffsetLeft = 14;
        _label.OffsetRight = -14;

        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--screenshot=")) _screenshotPath = arg.Substring("--screenshot=".Length);
            // Benchmarking: show the real frame rate instead of the monitor's refresh cap.
            if (arg.StartsWith("--shot-delay=")) _shotDelay = double.Parse(arg.Substring(13), System.Globalization.CultureInfo.InvariantCulture);
            if (arg == "--no-vsync") DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        }
    }

    public override void _Process(double delta)
    {
        _label.Visible = _photo == null || !_photo.Active; // the viewfinder replaces the HUD
        var p = _player.GlobalPosition;
        var (_, ny, _) = Terrain.Normal(p.X, p.Z);
        string biome = UiText.Get(Terrain.BiomeName(Terrain.Height(p.X, p.Z), Terrain.Moisture(p.X, p.Z), 1 - ny));
        string hint = Input.MouseMode == Input.MouseModeEnum.Captured
            ? UiText.Get("WASD move · Shift run · Space jump · C toggle crouch · Z toggle prone · Esc menu\n1-4 dawn/day/dusk/night · T pause time · - / = time speed · P photo mode · G gallery")
            : UiText.Get("Click to look around · G gallery");
        // Rendering stats for the whole frame (all viewports, including shadows and the water mirror).
        var rs = RenderingServer.Singleton;
        string stats = UiText.Format("{0:0.00}M tris, {1} draws, {2} objects",
            rs.GetRenderingInfo(RenderingServer.RenderingInfo.TotalPrimitivesInFrame) / 1e6,
            rs.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame),
            rs.GetRenderingInfo(RenderingServer.RenderingInfo.TotalObjectsInFrame));
        string speed = _dayNight.Speed > 0 ? string.Create(UiText.Culture, $"{_dayNight.Speed:0.##}×") : UiText.Get("paused");
        _label.Text = $"{Engine.GetFramesPerSecond()} FPS  ·  {stats}\n" +
                      string.Create(UiText.Culture, $"X {p.X:0.0}  Y {p.Y:0.0}  Z {p.Z:0.0}\n") +
                      $"{biome}\n" +
                      $"{UiText.Get(_dayNight.Phase)} {_dayNight.Clock}  ({speed})\n" +
                      UiText.Format("Chunks {0} (+{1} generating)", _chunks.LoadedChunks, _chunks.PendingChunks) + "\n" +
                      hint;

        if (_screenshotPath == null) return;
        _elapsed += delta;
        if (_elapsed > _shotDelay && _chunks.PendingChunks == 0)
        {
            GetViewport().GetTexture().GetImage().SavePng(_screenshotPath);
            GD.Print($"Saved screenshot to {_screenshotPath}");
            GetTree().Quit();
        }
    }
}
