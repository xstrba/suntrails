using System;
using System.Globalization;
using Godot;

namespace World;

/// <summary>
/// Photo mode (P): a camera with real settings for learning photography basics.
///   Mouse wheel   zoom, 14-300 mm (true full-frame field of view)
///   Q / E         lower / raise the camera
///   Up / Down     choose a setting: aperture, shutter, ISO, exposure compensation, focus
///   Left / Right  change it
///   Click / Enter take a photo (saved as PNG in Pictures/World Photos, without the viewfinder)
/// Exposure follows the time of day (CameraModel): on Auto the camera meters for you until the
/// light gets too dim; the meter shows how many stops over or under the photo is.
/// Depth of field uses Godot's camera blur, set from thin-lens optics (CameraModel.SharpRange).
/// Slow shutters really take that long: frames are averaged while the shutter is open, so moving
/// the camera, wind and water blur like on a real sensor. Handheld adds a little shake (more at
/// long focal lengths); "Tripod" keeps the camera still during the exposure.
/// Debug: `-- --photo --focal=85 --aperture=1.4 --iso=100 --shutter=0.004 --focus=5 --tripod --take-photo`
/// </summary>
public partial class PhotoMode : Node
{
    enum Setting { Aperture, Shutter, Iso, Ev, Focus, Support }
    const int SettingCount = 6;

    public bool Active { get; private set; }
    internal PhotoLibrary Library { get; private set; }
    internal bool IsCapturing => _shot != null;

    Player _player;
    ChunkManager _chunks;
    DayNight _dayNight;
    Godot.Environment _env;
    Camera3D _camera;
    CameraAttributesPractical _lens;

    double _focal = 24;
    double _aperture = 5.6;
    double? _shutter;           // null = Auto
    int _iso = 100;
    double _ev;
    double? _manualFocus;       // null = autofocus
    double _focus = 10, _focusTarget = 10, _focusTimer;
    Setting _selected = Setting.Aperture;

    CanvasLayer _lensLayer, _ui;
    ShaderMaterial _lensMaterial;
    Viewfinder _viewfinder;
    RichTextLabel _bar;
    Label _status, _toast;
    ColorRect _flash;
    double _toastTime;
    PhotoDetails _shot;
    bool _tripod;
    // Long exposures: frames are averaged into _acc while the shutter is open.
    SubViewport _acc;
    TextureRect _accFrame;
    bool _exposing;
    double _exposeTime, _exposeElapsed;
    string _windowTitle;
    // Hand shake: a few slow, unrelated sine waves per axis (breathing, muscle tremor).
    readonly float[] _shakePhase = new float[6];
    double _shakeTime;
    double _autoShotAt = -1; // debug: `--take-photo` presses the shutter after a few seconds

    public override void _Ready()
    {
        _player = GetNode<Player>("../Player");
        _chunks = GetNode<ChunkManager>("../ChunkManager");
        _dayNight = GetNode<DayNight>("../DayNight");
        _env = GetNode<WorldEnvironment>("../WorldEnvironment").Environment;
        Library = new PhotoLibrary();
        _lens = new CameraAttributesPractical { AutoExposureEnabled = false };
        BuildUi();

        bool start = false;
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            double Value() => double.Parse(arg[(arg.IndexOf('=') + 1)..], CultureInfo.InvariantCulture);
            if (arg == "--photo") start = true;
            else if (arg.StartsWith("--focal=")) _focal = Value();
            else if (arg.StartsWith("--aperture=")) _aperture = Value();
            else if (arg.StartsWith("--iso=")) _iso = (int)Value();
            else if (arg.StartsWith("--shutter=")) _shutter = Value();
            else if (arg.StartsWith("--focus=")) _manualFocus = Value();
            else if (arg == "--take-photo") _autoShotAt = 8;
            else if (arg == "--tripod") _tripod = true;
        }
        if (start) CallDeferred(MethodName.Toggle);
    }

    public void Toggle()
    {
        Active = !Active;
        _player.PhotoCameraActive = Active;
        _camera = _player.Camera;
        _ui.Visible = _lensLayer.Visible = Active;
        if (Active)
        {
            _camera.Attributes = _lens;
            _focusTimer = 0;
        }
        else
        {
            _shot = null;
            ReleaseExposure();
            _player.LookOffset = Vector2.Zero;
            _camera.Attributes = null;
            _player.FovOverride = null;
            _player.SensitivityScale = 1;
            _env.TonemapExposure = 1;
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.P })
        {
            Toggle();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (!Active) return;

        bool captured = Input.MouseMode == Input.MouseModeEnum.Captured;
        switch (e)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp }:
                _focal = Math.Clamp(_focal * 1.1, CameraModel.MinFocalMm, CameraModel.MaxFocalMm);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown }:
                _focal = Math.Clamp(_focal / 1.1, CameraModel.MinFocalMm, CameraModel.MaxFocalMm);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } when captured:
                TakePhoto();
                break;
            case InputEventKey { Pressed: true } key:
                switch (key.PhysicalKeycode)
                {
                    case Key.Up: _selected = (Setting)(((int)_selected + SettingCount - 1) % SettingCount); break;
                    case Key.Down: _selected = (Setting)(((int)_selected + 1) % SettingCount); break;
                    case Key.Left: Adjust(-1); break;
                    case Key.Right: Adjust(1); break;
                    case Key.Enter or Key.KpEnter when !key.Echo: TakePhoto(); break;
                    default: return;
                }
                break;
            default:
                return;
        }
        GetViewport().SetInputAsHandled();
    }

    static T Step<T>(T[] list, T value, int dir)
    {
        int i = Array.IndexOf(list, value);
        return list[Math.Clamp(i + dir, 0, list.Length - 1)];
    }

    void Adjust(int dir)
    {
        switch (_selected)
        {
            case Setting.Aperture: _aperture = Step(CameraModel.Apertures, _aperture, dir); break;
            case Setting.Shutter: _shutter = Step(CameraModel.Shutters, _shutter, dir); break;
            case Setting.Iso: _iso = Step(CameraModel.Isos, _iso, dir); break;
            case Setting.Ev: _ev = Math.Clamp(Math.Round((_ev + dir / 3.0) * 3) / 3, -3, 3); break;
            case Setting.Focus: _manualFocus = Step(CameraModel.FocusDistances, _manualFocus, dir); break;
            case Setting.Support: _tripod = dir > 0; break;
        }
    }

    double Brightness => CameraModel.SceneBrightness(_dayNight.SunDirection.Y);

    public override void _Process(double delta)
    {
        if (_toastTime > 0)
        {
            _toastTime -= delta;
            _toast.Modulate = new Color(1, 1, 1, (float)Math.Clamp(_toastTime, 0, 1));
        }
        if (_flash.Color.A > 0)
            _flash.Color = new Color(1, 1, 1, Mathf.Max(0, _flash.Color.A - (float)delta * 4));
        if (!Active) return;
        if (_autoShotAt > 0 && (_autoShotAt -= delta) <= 0) TakePhoto();
        UpdateShake(delta);
        if (_exposing) StepExposure(delta);

        float fov = (float)CameraModel.FovForFocalLength(_focal);
        _player.FovOverride = fov;
        // Keep the view equally steady under the hand at every zoom level.
        _player.SensitivityScale = Mathf.Tan(Mathf.DegToRad(fov / 2)) / Mathf.Tan(Mathf.DegToRad(35));

        // Autofocus on whatever is under the centre mark, a few times a second.
        _focusTimer -= delta;
        if (_manualFocus is double mf) _focusTarget = mf;
        else if (_focusTimer <= 0)
        {
            _focusTimer = 0.1;
            var t = _camera.GlobalTransform;
            _focusTarget = _chunks.MeasureDistance(t.Origin, -t.Basis.Z);
        }
        _focus += (_focusTarget - _focus) * (1 - Math.Exp(-10 * delta));

        double brightness = Brightness;
        _env.TonemapExposure = (float)CameraModel.Exposure(_aperture, _shutter, _iso, _ev, brightness);
        ApplyDepthOfField();

        _lensMaterial.SetShaderParameter("vignette", 0.34f * Mathf.Sqrt(1.4f / (float)_aperture));
        _lensMaterial.SetShaderParameter("grain", (float)Math.Max(0, Math.Log2(_iso / 100.0) - 1) * 0.014f);
        _lensMaterial.SetShaderParameter("seed", (float)(Time.GetTicksMsec() % 10000) * 0.001f);
        UpdateUi(brightness);
    }

    /// <summary>Blur beyond the far limit of sharpness and in front of the near limit.</summary>
    void ApplyDepthOfField()
    {
        var (near, far) = CameraModel.SharpRange(_focal, _aperture, _focus);
        float height = GetViewport().GetVisibleRect().Size.Y;
        double blurPx = Math.Min(40, CameraModel.BackgroundBlurPixels(_focal, _aperture, _focus, height));
        // Godot's blur amount is roughly the largest blur as a fraction of 64 px.
        _lens.DofBlurAmount = (float)Math.Clamp(blurPx / 64, 0, 1);
        _lens.DofBlurFarEnabled = !double.IsInfinity(far) && far < 2000;
        if (_lens.DofBlurFarEnabled)
        {
            _lens.DofBlurFarDistance = (float)far;
            // Full blur by the time things are about as far behind the focus as the focus is away.
            _lens.DofBlurFarTransition = (float)Math.Max(0.5, Math.Max(far - _focus, _focus));
        }
        _lens.DofBlurNearEnabled = near > 0.35;
        if (_lens.DofBlurNearEnabled)
        {
            _lens.DofBlurNearDistance = (float)near;
            _lens.DofBlurNearTransition = (float)Math.Max(0.2, near * 0.5);
        }
    }

    /// <summary>
    /// Handheld cameras shake a little; the longer the lens, the more it shows (the old rule of
    /// thumb: a shutter of 1/focal-length or faster stays sharp). On a tripod the camera is still.
    /// </summary>
    void UpdateShake(double delta)
    {
        if (_tripod)
        {
            _player.LookOffset = Vector2.Zero;
            return;
        }
        if (_shakePhase[0] == 0)
            for (int i = 0; i < _shakePhase.Length; i++) _shakePhase[i] = (float)GD.RandRange(0.1, Mathf.Tau);
        _shakeTime += delta;
        float t = (float)_shakeTime;
        // About 0.25 mrad at 50 mm: a pixel or two of blur at 1/50 s, much more at 1/4 s.
        float amp = 0.00025f * (float)(_focal / 50.0);
        float Wave(int i) => Mathf.Sin(t * 1.3f + _shakePhase[i]) * 0.5f + Mathf.Sin(t * 4.7f + _shakePhase[i + 1]) * 0.35f
            + Mathf.Sin(t * 11.0f + _shakePhase[i + 2] * 2) * 0.15f;
        _player.LookOffset = new Vector2(Wave(0), Wave(3)) * amp;
    }

    void TakePhoto()
    {
        if (IsCapturing) return;
        double shutter = CameraModel.ShutterInUse(_aperture, _shutter, _iso, _ev, Brightness);
        var position = _camera.GlobalPosition;
        var shot = new PhotoDetails(DateTimeOffset.UtcNow, position.X, position.Y, position.Z,
            _focal, _aperture, shutter, _shutter == null, _iso, _ev, _focus, _manualFocus == null, _tripod);
        _shot = shot;
        // Shorter than about a frame: one frame is the photo. Longer: average the frames.
        if (shutter <= 1.0 / 50) CaptureSingleFrame(shot);
        else BeginExposure(shot);
    }

    async void CaptureSingleFrame(PhotoDetails shot)
    {
        // Hide the viewfinder for one frame so the photo shows only the scene and lens effects.
        _ui.Visible = false;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        if (!ReferenceEquals(_shot, shot)) return;
        using var image = GetViewport().GetTexture().GetImage();
        Save(image, shot);
    }

    /// <summary>
    /// Long exposure: while the shutter is open, each rendered frame is blended into a
    /// high-precision buffer with weight (frame time / time so far), which keeps a running
    /// time-weighted average. Whatever moves meanwhile (your camera, leaves, water, stars)
    /// smears the way it would on a real sensor. Takes as long as the shutter time.
    /// </summary>
    async void BeginExposure(PhotoDetails shot)
    {
        _ui.Visible = false;
        _player.Frozen = shot.Tripod;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        if (!ReferenceEquals(_shot, shot)) return;
        _acc.Size = (Vector2I)GetViewport().GetVisibleRect().Size;
        _acc.RenderTargetClearMode = SubViewport.ClearMode.Once;
        _accFrame.Texture = GetViewport().GetTexture();
        _exposeTime = shot.Shutter;
        _exposeElapsed = 0;
        _windowTitle = GetWindow().Title;
        _exposing = true;
    }

    void StepExposure(double delta)
    {
        _exposeElapsed += delta;
        // Blend this frame in with weight dt / elapsed: a running time-weighted average.
        _accFrame.Modulate = new Color(1, 1, 1, (float)(delta / _exposeElapsed));
        _acc.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        GetWindow().Title = UiText.Format("Exposing {0:0.0} / {1}", Math.Min(_exposeElapsed, _exposeTime),
            CameraModel.FormatShutter(_exposeTime));
        if (_exposeElapsed >= _exposeTime) FinishExposure();
    }

    async void FinishExposure()
    {
        var shot = _shot;
        _exposing = false;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        if (!ReferenceEquals(_shot, shot)) return;
        using var accumulated = _acc.GetTexture().GetImage();
        using var image = ToSrgb8(accumulated);
        ReleaseExposure();
        Save(image, shot);
    }

    void ReleaseExposure()
    {
        _exposing = false;
        _accFrame.Texture = null;
        if (_windowTitle != null) GetWindow().Title = _windowTitle;
        _windowTitle = null;
        _player.Frozen = false;
    }

    /// <summary>
    /// The half-float accumulation buffer holds linear colour; the screen (and PNG) is sRGB.
    /// Convert in float precision so dark tones don't band.
    /// </summary>
    static Image ToSrgb8(Image linear)
    {
        linear.Convert(Image.Format.Rgbaf);
        int w = linear.GetWidth(), h = linear.GetHeight();
        var src = linear.GetData();
        var dst = new byte[w * h * 4];
        for (int i = 0, o = 0; o < dst.Length; i += 16, o += 4)
        {
            for (int c = 0; c < 3; c++)
            {
                float v = Math.Clamp(BitConverter.ToSingle(src, i + c * 4), 0f, 1f);
                v = v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1 / 2.4f) - 0.055f;
                dst[o + c] = (byte)Math.Round(v * 255);
            }
            dst[o + 3] = 255;
        }
        return Image.CreateFromData(w, h, false, Image.Format.Rgba8, dst);
    }

    void Save(Image image, PhotoDetails shot)
    {
        _ui.Visible = Active;
        _flash.Color = new Color(1, 1, 1, 0.8f);
        try
        {
            Library.Save(image, shot);
            ShowMessage("Photo saved · Press G to see it in the gallery");
        }
        catch (Exception e) when (PhotoLibrary.IsFileError(e))
        {
            GD.PushWarning(e.Message);
            ShowMessage("Couldn't save the photo · Press G to choose a save folder");
        }
        finally
        {
            _shot = null;
        }
    }

    internal void ShowMessage(string message)
    {
        _toast.Text = message;
        _toast.Modulate = Colors.White;
        _toastTime = 4;
    }

    // ---- Viewfinder UI ----------------------------------------------------------------------

    void BuildUi()
    {
        // Lens effects sit above the 3D view and below the viewfinder, and stay in the photo.
        _lensLayer = new CanvasLayer { Layer = 5, Visible = false };
        _lensMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/photo_lens.gdshader") };
        var lensRect = new ColorRect { Material = _lensMaterial, MouseFilter = Control.MouseFilterEnum.Ignore };
        lensRect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _lensLayer.AddChild(lensRect);
        AddChild(_lensLayer);

        // Accumulation buffer for long exposures (half-float, so thousands of frames average cleanly).
        _acc = new SubViewport
        {
            UseHdr2D = true,
            TransparentBg = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            RenderTargetClearMode = SubViewport.ClearMode.Once,
            Disable3D = true,
        };
        _accFrame = new TextureRect { StretchMode = TextureRect.StretchModeEnum.Scale };
        _accFrame.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _acc.AddChild(_accFrame);
        AddChild(_acc);

        _ui = new CanvasLayer { Layer = 10, Visible = false };
        AddChild(_ui);
        _viewfinder = new Viewfinder { MouseFilter = Control.MouseFilterEnum.Ignore };
        _viewfinder.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _ui.AddChild(_viewfinder);

        var title = MakeLabel(new Color(1, 0.3f, 0.25f));
        title.Text = "● PHOTO";
        title.Position = new Vector2(28, 18);
        _ui.AddChild(title);
        _status = MakeLabel(Colors.White);
        _status.HorizontalAlignment = HorizontalAlignment.Right;
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _ui.AddChild(_status);
        _status.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopWide);
        _status.OffsetLeft = 145;
        _status.OffsetTop = 18;
        _status.OffsetRight = -28;

        var controls = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        controls.AddThemeConstantOverride("separation", 8);
        _ui.AddChild(controls);
        controls.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomWide);
        controls.GrowVertical = Control.GrowDirection.Begin;
        controls.OffsetLeft = 16;
        controls.OffsetRight = -16;
        controls.OffsetTop = controls.OffsetBottom = -20;

        _bar = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _bar.HorizontalAlignment = HorizontalAlignment.Center;
        _bar.AddThemeFontSizeOverride("normal_font_size", 20);
        _bar.AddThemeFontSizeOverride("bold_font_size", 20);
        controls.AddChild(_bar);

        var hint = MakeLabel(new Color(1, 1, 1, 0.75f));
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        hint.Text = "Wheel zoom  ·  Hold Q/E lower/raise camera  ·  ↑↓ select  ·  ←→ adjust  ·  Click/Enter shoot  ·  G gallery  ·  P exit";
        controls.AddChild(hint);

        // Flash and "saved" message live on their own layer so they show after the photo is taken.
        var feedback = new CanvasLayer { Layer = 11 };
        AddChild(feedback);
        _flash = new ColorRect { Color = new Color(1, 1, 1, 0), MouseFilter = Control.MouseFilterEnum.Ignore };
        _flash.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        feedback.AddChild(_flash);
        _toast = MakeLabel(Colors.White);
        _toast.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        _toast.HorizontalAlignment = HorizontalAlignment.Center;
        _toast.Position = new Vector2(-400, 56);
        _toast.Size = new Vector2(800, 48);
        _toast.Modulate = new Color(1, 1, 1, 0);
        feedback.AddChild(_toast);
    }

    static Label MakeLabel(Color color)
    {
        var l = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        l.AddThemeColorOverride("font_color", color);
        l.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.75f));
        l.AddThemeConstantOverride("shadow_offset_x", 1);
        l.AddThemeConstantOverride("shadow_offset_y", 1);
        l.AddThemeFontSizeOverride("font_size", 18);
        return l;
    }

    void UpdateUi(double brightness)
    {
        bool auto = _shutter == null;
        double shutter = CameraModel.ShutterInUse(_aperture, _shutter, _iso, _ev, brightness);
        string ev = _ev > 0 ? string.Create(UiText.Culture, $"+{_ev:0.0}")
            : _ev < 0 ? string.Create(UiText.Culture, $"−{-_ev:0.0}") : string.Create(UiText.Culture, $"±{0:0.0}");
        string focus = _focus >= 399 ? "∞" : _focus.ToString(_focus < 10 ? "0.0" : "0", UiText.Culture) + " m";
        string[] values =
        {
            string.Create(UiText.Culture, $"f/{_aperture}"),
            $"{(auto ? "A " : "")}{CameraModel.FormatShutter(shutter)}",
            $"ISO {_iso}",
            $"{ev} EV",
            $"{(_manualFocus == null ? "AF" : "MF")} {focus}",
            UiText.Get(_tripod ? "Tripod" : "Handheld"),
        };
        var text = $"[color=#ffd27a]{Math.Round(_focal)} mm[/color]";
        for (int i = 0; i < values.Length; i++)
        {
            string cell = values[i];
            if (i == (int)Setting.Ev && !auto) cell = $"[color=#ffffff70]{cell}[/color]"; // EV only applies on Auto
            if (i == (int)_selected) cell = $"[bgcolor=#ffffff38][b] {cell} [/b][/bgcolor]";
            text += "     " + cell;
        }
        _bar.Text = $"[center]{text}[/center]";

        // Exposure meter, in stops from a neutral exposure.
        double stops = Math.Log2(CameraModel.Exposure(_aperture, _shutter, _iso, _ev, brightness));
        string meter = Math.Abs(stops) < 0.17 ? "±0" : string.Create(UiText.Culture, $"{(stops > 0 ? "+" : "−")}{Math.Abs(stops):0.0}");
        _status.Text = UiText.Format("Exposure {0}", meter);
        // Rule of thumb: handheld, a shutter slower than 1/focal-length shows shake.
        if (!_tripod && shutter > 1.0 / Math.Max(_focal, 30))
            _status.Text += "  ·  " + UiText.Get("Slow for handheld: use a tripod or hold steady");
    }

    /// <summary>Frame corners, rule-of-thirds grid and the centre focus mark.</summary>
    partial class Viewfinder : Control
    {
        public override void _Draw()
        {
            var size = Size;
            float m = Mathf.Min(size.X, size.Y) * 0.06f, len = 34, w = 2;
            var c = new Color(1, 1, 1, 0.85f);
            var tl = new Vector2(m, m);
            var br = size - new Vector2(m, m);
            foreach (var (corner, sx, sy) in new[] { (tl, 1, 1), (new Vector2(br.X, tl.Y), -1, 1), (new Vector2(tl.X, br.Y), 1, -1), (br, -1, -1) })
            {
                DrawLine(corner, corner + new Vector2(sx * len, 0), c, w);
                DrawLine(corner, corner + new Vector2(0, sy * len), c, w);
            }
            var g = new Color(1, 1, 1, 0.22f);
            for (int i = 1; i <= 2; i++)
            {
                DrawLine(new Vector2(size.X * i / 3, 0), new Vector2(size.X * i / 3, size.Y), g, 1);
                DrawLine(new Vector2(0, size.Y * i / 3), new Vector2(size.X, size.Y * i / 3), g, 1);
            }
            var center = size / 2;
            DrawRect(new Rect2(center - new Vector2(16, 12), new Vector2(32, 24)), c, false, 1.5f);
            DrawCircle(center, 1.5f, c);
        }

        public override void _Notification(int what)
        {
            if (what == NotificationResized) QueueRedraw();
        }
    }
}
