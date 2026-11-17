using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace World;

// Only create buttons and thumbnail textures for rows near the visible part of the gallery.
internal partial class PhotoGrid : ScrollContainer
{
    const int Gap = 12;
    const int CardHeight = 166;
    const int RowHeight = CardHeight + Gap;
    const int PreferredCardWidth = 192;

    sealed record Card(Button Button, TextureRect Thumbnail)
    {
        public bool Requested;
    }

    readonly Dictionary<int, Card> _cards = new();
    IReadOnlyList<PhotoEntry> _photos = Array.Empty<PhotoEntry>();
    Control _content;
    int _selected = -1;
    int _revealIndex;
    bool _loadingThumbnail;

    public event Action<int> PhotoSelected;

    public override void _Ready()
    {
        HorizontalScrollMode = ScrollMode.Disabled;
        VerticalScrollMode = ScrollMode.ShowAlways;
        FollowFocus = true;
        _content = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        AddChild(_content);
        GetVScrollBar().CustomMinimumSize = new Vector2(14, 0);
    }

    public void SetPhotos(IReadOnlyList<PhotoEntry> photos, int revealIndex = 0)
    {
        foreach (int index in _cards.Keys.ToArray()) RemoveCard(index);
        _photos = photos;
        _selected = -1;
        _revealIndex = revealIndex;
        ScrollVertical = 0;
    }

    public void Select(int index)
    {
        if (_cards.TryGetValue(_selected, out var previous)) previous.Button.SetPressedNoSignal(false);
        _selected = index;
        if (_cards.TryGetValue(index, out var current)) current.Button.SetPressedNoSignal(true);
    }

    public override void _Process(double delta)
    {
        if (!IsVisibleInTree() || _photos.Count == 0) return;
        float width = _content.Size.X - Gap;
        if (width <= 0) return;

        int columns = Math.Max(1, (int)((width + Gap) / (PreferredCardWidth + Gap)));
        int rows = (_photos.Count - 1) / columns + 1;
        float cardWidth = (width - Gap * (columns - 1)) / columns;
        _content.CustomMinimumSize = new Vector2(0, rows * RowHeight - Gap);
        if (_revealIndex > 0)
        {
            ScrollVertical = _revealIndex / columns * RowHeight;
            _revealIndex = 0;
        }
        int first = Math.Max(0, ScrollVertical / RowHeight - 1) * columns;
        int last = Math.Min(_photos.Count, ((int)((ScrollVertical + Size.Y) / RowHeight) + 2) * columns);
        foreach (int index in _cards.Keys.Where(i => i < first || i >= last).ToArray()) RemoveCard(index);
        for (int index = first; index < last; index++)
        {
            if (!_cards.TryGetValue(index, out var card))
                _cards.Add(index, card = CreateCard(index));
            card.Button.Position = new Vector2(index % columns * (cardWidth + Gap), index / columns * RowHeight);
            card.Button.Size = new Vector2(cardWidth, CardHeight);
        }

        if (_loadingThumbnail) return;
        for (int index = first; index < last; index++)
        {
            var card = _cards[index];
            if (card.Requested) continue;
            LoadThumbnail(index, card);
            break;
        }
    }

    Card CreateCard(int index)
    {
        var photo = _photos[index];
        var button = new Button
        {
            ThemeTypeVariation = "PhotoCard",
            ToggleMode = true,
            TooltipText = photo.Name,
            AccessibilityName = photo.Name,
            AutoTranslateMode = AutoTranslateModeEnum.Disabled,
            MouseDefaultCursorShape = CursorShape.PointingHand
        };
        button.SetPressedNoSignal(index == _selected);
        button.Pressed += () => PhotoSelected?.Invoke(index);
        _content.AddChild(button);

        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        box.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        box.OffsetLeft = box.OffsetTop = 8;
        box.OffsetRight = box.OffsetBottom = -8;
        button.AddChild(box);

        var well = new PanelContainer
        {
            ThemeTypeVariation = "PhotoWell",
            SizeFlagsVertical = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore
        };
        box.AddChild(well);
        var thumbnail = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        well.AddChild(thumbnail);
        var caption = new Label
        {
            Text = photo.Name,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseFilter = MouseFilterEnum.Ignore
        };
        box.AddChild(caption);
        box.AddChild(new Label
        {
            Text = photo.Date.ToLocalTime().ToString("d MMM yyyy", UiText.Culture),
            ThemeTypeVariation = "GallerySecondary",
            MouseFilter = MouseFilterEnum.Ignore
        });
        return new Card(button, thumbnail);
    }

    void RemoveCard(int index)
    {
        var card = _cards[index];
        _cards.Remove(index);
        _content.RemoveChild(card.Button);
        card.Button.QueueFree();
    }

    async void LoadThumbnail(int index, Card card)
    {
        card.Requested = true;
        _loadingThumbnail = true;
        var entry = _photos[index];
        try
        {
            using var image = await Task.Run(() => LoadImage(entry.Path, 320));
            if (image != null && _cards.TryGetValue(index, out var current) && current == card &&
                ReferenceEquals(_photos[index], entry))
                card.Thumbnail.Texture = ImageTexture.CreateFromImage(image);
        }
        finally
        {
            _loadingThumbnail = false;
        }
    }

    internal static Image LoadImage(string path, int maxEdge)
    {
        var image = new Image();
        if (image.Load(path) != Error.Ok || image.IsEmpty())
        {
            image.Dispose();
            return null;
        }

        int edge = Math.Max(image.GetWidth(), image.GetHeight());
        if (edge > maxEdge)
        {
            float scale = (float)maxEdge / edge;
            image.Resize(Math.Max(1, (int)(image.GetWidth() * scale)),
                Math.Max(1, (int)(image.GetHeight() * scale)), Image.Interpolation.Bilinear);
        }
        return image;
    }
}
