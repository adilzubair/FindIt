using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;

namespace FindIt.App;

public enum FindItIconKind
{
    Folder, Library, People, Photo, Video, Document, Audio,
    Drive, Prepare, Appearance, Updates, Eject, Search, Filters,
    Gallery, List, Close, Back, Reference, Review, Merge, Add, Check, Warning
}

/// <summary>Original, layered vector artwork shared by navigation and media placeholders.</summary>
public sealed class FindItIcon : FrameworkElement
{
    private const FrameworkPropertyMetadataOptions Render = FrameworkPropertyMetadataOptions.AffectsRender;
    private static readonly ConcurrentDictionary<string, Geometry> Shapes = new();

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(FindItIconKind), typeof(FindItIcon),
        new FrameworkPropertyMetadata(FindItIconKind.Folder, Render, PaletteChanged));
    public static readonly DependencyProperty IsColorfulProperty = DependencyProperty.Register(
        nameof(IsColorful), typeof(bool), typeof(FindItIcon),
        new FrameworkPropertyMetadata(true, Render, PaletteChanged));
    public static readonly DependencyProperty BackBrushProperty = DependencyProperty.Register(
        nameof(BackBrush), typeof(Brush), typeof(FindItIcon), new FrameworkPropertyMetadata(Brushes.DarkGoldenrod, Render));
    public static readonly DependencyProperty FrontBrushProperty = DependencyProperty.Register(
        nameof(FrontBrush), typeof(Brush), typeof(FindItIcon), new FrameworkPropertyMetadata(Brushes.Goldenrod, Render));
    public static readonly DependencyProperty DetailBrushProperty = DependencyProperty.Register(
        nameof(DetailBrush), typeof(Brush), typeof(FindItIcon), new FrameworkPropertyMetadata(Brushes.White, Render));

    public FindItIconKind Kind { get => (FindItIconKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public bool IsColorful { get => (bool)GetValue(IsColorfulProperty); set => SetValue(IsColorfulProperty, value); }
    public Brush BackBrush { get => (Brush)GetValue(BackBrushProperty); set => SetValue(BackBrushProperty, value); }
    public Brush FrontBrush { get => (Brush)GetValue(FrontBrushProperty); set => SetValue(FrontBrushProperty, value); }
    public Brush DetailBrush { get => (Brush)GetValue(DetailBrushProperty); set => SetValue(DetailBrushProperty, value); }

    public FindItIcon()
    {
        Focusable = false;
        IsHitTestVisible = false;
        UseLayoutRounding = true;
        ApplyPalette();
    }

    protected override AutomationPeer? OnCreateAutomationPeer() => null;
    protected override Size MeasureOverride(Size availableSize) => new(Math.Min(double.IsNaN(Width) ? 24 : Width, availableSize.Width), Math.Min(double.IsNaN(Height) ? 24 : Height, availableSize.Height));
    private static void PaletteChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((FindItIcon)sender).ApplyPalette();

    private void ApplyPalette()
    {
        var family = !IsColorful ? "Utility" : Kind switch
        {
            FindItIconKind.Folder => "Folder",
            FindItIconKind.Library or FindItIconKind.Prepare => "Library",
            FindItIconKind.People or FindItIconKind.Merge => "People",
            FindItIconKind.Photo or FindItIconKind.Reference or FindItIconKind.Review => "Photo",
            FindItIconKind.Video => "Video",
            FindItIconKind.Document => "Document",
            FindItIconKind.Audio => "Audio",
            _ => "Utility"
        };
        SetResourceReference(BackBrushProperty, $"Icon{family}BackBrush");
        SetResourceReference(FrontBrushProperty, $"Icon{family}FrontBrush");
        SetResourceReference(DetailBrushProperty, $"Icon{family}DetailBrush");
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var scale = Math.Min(RenderSize.Width, RenderSize.Height) / 24;
        if (!double.IsFinite(scale) || scale <= 0) return;
        drawing.PushTransform(new TranslateTransform((RenderSize.Width - 24 * scale) / 2, (RenderSize.Height - 24 * scale) / 2));
        drawing.PushTransform(new ScaleTransform(scale, scale));
        var back = BackBrush;
        var front = FrontBrush;
        var detail = DetailBrush;
        void Shape(string path, Brush fill) => drawing.DrawGeometry(fill, null, Shapes.GetOrAdd(path, static data => { var geometry = Geometry.Parse(data); geometry.Freeze(); return geometry; }));
        void Stroke(string path, Brush brush, double thickness = 1.6)
        {
            var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            drawing.DrawGeometry(null, pen, Shapes.GetOrAdd(path, static data => { var geometry = Geometry.Parse(data); geometry.Freeze(); return geometry; }));
        }
        void Round(Brush fill, double x, double y, double width, double height, double radius = 2) => drawing.DrawRoundedRectangle(fill, null, new Rect(x, y, width, height), radius, radius);
        void Dot(Brush fill, double x, double y, double radius) => drawing.DrawEllipse(fill, null, new Point(x, y), radius, radius);
        void Soft(Action draw, double opacity = .3) { drawing.PushOpacity(opacity); draw(); drawing.Pop(); }

        switch (Kind)
        {
            case FindItIconKind.Folder:
                Shape("M2,6 C2,4.8 2.8,4 4,4 L9,4 C9.7,4 10.1,4.4 10.6,5 L12,6.5 20,6.5 C21.1,6.5 22,7.4 22,8.5 L22,18.5 C22,19.9 21,21 19.5,21 L4.5,21 C3,21 2,20 2,18.5 Z", back);
                Round(detail, 4, 7.5, 16, 3, 1);
                Shape("M2.6,9 L21.1,9 C22.2,9 22.7,9.7 22.5,10.7 L20.7,19.1 C20.5,20.2 19.6,21 18.5,21 L4.2,21 C3.1,21 2.4,20.3 2.2,19.2 L.9,11.2 C.7,9.8 1.3,9 2.6,9 Z", front);
                Soft(() => Stroke("M3.2,10.3 L20.3,10.3", detail, 1), .55);
                break;
            case FindItIconKind.Library:
                Round(back, 6, 2, 16, 18, 2.5);
                Round(front, 2, 5, 17, 18, 2.5);
                Round(detail, 4.5, 8, 12, 9.5, 1.2);
                Shape("M5,16.8 L8.6,12.4 11.2,15 13,13 16,16.8 Z", back);
                Dot(front, 13.3, 10.6, 1.1);
                Stroke("M5.2,20 L12.7,20", detail, 1.25);
                break;
            case FindItIconKind.People:
                Dot(back, 16.8, 6.8, 3.2);
                Shape("M12,20.7 L12,17.8 C12,14.7 14,12.6 17,12.6 C20.3,12.6 22.5,14.7 22.5,17.8 L22.5,20.7 Z", back);
                Dot(front, 8.5, 6.5, 3.8);
                Shape("M1.5,21 L1.5,18.2 C1.5,14.5 4.1,12 8.5,12 C12.9,12 15.6,14.5 15.6,18.2 L15.6,21 Z", front);
                Soft(() => Stroke("M4.1,16.9 C4.5,15.5 5.6,14.8 7,14.6", detail, 1.2), .65);
                break;
            case FindItIconKind.Photo:
            case FindItIconKind.Reference:
                Round(back, 2, 3, 20, 18, 3);
                Soft(() => Round(front, 3.6, 4.6, 16.8, 14.8, 1.8), .5);
                Shape("M3.2,18.7 L8.3,11.7 C8.7,11.2 9.2,11.2 9.7,11.8 L13,15.5 15.1,13.3 C15.6,12.8 16,12.8 16.5,13.4 L20.7,18.7 Z", detail);
                Dot(detail, 16.4, 8.2, 2);
                if (Kind == FindItIconKind.Reference)
                {
                    Dot(front, 19.5, 19.2, 4.2);
                    Stroke("M19.5,16.9 L19.5,21.5 M17.2,19.2 L21.8,19.2", detail, 1.4);
                }
                break;
            case FindItIconKind.Video:
                Round(back, 5, 2, 17, 17, 3);
                Round(front, 1.5, 5, 18.5, 17, 3);
                Soft(() => Stroke("M4.3,7.3 L16.7,7.3", detail, 1.2), .45);
                Shape("M8.7,10 C8.7,9.5 9.1,9.3 9.6,9.6 L15,12.7 C15.6,13 15.6,13.6 15,13.9 L9.6,17 C9.1,17.3 8.7,17.1 8.7,16.6 Z", detail);
                break;
            case FindItIconKind.Document:
                Round(back, 6.5, 3.5, 14, 19, 2);
                Shape("M5,1.5 L13.3,1.5 19,7.2 19,20 C19,21.1 18.2,22 17,22 L5,22 C3.8,22 3,21.1 3,20 L3,3.5 C3,2.3 3.8,1.5 5,1.5 Z", front);
                Shape("M13.3,1.5 L13.3,5.7 C13.3,6.7 13.9,7.2 14.8,7.2 L19,7.2 Z", detail);
                Stroke("M6.5,11 L14.6,11 M6.5,14.5 L14.6,14.5 M6.5,18 L11.8,18", detail, 1.4);
                break;
            case FindItIconKind.Audio:
                Round(back, 4.5, 2, 17.5, 18.5, 3);
                Round(front, 1.5, 5, 18.5, 17, 3);
                Stroke("M5.3,12 L5.3,15 M8.7,9.7 L8.7,17.3 M12.1,8 L12.1,19 M15.5,11 L15.5,16.2", detail, 1.8);
                break;
            case FindItIconKind.Drive:
                Round(back, 2, 4, 20, 17, 3);
                Shape("M4,5.8 L20,5.8 21.3,14.2 2.7,14.2 Z", front);
                Round(front, 2, 13.5, 20, 7.5, 2.2);
                Stroke("M5.5,17.3 L12.5,17.3", detail, 1.5);
                Dot(detail, 18.2, 17.3, 1.1);
                Soft(() => Stroke("M5.7,7.7 L18.3,7.7", detail, 1), .5);
                break;
            case FindItIconKind.Prepare:
                Round(back, 2, 12.5, 20, 9, 2.6);
                Round(front, 2, 15.3, 20, 6.2, 2.2);
                Shape("M11,1.7 C11.5,1.2 12.5,1.2 13,1.7 L18.4,7.1 C19,7.7 18.6,8.7 17.7,8.7 L14.3,8.7 14.3,15 9.7,15 9.7,8.7 6.3,8.7 C5.4,8.7 5,7.7 5.6,7.1 Z", front);
                Dot(detail, 18.5, 18.2, .9);
                break;
            case FindItIconKind.Appearance:
                Dot(back, 12, 12, 10);
                Shape("M12,2 A10,10 0 0 1 12,22 Z", front);
                Dot(detail, 12, 12, 4);
                Soft(() => Stroke("M6.1,5.8 C4.4,7.5 3.7,9.7 3.7,12", detail, 1.1), .65);
                break;
            case FindItIconKind.Updates:
                Soft(() => Dot(back, 12, 12, 10), .2);
                Stroke("M4.3,10.2 A8,8 0 0 1 17.9,6.3 M19.7,13.8 A8,8 0 0 1 6.1,17.7", front, 2.1);
                Shape("M14.4,6.9 L19.5,7.3 19.8,2.1 Z M9.6,17.1 L4.5,16.7 4.2,21.9 Z", front);
                break;
            case FindItIconKind.Eject:
                Shape("M11.1,3.8 C11.5,3.2 12.5,3.2 12.9,3.8 L20.1,13.2 C20.7,14 20.2,15 19.2,15 L4.8,15 C3.8,15 3.3,14 3.9,13.2 Z", front);
                Round(back, 4, 18, 16, 3.2, 1.1);
                break;
            case FindItIconKind.Search:
                Soft(() => Dot(back, 10, 10, 7), .25);
                drawing.DrawEllipse(null, new Pen(front, 2), new Point(10, 10), 7, 7);
                Stroke("M15.4,15.4 L21.5,21.5", front, 2.8);
                Soft(() => Stroke("M5.9,9 C6.2,7.1 7.4,6 9,5.8", detail, 1.25), .8);
                break;
            case FindItIconKind.Filters:
                Shape("M3.8,3 L20.2,3 C21.1,3 21.5,4 20.9,4.6 L14.7,11.8 14.7,18.6 C14.7,19 14.5,19.4 14.1,19.6 L10.1,21.6 C9.4,22 8.7,21.5 8.7,20.8 L8.7,11.8 3.1,4.6 C2.5,4 2.9,3 3.8,3 Z", back);
                Shape("M8.7,11.8 L14.7,11.8 14.7,18.6 8.7,21.6 Z", front);
                Stroke("M6.4,5.7 L17.7,5.7", detail, 1.3);
                break;
            case FindItIconKind.Gallery:
                Round(front, 2, 2, 8.3, 8.3, 1.8);
                Round(back, 13.7, 2, 8.3, 8.3, 1.8);
                Round(back, 2, 13.7, 8.3, 8.3, 1.8);
                Round(front, 13.7, 13.7, 8.3, 8.3, 1.8);
                break;
            case FindItIconKind.List:
                for (var row = 0; row < 3; row++)
                {
                    var y = 3 + row * 7.4;
                    Round(back, 2, y, 4.1, 4.1, 1);
                    Round(front, 9.2, y + .7, 12.8, 2.6, 1.2);
                }
                break;
            case FindItIconKind.Close:
                Stroke("M5.3,5.3 L18.7,18.7 M18.7,5.3 L5.3,18.7", front, 2.1);
                break;
            case FindItIconKind.Back:
                Stroke("M11.1,4.6 L3.7,12 11.1,19.4 M4.2,12 L21,12", front, 2.1);
                break;
            case FindItIconKind.Review:
                Shape("M12,1.5 L21,5 20.5,12.7 C20.2,17 16.9,20.3 12,22.5 C7.1,20.3 3.8,17 3.5,12.7 L3,5 Z", back);
                Shape("M12,4.5 L18.1,6.9 17.8,12.3 C17.6,15.5 15.4,17.9 12,19.7 C8.6,17.9 6.4,15.5 6.2,12.3 L5.9,6.9 Z", front);
                Stroke("M8.1,11.8 L10.7,14.5 16.1,9", detail, 1.9);
                break;
            case FindItIconKind.Merge:
                Stroke("M4.4,3.5 L4.4,7 C4.4,10.7 12,10.1 12,14 L12,20.5 M19.6,3.5 L19.6,7 C19.6,10.7 12,10.1 12,14", back, 3);
                Shape("M7.4,17 L16.6,17 12,22.1 Z", front);
                Dot(front, 4.4, 3.5, 2.5); Dot(front, 19.6, 3.5, 2.5);
                break;
            case FindItIconKind.Add:
                Soft(() => Dot(back, 12, 12, 10), .23);
                Stroke("M12,5.5 L12,18.5 M5.5,12 L18.5,12", front, 2.3);
                break;
            case FindItIconKind.Check:
                Stroke("M3.9,12.4 L9.3,18 20.3,6.4", front, 2.7);
                break;
            case FindItIconKind.Warning:
                Shape("M10.4,2.5 C11.1,1.2 12.9,1.2 13.6,2.5 L23,19 C23.7,20.3 22.9,21.8 21.4,21.8 L2.6,21.8 C1.1,21.8 .3,20.3 1,19 Z", front);
                Stroke("M12,7.4 L12,13.6", detail, 2.1);
                Dot(detail, 12, 17.7, 1.2);
                break;
        }
        drawing.Pop();
        drawing.Pop();
    }
}
