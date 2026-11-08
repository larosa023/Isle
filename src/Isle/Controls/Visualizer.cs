using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Isle.Core;

namespace Isle.Controls;

/// <summary>
/// Four little bars. In "real" mode they follow what your speakers are actually playing (one shared
/// 30 fps meter read for every visualizer); in "animated" mode they bounce on their own. Either way
/// nothing runs while the bars aren't on screen, so an idle island costs ~0% CPU.
/// </summary>
public sealed class Visualizer : StackPanel
{
    /// <summary>"real", "animated" or "off".</summary>
    public static string Mode { get; set; } = "real";

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(Visualizer),
        new PropertyMetadata(Brushes.White, (d, _) => ((Visualizer)d).ApplyLook()));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(Visualizer),
        new PropertyMetadata(false, (d, _) => ((Visualizer)d).Update()));

    public static readonly DependencyProperty BarHeightProperty = DependencyProperty.Register(
        nameof(BarHeight), typeof(double), typeof(Visualizer),
        new PropertyMetadata(14.0, (d, _) => ((Visualizer)d).ApplyLook()));

    static readonly int[] Durations = { 430, 310, 520, 370 };
    static readonly double[] Weights = { 0.85, 1.0, 0.75, 0.92 };
    static readonly List<Visualizer> Live = new();
    static DispatcherTimer? _meterTimer;
    static double _level;
    static readonly Random Rng = new();

    readonly Rectangle[] _bars = new Rectangle[4];
    readonly ScaleTransform[] _scales = new ScaleTransform[4];
    readonly double[] _jitter = new double[4];
    string _running = "";

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
        IsVisibleChanged += (_, _) => Update();
    }

    public Brush BarBrush { get => (Brush)GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public double BarHeight { get => (double)GetValue(BarHeightProperty); set => SetValue(BarHeightProperty, value); }

    /// <summary>Re-evaluate every visualizer after the mode changes.</summary>
    public static void Refresh()
    {
        foreach (var v in Live.ToArray()) v.Update(force: true);
    }

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

    void Update(bool force = false)
    {
        var want = !IsActive || !IsVisible ? "" : Mode is "real" or "animated" ? Mode : "";
        if (want == _running && !force) return;
        Stop();
        _running = want;

        if (want == "animated")
        {
            for (int i = 0; i < _scales.Length; i++)
            {
                var anim = new DoubleAnimation(0.25, 1.0, TimeSpan.FromMilliseconds(Durations[i]))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromMilliseconds(i * 70),
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                };
                Timeline.SetDesiredFrameRate(anim, 30);
                _scales[i].BeginAnimation(ScaleTransform.ScaleYProperty, anim);
            }
        }
        else if (want == "real")
        {
            if (Live.Count == 0)
            {
                CoreAudio.Acquire();
                _meterTimer ??= CreateMeterTimer();
                _meterTimer.Start();
            }
            Live.Add(this);
        }
        else
        {
            foreach (var s in _scales) s.ScaleY = IsActive ? 0.6 : 0.35;
        }
    }

    void Stop()
    {
        foreach (var s in _scales) s.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        if (_running == "real" && Live.Remove(this) && Live.Count == 0)
        {
            _meterTimer?.Stop();
            CoreAudio.Release();
        }
        _running = "";
    }

    static DispatcherTimer CreateMeterTimer()
    {
        var t = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        t.Tick += (_, _) =>
        {
            double peak = Math.Sqrt(CoreAudio.Peak()); // perceptual-ish curve
            // fast attack, gentle release
            _level = peak > _level ? _level + (peak - _level) * 0.65 : _level + (peak - _level) * 0.18;
            foreach (var v in Live) v.Paint(_level);
        };
        return t;
    }

    void Paint(double level)
    {
        for (int i = 0; i < _scales.Length; i++)
        {
            _jitter[i] += ((Rng.NextDouble() - 0.5) * 0.5 - _jitter[i]) * 0.35;
            double v = level <= 0.01 ? 0.18 : 0.2 + level * Weights[i] * (0.85 + _jitter[i]);
            _scales[i].ScaleY = Math.Clamp(v, 0.15, 1);
        }
    }
}
