using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Media;
using System.Windows.Media;
using System.Windows.Threading;
using Isle.Core;

namespace Isle.Services;

/// <summary>Countdown timers, a stopwatch and focus sessions — from the island, tray, shortcuts or the local API.</summary>
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
        public string? FocusPhase;   // "focus" | "break" | "long" for focus sessions
    }

    const string StopwatchId = "stopwatch";
    const string FocusId = "focus";
    static readonly Color Orange = Color.FromRgb(0xFF, 0x9F, 0x0A);
    static readonly Color Teal = Color.FromRgb(0x40, 0xC8, 0xE0);
    static readonly Color Purple = Color.FromRgb(0xBF, 0x5A, 0xF2);
    static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58);
    static AppSettings S => AppSettings.Current;

    readonly ActivityHub _hub;
    readonly List<IslandTimer> _timers = new();
    readonly DispatcherTimer _tick;
    int _counter, _soundsLeft;
    DateTime _nextSound;
    SoundPlayer? _player;
    string? _playerPath;

    // stopwatch
    bool _swRunning;
    DateTime _swStartedAt;
    TimeSpan _swBanked;
    readonly List<TimeSpan> _laps = new();
    string _swShown = "";
    bool? _swActionsFor;

    // focus session
    int _focusRound;

    public TimerService(ActivityHub hub)
    {
        _hub = hub;
        _tick = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(200) };
        _tick.Tick += (_, _) => Tick();
    }

    /// <summary>A focus session is running (used for "Do not disturb during focus").</summary>
    public bool InFocus => _timers.Any(t => t.FocusPhase == "focus" && t.Running);

    public event Action? FocusChanged;

    // ───────────────────────────── countdowns ─────────────────────────────

    public string Start(TimeSpan duration, string? label = null) => StartCore(duration, label, null, "timer:" + (++_counter));

    string StartCore(TimeSpan duration, string? label, string? phase, string id)
    {
        if (duration < TimeSpan.FromSeconds(1)) duration = TimeSpan.FromSeconds(1);
        if (duration > TimeSpan.FromHours(24)) duration = TimeSpan.FromHours(24);

        var t = new IslandTimer
        {
            Id = id,
            Label = string.IsNullOrWhiteSpace(label) ? $"{Fmt.Human(duration)} timer" : label.Trim(),
            Duration = duration,
            Remaining = duration,
            EndsAt = DateTime.UtcNow + duration,
            Running = true,
            FocusPhase = phase,
        };
        _timers.RemoveAll(x => x.Id == id);
        _timers.Add(t);

        var color = phase switch { "focus" => Purple, "break" or "long" => Teal, _ => Orange };
        var a = new Activity(t.Id, ActivityKind.Timer)
        {
            Glyph = phase == null ? Glyphs.Timer : phase == "focus" ? "" : "",
            Source = phase == null ? "Timer" : $"Focus · round {_focusRound} of {Math.Max(1, S.FocusRounds)}",
            Priority = phase == "focus" ? 72 : 70,
            Accent = color,
            AppKey = phase == null ? "timer:countdown" : "timer:focus",
        };
        _hub.Add(a);
        Render(t);
        _hub.Peek(a);
        _tick.Start();
        if (phase != null) FocusChanged?.Invoke();
        return t.Id;
    }

    void Tick()
    {
        var now = DateTime.UtcNow;
        foreach (var t in _timers.ToArray())
        {
            if (_hub.Get(t.Id) == null) { _timers.Remove(t); if (t.FocusPhase != null) FocusChanged?.Invoke(); continue; }
            if (t.Running && t.EndsAt <= now) Finish(t);
            else Render(t);
        }

        if (_swRunning) RenderStopwatch();

        if (_soundsLeft > 0 && now >= _nextSound)
        {
            _soundsLeft--;
            _nextSound = now.AddSeconds(2.2);
            PlaySound();
        }

        if (_timers.Count == 0 && _soundsLeft == 0 && !_swRunning) _tick.Stop();
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

        a.Title = t.FocusPhase switch { "focus" => "Focus", "break" => "Short break", "long" => "Long break", _ => t.Label };
        a.Subtitle = t.Running ? "Ends at " + (DateTime.Now + remaining).ToString("h:mm tt") : "Paused";
        a.Trailing = text;
        a.Badge = text;
        a.Progress = 1 - remaining.TotalSeconds / t.Duration.TotalSeconds;

        if (t.ActionsForRunning == t.Running) return; // only rebuild buttons when state flips
        t.ActionsForRunning = t.Running;
        var playPause = t.Running
            ? new IslandAction(Glyphs.Pause, null, () => Pause(t), primary: true)
            : new IslandAction(Glyphs.Play, null, () => Resume(t), primary: true);

        a.Actions = t.FocusPhase == null
            ? new[]
            {
                new IslandAction(Glyphs.Add, "1 min", () => AddTime(t, TimeSpan.FromMinutes(1))),
                playPause,
                new IslandAction(Glyphs.Close, null, () => Cancel(t)),
            }
            : new[]
            {
                playPause,
                new IslandAction(Glyphs.Next, "Skip", () => Finish(t, skipped: true)),
                new IslandAction(Glyphs.Close, "End", StopFocus),
            };
    }

    void Pause(IslandTimer t)
    {
        if (!t.Running) return;
        t.Remaining = t.EndsAt - DateTime.UtcNow;
        t.Running = false;
        Render(t);
        if (t.FocusPhase != null) FocusChanged?.Invoke();
    }

    void Resume(IslandTimer t)
    {
        if (t.Running) return;
        t.EndsAt = DateTime.UtcNow + t.Remaining;
        t.Running = true;
        Render(t);
        _tick.Start();
        if (t.FocusPhase != null) FocusChanged?.Invoke();
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

    void Finish(IslandTimer t, bool skipped = false)
    {
        _timers.Remove(t);
        _hub.Remove(t.Id);

        if (t.FocusPhase != null)
        {
            NextFocusPhase(t.FocusPhase, skipped);
            return;
        }

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
            AppKey = "timer:countdown",
        };
        var label = t.Label;
        var duration = t.Duration;
        done.Actions = new[]
        {
            new IslandAction(Glyphs.Refresh, "Again", () => { Dismiss(done); Start(duration, label); }),
            new IslandAction(Glyphs.Add, "1 min", () => { Dismiss(done); Start(TimeSpan.FromMinutes(1), label); }),
            new IslandAction(Glyphs.Check, "Dismiss", () => Dismiss(done), primary: true),
        };
        done.OnOpen = () => Dismiss(done);
        done.SetTtl(TimeSpan.FromMinutes(2));
        _hub.Add(done);
        _hub.Peek(done);
        Ring();
    }

    void Dismiss(Activity a)
    {
        _soundsLeft = 0;
        _player?.Stop();
        _hub.Remove(a.Id);
    }

    // ───────────────────────────── focus sessions ─────────────────────────────

    /// <summary>Starts (or restarts) a focus session: focus, short break, … long break after N rounds.</summary>
    public void StartFocus()
    {
        _focusRound = 1;
        _hub.Remove(FocusId + ":next");
        StartCore(TimeSpan.FromMinutes(Math.Clamp(S.FocusMinutes, 1, 240)), "Focus", "focus", FocusId);
    }

    public void StopFocus()
    {
        _timers.RemoveAll(x => x.FocusPhase != null);
        _hub.Remove(FocusId);
        _hub.Remove(FocusId + ":next");
        _focusRound = 0;
        FocusChanged?.Invoke();
    }

    void NextFocusPhase(string finished, bool skipped)
    {
        int rounds = Math.Max(1, S.FocusRounds);
        string next;
        TimeSpan length;
        if (finished == "focus")
        {
            bool longBreak = _focusRound % rounds == 0;
            next = longBreak ? "long" : "break";
            length = TimeSpan.FromMinutes(Math.Clamp(longBreak ? S.LongBreakMinutes : S.BreakMinutes, 1, 120));
        }
        else
        {
            _focusRound++;
            next = "focus";
            length = TimeSpan.FromMinutes(Math.Clamp(S.FocusMinutes, 1, 240));
        }

        if (!skipped) Ring(1);

        if (S.FocusAutoStartBreaks || skipped)
        {
            StartCore(length, null, next, FocusId);
            return;
        }

        // Wait for the user to start the next phase.
        var title = next == "focus" ? "Break's over" : "Nice work — take a break";
        var a = new Activity(FocusId + ":next", ActivityKind.Timer)
        {
            Title = title,
            Subtitle = next == "focus" ? $"Round {_focusRound} of {rounds} is next" : $"{Fmt.Human(length)} {(next == "long" ? "long " : "")}break",
            Source = "Focus",
            Glyph = next == "focus" ? "" : "",
            Accent = next == "focus" ? Purple : Teal,
            Priority = 94,
            IsLive = true,
            AppKey = "timer:focus",
        };
        a.Actions = new[]
        {
            new IslandAction(Glyphs.Play, next == "focus" ? "Start focus" : "Start break", () => { _hub.Remove(a.Id); StartCore(length, null, next, FocusId); }, primary: true),
            new IslandAction(Glyphs.Close, "End session", StopFocus),
        };
        _hub.Add(a);
        _hub.Peek(a);
        FocusChanged?.Invoke();
    }

    // ───────────────────────────── stopwatch ─────────────────────────────

    public void ToggleStopwatch()
    {
        if (_hub.Get(StopwatchId) == null)
        {
            _swBanked = TimeSpan.Zero;
            _laps.Clear();
            _swRunning = true;
            _swStartedAt = DateTime.UtcNow;
            var a = new Activity(StopwatchId, ActivityKind.Timer)
            {
                Title = "Stopwatch",
                Source = "Stopwatch",
                Glyph = "",
                Accent = Green,
                Priority = 69,
                AppKey = "timer:stopwatch",
            };
            _swShown = "";
            _swActionsFor = null;
            _hub.Add(a);
            RenderStopwatch();
            _hub.Peek(a);
            _tick.Start();
            return;
        }
        if (_swRunning) PauseStopwatch(); else ResumeStopwatch();
    }

    TimeSpan Elapsed => _swBanked + (_swRunning ? DateTime.UtcNow - _swStartedAt : TimeSpan.Zero);

    void PauseStopwatch()
    {
        _swBanked = Elapsed;
        _swRunning = false;
        RenderStopwatch();
    }

    void ResumeStopwatch()
    {
        _swStartedAt = DateTime.UtcNow;
        _swRunning = true;
        RenderStopwatch();
        _tick.Start();
    }

    void Lap()
    {
        _laps.Add(Elapsed);
        _swShown = "";
        RenderStopwatch();
    }

    void RenderStopwatch()
    {
        var a = _hub.Get(StopwatchId);
        if (a == null) { _swRunning = false; return; }
        var e = Elapsed;
        // tenths while under an hour
        var text = e.TotalHours >= 1 ? Fmt.Clock(e) : $"{(int)e.TotalMinutes}:{e.Seconds:00}.{e.Milliseconds / 100}";
        if (text != _swShown)
        {
            _swShown = text;
            a.Trailing = e.TotalHours >= 1 ? text : $"{(int)e.TotalMinutes}:{e.Seconds:00}";
            a.Badge = text;
            a.Subtitle = _laps.Count == 0
                ? (_swRunning ? "Running" : "Paused")
                : $"Lap {_laps.Count} · {Fmt.Clock(_laps[^1] - (_laps.Count > 1 ? _laps[^2] : TimeSpan.Zero))}";
        }

        if (_swActionsFor == _swRunning) return;
        _swActionsFor = _swRunning;
        a.Actions = _swRunning
            ? new[]
            {
                new IslandAction("", "Lap", Lap),
                new IslandAction(Glyphs.Pause, null, PauseStopwatch, primary: true),
            }
            : new[]
            {
                new IslandAction(Glyphs.Refresh, "Reset", () => { _hub.Remove(StopwatchId); _swRunning = false; }),
                new IslandAction(Glyphs.Play, null, ResumeStopwatch, primary: true),
                new IslandAction(Glyphs.Close, null, () => { _hub.Remove(StopwatchId); _swRunning = false; }),
            };
    }

    // ───────────────────────────── sound ─────────────────────────────

    void Ring(int? times = null)
    {
        if (string.Equals(S.TimerSound, "none", StringComparison.OrdinalIgnoreCase)) return;
        _soundsLeft = Math.Clamp(times ?? S.TimerSoundRepeats, 1, 10);
        _nextSound = DateTime.UtcNow;
        _tick.Start();
    }

    /// <summary>Plays the chosen alarm once (also used by the Test button in Settings).</summary>
    public void PlaySound(string? choice = null)
    {
        var sound = choice ?? S.TimerSound;
        try
        {
            var path = SoundPath(sound);
            if (path == null)
            {
                SystemSounds.Exclamation.Play();
                return;
            }
            if (_playerPath != path)
            {
                _player?.Dispose();
                _player = new SoundPlayer(path);
                _playerPath = path;
            }
            _player!.Play();
        }
        catch (Exception ex)
        {
            Log.Error("timer sound", ex);
            SystemSounds.Exclamation.Play();
        }
    }

    static string? SoundPath(string sound)
    {
        if (string.IsNullOrWhiteSpace(sound) || sound.Equals("system", StringComparison.OrdinalIgnoreCase)) return null;
        if (File.Exists(sound)) return sound;
        var win = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", sound + ".wav");
        return File.Exists(win) ? win : null;
    }

    /// <summary>Alarm-like sounds that ship with Windows.</summary>
    public static IEnumerable<string> WindowsSounds()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
            return Directory.EnumerateFiles(dir, "*.wav")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => n != null && (n.StartsWith("Alarm", StringComparison.OrdinalIgnoreCase)
                                          || n.StartsWith("Ring", StringComparison.OrdinalIgnoreCase)
                                          || n.Contains("Notify", StringComparison.OrdinalIgnoreCase)
                                          || n.Contains("chimes", StringComparison.OrdinalIgnoreCase)))
                .Select(n => n!)
                .OrderBy(n => n)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
