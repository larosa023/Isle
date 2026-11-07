using System;
using System.Collections.Generic;
using System.Media;
using System.Windows.Media;
using System.Windows.Threading;
using Isle.Core;

namespace Isle.Services;

/// <summary>Countdown timers started from the island, tray, or the local API.</summary>
public sealed class TimerService
{
    sealed class IslandTimer
    {
        public string Id = "";
        public string Label = "";
        public TimeSpan Duration;
        public DateTime EndsAt;
        public TimeSpan Remaining;
        public bool Running;
        public bool? ActionsForRunning;
        public string Shown = "";
    }

    static readonly Color Orange = Color.FromRgb(0xFF, 0x9F, 0x0A);

    readonly ActivityHub _hub;
    readonly List<IslandTimer> _timers = new();
    readonly DispatcherTimer _tick;
    int _counter, _beepsLeft;
    DateTime _nextBeep;

    public TimerService(ActivityHub hub)
    {
        _hub = hub;
        _tick = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(250) };
        _tick.Tick += (_, _) => Tick();
    }

    public string Start(TimeSpan duration, string? label = null)
    {
        if (duration < TimeSpan.FromSeconds(1)) duration = TimeSpan.FromSeconds(1);
        if (duration > TimeSpan.FromHours(24)) duration = TimeSpan.FromHours(24);

        var t = new IslandTimer
        {
            Id = "timer:" + (++_counter),
            Label = string.IsNullOrWhiteSpace(label) ? $"{Fmt.Human(duration)} timer" : label.Trim(),
            Duration = duration,
            Remaining = duration,
            EndsAt = DateTime.UtcNow + duration,
            Running = true,
        };
        _timers.Add(t);

        var a = new Activity(t.Id, ActivityKind.Timer)
        {
            Glyph = Glyphs.Timer,
            Source = "Timer",
            Priority = 70,
            Accent = Orange,
        };
        _hub.Add(a);
        Render(t);
        _hub.Peek(a);
        _tick.Start();
        return t.Id;
    }

    void Tick()
    {
        var now = DateTime.UtcNow;
        foreach (var t in _timers.ToArray())
        {
            if (_hub.Get(t.Id) == null) { _timers.Remove(t); continue; }
            if (t.Running && t.EndsAt <= now) Finish(t);
            else Render(t);
        }

        if (_beepsLeft > 0 && now >= _nextBeep)
        {
            _beepsLeft--;
            _nextBeep = now.AddSeconds(1.3);
            SystemSounds.Exclamation.Play();
        }

        if (_timers.Count == 0 && _beepsLeft == 0) _tick.Stop();
    }

    void Render(IslandTimer t)
    {
        var a = _hub.Get(t.Id);
        if (a == null) return;

        var remaining = t.Running ? t.EndsAt - DateTime.UtcNow : t.Remaining;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        var text = Fmt.Countdown(remaining);
        var shown = text + t.Running + t.Duration.Ticks;
        if (shown == t.Shown) return; // nothing visible changed; don't repaint
        t.Shown = shown;

        a.Title = t.Label;
        a.Subtitle = t.Running ? "Ends at " + (DateTime.Now + remaining).ToString("h:mm tt") : "Paused";
        a.Trailing = text;
        a.Badge = text;
        a.Progress = 1 - remaining.TotalSeconds / t.Duration.TotalSeconds;

        if (t.ActionsForRunning == t.Running) return; // only rebuild buttons when state flips
        t.ActionsForRunning = t.Running;
        a.Actions = new[]
        {
            new IslandAction(Glyphs.Add, "1 min", () => AddTime(t, TimeSpan.FromMinutes(1))),
            t.Running
                ? new IslandAction(Glyphs.Pause, null, () => Pause(t), primary: true)
                : new IslandAction(Glyphs.Play, null, () => Resume(t), primary: true),
            new IslandAction(Glyphs.Close, null, () => Cancel(t)),
        };
    }

    void Pause(IslandTimer t)
    {
        if (!t.Running) return;
        t.Remaining = t.EndsAt - DateTime.UtcNow;
        t.Running = false;
        Render(t);
    }

    void Resume(IslandTimer t)
    {
        if (t.Running) return;
        t.EndsAt = DateTime.UtcNow + t.Remaining;
        t.Running = true;
        Render(t);
        _tick.Start();
    }

    void AddTime(IslandTimer t, TimeSpan extra)
    {
        if (t.Running) t.EndsAt += extra; else t.Remaining += extra;
        t.Duration += extra;
        Render(t);
    }

    void Cancel(IslandTimer t)
    {
        _timers.Remove(t);
        _hub.Remove(t.Id);
    }

    void Finish(IslandTimer t)
    {
        _timers.Remove(t);
        _hub.Remove(t.Id);

        var done = new Activity(t.Id + ":done", ActivityKind.Timer)
        {
            Title = t.Label,
            Subtitle = "Time's up",
            Source = "Timer",
            Glyph = Glyphs.Timer,
            Priority = 95,
            Accent = Orange,
            Trailing = "Done",
            Badge = "0:00",
            IsLive = true,
        };
        var label = t.Label;
        done.Actions = new[]
        {
            new IslandAction(Glyphs.Refresh, "1 min", () => { Dismiss(done); Start(TimeSpan.FromMinutes(1), label); }),
            new IslandAction(Glyphs.Check, "Dismiss", () => Dismiss(done), primary: true),
        };
        done.OnOpen = () => Dismiss(done);
        done.SetTtl(TimeSpan.FromMinutes(2));
        _hub.Add(done);
        _hub.Peek(done);

        _beepsLeft = 3;
        _nextBeep = DateTime.UtcNow;
    }

    void Dismiss(Activity a)
    {
        _beepsLeft = 0;
        _hub.Remove(a.Id);
    }
}
