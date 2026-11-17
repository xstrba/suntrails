using System.IO;
using Godot;

namespace World;

internal partial class PhotoViewer : Control
{
    const float MaximumZoom = 8f;
    const float ZoomStep = 1.25f;

    Control _stage;
    TextureRect _image;
    Label _title, _message, _zoomLevel;
    Button _back, _zoomOut, _fit, _zoomIn;
    PhotoEntry _entry, _requested;
    Window.ModeEnum _returnMode;
    bool _loading, _dragging;
    float _zoom = 1f;
    Vector2 _pan;

    public event System.Action Back;

    public override void _Ready()
    {
        var backdrop = new ColorRect { Color = new Color("#101714") };
        AddChild(backdrop);
        backdrop.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var margins = new MarginContainer();
        AddChild(margins);
        margins.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "left", "top", "right", "bottom" })
            margins.AddThemeConstantOverride($"margin_{side}", 16);
        var layout = new VBoxContainer();
        layout.AddThemeConstantOverride("separation", 10);
        margins.AddChild(layout);

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 16);
        layout.AddChild(header);
        _back = PhotoGallery.MakeButton("Back to gallery", "GalleryPrimary", () => Back?.Invoke());
        header.AddChild(_back);
        _title = new Label
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            AutoTranslateMode = AutoTranslateModeEnum.Disabled,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        header.AddChild(_title);

        _stage = new Control
        {
            Name = "ZoomArea",
            ClipContents = true,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        layout.AddChild(_stage);
        _stage.GuiInput += HandleImageInput;
        _stage.Resized += ArrangeImage;
        _image = new TextureRect
        {
            Name = "FullPhoto",
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _stage.AddChild(_image);
        _message = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _stage.AddChild(_message);
        _message.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var controls = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        controls.AddThemeConstantOverride("separation", 8);
        layout.AddChild(controls);
        _zoomOut = PhotoGallery.MakeButton("−", "", () => ZoomAt(_zoom / ZoomStep, _stage.Size / 2));
        _zoomOut.CustomMinimumSize = new Vector2(52, 44);
        controls.AddChild(_zoomOut);
        _fit = PhotoGallery.MakeButton("Fit", "", Fit);
        controls.AddChild(_fit);
        _zoomIn = PhotoGallery.MakeButton("+", "", () => ZoomAt(_zoom * ZoomStep, _stage.Size / 2));
        _zoomIn.CustomMinimumSize = new Vector2(52, 44);
        controls.AddChild(_zoomIn);
        _zoomLevel = new Label { CustomMinimumSize = new Vector2(64, 0), HorizontalAlignment = HorizontalAlignment.Center };
        controls.AddChild(_zoomLevel);
        var hint = new Label
        {
            Text = "Scroll to zoom · Drag to move",
            HorizontalAlignment = HorizontalAlignment.Center,
            ThemeTypeVariation = "GallerySecondary"
        };
        layout.AddChild(hint);
    }

    public void Open(PhotoEntry entry, Texture2D preview)
    {
        _entry = entry;
        _requested = null;
        _title.Text = entry.Name;
        _zoomOut.AccessibilityName = UiText.Get("Zoom out");
        _fit.AccessibilityName = UiText.Get("Fit photo to screen");
        _zoomIn.AccessibilityName = UiText.Get("Zoom in");
        _image.Texture = preview;
        _message.Text = "Opening photo…";
        _message.Visible = preview == null;
        _returnMode = GetWindow().Mode;
        if (_returnMode is not (Window.ModeEnum.Fullscreen or Window.ModeEnum.ExclusiveFullscreen))
            GetWindow().Mode = Window.ModeEnum.Fullscreen;
        Show();
        Fit();
        _back.GrabFocus();
    }

    public void Close()
    {
        if (!Visible) return;
        Hide();
        _entry = null;
        _requested = null;
        _image.Texture = null;
        _dragging = false;
        GetWindow().Mode = _returnMode;
    }

    public override void _Process(double delta)
    {
        if (Visible && !_loading && _entry != null && !ReferenceEquals(_entry, _requested))
            LoadOriginal(_entry);
    }

    async void LoadOriginal(PhotoEntry entry)
    {
        _loading = true;
        _requested = entry;
        try
        {
            byte[] png = await File.ReadAllBytesAsync(entry.Path);
            if (!Visible || !ReferenceEquals(entry, _entry)) return;
            using var image = new Image();
            if (image.LoadPngFromBuffer(png) == Error.Ok && !image.IsEmpty())
            {
                _image.Texture = ImageTexture.CreateFromImage(image);
                _message.Hide();
                ArrangeImage();
            }
            else if (_image.Texture == null)
            {
                _message.Text = "This photo couldn't be opened";
            }
        }
        catch (System.Exception error) when (PhotoLibrary.IsFileError(error))
        {
            if (Visible && ReferenceEquals(entry, _entry) && _image.Texture == null)
                _message.Text = "This photo couldn't be opened";
        }
        finally
        {
            _loading = false;
        }
    }

    void Fit()
    {
        _zoom = 1f;
        _pan = Vector2.Zero;
        _dragging = false;
        ArrangeImage();
    }

    void ZoomAt(float zoom, Vector2 point)
    {
        if (_image.Texture == null) return;
        float next = Mathf.Clamp(zoom, 1f, MaximumZoom);
        Vector2 fromCenter = point - _stage.Size / 2;
        _pan = fromCenter - (fromCenter - _pan) * (next / _zoom);
        _zoom = next;
        ArrangeImage();
    }

    void ArrangeImage()
    {
        if (_zoomLevel == null) return;
        bool loaded = _image.Texture != null;
        _zoomLevel.Text = string.Create(UiText.Culture, $"{_zoom:0.#}×");
        _zoomOut.Disabled = _fit.Disabled = !loaded || _zoom <= 1f;
        _zoomIn.Disabled = !loaded || _zoom >= MaximumZoom;
        _stage.MouseDefaultCursorShape = loaded && _zoom > 1f ? CursorShape.Drag : CursorShape.Arrow;
        if (!loaded) return;

        Vector2 area = _stage.Size;
        Vector2 pixels = _image.Texture.GetSize();
        float fit = Mathf.Min(area.X / pixels.X, area.Y / pixels.Y);
        Vector2 shown = pixels * (fit * _zoom);
        Vector2 limit = ((shown - area) / 2).Max(Vector2.Zero);
        _pan = _pan.Clamp(-limit, limit);
        _image.Size = shown;
        _image.Position = (area - shown) / 2 + _pan;
    }

    void HandleImageInput(InputEvent input)
    {
        if (_image.Texture == null) return;
        switch (input)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp } wheelUp:
                ZoomAt(_zoom * ZoomStep, wheelUp.Position);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown } wheelDown:
                ZoomAt(_zoom / ZoomStep, wheelDown.Position);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } button:
                _dragging = button.Pressed && _zoom > 1f;
                break;
            case InputEventMouseMotion motion when _dragging && (motion.ButtonMask & MouseButtonMask.Left) != 0:
                _pan += motion.Relative;
                ArrangeImage();
                break;
            default:
                return;
        }
        _stage.AcceptEvent();
    }
}
