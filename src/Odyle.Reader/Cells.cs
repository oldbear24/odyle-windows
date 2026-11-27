using System;
using System.Windows;
using System.Windows.Media;

namespace Odyle.Reader;
// A capsule (compact mode's floating chips): its corners are always half its height, so both ends are half circles.
// A Border with a large CornerRadius scales the radius apart along each side and turns the ends into flat ellipses.
public sealed class PillBorder : System.Windows.Controls.Border
{
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        var radius = Math.Min(sizeInfo.NewSize.Width, sizeInfo.NewSize.Height) / 2;
        if (Math.Abs(CornerRadius.TopLeft - radius) > 0.01) CornerRadius = new CornerRadius(radius);
    }
}

// The HUD's rings (variant C): how full something is, as an arc from the top clockwise over a faint track, with round
// ends. The card's big one holds the Odyle and fills with the natural pool towards its cap, inside a thin blue one that
// fills with what items added towards their limit (ClockRules.BonusCap); the party's and compact mode's fill the same
// way, and the checklist's with the tasks done.
public sealed class Ring : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(Ring), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(Ring), new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(Ring), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(nameof(Track), typeof(Brush), typeof(Ring), new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(0x17, 0xFF, 0xFF, 0xFF)), FrameworkPropertyMetadataOptions.AffectsRender));
    // How full, 0 to 100.
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);
    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = Math.Min(ActualWidth, ActualHeight); var thickness = Math.Min(Thickness, size / 2);
        if (size <= thickness * 2) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2); var radius = (size - thickness) / 2;
        drawingContext.DrawEllipse(null, new Pen(Track, thickness), center, radius, radius);
        var part = Math.Clamp(Value, 0, 100) / 100;
        if (part <= 0) return;
        var pen = new Pen(Fill, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (part >= 0.999) { drawingContext.DrawEllipse(null, pen, center, radius, radius); return; }
        var angle = part * 2 * Math.PI; var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
        var arc = new StreamGeometry();
        using (var draw = arc.Open()) { draw.BeginFigure(start, false, false); draw.ArcTo(end, new Size(radius, radius), 0, part > 0.5, SweepDirection.Clockwise, true, true); }
        arc.Freeze();
        drawingContext.DrawGeometry(null, pen, arc);
    }
}
