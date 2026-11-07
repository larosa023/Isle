using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Isle.Core;

namespace Isle.Services;

/// <summary>
/// Built-in updater. Reads the latest GitHub Release of larosa023/Isle, offers it on the island,
/// downloads the matching exe next to the running one, verifies its SHA-256 against the release's
/// SHA256SUMS.txt, swaps the files and relaunches. No installer, no admin rights.
/// </summary>
public sealed class UpdateService
{
    public const string Repo = "larosa023/Isle";
#if STANDALONE
    public const string AssetName = "Isle-standalone.exe";
#else
    public const string AssetName = "Isle.exe";
#endif
    const string Id = "isle:update";
    static readonly Color Blue = Color.FromRgb(0x0A, 0x84, 0xFF);

    sealed record Release(Version Version, string Tag, string AssetUrl, string? SumsUrl, string PageUrl, string Notes);

    readonly ActivityHub _hub;
    AppSettings _s;
    DispatcherTimer? _timer;
    bool _busy, _dismissedThisSession;

    public UpdateService(ActivityHub hub, AppSettings settings)
    {
        _hub = hub;
        _s = settings;
    }

    public void UpdateSettings(AppSettings s) => _s = s;

    public void Start()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(25) };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = TimeSpan.FromHours(6);
            await CheckAsync(manual: false);
        };
        _timer.Start();
    }

    public async Task CheckAsync(bool manual)
    {
        if (_busy || (!manual && !_s.AutoCheckUpdates)) return;
        _busy = true;
        try
        {
            var release = await FetchLatestAsync();
            if (release == null)
            {
                if (manual) Toast("Couldn't check for updates", "GitHub didn't answer. Try again in a bit.", Glyphs.Info);
                return;
            }
            if (release.Version <= AppInfo.Version)
            {
                if (manual) Toast("Isle is up to date", $"You're on {AppInfo.VersionString}.", Glyphs.Check);
                return;
            }
            if (!manual && (_dismissedThisSession || _s.SkippedVersion == release.Tag)) return;
            Offer(release);
        }
        catch (Exception ex)
        {
            Log.Error("update check", ex);
            if (manual) Toast("Couldn't check for updates", ex.Message, Glyphs.Info);
        }
        finally
        {
            _busy = false;
        }
    }

    static async Task<Release?> FetchLatestAsync()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var resp = await Http.Client.SendAsync(req, cts.Token);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        var raw = tag.TrimStart('v', 'V');
        int dash = raw.IndexOf('-');
        if (dash > 0) raw = raw[..dash];
        if (!Version.TryParse(raw, out var v)) return null;
        v = new Version(v.Major, v.Minor, Math.Max(0, v.Build));

        string? asset = null, sums = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString();
                var url = a.GetProperty("browser_download_url").GetString();
                if (string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase)) asset = url;
                if (string.Equals(name, "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) sums = url;
            }
        }
        if (asset == null) return null;

        var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : $"https://github.com/{Repo}/releases";
        var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        return new Release(v, tag, asset, sums, page, notes);
    }

    void Offer(Release r)
    {
        var a = _hub.GetOrAdd(Id, ActivityKind.Update);
        a.Title = $"Isle {r.Version.ToString(3)} is available";
        a.Subtitle = FirstLine(r.Notes) ?? $"You have {AppInfo.VersionString}.";
        a.Source = "Update";
        a.Glyph = Glyphs.Update;
        a.Accent = Blue;
        a.Progress = null;
        a.Priority = 40;
        a.ExpiresAt = null;
        a.Actions = new[]
        {
            new IslandAction(Glyphs.Download, "Update", () => _ = InstallAsync(r), primary: true),
            new IslandAction(Glyphs.Open, "What's new", () => Ui.OpenUrl(r.PageUrl)),
            new IslandAction(Glyphs.Close, "Later", () => { _dismissedThisSession = true; _hub.Remove(Id); }),
        };
        _hub.Reorder();
        _hub.Peek(a);
    }

    static string? FirstLine(string notes)
    {
        var line = notes.Split('\n')
            .Select(l => l.Trim().TrimStart('#', '-', '*', ' '))
            .FirstOrDefault(l => l.Length > 0 && !l.StartsWith("**Full Changelog", StringComparison.OrdinalIgnoreCase));
        return line == null ? null : line.Length > 90 ? line[..90] + "…" : line;
    }

    async Task InstallAsync(Release r)
    {
        if (_busy) return;
        _busy = true;
        var a = _hub.GetOrAdd(Id, ActivityKind.Update);
        a.Actions = Array.Empty<IslandAction>();
        a.Subtitle = "Downloading…";
        a.Progress = 0;

        var exe = Environment.ProcessPath;
        try
        {
            if (string.IsNullOrEmpty(exe)) throw new InvalidOperationException("Can't locate the running Isle.exe.");
            var staged = exe + ".new";

            using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10)))
            using (var resp = await Http.Client.GetAsync(r.AssetUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? 0;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var src = await resp.Content.ReadAsStreamAsync(cts.Token))
                await using (var dst = new FileStream(staged, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    var buf = new byte[1 << 16];
                    long done = 0;
                    var lastUi = DateTime.MinValue;
                    int n;
                    while ((n = await src.ReadAsync(buf, cts.Token)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n), cts.Token);
                        hash.AppendData(buf, 0, n);
                        done += n;
                        if (total > 0 && DateTime.UtcNow - lastUi > TimeSpan.FromMilliseconds(150))
                        {
                            lastUi = DateTime.UtcNow;
                            a.Progress = (double)done / total;
                            a.ProgressLeft = Fmt.Bytes(done);
                            a.ProgressRight = Fmt.Bytes(total);
                        }
                    }
                }

                var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (r.SumsUrl != null)
                {
                    var sums = await Http.GetStringAsync(r.SumsUrl, TimeSpan.FromSeconds(20), cts.Token);
                    var expected = sums.Split('\n')
                        .Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        .Where(p => p.Length >= 2 && p[^1].TrimStart('*').Equals(AssetName, StringComparison.OrdinalIgnoreCase))
                        .Select(p => p[0].ToLowerInvariant())
                        .FirstOrDefault();
                    if (expected == null || expected != actual)
                    {
                        File.Delete(staged);
                        throw new InvalidOperationException("The download didn't match its checksum, so it wasn't installed.");
                    }
                }
            }

            a.Progress = 1;
            a.Subtitle = "Restarting…";

            // Windows lets a running exe be renamed, so: current -> .old, new -> current, relaunch.
            var old = exe + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try
            {
                File.Move(staged, exe);
            }
            catch
            {
                File.Move(old, exe); // put things back
                throw;
            }

            Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = false });
            Application.Current.Shutdown();
        }
        catch (UnauthorizedAccessException)
        {
            Fail(a, r, "Isle can't write to its own folder. Move Isle.exe somewhere you own (like a folder in your user profile) and try again.");
        }
        catch (Exception ex)
        {
            Log.Error("update install", ex);
            Fail(a, r, ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    void Fail(Activity a, Release r, string message)
    {
        a.Progress = null;
        a.Title = "Update didn't finish";
        a.Subtitle = message;
        a.Actions = new[]
        {
            new IslandAction(Glyphs.Refresh, "Retry", () => _ = InstallAsync(r), primary: true),
            new IslandAction(Glyphs.Open, "Download page", () => Ui.OpenUrl(r.PageUrl)),
            new IslandAction(Glyphs.Close, null, () => _hub.Remove(Id)),
        };
        _hub.Peek(a);
    }

    void Toast(string title, string subtitle, string glyph)
    {
        var a = new Activity("isle:update-status", ActivityKind.Update)
        {
            Title = title,
            Subtitle = subtitle,
            Source = "Isle",
            Glyph = glyph,
            Accent = Blue,
            Priority = 45,
        };
        a.SetTtl(TimeSpan.FromSeconds(5));
        _hub.Add(a);
        _hub.Peek(a);
    }

    /// <summary>Removes the previous exe left behind by an update.</summary>
    public static void CleanupOldBinaries()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        _ = Task.Run(async () =>
        {
            foreach (var path in new[] { exe + ".old", exe + ".new" })
            {
                for (int attempt = 0; attempt < 10 && File.Exists(path); attempt++)
                {
                    try { File.Delete(path); }
                    catch { await Task.Delay(1000); } // the old process may still be exiting
                }
            }
        });
    }
}
