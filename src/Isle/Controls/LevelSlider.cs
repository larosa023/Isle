using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Isle.Controls;

/// <summary>
/// A slim volume slider with a live level meter inside its fill. Drag or click to set, scroll to
/// nudge. Drawn directly (no template), so a dozen of them updating 25 times a second stay cheap.
/// </summary>
public sealed class LevelSlider : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(LevelSlider), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(LevelSlider), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(LevelSlider), new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(LevelSlider), new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThumbBrushProperty = DependencyProperty.Register(
        nameof(ThumbBrush), typeof(Brush), typeof(LevelSlider), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty DimmedProperty = DependencyProperty.Register(
        nameof(Dimmed), typeof(bool), typeof(LevelSlider), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0–1.</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, Math.Clamp(value, 0, 1)); }
    /// <summary>The live level, 0–1, drawn inside the fill.</summary>
    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, Math.Clamp(value, 0, 1)); }
    public Brush FillBrush { get => (Brush)GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush ThumbBrush { get => (Brush)GetValue(ThumbBrushProperty); set => SetValue(ThumbBrushProperty, value); }
    /// <summary>Muted: the fill goes grey.</summary>
    public bool Dimmed { get => (bool)GetValue(DimmedProperty); set => SetValue(DimmedProperty, value); }

    /// <summary>The user moved it (not raised when <see cref="Value"/> is set from code).</summary>
    public event Action<double>? ValueChanged;

    public bool Dragging { get; private set; }
    bool _hot;

    const double Track = 4, Thumb = 6, ThumbHot = 7.5;

    public LevelSlider()
    {
        Height = 20;
        Cursor = Cursors.Hand;
        Focusable = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0) return;
        double r = _hot || Dragging ? ThumbHot : Thumb;
        double x0 = r, x1 = Math.Max(r, w - r), span = x1 - x0;
        double y = h / 2;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // whole area is clickable
        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(x0, y - Track / 2, span, Track), Track / 2, Track / 2);

        double fx = span * Value;
        if (fx > 0.5)
        {
            var fill = Dimmed ? TrackBrush : FillBrush;
            dc.PushOpacity(Dimmed ? 1 : 0.45);
            dc.DrawRoundedRectangle(fill, null, new Rect(x0, y - Track / 2, fx, Track), Track / 2, Track / 2);
            dc.Pop();
            double mx = fx * Level;
            if (!Dimmed && mx > 0.5)
                dc.DrawRoundedRectangle(FillBrush, null, new Rect(x0, y - Track / 2, mx, Track), Track / 2, Track / 2);
        }
        dc.DrawEllipse(ThumbBrush, null, new Point(x0 + fx, y), r, r);
    }

    void SetFrom(MouseEventArgs e)
    {
        double r = ThumbHot;
        double span = Math.Max(1, ActualWidth - r * 2);
        double v = Math.Clamp((e.GetPosition(this).X - r) / span, 0, 1);
        v = Math.Round(v * 100) / 100;
        if (Math.Abs(v - Value) < 0.001) return;
        Value = v;
        ValueChanged?.Invoke(v);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        Dragging = CaptureMouse();
        SetFrom(e);
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (Dragging) SetFrom(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        e.Handled = true; // the island would treat it as a click on the card
        if (Dragging) ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        Dragging = false;
        InvalidateVisual();
    }

    protected override void OnMouseEnter(MouseEventArgs e) { _hot = true; InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { _hot = false; InvalidateVisual(); }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        e.Handled = true;
        var v = Math.Clamp(Math.Round((Value + (e.Delta > 0 ? 0.02 : -0.02)) * 100) / 100, 0, 1);
        if (Math.Abs(v - Value) < 0.001) return;
        Value = v;
        ValueChanged?.Invoke(v);
    }
}
