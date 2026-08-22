# Tombstack for Unity — Documentation

Crash, exception, and session telemetry for Unity games, with an in-editor hub connected to
your Tombstack account. Requires **Unity 6 (6000.0)** or newer.

> Tombstack is a hosted service. The plugin is free; a Tombstack account is required
> (free tier under 10 CCU). Create one at the signup link inside the sign-in window.

## Quickstart

### 1. Install

- **Tarball (recommended):** download `com.anklebreaker.tombstack-0.19.6.tgz` from
  `https://tombstack.com/downloads/com.anklebreaker.tombstack-0.19.6.tgz`, then
  Package Manager ▸ `+` ▸ *Add package from tarball…*
- Or Package Manager ▸ `+` ▸ *Add package from git URL…* →
  `https://github.com/AnkleBreaker-Studio/tombstack-unity.git#v0.19.6`

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
  (signature `unclean-shutdown`), and uploads that session's retained log. On **Android 11+
  (0.17+)** the report is enriched with the real OS cause — `oom-kill` / `anr-kill` /
  `native-signal-<n>` / `native-crash` / … via `ApplicationExitInfo` — and deaths the OS
  attributes to a user force-stop / self-exit / permission change are not reported at all.
  Fail-soft everywhere else (pre-Android-11, non-Android, lookup failure) → the pre-0.17 heuristic.
- **Device identity (0.16+) — no anonymous players** — at first launch the SDK mints a
  persistent device-derived id (`dev_` + 16 hex, SHA-256 of the device identifier salted with
  your game token; the raw identifier never leaves the device and the same device is unlinkable
  across games) and uses it as the `userId` until you call `SetUser(realId)`, which upgrades the
  **same session** (a one-shot `priorUserId` merges the pre-auth telemetry into the real player).
  `SetUser(null)` (logout) reverts to the device id.
- **Crash `kind` (0.19+)** — reports are labelled `crash` / `exception` / `unclean_shutdown`
  automatically (no API) so the dashboard stops calling everything a "crash".
- **Breadcrumbs, heartbeats, offline retry** — as before, all automatic. Heartbeats also fire
  on **background (minimize)** and best-effort on **quit** (0.19+) — and `SendHeartbeatNow()`
  (main-thread only) is available for custom lifecycle moments.
- **Per-session frame stats (0.11+)** — every heartbeat carries the interval's average FPS,
  slow-frame % (> 33.4 ms), hitch count (> 250 ms), and worst frame ms; omitted when no frame
  ran (headless servers), sampled with zero per-frame allocation. A finer **20s `fpsSamples`
  series (0.18+)** is folded into the same 60s beat (no extra rows / ingest cost).
- **App-hang detection (0.11+)** — a background watchdog reports a `tmb.app_hang` event
  (duration, active scene, threshold) when the main thread stalls longer than
  *App Hang Threshold Seconds* (default 5, min 2; 0 disables) and then recovers, plus a
  Warning breadcrumb. Max one report per minute; no cross-thread stack is captured — hang
  events group by scene. Toggle via *Detect App Hangs* on the config asset.

Manual one-liners: `SetUser`, `TrackEvent`, `ReportBug` (now attaches the session log),
`AddBreadcrumb`, `ReportException`, `SendHeartbeatNow` (0.19+, main-thread only).

**Deferring the first beat (0.15+):** by default the SDK starts collecting at `Init`. To make the
first heartbeat carry the player's identity + environment instead of `anonymous` + `production`,
turn *Auto Start Session* off (config asset, or `Init(..., autoStartSession: false)`), configure
via `SetEnvironment` / `SetUser` / `SetUserMetadata`, then call `Tombstack.StartSession()`. Crash
and bug reports still send while deferred; the latch is idempotent and survives a pre-Init call.

Toggles on the `TombstackConfig` asset control the autonomy systems (all default ON):
*Auto Capture Exceptions*, *Upload Logs*, *Retain Launch Logs* (0.18+, count 1–10, default 3),
*Detect Unclean Shutdown*, *Auto Scene Breadcrumbs*, *Send Heartbeats* (0.12+ — OFF logs a
warning: live CCU, sessions, crash-free %, fleet, user metadata, and log pulls go dark),
*Collect Frame Stats* (0.12+), *Detect App Hangs* (with *App Hang Threshold Seconds*, default 5),
and the two screenshot toggles. At runtime, `Tombstack.SetCaptureEnabled(TombstackCapture.X, bool)` (0.12+) flips
*Exceptions* / *Heartbeats* / *Breadcrumbs* / *FrameStats* / *AppHangs* live — manual
`ReportException` and `AddBreadcrumb` always work regardless. All are consent-gated —
with *Require consent* enabled, nothing is captured, mirrored, or reported until your game
calls `Tombstack.SetConsent(true)`.

### Files the SDK keeps under `persistentDataPath/Tombstack/`

| File | Purpose |
|---|---|
| `session.log` | Rolling log of the current session (~512 KB cap, newest lines win) |
| `session-<sessionId>.log` (0.18+) | The last **N** per-launch logs (N = *Retain Launch Logs*, default 3), kept for unclean-shutdown upload and on-demand server log pulls of a past session. Supersedes the single `previous-session.log`; legacy `session.log`/`previous-session.log` are migrated, not lost |
| `identity.json` (0.16+) | The persisted device-derived provisional id (`dev_…`) used until `SetUser(realId)` |
| `session.lock` | Dirty-session marker (present while running; gone after a clean quit) |
| `*.json` | Write-ahead upload queue (crashes/bugs that have not been delivered yet) |

## Project Settings

**Edit ▸ Project Settings ▸ Tombstack**

| Setting | Effect |
|---|---|
| Base URL override | Point the plugin + SDK at a self-hosted/staging Tombstack tenant. Re-link after changing it. |
| Heartbeat (s) | Seconds between session heartbeats (written into the config asset; runtime clamps 15–240). The upper bound sits inside the server's 5-minute session window on purpose: beat more slowly and your sessions blink out between their own beats, understating live CCU and the peak concurrency you are billed on. |
| Environment | Deployment-environment label (production / staging / …) stamped on every payload; written into the config asset. Defaults to `production`. |
| Require consent | When on, the SDK captures nothing until your game calls `Tombstack.SetConsent(true)`. |
| Unlink project | Clears the game binding and blanks the SDK token in the config asset. |
| Sign out | Invalidates and deletes the editor token. |

## Runtime API (summary)

```csharp
Tombstack.Init(gameToken, endpoint, heartbeatIntervalSeconds = 60f, environment = null,
    autoStartSession = true, retainedLaunchLogs = 3);            // auto-called via TombstackConfig
Tombstack.SetConsent(bool granted);
Tombstack.SetUser(userId, steamId = null);                       // upgrades the device id in-session (0.16); SetUser(null) reverts to it
Tombstack.SetUserMetadata(Dictionary<string,string> metadata);   // per-player custom metadata
Tombstack.SetEnvironment(environment);                           // production / staging / … — wins over Init/config
Tombstack.StartSession();                                        // 0.15: begin collecting (only needed when autoStartSession = false)
Tombstack.SendHeartbeatNow();                                    // 0.19: one immediate beat — MAIN THREAD ONLY
Tombstack.TrackEvent(name, Dictionary<string,string> props = null);
Tombstack.TrackMetric(name, double value, string unit = null);
Tombstack.SetSampleRate(name, float rate0to1);                   // per-name keep-probability
Tombstack.SetCaptureEnabled(TombstackCapture capture, bool on);  // 0.12+: toggle a subsystem at runtime
Tombstack.AddBreadcrumb(message, BreadcrumbLevel level = Info, category = null);
Tombstack.ReportException(exception);
Tombstack.ReportBug(message, category = null);
Tombstack.MarkDedicatedServer(serverId, region = null, hostname = null); // 0.13: server identity without a match
Tombstack.SetMatchContext(serverId, matchId);                    // multiplayer correlation
Tombstack.SetServerInfo(region, hostname);                       // fleet labels
string matchId = Tombstack.StartMatch();                         // server: flips role to "server"
Tombstack.EndMatch();
Tombstack.RequestPlayerLogs(target, targetValue, reason);        // write-scoped server token
TombstackDiagnostics diag = Tombstack.GetDiagnostics();          // readonly snapshot
```

`SetEnvironment`, `TrackEvent`, `TrackMetric`, and `StartSession` are safe to call **before**
`Init`: an explicit `SetEnvironment` always wins over `Init`'s parameter / the config asset, and
pre-init events/metrics are buffered (64, drop-oldest) and replayed with their original timestamps
once the SDK initializes.

See the package `README.md` for full runtime behavior (offline-first durable queue,
breadcrumbs, consent gating, fail-silent guarantees).

## Which calls unlock which dashboard views

Every panel below reads telemetry this SDK **already sends** — none of them needs an SDK upgrade.
They are listed because the dashboard cannot show what your game never tells it, and the difference
between an empty panel and a useful one is usually one call you have not made yet.

| Dashboard view | What it needs from your game | If you do not call it |
|---|---|---|
| **Where players quit** → exit points | `TrackProgression(status, area, level)` | Quitters still appear, but "last thing they did" reads `(no event recorded)` — the panel says it cannot see where they went rather than guessing |
| **Live Fleet** → servers running matches | `StartMatch()` / `SetMatchContext(serverId, matchId)` on the server | The server shows as "players, no match" — connected players but no match reported, deliberately NOT the same as idle |
| **Live Fleet** → a server at all | `MarkDedicatedServer(serverId)` | It is still discovered from its players' `serverId`, but flagged `inferred`, and its own crashes/health are missing |
| **Crash breakdown** → why the process died | nothing (Android, 0.17+, automatic) | n/a — exit type / OS reason / signal arrive on their own |
| **Crash rate vs All errors** | `ReportException` for handled errors | Handled errors are simply absent; your crash rate is unaffected either way, because since 2026-08-02 it counts only process deaths |
| **Audiences** → typical FPS below N | nothing (0.11+, automatic) | n/a — frame stats ride heartbeats |
| **Retention / new users / churn** | `SetUser(...)` | Anonymous players cannot be followed between sessions, so they are excluded from every cohort, retention and churn figure |

**A crash is not an exception.** Since 2026-08-02 the dashboard's crash rate and crash-free
percentage count only *process deaths* — a hard crash or an unclean shutdown. `ReportException`
reports a handled error your game recovered from; it feeds the separate **All errors** metric and
the per-kind split, and no longer moves your crash rate. Report exceptions freely: doing so can no
longer make your stability numbers look worse.

**Progression is what turns "they left" into "they left at boss-3".** `TrackProgression` writes
`area` and `level` attributes, and the exit analysis reads the last one a departing player fired.
One call at each meaningful milestone is enough:

```csharp
Tombstack.TrackProgression(ProgressionStatus.Start,    area: "forest", level: "boss-3");
Tombstack.TrackProgression(ProgressionStatus.Fail,     area: "forest", level: "boss-3");
Tombstack.TrackProgression(ProgressionStatus.Complete, area: "forest", level: "boss-3");
```

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
