# Isle

**A Dynamic Island for Windows.** A small pill at the top of your screen that stays out of the way, then springs open to show what's happening right now — music, timers, downloads, calls, notifications and live scores.

- **Now playing** — album art, live progress and prev / play / next for anything that uses Windows media controls (Spotify, Apple Music, YouTube in any browser, Media Player…). Click the progress bar to jump, scroll the open card to change the volume. The visualizer follows what your speakers are actually playing, and the album art softly tints the open card.
- **Timers, stopwatch & focus** — timers from the island, right-click, the tray, the command line or the API (pause, +1 min, *Again*), a stopwatch with laps, and focus sessions (focus / short break / long break rounds, optional Do not disturb). Pick any Windows alarm sound or your own .wav.
- **Downloads** — live size and speed for Chrome, Edge, Firefox, Brave and Opera downloads, then *Open* / *Show in folder*. No extension needed.
- **Calls** — Teams, Discord, Zoom, Slack, Phone Link, WhatsApp, Meet in a browser… detected from microphone use, with a running call timer. Optional mic-in-use indicator for every other app.
- **Live sports + Fullcourt** — your teams' scores from ESPN (NBA, WNBA, NFL, NCAA, MLB, NHL, MLS, EPL, LaLiga, UCL), with a peek on finals and score changes. Isle follows the favorite teams you pick in **Fullcourt**, tracks any game you send with Fullcourt's **Show on Isle** button, and opens games in the Fullcourt app (or the Fullcourt site on GitHub Pages if the app isn't installed).
- **System moments** — a slim volume pill when you change or mute the volume, Caps Lock / Num Lock, charging and low battery, new screenshots (open, copy, show in folder), and an opt-in "copied" flash.
- **Weather & clock** — the open idle island shows the time, date and current weather (Open-Meteo, no account); the idle pill can show the time and temperature.
- **Two things at once** — when two activities are live, the second sits in a small bubble beside the pill. Click it to switch.
- **Notifications** — mirrors Windows toasts (experimental — see below), and your own apps can push notifications through the local API.
- **Amplite built in** — Isle talks to Amplite's local API: cover, song, live progress, prev / play / next, **Like**, and an **EQ preset picker** right on the island. No setup; it just appears while Amplite is running, and replaces the duplicate Spotify card.
- **You decide what shows** — every app Isle detects (players, call apps, notification senders, browsers, API clients) gets its own *show*, *peek* and *priority* setting on the **Apps** page, or right-click the island → *Hide this app*.
- **Built for your other apps** — a tiny local API lets **Savor**, **Fullcourt** or any script put live activities on the island. → [docs/integration.md](docs/integration.md)
- **Deeply customizable** — Midnight (true black), Graphite, Glass, Frost (light) or custom colors; content-matched or fixed accent; accent outline; size, shape and placement of every state; what each state shows; motion speed and springiness; peek style; frame rate. Settings has search (**Ctrl+F**) and export / import.
- **Light and smooth** — no web view, no background services. Animations run at your display's refresh rate (e.g. 144 Hz) and only while something is moving; Isle hands unused memory back to Windows when idle and hides over fullscreen games and video, or over apps you list.
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
| **Scroll** on the island | Flip between activities; on an open music or volume card, change the volume |
| **Click** the progress bar | Jump to that point in the song (or set the volume) |
| **Click** the bubble | Switch to the second activity |
| **Click** the expanded island | Open the activity (the game page, the downloaded file…) |
| **Right-click** | New timer, hide this app, Do not disturb, settings, updates, hide for an hour, quit |
| **Ctrl+Alt+I** | Open / close the island |
| **Ctrl+Alt+H** | Hide / show the island completely (both shortcuts are in Settings → General) |
| **Tray icon** | Same menu; left-click opens Settings |
| **Run Isle.exe again** | Opens Settings (or runs a command, below) |

### Commands

Run `Isle.exe` with a command while Isle is running — handy for desktop shortcuts, Stream Deck, AutoHotkey or a keyboard knob:

```
Isle.exe --timer 10m --label "Tea"     Isle.exe --stopwatch      Isle.exe --focus
Isle.exe --toggle                      Isle.exe --toggle-hide    Isle.exe --dnd toggle
Isle.exe --settings System             Isle.exe --preview        Isle.exe --quit
```

Durations: `90s`, `5m`, `1h30m`, `1:30`, or a plain number of minutes. Apps can send the same commands to `POST /v1/command`.

### Settings

Open from the tray icon or right-click → *Settings…*. Changes apply instantly, and *General → Try it* puts sample cards on the island so you can see them.

- **Island** — which display, left / center / right, offsets, size, idle / compact / expanded dimensions, corner roundness, and exactly which parts show in the compact pill and the expanded island; clock in the idle pill; your own quick-timer buttons.
- **Appearance** — themes or custom colors, accent (from the content, or fixed), opacity, border, font, visualizer.
- **Behavior & motion** — hover vs click, hover and close delays, click and scroll behavior, peeks, fullscreen hiding, animation speed, springiness, reduce motion.
- **Activities** — per type (music, timers, downloads, calls, notifications, sports, system, your apps, updates): show, peek, priority.
- **System** — volume pill, lock keys, battery alerts and threshold, screenshots, clipboard flash.
- **Timers & focus** — alarm sound and repeats, focus / break lengths, rounds, auto-start, Do not disturb while focusing.
- **Apps** — every detected app with show, peek, priority, forget.
- **General** — start with Windows, Do not disturb, quiet hours (calls and finished timers can still alert), a global shortcut (default **Ctrl+Alt+I**).
- **Amplite**, **Live sports**, **Integrations**, **About & updates** (export / import, diagnostics: memory, CPU time, connections).

Settings are saved to `%APPDATA%\Isle\settings.json` (per-app choices in `apps.json`) (log: `isle.log` in the same folder). The custom theme's colors live in that file under `customTheme`.

### Sports setup

Settings → Live sports: set **Leagues** (e.g. `nba, nfl`) and **Favorite teams** by abbreviation (`NYK, BKN, NYG`). If an abbreviation exists in more than one league, prefix it: `nfl:NYJ`. Set **Open scores in** to your Fullcourt URL to open games there.

### Fullcourt

- **Your team:** in Fullcourt, make a team your team. The Windows app tells Isle automatically; on the website, turn on **Settings → Isle** first (your browser may ask to allow access to apps on your device).
- **Track a game:** open any game in Fullcourt and click the island button in its top bar (*Show on Isle*). Isle shows it — upcoming, live, then the final — and forgets it after.
- **Opening games:** Isle → Settings → Live sports → *Open games in*: Auto (the Fullcourt app if installed, otherwise the site), the app, the website, ESPN or a custom link. The site address defaults to the GitHub Pages copy, `https://larosa023.github.io/fullcourt/`.

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
