using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;

namespace Isle.Core;

/// <summary>
/// All live activities. <see cref="Items"/> is what the island may show: filtered by the user's
/// per-type and per-app rules and ordered by priority. Modules keep updating activities that are
/// filtered out, so turning an app back on brings its card back instantly. UI thread only.
/// </summary>
public sealed class ActivityHub
{
    readonly List<Activity> _all = new();
    List<Activity> _visible = new();
    readonly DispatcherTimer _expiry;
    Activity? _pinned;

    public ActivityHub()
    {
        _expiry = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _expiry.Tick += (_, _) => Expire();
        _expiry.Start();
    }

    /// <summary>Whether an activity may be shown.</summary>
    public Func<Activity, bool> Filter { get; set; } = _ => true;

    /// <summary>Effective priority used for ordering.</summary>
    public Func<Activity, int> RankOf { get; set; } = a => a.Priority;

    /// <summary>Whether an activity may pop the island open.</summary>
    public Func<Activity, bool> PeekAllowed { get; set; } = _ => true;

    public event Action? Changed;
    public event Action<Activity>? PeekRequested;

    /// <summary>What the island can show, best first.</summary>
    public IReadOnlyList<Activity> Items => _visible;

    /// <summary>Everything, including filtered-out activities.</summary>
    public IReadOnlyList<Activity> All => _all;

    public Activity? Current { get; private set; }

    public Activity? Pinned
    {
        get => _pinned;
        set { _pinned = value; Recompute(); }
    }

    public int CurrentIndex => Current == null ? -1 : _visible.IndexOf(Current);

    public Activity? Get(string id) => _all.Find(a => a.Id == id);

    public Activity GetOrAdd(string id, ActivityKind kind, Action<Activity>? init = null)
    {
        var a = Get(id);
        if (a != null) return a;
        a = new Activity(id, kind);
        init?.Invoke(a);
        _all.Add(a);
        Recompute();
        return a;
    }

    public void Add(Activity a)
    {
        RemoveCore(a.Id);
        _all.Add(a);
        Recompute();
    }

    public void Remove(string id)
    {
        if (RemoveCore(id)) Recompute();
    }

    public void RemoveWhere(Func<Activity, bool> predicate)
    {
        bool any = false;
        for (int i = _all.Count - 1; i >= 0; i--)
        {
            if (!predicate(_all[i])) continue;
            if (ReferenceEquals(_all[i], _pinned)) _pinned = null;
            _all.RemoveAt(i);
            any = true;
        }
        if (any) Recompute();
    }

    /// <summary>Re-apply filters and ordering (after priorities or rules change).</summary>
    public void Reorder() => Recompute();

    public void Peek(Activity a)
    {
        if (_visible.Contains(a) && PeekAllowed(a)) PeekRequested?.Invoke(a);
    }

    /// <summary>Peek regardless of the user's peek rules (previews from Settings).</summary>
    public void ForcePeek(Activity a)
    {
        if (_visible.Contains(a)) PeekRequested?.Invoke(a);
    }

    public void Cycle(int direction)
    {
        if (_visible.Count < 2 || Current == null) return;
        int i = _visible.IndexOf(Current);
        int n = _visible.Count;
        _pinned = _visible[((i + direction) % n + n) % n];
        Recompute();
    }

    bool RemoveCore(string id)
    {
        int i = _all.FindIndex(x => x.Id == id);
        if (i < 0) return false;
        if (ReferenceEquals(_all[i], _pinned)) _pinned = null;
        _all.RemoveAt(i);
        return true;
    }

    void Recompute()
    {
        _visible = _all
            .Where(a => { try { return Filter(a); } catch { return true; } })
            .OrderByDescending(RankOf)
            .ThenByDescending(a => a.CreatedAt)
            .ToList();

        if (_pinned != null && !_visible.Contains(_pinned)) _pinned = null;
        Current = _pinned ?? (_visible.Count > 0 ? _visible[0] : null);
        Changed?.Invoke();
    }

    void Expire()
    {
        if (_all.Count == 0) return;
        var now = DateTime.UtcNow;
        RemoveWhere(a => a.ExpiresAt is DateTime t && t <= now);
    }
}
