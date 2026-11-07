using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Isle.Core;
using Isle.Services;

namespace Isle;

public partial class SettingsWindow : Window
{
    readonly AppSettings _s;

    public SettingsWindow(AppSettings current)
    {
        InitializeComponent();
        ThemeMode = ThemeMode.System; // Fluent styling, follows the Windows light/dark setting
        _s = current.Clone();
        Load();
    }

    void Load()
    {
        VersionText.Text = $"Isle {AppInfo.VersionString}";

        ThemeBox.SelectedItem = ThemeBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, _s.Theme, StringComparison.OrdinalIgnoreCase))
            ?? ThemeBox.Items[0];
        AccentBox.Text = _s.Accent;
        ScaleSlider.Value = _s.Scale;
        OffsetSlider.Value = _s.TopOffset;
        HideIdleBox.IsChecked = _s.HideWhenIdle;
        HideFullscreenBox.IsChecked = _s.HideInFullscreen;
        VisualizerBox.IsChecked = _s.AnimatedVisualizer;

        HoverBox.IsChecked = _s.HoverToExpand;
        PeekBox.IsChecked = _s.PeekOnNewActivity;
        PeekSlider.Value = _s.PeekSeconds;
        StartupBox.IsChecked = _s.StartWithWindows;

        MediaBox.IsChecked = _s.MediaEnabled;
        MediaPeekBox.IsChecked = _s.MediaPeekOnTrackChange;
        MediaPausedBox.Text = _s.MediaHidePausedAfterMinutes.ToString();
        DownloadsBox.IsChecked = _s.DownloadsEnabled;
        DownloadsFolderBox.Text = _s.DownloadsFolder;
        CallsBox.IsChecked = _s.CallsEnabled;
        AnyMicBox.IsChecked = _s.ShowAnyMicUse;
        NotificationsBox.IsChecked = _s.NotificationsEnabled;
        MutedBox.Text = string.Join(", ", _s.MutedApps);

        SportsBox.IsChecked = _s.SportsEnabled;
        LeaguesBox.Text = string.Join(", ", _s.SportsLeagues);
        TeamsBox.Text = string.Join(", ", _s.FavoriteTeams);
        SportsUrlBox.Text = _s.SportsOpenUrl;
        AllLiveBox.IsChecked = _s.SportsShowAllLive;
        ScorePeekBox.IsChecked = _s.SportsPeekOnScore;

        ApiBox.IsChecked = _s.ApiEnabled;
        PortBox.Text = _s.ApiPort.ToString();
        OriginsBox.Text = string.Join(", ", _s.AllowedOrigins);
        SampleBox.Text =
            $"curl -X POST http://127.0.0.1:{_s.ApiPort}/v1/activities -H \"Content-Type: application/json\" " +
            "-d \"{\\\"app\\\":\\\"Savor\\\",\\\"id\\\":\\\"order\\\",\\\"title\\\":\\\"Pasta is ready\\\",\\\"glyph\\\":\\\"check\\\",\\\"ttl\\\":30}\"";

        UpdatesBox.IsChecked = _s.AutoCheckUpdates;
    }

    AppSettings Collect()
    {
        var s = _s.Clone();
        s.Theme = (ThemeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "midnight";
        s.Accent = string.IsNullOrWhiteSpace(AccentBox.Text) ? "auto" : AccentBox.Text.Trim();
        s.Scale = Math.Round(ScaleSlider.Value, 2);
        s.TopOffset = OffsetSlider.Value;
        s.HideWhenIdle = HideIdleBox.IsChecked == true;
        s.HideInFullscreen = HideFullscreenBox.IsChecked == true;
        s.AnimatedVisualizer = VisualizerBox.IsChecked == true;

        s.HoverToExpand = HoverBox.IsChecked == true;
        s.PeekOnNewActivity = PeekBox.IsChecked == true;
        s.PeekSeconds = PeekSlider.Value;
        s.StartWithWindows = StartupBox.IsChecked == true;

        s.MediaEnabled = MediaBox.IsChecked == true;
        s.MediaPeekOnTrackChange = MediaPeekBox.IsChecked == true;
        s.MediaHidePausedAfterMinutes = int.TryParse(MediaPausedBox.Text, out var mins) ? Math.Max(0, mins) : _s.MediaHidePausedAfterMinutes;
        s.DownloadsEnabled = DownloadsBox.IsChecked == true;
        s.DownloadsFolder = DownloadsFolderBox.Text.Trim();
        s.CallsEnabled = CallsBox.IsChecked == true;
        s.ShowAnyMicUse = AnyMicBox.IsChecked == true;
        s.NotificationsEnabled = NotificationsBox.IsChecked == true;
        s.MutedApps = SplitList(MutedBox.Text);

        s.SportsEnabled = SportsBox.IsChecked == true;
        s.SportsLeagues = SplitList(LeaguesBox.Text).Select(x => x.ToLowerInvariant()).ToList();
        s.FavoriteTeams = SplitList(TeamsBox.Text);
        s.SportsOpenUrl = SportsUrlBox.Text.Trim();
        s.SportsShowAllLive = AllLiveBox.IsChecked == true;
        s.SportsPeekOnScore = ScorePeekBox.IsChecked == true;

        s.ApiEnabled = ApiBox.IsChecked == true;
        s.ApiPort = int.TryParse(PortBox.Text, out var port) && port is > 1024 and < 65536 ? port : _s.ApiPort;
        s.AllowedOrigins = SplitList(OriginsBox.Text).Select(o => o.TrimEnd('/')).ToList();

        s.AutoCheckUpdates = UpdatesBox.IsChecked == true;
        return s;
    }

    static List<string> SplitList(string text) =>
        text.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    void Save_Click(object sender, RoutedEventArgs e)
    {
        App.Instance.ApplySettings(Collect());
        Close();
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    void ScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ScaleText != null) ScaleText.Text = $"{e.NewValue:0.00}×";
    }

    void OffsetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OffsetText != null) OffsetText.Text = $"{e.NewValue:0} px";
    }

    void PeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PeekText != null) PeekText.Text = $"{e.NewValue:0.0} s";
    }

    void CheckUpdates_Click(object sender, RoutedEventArgs e) => _ = App.Instance.Updates.CheckAsync(manual: true);

    void Releases_Click(object sender, RoutedEventArgs e) => Ui.OpenUrl($"https://github.com/{UpdateService.Repo}/releases");

    void OpenFolder_Click(object sender, RoutedEventArgs e) => Ui.OpenUrl(Paths.DataDir);

    void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(Paths.LogFile)) Ui.OpenUrl(Paths.LogFile);
        else Ui.OpenUrl(Paths.DataDir);
    }
}
