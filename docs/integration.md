# Isle local API

Isle listens on **`http://127.0.0.1:47820`** (port configurable in Settings → Integrations). Anything on your PC — Amplite, Savor, Fullcourt, a PowerShell script — can put a live activity on the island with one HTTP call. It never listens on the network, only on this machine.

**Who may call it**

- **Desktop apps and scripts** (no `Origin` header): always allowed.
- **Fullcourt** (its GitHub Pages, Netlify and Cloudflare sites, and the Windows app) is always allowed.
- **Other websites** (browser `fetch`, which sends `Origin`): only origins listed under *Settings → Integrations → Allowed websites*, e.g. `https://fullcourt.netlify.app`. `http://localhost:*` dev servers are always allowed. Isle answers CORS and Chrome's Private Network Access preflights for allowed origins.
- Websites can only attach `http(s)` links. Desktop apps can also use custom schemes (`amplite://preset/bass`). `file:` and script schemes are refused for everyone.

## Endpoints

| Method | Path | What it does |
|---|---|---|
| `GET` | `/v1/status` | `{ name, version, activities }` — use it to detect Isle. |
| `GET` | `/v1/activities` | What's on the island right now. |
| `POST` | `/v1/activities` | Create **or update** a live activity (same `app` + `id` = update). |
| `DELETE` | `/v1/activities/{app}/{id}` | Remove one. |
| `POST` | `/v1/notify` | One-off notification that peeks for a few seconds. |
| `POST` | `/v1/timers` | Start an Isle timer: `{ "minutes": 12, "label": "Pasta" }`. |
| `POST` | `/v1/command` | Same commands as the command line: `{ "cmd": "--timer 5m --label Tea" }`, `{ "cmd": "toggle-hide" }`, `{ "cmd": "focus" }`. |
| `POST` | `/v1/fullcourt/sync` | Fullcourt: `{ "league": "nba", "favorite": { "abbr": "NY", "name": "Knicks" } }` (`favorite: null` clears it). |
| `POST` | `/v1/fullcourt/track` | Fullcourt: `{ "league": "nba", "game": "401…", "on": true }` — track one game until it ends. |
| `GET` | `/v1/fullcourt/track` | Games currently tracked from Fullcourt. |

### `POST /v1/activities`

```json
{
  "app": "Savor",
  "id": "order-42",
  "title": "Rigatoni alla vodka",
  "subtitle": "Simmer the sauce",
  "glyph": "timer",
  "image": "https://…/thumb.jpg",
  "accent": "#FF6B35",
  "progress": 0.6,
  "progressLeft": "Step 3",
  "progressRight": "of 5",
  "trailing": "6:00",
  "badge": "6:00",
  "live": true,
  "priority": 55,
  "ttl": 3600,
  "peek": true,
  "url": "savor://recipe/rigatoni",
  "actions": [
    { "label": "Next step", "glyph": "next", "callback": "http://127.0.0.1:5123/isle", "primary": true },
    { "label": "Done", "glyph": "check", "dismiss": true }
  ]
}
```

Only `app` and `title` are required. Fields:

- `glyph` — a name (`music play pause next previous timer download check phone mic bell sports star heart mail calendar chat people link home warning cart location sync volume equalizer headphones info update open folder close add refresh`), a Segoe Fluent Icons code point like `"E8D6"`, or a single character.
- `image` — `https://` URL, `data:image/png;base64,…`, or (desktop callers) a local file path. Shown instead of the glyph.
- `accent` — `#RRGGBB`. Tints the icon, progress bar and trailing text (unless the user fixed an accent color).
- `progress` — `0`–`1`; omit for none.
- `trailing` — short text on the right of the collapsed pill; `badge` — large text on the right when expanded.
- `priority` — `0`–`90`, higher wins the pill. Built-ins for reference: calls 100, finished timer 95, notifications 85–90, downloads 65, timers 70, live sports 60, playing music 50, paused music 15.
- `ttl` — seconds until it disappears (default and max 24 h). Re-POST to extend.
- `peek` — briefly expand. Defaults to `true` for new activities, `false` for updates.
- `url` — opened when the expanded island is clicked.
- `openCallback` — instead of a URL, have Isle POST `{ "app", "id", "action": "open" }` to this `127.0.0.1` / `localhost` address when the open island is clicked.
- `since` / `until` — Unix time in milliseconds. Isle shows a live elapsed clock (`since`) or countdown (`until`) as the trailing text and badge, ticking by itself — no need to re-send every second.
- `actions` — up to 4 buttons. Each can open a `url`, POST to a `callback` (must be `127.0.0.1`/`localhost`), and/or `dismiss` the activity. Callbacks receive `{ "app", "id", "action" }`.

### `POST /v1/notify`

```json
{ "app": "Fullcourt", "title": "Knicks win 112–104", "body": "Brunson 38 pts", "glyph": "sports", "accent": "#F58426", "url": "https://…", "seconds": 6 }
```

## Snippets

**PowerShell**

```powershell
Invoke-RestMethod -Method Post http://127.0.0.1:47820/v1/activities -ContentType application/json -Body (@{
  app = 'Amplite'; id = 'preset'; title = 'Bass Boost'; subtitle = 'EQ preset applied'; glyph = 'equalizer'; ttl = 4
} | ConvertTo-Json)
```

**C# (.NET)** — drop into Amplite or Savor:

```csharp
using System.Net.Http.Json;

static readonly HttpClient Isle = new() { BaseAddress = new Uri("http://127.0.0.1:47820"), Timeout = TimeSpan.FromSeconds(2) };

static async Task IslePostAsync(object activity)
{
    try { await Isle.PostAsJsonAsync("/v1/activities", activity); }
    catch { /* Isle isn't running — that's fine */ }
}

await IslePostAsync(new { app = "Amplite", id = "preset", title = "Vocal Clarity", subtitle = "EQ preset", glyph = "equalizer", ttl = 4 });
```

**JavaScript (Fullcourt, browser)** — add the site's origin under *Allowed websites* first:

```js
async function isle(path, body) {
  try {
    await fetch(`http://127.0.0.1:47820${path}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
  } catch { /* not on Windows, or Isle isn't running */ }
}

isle('/v1/activities', {
  app: 'Fullcourt', id: `game-${gameId}`,
  title: `NYK ${away} – ${home} BOS`, subtitle: 'Q4 · 2:31',
  glyph: 'sports', accent: '#F58426', trailing: `${away}–${home}`, live: true, ttl: 600,
  url: location.href,
});
```

Tip: clear an activity when you're done with `DELETE /v1/activities/Fullcourt/game-401585`.
