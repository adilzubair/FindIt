using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FindIt.App;

public readonly record struct TimelineRange(long StartMs, long EndMs, bool Person);

/// <summary>
/// The preview's seek bar. Matching moments are marked on the track, a click or drag anywhere seeks
/// there, and the time under the pointer is reported so the window can show that frame.
/// </summary>
public sealed class MediaTimeline : Grid
{
    // The thumb's radius, so both ends of the recording stay reachable.
    private const double Inset = 7;
    private readonly Border _track = new() { Height = 4, CornerRadius = new(2), VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _played = new() { Height = 4, CornerRadius = new(2), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Canvas _marks = new() { IsHitTestVisible = false };
    private readonly Ellipse _thumb = new() { Width = 14, Height = 14, StrokeThickness = 2, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
    private IReadOnlyList<TimelineRange> _ranges = [];
    private long _duration, _position;

    /// <summary>A seek the user asked for; <c>final</c> is false while a drag is still moving.</summary>
    public event Action<long, bool>? Seek;
    /// <summary>The time under the pointer and its x position, or null when the pointer left.</summary>
    public event Action<long?, double>? Hover;
    public bool IsScrubbing { get; private set; }
    public long DurationMs { get => _duration; set { if (value == _duration) return; _duration = Math.Max(0, value); Arrange(); } }
    public long PositionMs { get => _position; set { if (IsScrubbing) return; _position = Math.Clamp(value, 0, Math.Max(0, _duration)); Arrange(); } }
    public IReadOnlyList<TimelineRange> Ranges { get => _ranges; set { _ranges = value; Arrange(); } }

    public MediaTimeline()
    {
        Height = 28; Background = Brushes.Transparent; Focusable = true; Cursor = Cursors.Hand; ClipToBounds = false;
        _track.Margin = new Thickness(Inset, 0, Inset, 0); _played.Margin = new Thickness(Inset, 0, 0, 0);
        _track.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
        _played.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        _thumb.SetResourceReference(Shape.FillProperty, "AccentBrush");
        _thumb.SetResourceReference(Shape.StrokeProperty, "SurfaceBrush");
        Children.Add(_track); Children.Add(_played); Children.Add(_marks); Children.Add(_thumb);
        SizeChanged += (_, _) => Arrange();
    }

    public void Reset() { IsScrubbing = false; _duration = _position = 0; _ranges = []; Arrange(); }

    private double Usable => Math.Max(0, ActualWidth - 2 * Inset);
    private double X(long ms) => Inset + (_duration > 0 ? Usable * Math.Clamp(ms, 0, _duration) / _duration : 0);
    private long TimeAt(double x) => _duration <= 0 || Usable <= 0 ? 0 : (long)Math.Round(Math.Clamp((x - Inset) / Usable, 0, 1) * _duration);

    private void Arrange()
    {
        var known = _duration > 0;
        _played.Width = known ? X(_position) - Inset : 0;
        _thumb.Visibility = known ? Visibility.Visible : Visibility.Hidden;
        _thumb.Margin = new Thickness(X(_position) - Inset, 0, 0, 0);
        _marks.Children.Clear();
        if (!known || ActualWidth <= 0) return;
        foreach (var range in _ranges)
        {
            if (range.StartMs > _duration) continue;
            // Marks sit in a strip above the track, so a match spanning the whole recording still
            // leaves the played part visible. A sampled frame is an instant; it still needs a mark.
            var left = X(range.StartMs); var width = Math.Max(4, X(Math.Max(range.StartMs, range.EndMs)) - left);
            var mark = new Rectangle { Width = width, Height = 3, RadiusX = 1.5, RadiusY = 1.5 };
            mark.SetResourceReference(Shape.FillProperty, range.Person ? "VideoInkBrush" : "WarningBrush");
            Canvas.SetLeft(mark, Math.Min(left, ActualWidth - Inset - width)); Canvas.SetTop(mark, ActualHeight / 2 - 8);
            _marks.Children.Add(mark);
        }
    }

    private void MoveTo(long ms, bool final)
    {
        _position = Math.Clamp(ms, 0, _duration); Arrange();
        Seek?.Invoke(_position, final);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_duration <= 0) return;
        Focus(); IsScrubbing = CaptureMouse(); e.Handled = true;
        MoveTo(TimeAt(e.GetPosition(this).X), !IsScrubbing);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_duration <= 0) return;
        var x = e.GetPosition(this).X; var at = TimeAt(x);
        Hover?.Invoke(at, Math.Clamp(x, Inset, ActualWidth - Inset));
        if (IsScrubbing) MoveTo(at, false);
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsScrubbing) return;
        IsScrubbing = false; ReleaseMouseCapture(); e.Handled = true;
        MoveTo(TimeAt(e.GetPosition(this).X), true);
        if (!IsMouseOver) Hover?.Invoke(null, 0);
    }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        // Capture taken away mid-drag (Alt+Tab, a dialog) still settles where the thumb stopped.
        if (IsScrubbing) { IsScrubbing = false; Seek?.Invoke(_position, true); Hover?.Invoke(null, 0); }
    }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); if (!IsScrubbing) Hover?.Invoke(null, 0); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_duration <= 0 || e.Handled) return;
        long? target = e.Key switch { Key.Left => _position - 5000, Key.Right => _position + 5000, Key.Home => 0, Key.End => _duration, _ => null };
        if (target is long at) { MoveTo(at, true); e.Handled = true; }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new TimelinePeer(this);
    private sealed class TimelinePeer(MediaTimeline owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
        protected override string GetClassNameCore() => nameof(MediaTimeline);
        public override object GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.RangeValue ? this : base.GetPattern(patternInterface);
        // Seconds, so screen readers announce a position people can relate to.
        public double Value => owner._position / 1000d;
        public double Minimum => 0;
        public double Maximum => owner._duration / 1000d;
        public double SmallChange => 5;
        public double LargeChange => 30;
        public bool IsReadOnly => owner._duration <= 0;
        public void SetValue(double value) { if (!IsReadOnly) owner.MoveTo((long)(value * 1000), true); }
    }
}
