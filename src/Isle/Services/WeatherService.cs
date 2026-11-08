using System;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Isle.Core;

namespace Isle.Services;

/// <summary>
/// Current weather for the open idle island, from Open-Meteo (free, no account, no key).
/// Refreshes every 30 minutes, only once a location is set in Settings → Island.
/// </summary>
public sealed class WeatherService : IModule
{
    public sealed record Now(double Temp, double High, double Low, string Text, string Glyph, string Unit, string Place, DateTime At);

    public static Now? Current { get; private set; }
    public static event Action? Updated;

    static AppSettings S => AppSettings.Current;
    readonly CancellationTokenSource _cts = new();

    public void Start() => _ = LoopAsync(_cts.Token);

    async Task LoopAsync(CancellationToken ct)
    {
        if (S.WeatherLat == 0 && S.WeatherLon == 0)
        {
            if (Current != null) { Current = null; Ui.Post(() => Updated?.Invoke()); }
            return;
        }
        while (!ct.IsCancellationRequested)
        {
            var next = TimeSpan.FromMinutes(30);
            try
            {
                var now = await FetchAsync(S.WeatherLat, S.WeatherLon, S.WeatherPlace, ct).ConfigureAwait(false);
                if (now != null)
                {
                    Current = now;
                    Ui.Post(() => Updated?.Invoke());
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log.Info("weather: " + ex.Message);
                next = TimeSpan.FromMinutes(5);
            }
            try { await Task.Delay(next, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    static bool Fahrenheit => S.WeatherUnits switch
    {
        "f" => true,
        "c" => false,
        _ => !RegionInfo.CurrentRegion.IsMetric,
    };

    static async Task<Now?> FetchAsync(double lat, double lon, string place, CancellationToken ct)
    {
        bool f = Fahrenheit;
        var url = FormattableString.Invariant(
            $"https://api.open-meteo.com/v1/forecast?latitude={lat:0.####}&longitude={lon:0.####}&current=temperature_2m,weather_code,is_day&daily=temperature_2m_max,temperature_2m_min&forecast_days=1&timezone=auto{(f ? "&temperature_unit=fahrenheit" : "")}");
        var json = await Http.GetStringAsync(url, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var cur = r.GetProperty("current");
        var daily = r.GetProperty("daily");
        int code = cur.GetProperty("weather_code").GetInt32();
        bool day = cur.TryGetProperty("is_day", out var d) && d.GetInt32() == 1;
        var (text, glyph) = Describe(code, day);
        return new Now(
            cur.GetProperty("temperature_2m").GetDouble(),
            daily.GetProperty("temperature_2m_max")[0].GetDouble(),
            daily.GetProperty("temperature_2m_min")[0].GetDouble(),
            text, glyph, f ? "°F" : "°C", place, DateTime.Now);
    }

    /// <summary>Looks up a place name ("Holmdel, NJ", "10001", "London").</summary>
    public static async Task<(string name, double lat, double lon)?> FindAsync(string query)
    {
        query = query.Trim();
        if (query.Length == 0) return null;
        var name = query.Split(',')[0].Trim();
        var url = "https://geocoding-api.open-meteo.com/v1/search?count=8&language=en&format=json&name=" + Uri.EscapeDataString(name);
        var json = await Http.GetStringAsync(url, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;

        // "Holmdel, NJ" → prefer the result whose state/country matches what came after the comma.
        var hint = query.Contains(',') ? query[(query.IndexOf(',') + 1)..].Trim().ToLowerInvariant() : "";
        JsonElement best = results[0];
        if (hint.Length > 0)
        {
            foreach (var res in results.EnumerateArray())
            {
                var admin = Str(res, "admin1").ToLowerInvariant();
                var country = Str(res, "country").ToLowerInvariant();
                var code = Str(res, "country_code").ToLowerInvariant();
                if (admin.StartsWith(hint) || country.StartsWith(hint) || code == hint || StateAbbr(admin) == hint)
                {
                    best = res;
                    break;
                }
            }
        }
        var label = Str(best, "name");
        var region = Str(best, "admin1");
        if (region.Length > 0) label += ", " + region;
        return (label, best.GetProperty("latitude").GetDouble(), best.GetProperty("longitude").GetDouble());
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static string StateAbbr(string state) => state switch
    {
        "new jersey" => "nj", "new york" => "ny", "california" => "ca", "texas" => "tx", "florida" => "fl",
        "pennsylvania" => "pa", "massachusetts" => "ma", "illinois" => "il", "connecticut" => "ct", "washington" => "wa",
        "georgia" => "ga", "north carolina" => "nc", "ohio" => "oh", "michigan" => "mi", "virginia" => "va",
        "maryland" => "md", "arizona" => "az", "colorado" => "co", "oregon" => "or", "nevada" => "nv",
        _ => "",
    };

    /// <summary>WMO weather codes → words and a symbol (drawn in Segoe UI Symbol/Emoji, so always available).</summary>
    static (string, string) Describe(int code, bool day) => code switch
    {
        0 => ("Clear", day ? "\u2600" : "\u263E"),
        1 => ("Mostly clear", day ? "\u2600" : "\u263E"),
        2 => ("Partly cloudy", day ? "\u26C5" : "\u2601"),
        3 => ("Cloudy", "\u2601"),
        45 or 48 => ("Fog", "\u2601"),
        51 or 53 or 55 or 56 or 57 => ("Drizzle", "\u2614"),
        61 or 63 or 80 or 81 => ("Rain", "\u2614"),
        65 or 82 => ("Heavy rain", "\u2614"),
        66 or 67 => ("Freezing rain", "\u2614"),
        71 or 73 or 75 or 77 or 85 or 86 => ("Snow", "\u2744"),
        95 or 96 or 99 => ("Thunderstorms", "\u26A1"),
        _ => ("\u2014", "\u2601"),
    };

    public void Dispose() => _cts.Cancel();
}
