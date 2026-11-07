using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace Isle.Core;

public enum ActivityKind { Media, Timer, Download, Call, Notification, Sports, Update, Custom }

/// <summary>A button shown in the expanded island.</summary>
public sealed class IslandAction
{
    public IslandAction(string glyph, string? label, Action invoke, bool primary = false)
    {
        Glyph = glyph;
        Label = label;
        Invoke = invoke;
        Primary = primary;
    }

    public string Glyph { get; }
    public string? Label { get; }
    public Action Invoke { get; }
    public bool Primary { get; }
    public bool HasGlyph => !string.IsNullOrEmpty(Glyph);
    public bool HasLabel => !string.IsNullOrEmpty(Label);
    public Thickness LabelMargin => HasGlyph ? new Thickness(6, 0, 0, 0) : new Thickness(0);
}

/// <summary>
/// One live thing the island can show. Every module (media, timers, downloads, calls, sports,
/// external apps) describes itself with this same shape, so the UI has a single template.
/// Mutate only on the UI thread.
/// </summary>
public sealed class Activity : INotifyPropertyChanged
{
    public Activity(string id, ActivityKind kind)
    {
        Id = id;
        Kind = kind;
    }

    public string Id { get; }
    public ActivityKind Kind { get; }
    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    /// <summary>When set, the hub removes the activity after this time (UTC).</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Clicking the expanded island body runs this.</summary>
    public Action? OnOpen { get; set; }

    public void SetTtl(TimeSpan ttl) => ExpiresAt = DateTime.UtcNow + ttl;

    string? _source;
    public string? Source { get => _source; set { if (Set(ref _source, value)) Raise(nameof(HasSource)); } }
    public bool HasSource => !string.IsNullOrWhiteSpace(_source);

    string _title = "";
    public string Title { get => _title; set => Set(ref _title, value ?? ""); }

    string? _subtitle;
    public string? Subtitle { get => _subtitle; set { if (Set(ref _subtitle, value)) Raise(nameof(HasSubtitle)); } }
    public bool HasSubtitle => !string.IsNullOrWhiteSpace(_subtitle);

    ImageSource? _image;
    public ImageSource? Image { get => _image; set { if (Set(ref _image, value)) { Raise(nameof(HasImage)); Raise(nameof(ShowGlyph)); } } }
    public bool HasImage => _image != null;

    string _glyph = "";
    public string Glyph { get => _glyph; set { if (Set(ref _glyph, value ?? "")) Raise(nameof(ShowGlyph)); } }
    public bool ShowGlyph => _image == null && !string.IsNullOrEmpty(_glyph);

    Color? _accent;
    public Color? Accent { get => _accent; set { if (Set(ref _accent, value)) Raise(nameof(AccentBrush)); } }
    public Brush AccentBrush => ThemeManager.BrushFor(_accent);

    double? _progress;
    public double? Progress
    {
        get => _progress;
        set { if (Set(ref _progress, value)) { Raise(nameof(HasProgress)); Raise(nameof(ProgressValue)); } }
    }
    public bool HasProgress => _progress.HasValue;
    public double ProgressValue => Math.Clamp(_progress ?? 0, 0, 1);

    string? _progressLeft;
    public string? ProgressLeft { get => _progressLeft; set => Set(ref _progressLeft, value); }

    string? _progressRight;
    public string? ProgressRight { get => _progressRight; set => Set(ref _progressRight, value); }

    /// <summary>Short text on the right of the compact pill (e.g. "4:59", "98–95").</summary>
    string? _trailing;
    public string? Trailing { get => _trailing; set { if (Set(ref _trailing, value)) Raise(nameof(HasTrailing)); } }
    public bool HasTrailing => !string.IsNullOrEmpty(_trailing) && !ShowVisualizer;

    /// <summary>Large text on the right of the expanded island (e.g. a countdown).</summary>
    string? _badge;
    public string? Badge { get => _badge; set { if (Set(ref _badge, value)) Raise(nameof(HasBadge)); } }
    public bool HasBadge => !string.IsNullOrEmpty(_badge);

    bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        set { if (Set(ref _isPlaying, value)) { Raise(nameof(ShowVisualizer)); Raise(nameof(HasTrailing)); } }
    }
    public bool ShowVisualizer => Kind == ActivityKind.Media && _isPlaying;

    bool _isLive;
    public bool IsLive { get => _isLive; set => Set(ref _isLive, value); }

    int _priority;
    public int Priority { get => _priority; set => Set(ref _priority, value); }

    IReadOnlyList<IslandAction> _actions = Array.Empty<IslandAction>();
    public IReadOnlyList<IslandAction> Actions
    {
        get => _actions;
        set { if (Set(ref _actions, value ?? Array.Empty<IslandAction>())) Raise(nameof(HasActions)); }
    }
    public bool HasActions => _actions.Count > 0;

    public void RefreshTheme() => Raise(nameof(AccentBrush));

    public event PropertyChangedEventHandler? PropertyChanged;

    bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
