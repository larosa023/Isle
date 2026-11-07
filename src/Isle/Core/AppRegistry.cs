using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Threading;

namespace Isle.Core;

/// <summary>A per-app rule for something Isle has detected (a media player, a call app, an API client…).</summary>
public sealed class AppRule
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool Peek { get; set; } = true;
    public int Boost { get; set; }
    public DateTime LastSeen { get; set; }
}

/// <summary>
/// Every app Isle has seen, with the user's show / peek / priority choice for each. Stored in
/// %AppData%\Isle\apps.json. Keys look like "media:spotify", "mic:discord", "api:savor". UI thread only.
/// </summary>
public static class AppRegistry
{
    public static readonly IReadOnlyDictionary<string, string> Categories = new Dictionary<string, string>
    {
        ["media"] = "Now playing",
        ["mic"] = "Calls & microphone",
        ["notify"] = "Notifications",
        ["api"] = "Connected apps",
        ["download"] = "Downloads",
    };

    static readonly string File = Path.Combine(Paths.DataDir, "apps.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    static Dictionary<string, AppRule> _rules = Load();
    static DispatcherTimer? _saveTimer;

    /// <summary>The user changed a rule (show/peek/priority) or forgot an app.</summary>
    public static event Action? Changed;

    /// <summary>A new app showed up.</summary>
    public static event Action? Detected;

    public static IEnumerable<AppRule> All => _rules.Values.OrderBy(r => r.Category).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase);

    public static AppRule? Get(string? key) => key != null && _rules.TryGetValue(key, out var r) ? r : null;
    public static bool IsEnabled(string? key) => Get(key)?.Enabled ?? true;
    public static bool CanPeek(string? key) => Get(key)?.Peek ?? true;
    public static int Boost(string? key) => Get(key)?.Boost ?? 0;

    /// <summary>Record that an app is present. Returns its rule.</summary>
    public static AppRule Seen(string key, string name, string category)
    {
        var now = DateTime.Now;
        if (_rules.TryGetValue(key, out var r))
        {
            bool save = now - r.LastSeen > TimeSpan.FromMinutes(10) || (r.Name != name && name.Length > 0);
            if (name.Length > 0) r.Name = name;
            r.LastSeen = now;
            if (save) ScheduleSave();
            return r;
        }
        r = new AppRule { Key = key, Name = name.Length > 0 ? name : key, Category = category, LastSeen = now };
        _rules[key] = r;
        ScheduleSave();
        Detected?.Invoke();
        return r;
    }

    public static void Update(AppRule rule)
    {
        _rules[rule.Key] = rule;
        ScheduleSave();
        Changed?.Invoke();
    }

    public static void Forget(string key)
    {
        if (_rules.Remove(key))
        {
            ScheduleSave();
            Changed?.Invoke();
        }
    }

    public static void ResetAll()
    {
        foreach (var r in _rules.Values) { r.Enabled = true; r.Peek = true; r.Boost = 0; }
        ScheduleSave();
        Changed?.Invoke();
    }

    static Dictionary<string, AppRule> Load()
    {
        try
        {
            if (System.IO.File.Exists(File))
            {
                var list = JsonSerializer.Deserialize<List<AppRule>>(System.IO.File.ReadAllText(File), Json);
                if (list != null) return list.Where(r => r.Key.Length > 0).GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.First());
            }
        }
        catch (Exception ex)
        {
            Log.Error("apps load", ex);
        }
        return new Dictionary<string, AppRule>();
    }

    static void ScheduleSave()
    {
        _saveTimer ??= CreateTimer();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    static DispatcherTimer CreateTimer()
    {
        var t = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (_, _) => { t.Stop(); SaveNow(); };
        return t;
    }

    public static void SaveNow()
    {
        try
        {
            var tmp = File + ".tmp";
            System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(_rules.Values.ToList(), Json));
            System.IO.File.Move(tmp, File, true);
        }
        catch (Exception ex)
        {
            Log.Error("apps save", ex);
        }
    }
}
