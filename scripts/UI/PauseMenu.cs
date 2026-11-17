using System;
using System.Globalization;
using Godot;

namespace World;

/// <summary>
/// Esc: pauses the game and opens the settings (quality preset, render distance, tree detail,
/// grass and undergrowth distance, shadows, water reflections, ambient occlusion, resolution
/// scale, vsync). Most options apply at once; distance options rebuild the world on Resume.
/// Settings are saved to user://settings.cfg and loaded at startup.
/// Debug: `-- --preset=low|medium|high|ultra`, `-- --menu` (start with the menu open).
/// </summary>
public partial class PauseMenu : CanvasLayer
{
    GraphicsSettings _s;
    (int, float, int, float, int) _builtWith;
    bool _syncing;

    ChunkManager _chunks;
    Water _water;
    DirectionalLight3D _sun;
    Godot.Environment _env;

    Control _root;
    OptionButton _preset, _shadows, _reflections;
    HSlider _distance, _trees, _grass, _undergrowth, _scale;
    Label _distanceValue, _treesValue, _grassValue, _undergrowthValue, _scaleValue;
    CheckButton _ao, _vsync;

    public bool IsOpen => _root.Visible;

    public override void _Ready()
    {
        Layer = 20;
        ProcessMode = ProcessModeEnum.Always; // keeps working while the game is paused
        _chunks = GetNode<ChunkManager>("../ChunkManager");
        _water = GetNode<Water>("../Water");
        _sun = GetNode<DirectionalLight3D>("../Sun");
        _env = GetNode<WorldEnvironment>("../WorldEnvironment").Environment;

        UiText.LoadLanguage();
        _s = GraphicsSettings.Load();
        bool open = false;
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--preset="))
            {
                int i = Array.FindIndex(GraphicsSettings.PresetNames, n => n.Equals(arg[9..], StringComparison.OrdinalIgnoreCase));
                if (i >= 0 && i != GraphicsSettings.Custom) _s.ApplyPreset(i);
            }
            if (arg == "--menu") open = true;
            if (arg.StartsWith("--tree-detail=")) { _s.TreeDetail = float.Parse(arg[14..], CultureInfo.InvariantCulture); _s.Preset = GraphicsSettings.Custom; }
        }
        BuildUi();
        ApplyImmediate();
        _chunks.ApplySettings(_s, rebuild: false);
        _builtWith = _s.WorldKey;
        SyncControls();
        _root.Visible = false;
        if (open) GetTree().CreateTimer(7).Timeout += Open;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.Escape }) return;
        if (IsOpen) Resume(); else Open();
        GetViewport().SetInputAsHandled();
    }

    void Open()
    {
        if (GetTree().Paused) return;
        SyncControls();
        _root.Visible = true;
        GetTree().Paused = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    void Resume()
    {
        _root.Visible = false;
        GetTree().Paused = false;
        Input.MouseMode = Input.MouseModeEnum.Captured;
        _s.Save();
        // Distances are baked into chunks when they are built: rebuild only if they changed.
        bool rebuild = !_s.WorldKey.Equals(_builtWith);
        _chunks.ApplySettings(_s, rebuild);
        _builtWith = _s.WorldKey;
    }

    /// <summary>Options that take effect right away.</summary>
    void ApplyImmediate()
    {
        // Shadows: distance, cascades and shadow-map resolution per level.
        _sun.ShadowEnabled = _s.Shadows > 0;
        var (dist, mode, size) = _s.Shadows switch
        {
            1 => (45f, DirectionalLight3D.ShadowMode.Parallel2Splits, 1024),
            2 => (70f, DirectionalLight3D.ShadowMode.Parallel2Splits, 2048),
            _ => (110f, DirectionalLight3D.ShadowMode.Parallel4Splits, 4096),
        };
        _sun.DirectionalShadowMaxDistance = dist;
        _sun.DirectionalShadowMode = mode;
        RenderingServer.DirectionalShadowAtlasSetSize(size, true);

        _water.ReflectionsEnabled = _s.Reflections > 0;
        _water.ReflectionScale = _s.Reflections >= 2 ? 1f : 0.5f;
        _env.SsaoEnabled = _s.AmbientOcclusion;

        var vp = GetViewport();
        vp.Scaling3DMode = _s.RenderScale < 0.99f ? Viewport.Scaling3DModeEnum.Fsr : Viewport.Scaling3DModeEnum.Bilinear;
        vp.Scaling3DScale = _s.RenderScale;
        DisplayServer.WindowSetVsyncMode(_s.VSync ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
        foreach (var arg in OS.GetCmdlineUserArgs()) // benchmarking override
            if (arg == "--no-vsync") DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
    }

    // ---- UI -----------------------------------------------------------------------------------

    void BuildUi()
    {
        _root = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);

        var dim = new ColorRect { Color = new Color(0.02f, 0.04f, 0.06f, 0.55f) };
        dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(dim);

        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(center);

        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.1f, 0.12f, 0.92f),
            CornerRadiusTopLeft = 12, CornerRadiusTopRight = 12, CornerRadiusBottomLeft = 12, CornerRadiusBottomRight = 12,
            ContentMarginLeft = 32, ContentMarginRight = 32, ContentMarginTop = 24, ContentMarginBottom = 24,
            BorderColor = new Color(1, 1, 1, 0.08f), BorderWidthTop = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
        });
        center.AddChild(panel);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 14);
        panel.AddChild(box);

        var title = new Label { Text = "Paused", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 30);
        box.AddChild(title);

        var languageRow = new HBoxContainer();
        languageRow.AddThemeConstantOverride("separation", 18);
        box.AddChild(languageRow);
        languageRow.AddChild(new Label { Text = "Language", CustomMinimumSize = new Vector2(220, 0) });
        var language = new OptionButton { Name = "Language", CustomMinimumSize = new Vector2(260, 0) };
        language.AddItem("English");
        language.AddItem("Čeština");
        language.Select(UiText.Language == UiText.Czech ? 1 : 0);
        language.ItemSelected += i =>
        {
            UiText.ChangeLanguage(i == 1 ? UiText.Czech : UiText.English);
            SyncControls();
        };
        languageRow.AddChild(language);
        box.AddChild(Section("Graphics"));

        var grid = new GridContainer { Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", 18);
        grid.AddThemeConstantOverride("v_separation", 10);
        box.AddChild(grid);

        _preset = Options(grid, "Quality preset", GraphicsSettings.PresetNames, i =>
        {
            if (i == GraphicsSettings.Custom) return;
            _s.ApplyPreset(i);
            ApplyImmediate();
            SyncControls();
        });
        (_distance, _distanceValue) = Slider(grid, "Render distance", 3, 9, 1, v => _s.RenderDistance = (int)v);
        (_trees, _treesValue) = Slider(grid, "Full tree detail up to", 30, 150, 10, v => _s.TreeDetail = (float)v);
        (_grass, _grassValue) = Slider(grid, "Grass distance", 1, 3, 1, v => _s.GrassDistance = (int)v);
        (_undergrowth, _undergrowthValue) = Slider(grid, "Bushes, ferns, flowers", 0, 1.5, 0.1, v => _s.Undergrowth = (float)v);
        _shadows = Options(grid, "Shadows", GraphicsSettings.ShadowNames, i => { _s.Shadows = i; Changed(); });
        _reflections = Options(grid, "Water reflections", GraphicsSettings.ReflectionNames, i => { _s.Reflections = i; Changed(); });
        _ao = Toggle(grid, "Ambient occlusion", on => { _s.AmbientOcclusion = on; Changed(); });
        (_scale, _scaleValue) = Slider(grid, "Resolution scale", 50, 100, 5, v => _s.RenderScale = (float)(v / 100));
        _vsync = Toggle(grid, "VSync", on => { _s.VSync = on; Changed(); });

        var note = new Label
        {
            Text = "Lower resolution scale is the biggest speed-up: the image is upscaled with AMD FSR.\nDistance changes rebuild the world when you resume.",
            Modulate = new Color(1, 1, 1, 0.6f),
        };
        note.AddThemeFontSizeOverride("font_size", 14);
        box.AddChild(note);

        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride("separation", 16);
        box.AddChild(buttons);
        var resume = new Button { Text = "Resume", CustomMinimumSize = new Vector2(160, 40) };
        resume.Pressed += Resume;
        buttons.AddChild(resume);
        var quit = new Button { Text = "Quit to desktop", CustomMinimumSize = new Vector2(160, 40) };
        quit.Pressed += () => { _s.Save(); GetTree().Quit(); };
        buttons.AddChild(quit);
    }

    /// <summary>Any single option change makes the preset "Custom" and applies what can apply now.</summary>
    void Changed()
    {
        if (_syncing) return;
        _s.Preset = GraphicsSettings.Custom;
        ApplyImmediate();
        SyncControls();
    }

    void SyncControls()
    {
        _syncing = true;
        _preset.Selected = _s.Preset;
        _distance.Value = _s.RenderDistance;
        _trees.Value = _s.TreeDetail;
        _grass.Value = _s.GrassDistance;
        _undergrowth.Value = _s.Undergrowth;
        _shadows.Selected = _s.Shadows;
        _reflections.Selected = _s.Reflections;
        _ao.ButtonPressed = _s.AmbientOcclusion;
        _scale.Value = Math.Round(_s.RenderScale * 100);
        _vsync.ButtonPressed = _s.VSync;
        _distanceValue.Text = string.Create(UiText.Culture, $"{_s.RenderDistance * ChunkManager.ChunkSize:0} m");
        _treesValue.Text = string.Create(UiText.Culture, $"{_s.TreeDetail:0} m");
        _grassValue.Text = string.Create(UiText.Culture, $"{(_s.GrassDistance + 0.5f) * ChunkManager.ChunkSize:0} m");
        _undergrowthValue.Text = _s.Undergrowth < 0.05f ? "Off" : string.Create(UiText.Culture, $"{_s.Undergrowth * 100:0}%");
        _scaleValue.Text = _s.RenderScale >= 0.99f ? "100%" : string.Create(UiText.Culture, $"{_s.RenderScale * 100:0}% FSR");
        _syncing = false;
    }

    static Label Section(string text)
    {
        var l = new Label { Text = text, Uppercase = true, Modulate = new Color(1, 0.82f, 0.48f) };
        l.AddThemeFontSizeOverride("font_size", 14);
        return l;
    }

    static Label RowLabel(GridContainer grid, string text)
    {
        var l = new Label { Text = text, CustomMinimumSize = new Vector2(220, 0) };
        grid.AddChild(l);
        return l;
    }

    OptionButton Options(GridContainer grid, string label, string[] items, Action<int> onPick)
    {
        RowLabel(grid, label);
        var o = new OptionButton { CustomMinimumSize = new Vector2(260, 0) };
        foreach (var item in items) o.AddItem(item);
        o.ItemSelected += i => { if (!_syncing) onPick((int)i); };
        grid.AddChild(o);
        grid.AddChild(new Control());
        return o;
    }

    (HSlider, Label) Slider(GridContainer grid, string label, double min, double max, double step, Action<double> onChange)
    {
        RowLabel(grid, label);
        var s = new HSlider { MinValue = min, MaxValue = max, Step = step, CustomMinimumSize = new Vector2(260, 24), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        var value = new Label { CustomMinimumSize = new Vector2(90, 0) };
        s.ValueChanged += v =>
        {
            if (_syncing) return;
            onChange(v);
            Changed();
        };
        grid.AddChild(s);
        grid.AddChild(value);
        return (s, value);
    }

    CheckButton Toggle(GridContainer grid, string label, Action<bool> onToggle)
    {
        RowLabel(grid, label);
        var c = new CheckButton();
        c.Toggled += on => { if (!_syncing) onToggle(on); };
        grid.AddChild(c);
        grid.AddChild(new Control());
        return c;
    }
}
