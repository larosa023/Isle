# Isle

**A Dynamic Island for Windows.** A small pill at the top of your screen that stays out of the way, then springs open to show what's happening right now — music, timers, downloads, calls, notifications and live scores.

- **Now playing** — album art, live progress and prev / play / next for anything that uses Windows media controls (Spotify, Apple Music, YouTube in any browser, Media Player…). The accent color is sampled from the artwork.
- **Timers** — start from the island (hover it when idle), right-click, the tray, or the API. Pause, +1 min, and a chime when done.
- **Downloads** — live size and speed for Chrome, Edge, Firefox, Brave and Opera downloads, then *Open* / *Show in folder*. No extension needed.
- **Calls** — Teams, Discord, Zoom, Slack, Phone Link, WhatsApp, Meet in a browser… detected from microphone use, with a running call timer. Optional mic-in-use indicator for every other app.
- **Live sports** — your teams' scores from ESPN (NBA, WNBA, NFL, NCAA, MLB, NHL, MLS, EPL, LaLiga, UCL), with a peek on finals and score changes. Click to open the game in Fullcourt or ESPN.
- **Notifications** — mirrors Windows toasts (experimental — see below), and your own apps can push notifications through the local API.
- **Built for your other apps** — a tiny local API lets **Amplite**, **Savor**, **Fullcourt** or any script put live activities on the island. → [docs/integration.md](docs/integration.md)
- **Themeable** — Midnight (true black), Graphite, Glass, Frost (light) or fully custom colors; fixed or automatic accent; size and position.
- **Light** — no web view, no background services. Animations run only while something is moving and stop when hidden; it hides itself over fullscreen games and video.
- **Built-in updater** — checks GitHub Releases, verifies the download's SHA-256, swaps itself and restarts. No installer, no admin.

## Install

1. Download from **[Releases](https://github.com/larosa023/Isle/releases/latest)**:
   - **`Isle.exe`** (recommended, small) — needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0). If it's missing, Windows offers to install it the first time you run Isle.
   - **`Isle-standalone.exe`** — bigger, but has no prerequisites.
2. Put it in a folder you own, e.g. `%LOCALAPPDATA%\Programs\Isle\` — the updater needs to be able to replace the file, so avoid `Program Files`.
3. Run it. Isle lives in the notification area; turn on *Start Isle when I sign in* in Settings.

Windows SmartScreen may warn about an unsigned app the first time — choose **More info → Run anyway**.

Requires Windows 10 (2004 / build 19041) or Windows 11.

## Using it

| | |
|---|---|
| **Hover** | Expand (or click, if you turned hover off) |
| **Scroll** on the island | Flip between activities when several are live |
| **Click** the expanded island | Open the activity (the game page, the downloaded file…) |
| **Right-click** | New timer, settings, check for updates, hide for an hour, quit |
| **Tray icon** | Same menu; left-click opens Settings |

Settings are saved to `%APPDATA%\Isle\settings.json` (log: `isle.log` in the same folder). The custom theme's colors live in that file under `customTheme`.

### Sports setup

Settings → Live sports: set **Leagues** (e.g. `nba, nfl`) and **Favorite teams** by abbreviation (`NYK, BKN, NYG`). If an abbreviation exists in more than one league, prefix it: `nfl:NYJ`. Set **Open scores in** to your Fullcourt URL to open games there.

### About notification mirroring

Windows only gives notification access to apps it can identify (usually Store-packaged apps). Isle asks for it when the option is on; if Windows refuses, Isle logs that and carries on. Your own apps don't depend on this — they can send notifications to the island through `POST /v1/notify`.

## Building

```powershell
dotnet publish src/Isle/Isle.csproj -c Release -r win-x64 -p:PublishSingleFile=true --self-contained false -o out
```

Requires the .NET 10 SDK on Windows. Every push to `main` builds both flavors in GitHub Actions (artifacts on the run page).

## Releasing (and how updates reach users)

```bash
git tag v0.2.0
git push origin v0.2.0
```

The workflow builds `Isle.exe`, `Isle-standalone.exe` and `SHA256SUMS.txt`, stamps the version from the tag, and publishes a GitHub Release with generated notes. Running copies of Isle notice within 6 hours (or immediately via *Check for updates*), show an update card on the island, and install with one click. The version in the tag must be higher than the running one.

## Project layout

```
src/Isle/
  App.xaml(.cs)            startup, single instance, module lifecycle
  MainWindow.xaml(.cs)     the island: layers, spring animations, hover/peek/scroll, fullscreen hiding
  SettingsWindow.xaml(.cs) settings UI (Fluent)
  TrayIcon.cs
  Core/                    Activity model + hub, settings, theming, helpers, Win32 interop
  Controls/Visualizer.cs   the bouncing bars
  Services/                Media, Timer, Download, Call, Notification, Sports, IntegrationServer, Update
```

Every module produces the same `Activity` shape (title, subtitle, image/glyph, accent, progress, trailing text, buttons), so the island has one template and adding a new kind of activity is a single class.
