using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace Isle.Controls;

/// <summary>
/// A damped spring, like the island's motion on iOS: fast out of the gate, settles softly, with an
/// optional small overshoot. Damping 1 = no overshoot; lower values bounce more.
/// </summary>
public sealed class SpringEase : EasingFunctionBase
{
    public static readonly DependencyProperty DampingProperty = DependencyProperty.Register(
        nameof(Damping), typeof(double), typeof(SpringEase), new PropertyMetadata(0.8));

    /// <summary>0.3 (very bouncy) … 1 (no bounce).</summary>
    public double Damping
    {
        get => (double)GetValue(DampingProperty);
        set => SetValue(DampingProperty, value);
    }

    public SpringEase()
    {
        EasingMode = EasingMode.EaseIn; // EaseInCore below is already the final curve
    }

    protected override double EaseInCore(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        const double omega = 10; // stiffness: fully settled by t = 1
        double z = Math.Clamp(Damping, 0.3, 1);
        if (z >= 0.999)
            return 1 - Math.Exp(-omega * t) * (1 + omega * t); // critically damped
        double wd = omega * Math.Sqrt(1 - z * z);
        return 1 - Math.Exp(-z * omega * t) * (Math.Cos(wd * t) + z / Math.Sqrt(1 - z * z) * Math.Sin(wd * t));
    }

    protected override Freezable CreateInstanceCore() => new SpringEase();
}
