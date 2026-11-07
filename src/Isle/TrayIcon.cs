using System;
using Isle.Core;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Isle;

/// <summary>Notification-area icon: timers, hide/show, settings, updates, quit.</summary>
public sealed class TrayIcon : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    readonly Forms.ToolStripMenuItem _toggle;

    public TrayIcon(App app)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem($"Isle {AppInfo.VersionString}") { Enabled = false });
        menu.Items.Add(new Forms.ToolStripSeparator());

        var timers = new Forms.ToolStripMenuItem("New timer");
        foreach (var minutes in new[] { 1, 5, 10, 15, 25, 45, 60 })
        {
            var m = minutes;
            timers.DropDownItems.Add($"{m} min", null, (_, _) => app.Timers.Start(TimeSpan.FromMinutes(m)));
        }
        menu.Items.Add(timers);

        _toggle = new Forms.ToolStripMenuItem("Hide island", null, (_, _) => app.ToggleIslandHidden());
        menu.Items.Add(_toggle);
        menu.Items.Add("Settings…", null, (_, _) => app.ShowSettings());
        menu.Items.Add("Check for updates", null, (_, _) => _ = app.Updates.CheckAsync(manual: true));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit Isle", null, (_, _) => app.Quit());

        _icon = new Forms.NotifyIcon
        {
            Text = "Isle",
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) app.ShowSettings();
        };
    }

    public void SetHidden(bool hidden) => _toggle.Text = hidden ? "Show island" : "Hide island";

    static Drawing.Icon LoadIcon()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/isle.ico"));
            if (info != null)
            {
                using var stream = info.Stream;
                return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
            }
        }
        catch (Exception ex)
        {
            Log.Error("tray icon", ex);
        }
        return Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
