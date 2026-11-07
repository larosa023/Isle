using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Isle.Core;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Isle;

/// <summary>
/// Notification-area icon. Left-click opens Settings; right-click shows the same Windows 11-style
/// menu as right-clicking the island.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    readonly App _app;

    public TrayIcon(App app)
    {
        _app = app;
        _icon = new Forms.NotifyIcon
        {
            Text = "Isle",
            Icon = LoadIcon(),
            Visible = true,
        };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Right) ShowMenu();
            else if (e.Button == Forms.MouseButtons.Left) _app.ShowSettings();
        };
    }

    public void SetHidden(bool hidden) => _icon.Text = hidden ? "Isle (hidden)" : "Isle";

    void ShowMenu()
    {
        // A WPF menu needs an active window to close properly when you click elsewhere;
        // a tiny invisible one does the job.
        var host = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = true,
            Topmost = true,
            Width = 1,
            Height = 1,
            Left = -32000,
            Top = -32000,
        };
        host.Show();
        host.Activate();

        var menu = _app.BuildMenu(null, includeHeader: true);
        menu.Placement = PlacementMode.MousePoint;
        menu.Closed += (_, _) => host.Close();
        menu.IsOpen = true;
    }

    static Drawing.Icon LoadIcon()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/isle.ico"));
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
