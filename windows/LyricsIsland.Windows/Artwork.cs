using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LyricsIsland.Windows;

/// <summary>
/// Artwork per track ID. The SMTC player fills it from the session thumbnail; views ask for the current
/// track and fall back to <see cref="PlaceholderArt"/> while there's none. UI thread only.
/// </summary>
public static class ArtworkStore
{
    private static readonly Dictionary<string, ImageSource> Images = new();

    /// <summary>Raised with the track ID whose artwork arrived.</summary>
    public static event Action<string>? Changed;

    public static ImageSource? Get(string? trackId) =>
        trackId is not null && Images.TryGetValue(trackId, out var image) ? image : null;

    public static void Set(string trackId, ImageSource image)
    {
        // Keep only a few: artwork is only ever shown for the current song.
        if (Images.Count > 8) Images.Clear();
        Images[trackId] = image;
        Changed?.Invoke(trackId);
    }

    /// <summary>Decodes image bytes off the UI thread into a frozen bitmap at most <paramref name="pixels"/> wide.</summary>
    public static Task<ImageSource?> DecodeAsync(byte[] bytes, int pixels = 176) => Task.Run<ImageSource?>(() =>
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = pixels;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null; // not an image we can read: keep the placeholder
        }
    });
}

/// <summary>Album art with rounded corners (port of <c>AlbumArt</c>): the track's artwork, or the sunset placeholder.</summary>
public sealed class ArtView : FrameworkElement
{
    private readonly PlaceholderArt _placeholder = new();
    private ImageSource? _image;
    private string? _trackId;

    public ArtView(double size, double radius)
    {
        Width = Height = size;
        CornerRadius = radius;
        _placeholder.Width = _placeholder.Height = size;
        _placeholder.CornerRadius = radius;
        AddVisualChild(_placeholder);
        Loaded += (_, _) => ArtworkStore.Changed += OnArtworkChanged;
        Unloaded += (_, _) => ArtworkStore.Changed -= OnArtworkChanged;
    }

    public double CornerRadius { get; }

    /// <summary>Which track's artwork to show.</summary>
    public string? TrackId
    {
        get => _trackId;
        set
        {
            if (_trackId == value) return;
            _trackId = value;
            SetImage(ArtworkStore.Get(value));
        }
    }

    protected override int VisualChildrenCount => _image is null ? 1 : 0;

    protected override Visual GetVisualChild(int index) => _placeholder;

    protected override Size MeasureOverride(Size available)
    {
        _placeholder.Measure(new Size(Width, Height));
        return new Size(Width, Height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        _placeholder.Arrange(new Rect(final));
        return final;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_image is null) return;
        var rect = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.PushClip(new RectangleGeometry(rect, CornerRadius, CornerRadius));
        // Fill the square (scaledToFill): crop the longer side.
        var w = _image.Width;
        var h = _image.Height;
        var scale = Math.Max(rect.Width / w, rect.Height / h);
        var drawn = new Rect((rect.Width - w * scale) / 2, (rect.Height - h * scale) / 2, w * scale, h * scale);
        dc.DrawImage(_image, drawn);
        dc.Pop();
    }

    private void OnArtworkChanged(string trackId)
    {
        if (trackId == _trackId) SetImage(ArtworkStore.Get(trackId));
    }

    private void SetImage(ImageSource? image)
    {
        if (ReferenceEquals(image, _image)) return;
        var hadImage = _image is not null;
        _image = image;
        if (hadImage != (_image is not null))
        {
            if (_image is null) AddVisualChild(_placeholder);
            else RemoveVisualChild(_placeholder);
        }
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        InvalidateVisual();
    }
}

/// <summary>SF Symbol stand-ins drawn as geometry: the transport icons and the offset buttons.</summary>
public static class Glyphs
{
    /// <summary><c>play.fill</c> in a <paramref name="size"/> square.</summary>
    public static Geometry Play(double size) => Polygons(
        new Point[] { new(size * 0.18, size * 0.08), new(size * 0.92, size * 0.5), new(size * 0.18, size * 0.92) });

    /// <summary><c>pause.fill</c>: two rounded bars.</summary>
    public static Geometry Pause(double size)
    {
        var group = new GeometryGroup();
        group.Children.Add(new RectangleGeometry(new Rect(size * 0.18, size * 0.1, size * 0.22, size * 0.8), size * 0.05, size * 0.05));
        group.Children.Add(new RectangleGeometry(new Rect(size * 0.6, size * 0.1, size * 0.22, size * 0.8), size * 0.05, size * 0.05));
        return Frozen(group);
    }

    /// <summary><c>forward.fill</c> (or <c>backward.fill</c> when <paramref name="back"/>): two triangles.</summary>
    public static Geometry Skip(double size, bool back)
    {
        double w = size * 1.25, h = size * 0.7, top = (size - h) / 2, half = w / 2, mid = top + h / 2;
        return back
            ? Polygons(new Point[] { new(half, top), new(0, mid), new(half, top + h) }, new Point[] { new(w, top), new(half, mid), new(w, top + h) })
            : Polygons(new Point[] { new(0, top), new(half, mid), new(0, top + h) }, new Point[] { new(half, top), new(w, mid), new(half, top + h) });
    }

    /// <summary><c>minus</c> / <c>plus</c> strokes in a <paramref name="size"/> square (draw with a pen).</summary>
    public static Geometry Sign(double size, bool plus)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(0, size / 2), false, false);
            ctx.LineTo(new Point(size, size / 2), true, false);
            if (plus)
            {
                ctx.BeginFigure(new Point(size / 2, 0), false, false);
                ctx.LineTo(new Point(size / 2, size), true, false);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>Filled, closed polygons (no text parsing, so the system locale's decimal comma can't break it).</summary>
    private static Geometry Polygons(params Point[][] polygons)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            foreach (var points in polygons)
            {
                ctx.BeginFigure(points[0], true, true);
                ctx.PolyLineTo(points[1..], true, true);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private static Geometry Frozen(Geometry g)
    {
        g.Freeze();
        return g;
    }
}
