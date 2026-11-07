using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Isle.Core;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Isle.Services;

/// <summary>
/// Mirrors Windows toast notifications into the island (experimental). Windows only grants this
/// to apps it trusts with notification access; when access is refused the module logs it and
/// stays quiet — apps can still post notifications through the local API.
/// </summary>
public sealed class NotificationService : IModule
{
    readonly ActivityHub _hub;
    readonly AppSettings _s;
    readonly HashSet<uint> _seen = new();
    readonly DispatcherTimer _poll;
    UserNotificationListener? _listener;
    bool _primed, _busy, _disposed;

    public NotificationService(ActivityHub hub, AppSettings settings)
    {
        _hub = hub;
        _s = settings;
        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _poll.Tick += async (_, _) => await PollAsync();
    }

    public async void Start()
    {
        try
        {
            _listener = UserNotificationListener.Current;
            var status = await _listener.RequestAccessAsync();
            if (status != UserNotificationListenerAccessStatus.Allowed)
            {
                Log.Info($"notification access: {status}");
                return;
            }
            if (_disposed) return;
            await PollAsync();
            _poll.Start();
        }
        catch (Exception ex)
        {
            Log.Info("notification mirroring unavailable: " + ex.Message);
        }
    }

    async Task PollAsync()
    {
        if (_listener == null || _busy || _disposed) return;
        _busy = true;
        try
        {
            var list = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            foreach (var n in list)
            {
                if (!_seen.Add(n.Id) || !_primed) continue; // first pass only records what's already there
                Show(n);
            }
            _primed = true;
            if (_seen.Count > 2000) _seen.IntersectWith(list.Select(n => n.Id));
        }
        catch (Exception ex)
        {
            Log.Error("notifications poll", ex);
            _poll.Stop();
        }
        finally
        {
            _busy = false;
        }
    }

    void Show(UserNotification n)
    {
        string app = "Notification";
        try { app = n.AppInfo.DisplayInfo.DisplayName; } catch { }
        if (app.Contains("Isle", StringComparison.OrdinalIgnoreCase)) return;
        if (_s.MutedApps.Any(m => string.Equals(m, app, StringComparison.OrdinalIgnoreCase))) return;

        var texts = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric)?.GetTextElements();
        var title = texts?.FirstOrDefault()?.Text ?? app;
        var body = texts == null ? null : string.Join(" ", texts.Skip(1).Select(t => t.Text));

        var a = new Activity("notification:" + n.Id, ActivityKind.Notification)
        {
            Source = app,
            Title = title,
            Subtitle = string.IsNullOrWhiteSpace(body) ? null : body,
            Glyph = Glyphs.Bell,
            Priority = 90,
        };
        a.SetTtl(TimeSpan.FromSeconds(Math.Max(4, _s.PeekSeconds + 1.5)));
        _hub.Add(a);
        _hub.Peek(a);
    }

    public void Dispose()
    {
        _disposed = true;
        _poll.Stop();
        _hub.RemoveWhere(a => a.Kind == ActivityKind.Notification && a.Id.StartsWith("notification:", StringComparison.Ordinal));
    }
}
