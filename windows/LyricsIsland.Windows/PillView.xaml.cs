using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LyricsIsland.Core;

namespace LyricsIsland.Windows;

/// <summary>
/// The pill and its motion, ported from IslandView.swift:
/// <list type="bullet">
/// <item>Mode change: width, height and corner radius follow the mode spring (response 0.42, damping 0.82);
/// the old and new rows cross-fade in <see cref="CrossFadeSeconds"/>, centered and clipped by the pill.</item>
/// <item>Line change: the row's <see cref="LyricTicker"/> slides and resizes on the line spring (0.35, 0.86);
/// the pill hugs it.</item>
/// <item>Appear / disappear: scale from 0.4, anchored at the screen edge, plus fade, on the mode spring.</item>
/// </list>
/// Everything is stepped by <see cref="Animator"/> only while something moves.
/// </summary>
public partial class PillView : UserControl, IAnimated
{
    public const double CrossFadeSeconds = 0.18;

    /// <summary>
    /// Compact shadow. Mac: <c>.shadow(color: .black.opacity(0.35), radius: 12, y: 8)</c> (spec "0/8/24, 35%").
    /// WPF's DropShadowEffect blurs differently: with the literal values (24, 8, 0.35) the shadow ends
    /// much closer to the pill. (40, 8, 0.42) matches the darkening under the pill in the Mac's lyric.png
    /// best (RMS difference 0.0095 vs. 0.043, measured every 3 px over 70 px at 2×).
    /// </summary>
    public static (double Blur, double Offset, double Opacity) CompactShadow { get; set; } = (40, 8, 0.42);

    // Settle thresholds: a quarter pixel, below what can be seen (presence: 0.3% of the scale ≈ 1 px of a wide pill).
    private readonly Spring _width = new(Spring.Mode.Response, Spring.Mode.Damping, epsilon: 0.25);
    private readonly Spring _height = new(Spring.Mode.Response, Spring.Mode.Damping, epsilon: 0.25);
    private readonly Spring _radius = new(Spring.Mode.Response, Spring.Mode.Damping, epsilon: 0.1);
    private readonly Spring _presence = new(Spring.Mode.Response, Spring.Mode.Damping, epsilon: 0.003);
    private readonly List<(PillRow Row, Tween Fade)> _outgoing = [];
    private PillRow? _row;
    private Tween? _fadeIn;
    private bool _springFrame;
    private bool _shown;
    private (double W, double H, double R) _applied = (-1, -1, -1);

    public PillView()
    {
        InitializeComponent();
        SetShadow(CompactShadow);
        AnchorTop = true;
    }

    /// <summary>Grow from the top edge (top position) or the bottom edge (above the taskbar).</summary>
    public bool AnchorTop
    {
        set => Root.RenderTransformOrigin = new Point(0.5, value ? 0 : 1);
    }

    /// <summary>The visible pill, for hover hit-testing (null while hidden or leaving).</summary>
    public FrameworkElement? HitArea => _shown && Root.Visibility == Visibility.Visible ? Frame : null;

    public RowKind? CurrentRow => _shown ? _row?.Kind : null;

    /// <summary>Brings the pill up to date with the model. Cheap when nothing changed; called on every tick.</summary>
    public void Render(IslandModel model)
    {
        var kind = PillRow.KindFor(model);
        if (kind is null)
        {
            if (_shown)
            {
                _shown = false;
                _presence.Target = 0;
                Animator.Start(this);
            }
            return;
        }

        if (!_shown)
        {
            _shown = true;
            if (Root.Visibility != Visibility.Visible)
            {
                // Appearing from nothing: the frame starts at the row's size; only scale and opacity move.
                ShowRow(new PillRow(kind.Value, model), crossFade: false);
                SnapFrame();
                _presence.Snap(0);
                Root.Visibility = Visibility.Visible;
            }
            else if (_row?.Kind != kind)
            {
                ShowRow(new PillRow(kind.Value, model), crossFade: true);
            }
            _presence.Target = 1;
            Animator.Start(this);
            return;
        }

        if (_row is null || _row.Kind != kind)
        {
            ShowRow(new PillRow(kind.Value, model), crossFade: true);
            Animator.Start(this);
            return;
        }

        if (_row.Update(model, animate: true))
        {
            // Text size changed: Mac animates it on the line spring.
            UseSpring(Spring.Line);
            _springFrame = true;
        }
        if (_row.IsAnimating || _springFrame) Animator.Start(this);
        else ApplyFrame(RowSize(), snap: true);
    }

    public bool Step(double dt)
    {
        var moving = _row?.Step(dt) ?? false;
        foreach (var (row, fade) in _outgoing.ToArray())
        {
            if (fade.Step(dt)) moving = true;
            else _outgoing.Remove((row, fade));
        }
        if (_fadeIn is not null)
        {
            if (_fadeIn.Step(dt)) moving = true;
            else _fadeIn = null;
        }

        var size = RowSize();
        if (_springFrame)
        {
            _width.Target = size.Width;
            _height.Target = size.Height;
            _radius.Target = _row?.Radius ?? 17;
            var framing = _width.Step(dt) | _height.Step(dt) | _radius.Step(dt);
            if (framing) moving = true;
            else _springFrame = false;
            ApplyFrame(size, snap: false);
        }
        else
        {
            ApplyFrame(size, snap: true);
        }

        if (_presence.Step(dt)) moving = true;
        ApplyPresence();

        if (!_shown && !moving)
        {
            // Gone: drop the content so nothing keeps animating behind an invisible pill.
            Root.Visibility = Visibility.Collapsed;
            ContentHost.Children.Clear();
            _outgoing.Clear();
            _row = null;
        }
        return moving;
    }

    /// <summary>Jumps every animation to its end (snapshots).</summary>
    public void Finish()
    {
        foreach (var (_, fade) in _outgoing.ToArray()) fade.Finish();
        _outgoing.Clear();
        _fadeIn?.Finish();
        _fadeIn = null;
        _row?.Finish();
        _springFrame = false;
        SnapFrame();
        _presence.Snap(_presence.Target);
        ApplyPresence();
        if (!_shown)
        {
            Root.Visibility = Visibility.Collapsed;
            ContentHost.Children.Clear();
            _row = null;
        }
        Animator.Stop(this);
    }

    /// <summary>Element sizes in DIPs, for <c>--diagnose</c>.</summary>
    public string DescribeLayout() =>
        $"row {_row?.Kind}, frame {Frame.Width:0.#}x{Frame.Height:0.#} r{_radius.Value:0.#}, presence {_presence.Value:0.##}";

    public void SetShadow((double Blur, double Offset, double Opacity) shadow)
    {
        Shadow.BlurRadius = shadow.Blur;
        Shadow.ShadowDepth = shadow.Offset;
        Shadow.Opacity = shadow.Opacity;
    }

    private void ShowRow(PillRow next, bool crossFade)
    {
        if (_row is { } old && crossFade)
        {
            var start = old.Opacity;
            _outgoing.Add((old, new Tween(CrossFadeSeconds, t => old.Opacity = start * (1 - t), () => ContentHost.Children.Remove(old))));
        }
        else
        {
            ContentHost.Children.Clear();
            _outgoing.Clear();
        }

        _row = next;
        ContentHost.Children.Add(next);
        if (crossFade)
        {
            next.Opacity = 0;
            _fadeIn = new Tween(CrossFadeSeconds, t => next.Opacity = t);
            UseSpring(Spring.Mode);
            _springFrame = true;
        }
        else
        {
            _fadeIn = null;
        }
    }

    private void UseSpring((double Response, double Damping) parameters)
    {
        _width.Use(parameters);
        _height.Use(parameters);
        _radius.Use(parameters);
    }

    private Size RowSize()
    {
        if (_row is null) return new Size(_width.Value, _height.Value);
        _row.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return _row.DesiredSize;
    }

    private void SnapFrame()
    {
        var size = RowSize();
        _width.Snap(size.Width);
        _height.Snap(size.Height);
        _radius.Snap(_row?.Radius ?? 17);
        ApplyFrame(size, snap: true);
    }

    private void ApplyFrame(Size rowSize, bool snap)
    {
        if (snap)
        {
            _width.Snap(rowSize.Width);
            _height.Snap(rowSize.Height);
            _radius.Snap(_row?.Radius ?? _radius.Target);
        }
        var w = Math.Max(0, _width.Value);
        var h = Math.Max(0, _height.Value);
        var r = Math.Clamp(_radius.Value, 0, Math.Min(w, h) / 2);
        if ((w, h, r) == _applied) return;
        _applied = (w, h, r);

        Frame.Width = w;
        Frame.Height = h;
        var corner = new CornerRadius(r);
        ShadowLayer.CornerRadius = corner;
        Body.CornerRadius = corner;
        Stroke.CornerRadius = corner;
        Body.Clip = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
    }

    private void ApplyPresence()
    {
        var p = _presence.Value;
        RootScale.ScaleX = RootScale.ScaleY = 0.4 + 0.6 * p;
        Root.Opacity = Math.Clamp(p, 0, 1);
    }
}

/// <summary>Lays every child out at its own desired size, centered, even when it's larger than the panel (it's clipped by the pill).</summary>
public sealed class CenterPanel : Panel
{
    protected override Size MeasureOverride(Size available)
    {
        foreach (UIElement child in InternalChildren) child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return new Size(0, 0);
    }

    protected override Size ArrangeOverride(Size final)
    {
        foreach (UIElement child in InternalChildren)
        {
            var size = child.DesiredSize;
            child.Arrange(new Rect((final.Width - size.Width) / 2, (final.Height - size.Height) / 2, size.Width, size.Height));
        }
        return final;
    }
}
