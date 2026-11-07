using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Isle.Controls;

/// <summary>
/// Four little bars that bounce while music plays. Animations run at a capped frame rate and
/// stop entirely whenever the control is not visible, so an idle island costs ~0% CPU.
/// </summary>
public sealed class Visualizer : StackPanel
{
    public static bool AnimationsEnabled { get; set; } = true;

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(Visualizer),
        new PropertyMetadata(Brushes.White, (d, _) => ((Visualizer)d).ApplyLook()));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(Visualizer),
        new PropertyMetadata(false, (d, _) => ((Visualizer)d).UpdateAnimation()));

    public static readonly DependencyProperty BarHeightProperty = DependencyProperty.Register(
        nameof(BarHeight), typeof(double), typeof(Visualizer),
        new PropertyMetadata(14.0, (d, _) => ((Visualizer)d).ApplyLook()));

    static readonly int[] Durations = { 430, 310, 520, 370 };

    readonly Rectangle[] _bars = new Rectangle[4];
    readonly ScaleTransform[] _scales = new ScaleTransform[4];
    bool _running;

    public Visualizer()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        for (int i = 0; i < _bars.Length; i++)
        {
            _scales[i] = new ScaleTransform(1, 0.35);
            _bars[i] = new Rectangle
            {
                Width = 3,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Margin = new Thickness(1.25, 0, 1.25, 0),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = _scales[i],
            };
            Children.Add(_bars[i]);
        }
        ApplyLook();
        IsVisibleChanged += (_, _) => UpdateAnimation();
    }

    public Brush BarBrush { get => (Brush)GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public double BarHeight { get => (double)GetValue(BarHeightProperty); set => SetValue(BarHeightProperty, value); }

    void ApplyLook()
    {
        foreach (var bar in _bars)
        {
            if (bar == null) continue;
            bar.Fill = BarBrush;
            bar.Height = BarHeight;
            bar.Width = Math.Max(2.5, BarHeight / 4.5);
            bar.RadiusX = bar.RadiusY = bar.Width / 2;
        }
    }

    void UpdateAnimation()
    {
        bool run = IsActive && IsVisible && AnimationsEnabled;
        if (run == _running) return;
        _running = run;

        for (int i = 0; i < _scales.Length; i++)
        {
            if (run)
            {
                var anim = new DoubleAnimation(0.25, 1.0, TimeSpan.FromMilliseconds(Durations[i]))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromMilliseconds(i * 70),
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                };
                Timeline.SetDesiredFrameRate(anim, 24);
                _scales[i].BeginAnimation(ScaleTransform.ScaleYProperty, anim);
            }
            else
            {
                _scales[i].BeginAnimation(ScaleTransform.ScaleYProperty, null);
                _scales[i].ScaleY = IsActive ? 0.6 : 0.35;
            }
        }
    }
}
