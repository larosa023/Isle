using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;
using Isle.Core;

namespace Isle.Services;

/// <summary>
/// Watches the Downloads folder for browsers' in-progress files (.crdownload, .part, …) and shows
/// size, speed and a "done" card with Open / Show in folder. No browser extension needed.
/// </summary>
public sealed class DownloadService : IModule
{
    static readonly string[] PartialExtensions = { ".crdownload", ".part", ".partial", ".download", ".opdownload" };
    static readonly Color Blue = Color.FromRgb(0x0A, 0x84, 0xFF);
    static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58);

    sealed class Tracked
    {
        public string Path = "";
        public string ActivityId = "";
        public long LastSize;
        public DateTime LastSample;
        public DateTime LastGrowth;
        public double Speed;
        public string? AppKey;
    }

    readonly ActivityHub _hub;
    static AppSettings S => AppSettings.Current;
    readonly Dictionary<string, Tracked> _tracked = new(StringComparer.OrdinalIgnoreCase);
    readonly DispatcherTimer _poll;
    FileSystemWatcher? _watcher;
    int _counter;

    public DownloadService(ActivityHub hub, AppSettings settings)
    {
        _hub = hub;
                _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _poll.Tick += (_, _) => Poll();
    }

    public void Start()
    {
        var folder = string.IsNullOrWhiteSpace(S.DownloadsFolder) ? Native.DownloadsFolder() : S.DownloadsFolder;
        if (!Directory.Exists(folder))
        {
            Log.Info("downloads folder not found: " + folder);
            return;
        }

        _watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName,
            InternalBufferSize = 32 * 1024,
        };
        _watcher.Created += (_, e) => Ui.Post(() => OnCreated(e.FullPath));
        _watcher.Renamed += (_, e) => Ui.Post(() => OnRenamed(e.OldFullPath, e.FullPath));
        _watcher.Deleted += (_, e) => Ui.Post(() => OnDeleted(e.FullPath));
        _watcher.EnableRaisingEvents = true;
    }

    static bool IsPartial(string path) =>
        PartialExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    static (string key, string name) Browser(string partialPath)
    {
        var p = partialPath.ToLowerInvariant();
        if (p.EndsWith(".crdownload")) return ("download:chromium", "Chrome / Edge / Brave");
        if (p.EndsWith(".part")) return ("download:firefox", "Firefox");
        if (p.EndsWith(".opdownload")) return ("download:opera", "Opera");
        return ("download:other", "Other downloads");
    }

    static string DisplayName(string partialPath)
    {
        var name = Path.GetFileName(partialPath);
        var ext = PartialExtensions.FirstOrDefault(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        if (ext != null) name = name[..^ext.Length];
        // Chrome/Edge use "Unconfirmed 123456" until the real name is known.
        if (name.StartsWith("Unconfirmed ", StringComparison.OrdinalIgnoreCase)) return "Preparing download…";
        return name;
    }

    void OnCreated(string path)
    {
        if (IsPartial(path) && !_tracked.ContainsKey(path)) Track(path);
    }

    void OnRenamed(string oldPath, string newPath)
    {
        if (_tracked.TryGetValue(oldPath, out var t))
        {
            _tracked.Remove(oldPath);
            if (IsPartial(newPath))
            {
                t.Path = newPath; // Chrome: "Unconfirmed 1.crdownload" -> "file.zip.crdownload"
                _tracked[newPath] = t;
                Render(t);
            }
            else
            {
                Complete(t, newPath);
            }
        }
        else if (IsPartial(newPath))
        {
            Track(newPath);
        }
    }

    void OnDeleted(string path)
    {
        if (!_tracked.TryGetValue(path, out var t)) return;
        _tracked.Remove(path);
        _hub.Remove(t.ActivityId); // cancelled
    }

    void Track(string path)
    {
        var t = new Tracked
        {
            Path = path,
            ActivityId = "download:" + (++_counter),
            LastSample = DateTime.UtcNow,
            LastGrowth = DateTime.UtcNow,
        };
        _tracked[path] = t;

        var (appKey, appName) = Browser(path);
        AppRegistry.Seen(appKey, appName, "download");
        t.AppKey = appKey;
        var a = new Activity(t.ActivityId, ActivityKind.Download)
        {
            AppKey = appKey,
            Glyph = Glyphs.Download,
            Source = "Downloading",
            Priority = 65,
            Accent = Blue,
        };
        a.Actions = new[] { new IslandAction(Glyphs.Folder, "Show in folder", () => Ui.ShowInFolder(t.Path)) };
        _hub.Add(a);
        Render(t);
        _hub.Peek(a);
        _poll.Start();
    }

    void Poll()
    {
        if (_tracked.Count == 0)
        {
            _poll.Stop();
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var t in _tracked.Values.ToArray())
        {
            long size;
            try
            {
                var fi = new FileInfo(t.Path);
                if (!fi.Exists)
                {
                    // Missed the rename/delete event; drop it.
                    _tracked.Remove(t.Path);
                    _hub.Remove(t.ActivityId);
                    continue;
                }
                size = fi.Length;
            }
            catch
            {
                continue;
            }

            double seconds = Math.Max(0.25, (now - t.LastSample).TotalSeconds);
            double instant = Math.Max(0, size - t.LastSize) / seconds;
            t.Speed = t.Speed <= 0 ? instant : t.Speed * 0.6 + instant * 0.4; // smooth
            if (size > t.LastSize) t.LastGrowth = now;
            t.LastSize = size;
            t.LastSample = now;

            // A paused download sitting for ages shouldn't occupy the island.
            if (now - t.LastGrowth > TimeSpan.FromMinutes(20))
            {
                _tracked.Remove(t.Path);
                _hub.Remove(t.ActivityId);
                continue;
            }
            Render(t);
        }
    }

    void Render(Tracked t)
    {
        var a = _hub.Get(t.ActivityId);
        if (a == null) return;
        a.Title = DisplayName(t.Path);
        bool stalled = DateTime.UtcNow - t.LastGrowth > TimeSpan.FromSeconds(8);
        a.Subtitle = t.LastSize <= 0
            ? "Starting…"
            : stalled ? $"{Fmt.Bytes(t.LastSize)} · waiting" : $"{Fmt.Bytes(t.LastSize)} · {Fmt.Bytes(t.Speed)}/s";
        a.Trailing = t.Speed > 0 && !stalled ? $"{Fmt.Bytes(t.Speed)}/s" : null;
    }

    void Complete(Tracked t, string finalPath)
    {
        _hub.Remove(t.ActivityId);

        long size = 0;
        try { size = new FileInfo(finalPath).Length; } catch { }

        var done = new Activity(t.ActivityId + ":done", ActivityKind.Download)
        {
            Title = Path.GetFileName(finalPath),
            Subtitle = size > 0 ? $"Download complete · {Fmt.Bytes(size)}" : "Download complete",
            Source = "Downloads",
            Glyph = Glyphs.Check,
            Priority = 80,
            Accent = Green,
            Trailing = "Done",
            AppKey = t.AppKey,
            OnOpen = () => Ui.OpenUrl(finalPath),
        };
        done.Actions = new[]
        {
            new IslandAction(Glyphs.Open, "Open", () => { Ui.OpenUrl(finalPath); _hub.Remove(done.Id); }, primary: true),
            new IslandAction(Glyphs.Folder, "Show in folder", () => { Ui.ShowInFolder(finalPath); _hub.Remove(done.Id); }),
        };
        done.SetTtl(TimeSpan.FromSeconds(12));
        _hub.Add(done);
        _hub.Peek(done);
    }

    public void Dispose()
    {
        _poll.Stop();
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
        _hub.RemoveWhere(a => a.Kind == ActivityKind.Download);
    }
}
