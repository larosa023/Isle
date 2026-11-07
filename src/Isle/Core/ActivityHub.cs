using System;
using System.Collections.Generic;
using System.Windows.Threading;

namespace Isle.Core;

/// <summary>
/// The set of live activities, ordered by priority. The island shows <see cref="Current"/>;
/// the user can scroll to pin a different one. UI thread only.
/// </summary>
public sealed class ActivityHub
{
    readonly List<Activity> _items = new();
    readonly DispatcherTimer _expiry;
    Activity? _pinned;

    public ActivityHub()
    {
        _expiry = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _expiry.Tick += (_, _) => Expire();
        _expiry.Start();
    }

    /// <summary>The set, ordering or current item changed.</summary>
    public event Action? Changed;

    /// <summary>A module wants the island to briefly expand to show this activity.</summary>
    public event Action<Activity>? PeekRequested;

    public IReadOnlyList<Activity> Items => _items;
    public Activity? Current { get; private set; }

    public Activity? Pinned
    {
        get => _pinned;
        set { _pinned = value; Recompute(); }
    }

    public int CurrentIndex => Current == null ? -1 : _items.IndexOf(Current);

    public Activity? Get(string id) => _items.Find(a => a.Id == id);

    public Activity GetOrAdd(string id, ActivityKind kind, Action<Activity>? init = null)
    {
        var a = Get(id);
        if (a != null) return a;
        a = new Activity(id, kind);
        init?.Invoke(a);
        _items.Add(a);
        Recompute();
        return a;
    }

    public void Add(Activity a)
    {
        RemoveCore(a.Id);
        _items.Add(a);
        Recompute();
    }

    public void Remove(string id)
    {
        if (RemoveCore(id)) Recompute();
    }

    public void RemoveWhere(Func<Activity, bool> predicate)
    {
        bool any = false;
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            if (!predicate(_items[i])) continue;
            if (ReferenceEquals(_items[i], _pinned)) _pinned = null;
            _items.RemoveAt(i);
            any = true;
        }
        if (any) Recompute();
    }

    /// <summary>Call after changing priorities.</summary>
    public void Reorder() => Recompute();

    public void Peek(Activity a) => PeekRequested?.Invoke(a);

    public void Cycle(int direction)
    {
        if (_items.Count < 2 || Current == null) return;
        int i = _items.IndexOf(Current);
        int n = _items.Count;
        _pinned = _items[((i + direction) % n + n) % n];
        Recompute();
    }

    bool RemoveCore(string id)
    {
        int i = _items.FindIndex(x => x.Id == id);
        if (i < 0) return false;
        if (ReferenceEquals(_items[i], _pinned)) _pinned = null;
        _items.RemoveAt(i);
        return true;
    }

    void Recompute()
    {
        _items.Sort((x, y) => y.Priority != x.Priority
            ? y.Priority.CompareTo(x.Priority)
            : y.CreatedAt.CompareTo(x.CreatedAt));

        Current = _pinned != null && _items.Contains(_pinned)
            ? _pinned
            : _items.Count > 0 ? _items[0] : null;

        Changed?.Invoke();
    }

    void Expire()
    {
        if (_items.Count == 0) return;
        var now = DateTime.UtcNow;
        RemoveWhere(a => a.ExpiresAt is DateTime t && t <= now);
    }
}
