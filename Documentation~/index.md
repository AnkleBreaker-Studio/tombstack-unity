# Tombstack for Unity — Documentation

Crash, exception, and session telemetry for Unity games, with an in-editor hub connected to
your Tombstack account. Requires **Unity 6 (6000.0)** or newer.

> Tombstack is a hosted service. The plugin is free; a Tombstack account is required
> (free tier under 10 CCU). Create one at the signup link inside the sign-in window.

## Quickstart

### 1. Install

- **Tarball (recommended):** download `com.anklebreaker.tombstack-0.12.0.tgz` from
  `https://tombstack.com/downloads/com.anklebreaker.tombstack-0.12.0.tgz`, then
  Package Manager ▸ `+` ▸ *Add package from tarball…*
- Or Package Manager ▸ `+` ▸ *Add package from git URL…* →
  `https://github.com/AnkleBreaker-Studio/tombstack-unity.git#v0.12.0`

### 2. Sign in (mandatory)

The plugin is inactive until you connect your Tombstack account.

1. Open **Window ▸ Tombstack ▸ Sign In** (a one-time prompt also appears on first load).
2. Enter your account email + password. The editor token is stored per-user in
   EditorPrefs — it is never written into your project or version control.

*Screenshot placeholder: `images/signin.png` — the forge-dark sign-in window.*

### 3. Link this project

1. Open **Window ▸ Tombstack ▸ Hub**.
2. On the **Connection** tab, pick your studio and game, then click **LINK THIS PROJECT**.
3. The plugin mints a per-game SDK token (`tmb_…`) and writes it — together with the
   endpoint — into `Assets/Tombstack/Resources/TombstackConfig.asset` (created if missing).
   This is the asset the runtime SDK auto-initializes from; the token is game-facing and is
   supposed to ship with your build.

*Screenshot placeholder: `images/hub-connection.png` — Hub connection tab with status card.*

### 4. Watch crashes from the editor

Switch to the Hub's **Dashboard** tab:

- Crash-free %, crashes in the last 24h / 7d
- Crash-spike banner when volume trends above baseline
- Top-10 signatures, colored by triage status — click a row to open the full signature page
  in your browser
- 30-day crash trend
- Manual **REFRESH** and a 60-second auto-refresh toggle

*Screenshot placeholder: `images/hub-dashboard.png` — live dashboard tab.*

### 5. Verify the runtime

Enter Play Mode and throw a test exception — it appears on the dashboard within seconds:

```csharp
throw new System.Exception("Tombstack smoke test");
```

## What's automatic

Once initialized, the SDK needs no further integration for the common cases:

- **Exceptions** — unhandled exceptions on any thread, unobserved `Task` exceptions, and
  AppDomain unhandled exceptions are captured automatically and deduped (≤1 report per
  signature per minute; repeats become a counter breadcrumb).
- **Player log** — every log line mirrors into a rolling ~512 KB
  `persistentDataPath/Tombstack/session.log`; when a crash or bug report is accepted, the log
  uploads automatically to a presigned URL returned by the server.
- **Unclean shutdowns** — if the app dies without a clean quit (hard crash, OOM kill, force
  quit), the next launch detects it via the `session.lock` marker, reports a synthetic crash
  (signature `unclean-shutdown`), and uploads the preserved `previous-session.log`.
- **Breadcrumbs, heartbeats, offline retry** — as before, all automatic.
- **Per-session frame stats (0.11+)** — every heartbeat carries the interval's average FPS,
  slow-frame % (> 33.4 ms), hitch count (> 250 ms), and worst frame ms; omitted when no frame
  ran (headless servers), sampled with zero per-frame allocation.
- **App-hang detection (0.11+)** — a background watchdog reports a `tmb.app_hang` event
  (duration, active scene, threshold) when the main thread stalls longer than
  *App Hang Threshold Seconds* (default 5, min 2; 0 disables) and then recovers, plus a
  Warning breadcrumb. Max one report per minute; no cross-thread stack is captured — hang
  events group by scene. Toggle via *Detect App Hangs* on the config asset.

Manual one-liners: `SetUser`, `TrackEvent`, `ReportBug` (now attaches the session log),
`AddBreadcrumb`, `ReportException`.

Toggles on the `TombstackConfig` asset control the autonomy systems (all default ON):
*Auto Capture Exceptions*, *Upload Logs*, *Detect Unclean Shutdown*, *Auto Scene
Breadcrumbs*, *Send Heartbeats* (0.12+ — OFF logs a warning: live CCU, sessions,
crash-free %, fleet, user metadata, and log pulls go dark), *Collect Frame Stats* (0.12+),
*Detect App Hangs* (with *App Hang Threshold Seconds*, default 5), and the two screenshot
toggles. At runtime, `Tombstack.SetCaptureEnabled(TombstackCapture.X, bool)` (0.12+) flips
*Exceptions* / *Heartbeats* / *Breadcrumbs* / *FrameStats* / *AppHangs* live — manual
`ReportException` and `AddBreadcrumb` always work regardless. All are consent-gated —
with *Require consent* enabled, nothing is captured, mirrored, or reported until your game
calls `Tombstack.SetConsent(true)`.

### Files the SDK keeps under `persistentDataPath/Tombstack/`

| File | Purpose |
|---|---|
| `session.log` | Rolling log of the current session (~512 KB cap, newest lines win) |
| `previous-session.log` | The previous session's log, preserved at launch for unclean-shutdown upload |
| `session.lock` | Dirty-session marker (present while running; gone after a clean quit) |
| `*.json` | Write-ahead upload queue (crashes/bugs that have not been delivered yet) |

## Project Settings

**Edit ▸ Project Settings ▸ Tombstack**

| Setting | Effect |
|---|---|
| Base URL override | Point the plugin + SDK at a self-hosted/staging Tombstack tenant. Re-link after changing it. |
| Heartbeat (s) | Seconds between session heartbeats (written into the config asset; runtime clamps 15–600). |
| Environment | Deployment-environment label (production / staging / …) stamped on every payload; written into the config asset. Defaults to `production`. |
| Require consent | When on, the SDK captures nothing until your game calls `Tombstack.SetConsent(true)`. |
| Unlink project | Clears the game binding and blanks the SDK token in the config asset. |
| Sign out | Invalidates and deletes the editor token. |

## Runtime API (summary)

```csharp
Tombstack.Init(gameToken, endpoint, heartbeatIntervalSeconds = 60f, environment = null); // auto-called via TombstackConfig
Tombstack.SetConsent(bool granted);
Tombstack.SetUser(userId, steamId = null);
Tombstack.SetUserMetadata(Dictionary<string,string> metadata);   // per-player custom metadata
Tombstack.SetEnvironment(environment);                           // production / staging / … — wins over Init/config
Tombstack.TrackEvent(name, Dictionary<string,string> props = null);
Tombstack.TrackMetric(name, double value, string unit = null);
Tombstack.SetSampleRate(name, float rate0to1);                   // per-name keep-probability
Tombstack.SetCaptureEnabled(TombstackCapture capture, bool on);  // 0.12+: toggle a subsystem at runtime
Tombstack.AddBreadcrumb(message, BreadcrumbLevel level = Info, category = null);
Tombstack.ReportException(exception);
Tombstack.ReportBug(message, category = null);
Tombstack.SetMatchContext(serverId, matchId);                    // multiplayer correlation
Tombstack.SetServerInfo(region, hostname);                       // fleet labels
string matchId = Tombstack.StartMatch();                         // server: flips role to "server"
Tombstack.EndMatch();
Tombstack.RequestPlayerLogs(target, targetValue, reason);        // write-scoped server token
TombstackDiagnostics diag = Tombstack.GetDiagnostics();          // readonly snapshot
```

`SetEnvironment`, `TrackEvent`, and `TrackMetric` are safe to call **before** `Init`: an explicit
`SetEnvironment` always wins over `Init`'s parameter / the config asset, and pre-init
events/metrics are buffered (64, drop-oldest) and replayed with their original timestamps once
the SDK initializes.

See the package `README.md` for full runtime behavior (offline-first durable queue,
breadcrumbs, consent gating, fail-silent guarantees).

## Where credentials live

| Credential | Location | In version control? |
|---|---|---|
| Editor token (your account) | EditorPrefs (per user, per machine) | Never |
| SDK ingest token (`tmb_…`, per game) | `Assets/Tombstack/Resources/TombstackConfig.asset` | Yes — it is game-facing by design |
| Project ↔ game binding (ids only) | `ProjectSettings/TombstackSettings.asset` | Yes (no secrets) |

## Troubleshooting

- **"Wrong email or password"** — credentials rejected (HTTP 401). Reset your password on
  the web dashboard if needed.
- **"Too many attempts"** — sign-in rate limit (HTTP 429). Wait a minute.
- **"Could not reach Tombstack"** — offline or the endpoint override is wrong. Check
  *Project Settings ▸ Tombstack ▸ Base URL*.
- **"Session expired — sign in again"** — the editor token expired; sign in again. Your
  project link and SDK token are unaffected.
- **Dashboard empty** — make sure the project is linked and the game has reported at least
  one session or crash.

## Support

- Issues: https://github.com/AnkleBreaker-Studio/tombstack-unity/issues
