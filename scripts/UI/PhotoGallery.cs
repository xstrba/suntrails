using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace World;

public partial class PhotoGallery : CanvasLayer
{
    enum SortOrder { Newest, Oldest, NameAscending, NameDescending }

    static readonly Color Background = new("#142520");
    static readonly Color Surface = new("#20372e");
    static readonly Color PhotoBackground = new("#13241d");
    static readonly Color Outline = new("#456457");
    static readonly Color Text = new("#f6f4e8");
    static readonly Color Secondary = new("#bfd4c7");
    static readonly Color Mint = new("#bce88c");

    PhotoMode _photo;
    Control _root;
    MarginContainer _page;
    PhotoViewer _viewer;
    BoxContainer _body;
    VBoxContainer _browser;
    PhotoGrid _grid;
    ScrollContainer _details;
    Label _folder, _count, _emptyTitle, _emptyMessage, _photoName, _date, _fileStats, _coordinates, _settings, _previewNote;
    OptionButton _sort;
    CenterContainer _empty;
    TextureRect _preview;
    Button _close, _fullScreenButton, _renameButton;
    FileDialog _folderDialog;
    ConfirmationDialog _renameDialog, _deleteDialog;
    AcceptDialog _notice;
    LineEdit _nameInput;
    Label _renameError;

    List<PhotoEntry> _entries = new();
    CancellationTokenSource _scan;
    Input.MouseModeEnum _previousMouseMode;
    PhotoEntry _selected, _previewRequested;
    PhotoEntry _deleteEntry;
    bool _previewLoading;

    public override void _Ready()
    {
        Layer = 30;
        ProcessMode = ProcessModeEnum.Always;
        _photo = GetNode<PhotoMode>("../PhotoMode");
        BuildUi();
        _root.Hide();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } key) return;

        if (_root.Visible)
        {
            if (_folderDialog.Visible || _renameDialog.Visible || _deleteDialog.Visible || _notice.Visible) return;
            if (key.PhysicalKeycode is not (Key.G or Key.Escape)) return;
            if (_viewer.Visible) CloseViewer();
            else Close();
        }
        else
        {
            if (key.PhysicalKeycode != Key.G || GetTree().Paused) return;
            if (_photo.IsCapturing) _photo.ShowMessage("Wait for the photo to finish, then press G for the gallery");
            else Open();
        }
        GetViewport().SetInputAsHandled();
    }

    void Open()
    {
        _sort.TooltipText = _sort.AccessibilityName = UiText.Get("Sort photos by date or name");
        _folderDialog.Title = UiText.Get("Choose where to save photos");
        _folderDialog.OkButtonText = UiText.Get("Use this folder");
        _folderDialog.CancelButtonText = UiText.Get("Cancel");
        _renameDialog.Title = UiText.Get("Rename photo");
        _renameDialog.OkButtonText = UiText.Get("Save name");
        _renameDialog.CancelButtonText = UiText.Get("Cancel");
        _deleteDialog.Title = UiText.Get("Delete photo?");
        _deleteDialog.OkButtonText = UiText.Get("Move to Trash");
        _deleteDialog.CancelButtonText = UiText.Get("Keep photo");
        _previousMouseMode = Input.MouseMode;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        GetTree().Paused = true;
        _root.Show();
        _close.GrabFocus();
        RefreshFolder();
    }

    void Close()
    {
        _scan?.Cancel();
        _grid.SetPhotos(Array.Empty<PhotoEntry>());
        _entries.Clear();
        ShowDetails(null);
        _root.Hide();
        GetTree().Paused = false;
        Input.MouseMode = _previousMouseMode;
    }

    async void RefreshFolder()
    {
        _scan?.Cancel();
        _scan = new CancellationTokenSource();
        var request = _scan;
        string folder = _photo.Library.Folder;
        _folder.Text = _folder.TooltipText = folder;
        _entries = new();
        _count.Text = "";
        _sort.Disabled = true;
        _grid.SetPhotos(_entries);
        ShowDetails(null);
        ShowEmpty("Opening your photos…", "");

        try
        {
            var entries = await ReadFolder(folder, request);
            if (request.IsCancellationRequested) return;
            _entries = entries;
            UpdatePhotoCount();
            if (_entries.Count == 0)
            {
                ShowEmptyAlbum();
                return;
            }

            _empty.Hide();
            _grid.Show();
            SortPhotos();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (PhotoLibrary.IsFileError(error))
        {
            if (!request.IsCancellationRequested)
                ShowEmpty("This folder couldn't be opened", "Choose a different save folder to see your photos.");
        }
        finally
        {
            if (_scan == request) _scan = null;
            request.Dispose();
        }
    }

    static Task<List<PhotoEntry>> ReadFolder(string folder, CancellationTokenSource request)
    {
        var cancellation = request.Token;
        return Task.Run(() =>
        {
            try { return PhotoLibrary.ReadFolder(folder, cancellation); }
            catch (DirectoryNotFoundException) { return new List<PhotoEntry>(); }
        }, cancellation);
    }

    void ShowEmpty(string title, string message)
    {
        _grid.Hide();
        _emptyTitle.Text = title;
        _emptyMessage.Text = message;
        _empty.Show();
    }

    void ShowEmptyAlbum() => ShowEmpty("No photos here yet",
        "Take a photo in photo mode, or choose a folder that already has photos.");

    void UpdatePhotoCount()
    {
        _count.Text = UiText.Count("{0} photo", "{0} photos", _entries.Count);
        _sort.Disabled = _entries.Count == 0;
    }

    void SortPhotos(PhotoEntry keepSelected = null)
    {
        if (_entries.Count == 0) return;
        var order = (SortOrder)_sort.GetSelectedId();
        _entries.Sort(order switch
        {
            SortOrder.NameAscending => CompareNames,
            SortOrder.NameDescending => (a, b) => CompareNames(b, a),
            SortOrder.Oldest => (a, b) => CompareByTime(a.Date.CompareTo(b.Date), a, b),
            _ => (a, b) => CompareByTime(b.Date.CompareTo(a.Date), a, b)
        });

        int index = keepSelected == null ? 0 : _entries.IndexOf(keepSelected);
        _grid.SetPhotos(_entries, index);
        SelectPhoto(index);
    }

    static int CompareByTime(int timeOrder, PhotoEntry a, PhotoEntry b) =>
        timeOrder != 0 ? timeOrder : CompareNames(a, b);

    static int CompareNames(PhotoEntry a, PhotoEntry b)
    {
        int byName = UiText.Culture.CompareInfo.Compare(a.Name, b.Name, System.Globalization.CompareOptions.IgnoreCase);
        return byName != 0 ? byName : StringComparer.Ordinal.Compare(a.Path, b.Path);
    }

    void SelectPhoto(int index)
    {
        _grid.Select(index);
        ShowDetails(_entries[index]);
    }

    void ShowDetails(PhotoEntry entry)
    {
        if (!ReferenceEquals(entry, _selected))
        {
            _preview.Texture = null;
            _previewNote.Text = "Opening photo…";
            _previewNote.Show();
        }
        _selected = entry;
        _details.Visible = entry != null;
        if (entry == null)
        {
            _previewRequested = null;
            return;
        }

        _details.ScrollVertical = 0;
        _photoName.Text = _photoName.TooltipText = entry.Name;
        _date.Text = UiText.Get(entry.Details == null ? "File date" : "Taken") + " · " +
            entry.Date.ToLocalTime().ToString("g", UiText.Culture);
        _fileStats.Text = entry.Resolution == Vector2I.Zero
            ? FormatFileSize(entry.FileSize) + "   ·   " + UiText.Get("Unknown image format")
            : $"{entry.Resolution.X} × {entry.Resolution.Y} px   ·   {FormatFileSize(entry.FileSize)}   ·   PNG";
        if (entry.Details is not { } shot)
        {
            _coordinates.Text = "";
            _settings.Text = "This photo has no saved camera details.";
            return;
        }

        _coordinates.Text = UiText.Get("Camera position") + "\n" +
            string.Create(UiText.Culture, $"X {shot.X:0.0} m     Y {shot.Y:0.0} m     Z {shot.Z:0.0} m");
        string focus = shot.Focus >= 399 ? "∞" : string.Create(UiText.Culture, $"{shot.Focus:0.0} m");
        string shutter = CameraModel.FormatShutter(shot.Shutter).TrimEnd('"') + " s";
        if (shot.AutoShutter) shutter += $" ({UiText.Get("auto")})";
        _settings.Text = UiText.Format("Zoom {0:0.#} mm   ·   Aperture f/{1:0.#}", shot.FocalLength, shot.Aperture) + "\n" +
            UiText.Format("Shutter {0}   ·   ISO {1}", shutter, shot.Iso) + "\n" +
            UiText.Format("Exposure {0:+0.0;-0.0;0.0} EV   ·   Focus {1} ({2})", shot.ExposureCompensation, focus,
                UiText.Get(shot.AutoFocus ? "auto" : "manual")) + "\n" +
            UiText.Get(shot.Tripod ? "Tripod" : "Handheld");
    }

    static string FormatFileSize(long bytes) => bytes switch
    {
        < 1_000 => $"{bytes} B",
        < 1_000_000 => string.Create(UiText.Culture, $"{bytes / 1_000.0:0.#} KB"),
        < 1_000_000_000 => string.Create(UiText.Culture, $"{bytes / 1_000_000.0:0.#} MB"),
        _ => string.Create(UiText.Culture, $"{bytes / 1_000_000_000.0:0.#} GB")
    };

    public override void _Process(double delta)
    {
        if (!_root.Visible || _viewer.Visible) return;
        bool stack = _root.Size.X < 900;
        if (_body.Vertical != stack)
        {
            _body.Vertical = stack;
            _browser.SizeFlagsStretchRatio = stack ? 1f : 1.65f;
            _details.SizeFlagsStretchRatio = stack ? 1.45f : 1f;
        }
        if (!_previewLoading && _selected != null && !ReferenceEquals(_selected, _previewRequested))
            LoadPreview(_selected);
    }

    async void LoadPreview(PhotoEntry entry)
    {
        _previewLoading = true;
        _previewRequested = entry;
        try
        {
            using var image = await Task.Run(() => PhotoGrid.LoadImage(entry.Path, 1440));
            if (!ReferenceEquals(entry, _selected)) return;
            if (image == null) _previewNote.Text = "This photo couldn't be opened";
            else
            {
                _preview.Texture = ImageTexture.CreateFromImage(image);
                _previewNote.Hide();
            }
        }
        finally
        {
            _previewLoading = false;
        }
    }

    void OpenViewer()
    {
        if (_selected == null || _viewer.Visible) return;
        _page.Hide();
        _viewer.Open(_selected, _preview.Texture);
    }

    void CloseViewer()
    {
        _viewer.Close();
        _page.Show();
        _fullScreenButton.GrabFocus();
    }

    void PickFolder()
    {
        string folder = _photo.Library.Folder;
        if (Directory.Exists(folder)) _folderDialog.CurrentDir = folder;
        else if (Path.GetDirectoryName(folder) is { } parent && Directory.Exists(parent))
            _folderDialog.CurrentDir = parent;
        _folderDialog.PopupCenteredRatio(0.8f);
    }

    void ChangeFolder(string folder)
    {
        try
        {
            _photo.Library.SetFolder(folder);
            RefreshFolder();
        }
        catch (Exception error) when (PhotoLibrary.IsFileError(error))
        {
            GD.PushWarning(error.Message);
            _notice.Title = UiText.Get("Choose another folder");
            _notice.DialogText = "Photos couldn't be saved to that folder. Choose another folder.";
            _notice.CallDeferred(Window.MethodName.PopupCentered);
        }
    }

    void OpenRename()
    {
        if (_selected == null) return;
        _nameInput.Text = _selected.Name;
        _renameError.Hide();
        _renameDialog.PopupCentered(new Vector2I(460, 0));
        _nameInput.GrabFocus();
        _nameInput.SelectAll();
    }

    void SaveName()
    {
        if (_selected == null) return;
        try
        {
            int index = _entries.IndexOf(_selected);
            var renamed = PhotoLibrary.Rename(_selected, _nameInput.Text);
            if (!ReferenceEquals(renamed, _selected))
            {
                _entries[index] = renamed;
                SortPhotos(renamed);
            }
            _renameDialog.Hide();
            _renameButton.GrabFocus();
        }
        catch (ArgumentException error)
        {
            RenameError(error.Message);
        }
        catch (Exception error) when (PhotoLibrary.IsFileError(error))
        {
            RenameError("This photo couldn't be renamed. Check that the folder allows changes.");
        }
    }

    void RenameError(string message)
    {
        _renameError.Text = UiText.Get(message);
        _renameError.Show();
        _nameInput.GrabFocus();
    }

    void OpenDelete()
    {
        if (_selected == null) return;
        _deleteEntry = _selected;
        _deleteDialog.DialogText = UiText.Format("Move “{0}” to the Trash?", _deleteEntry.Name);
        _deleteDialog.PopupCentered(new Vector2I(440, 0));
        _deleteDialog.GetCancelButton().GrabFocus();
    }

    void DeletePhoto()
    {
        int index = _entries.IndexOf(_deleteEntry);
        _deleteEntry = null;
        if (index < 0) return;
        var error = OS.MoveToTrash(_entries[index].Path);
        if (error != Error.Ok)
        {
            _notice.Title = UiText.Get("Couldn't delete photo");
            _notice.DialogText = "The photo couldn't be moved to the Trash. Check that the file is available and the folder allows changes.";
            _notice.CallDeferred(Window.MethodName.PopupCentered);
            return;
        }

        _entries.RemoveAt(index);
        UpdatePhotoCount();
        int next = Math.Min(index, _entries.Count - 1);
        _grid.SetPhotos(_entries, next);
        if (next >= 0)
        {
            SelectPhoto(next);
            _fullScreenButton.GrabFocus();
        }
        else
        {
            ShowDetails(null);
            ShowEmptyAlbum();
            _close.GrabFocus();
        }
    }

    void BuildUi()
    {
        var theme = GalleryTheme();
        _root = new Control { Name = "Gallery", Theme = theme };
        AddChild(_root);
        _root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var background = new ColorRect { Color = Background };
        _root.AddChild(background);
        background.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        _page = new MarginContainer();
        _root.AddChild(_page);
        _page.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        foreach (string side in new[] { "left", "top", "right", "bottom" })
            _page.AddThemeConstantOverride($"margin_{side}", 24);

        var page = new VBoxContainer();
        page.AddThemeConstantOverride("separation", 18);
        _page.AddChild(page);

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 14);
        page.AddChild(header);
        var title = MakeLabel("Photo gallery", 34);
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        header.AddChild(title);
        _close = MakeButton("Back to game", "GalleryPrimary", Close);
        header.AddChild(_close);

        var toolbarPanel = new PanelContainer { ThemeTypeVariation = "GalleryPanel" };
        page.AddChild(toolbarPanel);
        var toolbar = new HBoxContainer();
        toolbar.AddThemeConstantOverride("separation", 12);
        toolbarPanel.AddChild(toolbar);
        var folderInfo = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        toolbar.AddChild(folderInfo);
        folderInfo.AddChild(MakeLabel("Save folder", 16, secondary: true));
        _folder = MakeLabel("", 16, ellipsis: true);
        _folder.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        folderInfo.AddChild(_folder);
        toolbar.AddChild(MakeButton("Choose folder…", "", PickFolder));

        _body = new BoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _body.AddThemeConstantOverride("separation", 18);
        page.AddChild(_body);
        _browser = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1.65f
        };
        _browser.AddThemeConstantOverride("separation", 10);
        _body.AddChild(_browser);
        var gridHeader = new HBoxContainer();
        _browser.AddChild(gridHeader);
        _count = MakeLabel("", 17, secondary: true);
        _count.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _count.VerticalAlignment = VerticalAlignment.Center;
        gridHeader.AddChild(_count);
        _sort = new OptionButton
        {
            CustomMinimumSize = new Vector2(148, 44),
            MouseDefaultCursorShape = Control.CursorShape.PointingHand
        };
        _sort.AddItem("Newest first", (int)SortOrder.Newest);
        _sort.AddItem("Oldest first", (int)SortOrder.Oldest);
        _sort.AddItem("Name A–Z", (int)SortOrder.NameAscending);
        _sort.AddItem("Name Z–A", (int)SortOrder.NameDescending);
        _sort.ItemSelected += _ => SortPhotos();
        gridHeader.AddChild(_sort);
        _grid = new PhotoGrid
        {
            Name = "PhotoGrid",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        _grid.PhotoSelected += SelectPhoto;
        _browser.AddChild(_grid);
        _empty = new CenterContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _browser.AddChild(_empty);
        var emptyCopy = new VBoxContainer();
        emptyCopy.AddThemeConstantOverride("separation", 10);
        _empty.AddChild(emptyCopy);
        _emptyTitle = MakeLabel("", 27);
        _emptyTitle.HorizontalAlignment = HorizontalAlignment.Center;
        emptyCopy.AddChild(_emptyTitle);
        _emptyMessage = MakeLabel("", 17, secondary: true);
        _emptyMessage.CustomMinimumSize = new Vector2(300, 0);
        _emptyMessage.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _emptyMessage.HorizontalAlignment = HorizontalAlignment.Center;
        emptyCopy.AddChild(_emptyMessage);

        BuildDetails();
        BuildDialogs(theme);
        _viewer = new PhotoViewer { Name = "PhotoViewer", Visible = false };
        _root.AddChild(_viewer);
        _viewer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _viewer.Back += CloseViewer;
    }

    void BuildDetails()
    {
        _details = new ScrollContainer
        {
            Name = "PhotoDetails",
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            FollowFocus = true
        };
        _body.AddChild(_details);
        var panel = new PanelContainer
        {
            ThemeTypeVariation = "GalleryPanel",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        _details.AddChild(panel);
        var info = new VBoxContainer();
        info.AddThemeConstantOverride("separation", 10);
        panel.AddChild(info);
        var well = new PanelContainer
        {
            ThemeTypeVariation = "PhotoWell",
            CustomMinimumSize = new Vector2(0, 170),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand
        };
        well.GuiInput += input =>
        {
            if (input is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) return;
            well.AcceptEvent();
            OpenViewer();
        };
        info.AddChild(well);
        _preview = new TextureRect
        {
            Name = "PhotoPreview",
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        well.AddChild(_preview);
        _previewNote = MakeLabel("", 17, secondary: true);
        _previewNote.HorizontalAlignment = HorizontalAlignment.Center;
        _previewNote.VerticalAlignment = VerticalAlignment.Center;
        _previewNote.MouseFilter = Control.MouseFilterEnum.Ignore;
        well.AddChild(_previewNote);

        _photoName = MakeLabel("", 25, ellipsis: true);
        _photoName.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        info.AddChild(_photoName);
        _date = MakeLabel("", 16, secondary: true);
        info.AddChild(_date);
        _fileStats = MakeLabel("", 16, secondary: true);
        _fileStats.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        info.AddChild(_fileStats);
        _fullScreenButton = MakeButton("Full screen", "GalleryPrimary", OpenViewer);
        info.AddChild(_fullScreenButton);
        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 8);
        info.AddChild(actions);
        _renameButton = MakeButton("Rename photo…", "", OpenRename);
        _renameButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        actions.AddChild(_renameButton);
        var delete = MakeButton("Delete…", "", OpenDelete);
        delete.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        actions.AddChild(delete);
        info.AddChild(new HSeparator());
        _coordinates = MakeLabel("", 16);
        _coordinates.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        info.AddChild(_coordinates);
        _settings = MakeLabel("", 16, secondary: true);
        _settings.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        info.AddChild(_settings);
    }

    void BuildDialogs(Theme theme)
    {
        _folderDialog = new FileDialog
        {
            Name = "SaveFolderDialog",
            FileMode = FileDialog.FileModeEnum.OpenDir,
            Access = FileDialog.AccessEnum.Filesystem,
            UseNativeDialog = true,
            Transient = true,
            Exclusive = true
        };
        AddChild(_folderDialog);
        _folderDialog.DirSelected += ChangeFolder;

        _renameDialog = new ConfirmationDialog
        {
            Name = "RenamePhotoDialog",
            DialogHideOnOk = false,
            Theme = theme,
            Transient = true,
            Exclusive = true
        };
        AddChild(_renameDialog);
        var form = new VBoxContainer();
        form.AddThemeConstantOverride("separation", 10);
        _renameDialog.AddChild(form);
        form.AddChild(MakeLabel("Photo name", 17));
        _nameInput = new LineEdit { Name = "PhotoName", CustomMinimumSize = new Vector2(0, 44) };
        form.AddChild(_nameInput);
        _renameDialog.RegisterTextEnter(_nameInput);
        _renameError = MakeLabel("", 16);
        _renameError.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _renameError.AddThemeColorOverride("font_color", new Color("#ffc6ae"));
        form.AddChild(_renameError);
        _renameDialog.Confirmed += SaveName;

        _deleteDialog = new ConfirmationDialog
        {
            Name = "DeletePhotoDialog",
            DialogAutowrap = true,
            Theme = theme,
            Transient = true,
            Exclusive = true
        };
        AddChild(_deleteDialog);
        _deleteDialog.Confirmed += DeletePhoto;
        _deleteDialog.Canceled += () => _deleteEntry = null;

        _notice = new AcceptDialog
        {
            DialogAutowrap = true,
            MinSize = new Vector2I(370, 0),
            Theme = theme,
            Transient = true,
            Exclusive = true
        };
        AddChild(_notice);
    }

    internal static Button MakeButton(string text, string variation, Action activate)
    {
        var button = new Button
        {
            Text = text,
            ThemeTypeVariation = variation,
            CustomMinimumSize = new Vector2(0, 44),
            MouseDefaultCursorShape = Control.CursorShape.PointingHand
        };
        button.Pressed += activate;
        return button;
    }

    static Label MakeLabel(string text, int size, bool secondary = false, bool ellipsis = false)
    {
        var label = new Label
        {
            Text = text,
            ThemeTypeVariation = secondary ? "GallerySecondary" : "",
            TextOverrunBehavior = ellipsis ? TextServer.OverrunBehavior.TrimEllipsis : TextServer.OverrunBehavior.NoTrimming
        };
        label.AddThemeFontSizeOverride("font_size", size);
        return label;
    }

    static Theme GalleryTheme()
    {
        var theme = new Theme { DefaultFontSize = 17 };
        theme.SetColor("font_color", "Label", Text);
        theme.SetTypeVariation("GallerySecondary", "Label");
        theme.SetColor("font_color", "GallerySecondary", Secondary);
        theme.SetFontSize("font_size", "GallerySecondary", 15);

        theme.SetTypeVariation("GalleryPanel", "PanelContainer");
        theme.SetStylebox("panel", "GalleryPanel", Style(Surface, Outline, 14, 14));
        theme.SetTypeVariation("PhotoWell", "PanelContainer");
        theme.SetStylebox("panel", "PhotoWell", Style(PhotoBackground, PhotoBackground, 8, 0));
        theme.SetTypeVariation("PhotoCard", "Button");
        theme.SetStylebox("normal", "PhotoCard", Style(Surface, Outline, 10, 0));
        theme.SetStylebox("hover", "PhotoCard", Style(new Color("#2d493d"), Secondary, 10, 0));
        theme.SetStylebox("pressed", "PhotoCard", Style(new Color("#304938"), Mint, 10, 0, 2));
        theme.SetStylebox("hover_pressed", "PhotoCard", Style(new Color("#365540"), Mint, 10, 0, 2));

        foreach (string control in new[] { "Button", "OptionButton" })
        {
            theme.SetColor("font_color", control, Text);
            theme.SetColor("font_hover_color", control, Text);
            theme.SetStylebox("normal", control, Style(new Color("#294438"), Outline, 9, 12));
            theme.SetStylebox("hover", control, Style(new Color("#365545"), Secondary, 9, 12));
            theme.SetStylebox("pressed", control, Style(new Color("#365545"), Mint, 9, 12));
            var focus = Style(Colors.Transparent, Mint, 9, 0, 2);
            focus.DrawCenter = false;
            theme.SetStylebox("focus", control, focus);
        }

        theme.SetTypeVariation("GalleryPrimary", "Button");
        foreach (string state in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" })
            theme.SetColor(state, "GalleryPrimary", Background);
        theme.SetStylebox("normal", "GalleryPrimary", Style(Mint, Mint, 9, 14));
        theme.SetStylebox("hover", "GalleryPrimary", Style(new Color("#d0f5a9"), Mint, 9, 14));
        theme.SetStylebox("pressed", "GalleryPrimary", Style(new Color("#a0d375"), Mint, 9, 14));
        return theme;
    }

    static StyleBoxFlat Style(Color fill, Color border, int corner, int padding, int borderWidth = 1)
    {
        var style = new StyleBoxFlat { BgColor = fill, BorderColor = border };
        style.SetCornerRadiusAll(corner);
        style.SetBorderWidthAll(borderWidth);
        style.ContentMarginLeft = style.ContentMarginRight = padding;
        style.ContentMarginTop = style.ContentMarginBottom = Math.Min(14, padding);
        return style;
    }
}
