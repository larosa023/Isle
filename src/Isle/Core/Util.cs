using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Isle.Core;

public interface IModule : IDisposable
{
    void Start();
}

public static class AppInfo
{
    public static Version Version { get; } = Normalize(Assembly.GetEntryAssembly()?.GetName().Version);
    public static string VersionString => $"{Version.Major}.{Version.Minor}.{Version.Build}";

    static Version Normalize(Version? v) =>
        v == null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(0, v.Build));
}

public static class Paths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Isle");

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string LogFile => Path.Combine(DataDir, "isle.log");

    static Paths() => Directory.CreateDirectory(DataDir);
}

public static class Log
{
    static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string context, Exception? ex) => Write("ERROR", $"{context}: {ex}");

    static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var file = Paths.LogFile;
                var fi = new FileInfo(file);
                if (fi.Exists && fi.Length > 1_000_000) File.Move(file, file + ".1", true);
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never take the app down
        }
    }
}

public static class Ui
{
    public static void Post(Action action) => Application.Current?.Dispatcher.InvokeAsync(action);

    public static Task InvokeAsync(Action action) =>
        Application.Current?.Dispatcher.InvokeAsync(action).Task ?? Task.CompletedTask;

    public static Task<T> InvokeAsync<T>(Func<T> func) =>
        Application.Current.Dispatcher.InvokeAsync(func).Task;

    public static void OpenUrl(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("open " + target, ex); }
    }

    public static void ShowInFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("reveal " + path, ex); }
    }
}

public static class Http
{
    public static readonly HttpClient Client = Create();

    /// <summary>For apps on this PC (Amplite, API callbacks): never goes through a proxy.</summary>
    public static readonly HttpClient Local = new(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(2) })
    {
        Timeout = TimeSpan.FromSeconds(4),
    };

    static HttpClient Create()
    {
        var c = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan, // callers pass their own deadline
        };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"Isle/{AppInfo.VersionString}");
        return c;
    }

    public static async Task<string> GetStringAsync(string url, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return await Client.GetStringAsync(url, cts.Token).ConfigureAwait(false);
    }
}

public static class Fmt
{
    public static string Clock(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>Countdowns round up, so a timer never reads 0:00 while still running.</summary>
    public static string Countdown(TimeSpan t) =>
        Clock(TimeSpan.FromSeconds(Math.Ceiling(Math.Max(0, t.TotalSeconds))));

    public static string Human(TimeSpan t)
    {
        if (t.TotalHours >= 1)
            return t.Minutes == 0 ? $"{(int)t.TotalHours} hr" : $"{(int)t.TotalHours} hr {t.Minutes} min";
        if (t.TotalMinutes >= 1)
            return t.Seconds == 0 ? $"{(int)t.TotalMinutes} min" : $"{(int)t.TotalMinutes} min {t.Seconds} sec";
        return $"{Math.Max(1, (int)t.TotalSeconds)} sec";
    }

    public static string Bytes(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (bytes >= 1024 && i < units.Length - 1) { bytes /= 1024; i++; }
        return i == 0 ? $"{bytes:0} {units[i]}" : $"{bytes:0.0} {units[i]}";
    }
}

public static class Glyphs
{
    // Segoe Fluent Icons / Segoe MDL2 Assets code points
    public const string Play = "", Pause = "", Previous = "", Next = "";
    public const string Music = "", Timer = "", Download = "", Check = "";
    public const string Phone = "", Mic = "", Bell = "", Sports = "";
    public const string Update = "", Open = "", Folder = "", Close = "";
    public const string Add = "", Info = "", Refresh = "", Star = "";

    static readonly Dictionary<string, string> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["play"] = Play, ["pause"] = Pause, ["previous"] = Previous, ["next"] = Next,
        ["music"] = Music, ["timer"] = Timer, ["download"] = Download, ["check"] = Check,
        ["phone"] = Phone, ["mic"] = Mic, ["bell"] = Bell, ["sports"] = Sports, ["game"] = Sports,
        ["update"] = Update, ["open"] = Open, ["folder"] = Folder, ["close"] = Close,
        ["add"] = Add, ["info"] = Info, ["refresh"] = Refresh, ["star"] = Star,
        ["heart"] = "", ["mail"] = "", ["calendar"] = "", ["chat"] = "",
        ["people"] = "", ["link"] = "", ["home"] = "", ["warning"] = "",
        ["cart"] = "", ["location"] = "", ["sync"] = "", ["volume"] = "",
        ["equalizer"] = "", ["headphones"] = "",
    };

    /// <summary>Accepts a name ("music"), a code point ("E8D6" / "U+E8D6") or a literal character.</summary>
    public static string? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (Named.TryGetValue(s, out var g)) return g;
        var hex = s.StartsWith("U+", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
        if (hex.Length is 4 or 5 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp)
            && cp is >= 0x20 and <= 0x10FFFF)
            return char.ConvertFromUtf32(cp);
        return s.Length <= 2 ? s : null;
    }
}

public static class Images
{
    /// <summary>Decodes small, frozen bitmaps safe to hand across threads.</summary>
    public static BitmapImage? Decode(byte[] bytes, int width = 128)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = width;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Error("image decode", ex);
            return null;
        }
    }

    /// <summary>A vivid, readable accent sampled from artwork.</summary>
    public static Color? Dominant(BitmapSource src)
    {
        try
        {
            var scaled = new TransformedBitmap(src, new ScaleTransform(24.0 / src.PixelWidth, 24.0 / src.PixelHeight));
            var conv = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);

            double r = 0, g = 0, b = 0, sum = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                if (px[i + 3] < 128) continue;
                double B = px[i], G = px[i + 1], R = px[i + 2];
                double max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B));
                double sat = max == 0 ? 0 : (max - min) / max;
                double val = max / 255.0;
                double weight = sat * sat * val + 0.02; // favor saturated, bright pixels
                r += R * weight; g += G * weight; b += B * weight; sum += weight;
            }
            if (sum <= 0) return null;

            ToHsv(r / sum, g / sum, b / sum, out var hh, out var ss, out var vv);
            vv = Math.Max(vv, 0.78);          // readable on a black pill
            ss = Math.Min(ss * 1.15, 0.85);
            return FromHsv(hh, ss, vv);
        }
        catch
        {
            return null;
        }
    }

    static void ToHsv(double r, double g, double b, out double h, out double s, out double v)
    {
        r /= 255; g /= 255; b /= 255;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        h = 0;
        if (d > 0)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * (((b - r) / d) + 2);
            else h = 60 * (((r - g) / d) + 4);
        }
        if (h < 0) h += 360;
        s = max == 0 ? 0 : d / max;
        v = max;
    }

    static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c;
        double r, g, b;
        if (h < 60) (r, g, b) = (c, x, 0);
        else if (h < 120) (r, g, b) = (x, c, 0);
        else if (h < 180) (r, g, b) = (0, c, x);
        else if (h < 240) (r, g, b) = (0, x, c);
        else if (h < 300) (r, g, b) = (x, 0, c);
        else (r, g, b) = (c, 0, x);
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}

public static class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (enabled && Environment.ProcessPath is string exe)
                key.SetValue("Isle", $"\"{exe}\" --autostart");
            else
                key.DeleteValue("Isle", false);
        }
        catch (Exception ex)
        {
            Log.Error("startup registration", ex);
        }
    }
}
