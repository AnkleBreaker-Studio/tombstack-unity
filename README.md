# Tombstack Unity package — `com.anklebreaker.tombstack`

UPM package that captures **managed C# exceptions**, **session heartbeats**, **analytics
events**, **breadcrumbs**, and **player bug reports** in a Unity game and uploads them to a
Tombstack ingestion endpoint with a per-game SDK token. The native crash core
([`../native/`](../native/)) reports native crashes through the same endpoints once
integrated. Follows the AnkleBreaker `com.anklebreaker.*` convention and C# naming standard.

## What's automatic

After `Tombstack.Init` (or zero-code auto-init from the config asset), the SDK handles most
cases with **zero further integration**:

| Capability | Automatic? | How |
|---|---|---|
| Unhandled exceptions (main + background threads) | ✅ Automatic | `Application.logMessageReceivedThreaded`, deduped (≤1 report per signature per 60s; repeats become a counter breadcrumb) |
| Unobserved `Task` exceptions | ✅ Automatic | `TaskScheduler.UnobservedTaskException` (observed + reported, never escalated) |
| AppDomain unhandled exceptions | ✅ Automatic | `AppDomain.CurrentDomain.UnhandledException` (write-ahead persisted before the process dies) |
| Errors / warnings / logs as breadcrumbs | ✅ Automatic | Every Unity log line → 50-entry ring, attached to crashes & bug reports |
| Player log upload on crash | ✅ Automatic | Rolling ~512 KB `session.log`, PUT to a presigned URL after the crash report's 2xx |
| Unclean shutdown (hard crash / OOM kill / force quit) | ✅ Automatic (next launch) | `session.lock` marker + preserved `previous-session.log`, reported as signature `unclean-shutdown` |
| Session heartbeats / CCU | ✅ Automatic | Every N seconds (default 60) |
| Offline durability + retry | ✅ Automatic | Write-ahead queue, exponential backoff, next-launch retry |
| Event + metric batching (§16) | ✅ Automatic | `TrackEvent`/`TrackMetric` accumulate in a bounded, preallocated, drop-oldest buffer (cap 256) and flush as one batch on count ≥ 50 / age ≥ 10s / near-full / pause / quit / pre-crash |
| Player identification | One-liner | `Tombstack.SetUser("user-123", steamId)` |
| Analytics events (batched) | One-liner | `Tombstack.TrackEvent("level_complete", props)` |
| Numeric metrics (batched) | One-liner | `Tombstack.TrackMetric("tickrate", 60, "hz")` |
| Player bug reports (log attached automatically) | One-liner | `Tombstack.ReportBug("…", category)` |
| Manual breadcrumbs | One-liner | `Tombstack.AddBreadcrumb("…", level, category)` |
| Caught-but-interesting exceptions | One-liner | `Tombstack.ReportException(ex)` |
| Server-triggered log pull — client honouring | ✅ Automatic | The heartbeat ack carries pull requests; a **consenting**, **targeted** client uploads its rolling session log via the existing presigned-log path, off-thread. A non-consenting client never uploads. |
| Server-side log pull (request a player's logs) | One-liner | `Tombstack.RequestPlayerLogs(...)` / `Tombstack.OnAnomalousDisconnect(userId, reason)` (write-scoped server token) |

All three autonomy systems can be toggled on the config asset (`Auto Capture Exceptions`,
`Upload Logs`, `Detect Unclean Shutdown` — default ON) and are consent-gated: with
*Require Consent* enabled, nothing is captured, mirrored, or reported until
`Tombstack.SetConsent(true)`.

## Install

**Via UPM git URL** — Window ▸ Package Manager ▸ `+` ▸ *Add package from git URL…*:

```
https://github.com/AnkleBreaker-Studio/tombstack.git?path=unity
```

Or add to `Packages/manifest.json`:

```jsonc
{ "dependencies": { "com.anklebreaker.tombstack": "https://github.com/AnkleBreaker-Studio/tombstack.git?path=unity" } }
```

Or copy `unity/` into your project's `Packages/`. Requires Unity **6 (6000.0)+** (Mono and IL2CPP).

## Editor plugin & account sign-in

The package ships an editor plugin (UI Toolkit, forge-dark themed like the web dashboard).
**It requires a Tombstack account** — until you sign in, the editor tooling is inert and a
gentle one-time prompt points you to the Hub.

1. **Sign in** — `Window ▸ Tombstack ▸ Sign In` (or the Hub's sign-in button). Credentials
   are exchanged for an editor token stored **per-user in EditorPrefs** — it never enters
   the project folder or version control. No account? The window links to the signup page.
2. **Link the project** — `Window ▸ Tombstack ▸ Hub` → pick your studio + game (loaded from
   your account) → **Link this project**. This mints a per-game `tmb_` SDK token and writes
   endpoint + token into the `TombstackConfig` asset (created at
   `Assets/Tombstack/Resources/TombstackConfig.asset` if missing) — the same asset the
   runtime auto-init reads. The game binding is saved to
   `ProjectSettings/TombstackSettings.asset` (safe to commit; it contains ids, not secrets).
3. **Watch the dashboard** — the Hub's *Dashboard* tab shows live crash-free %, 24h/7d crash
   counts, a spike banner, the top-10 signatures (click → opens the signature in the web
   dashboard), and a 30-day trend. Refresh manually or enable 60s auto-refresh.
4. **Project Settings** — `Edit ▸ Project Settings ▸ Tombstack`: endpoint URL override
   (self-hosted/staging), heartbeat interval and consent defaults (written into the config
   asset), unlink, and sign out.

## Quick start

1. Sign in and **link this project** from the editor Hub (above) — it mints the SDK token
   and writes the config for you. (Manual alternative: mint a `tmb_…` token from the game's
   *SDK tokens* page on the web dashboard.)
2. Initialize the SDK, either way:

**Zero-code:** `Create ▸ Tombstack ▸ Config`, fill in your token + endpoint, place the
asset under any `Resources/` folder named `TombstackConfig`. It auto-initializes on load
(enable *Require Consent* to stay silent until `SetConsent(true)`).

**Manual:**
```csharp
using System.Collections.Generic;
using AnkleBreaker.Tombstack;

Tombstack.Init("tmb_…", "https://your-tenant.example.com");   // once, at boot
Tombstack.SetConsent(true);                                    // GDPR / store-policy gate
Tombstack.SetUser("user-123", steamId: "7656119…");            // once auth resolves

// Analytics events (events & funnels screens) — batched, flushed on count/age/pause/quit
Tombstack.TrackEvent("level_complete",
    new Dictionary<string, string> { { "level", "3" }, { "difficulty", "hard" } });

// Numeric metrics (time-series, p50/p95/p99) — batched alongside events
Tombstack.TrackMetric("tickrate", 60, "hz");
Tombstack.TrackMetric("rtt_ms", 42.5, "ms");

// Breadcrumbs: the log trail attached to the next crash / bug report
Tombstack.AddBreadcrumb("matchmaking started", BreadcrumbLevel.Info, category: "net");

// In-game feedback form
Tombstack.ReportBug("Quest log empty after load", category: "ui");

// Caught-but-interesting exceptions
try { Load(); } catch (Exception e) { Tombstack.ReportException(e); }
```

3. Trigger a test exception — the crash, its signature, and the 30-day trend appear on the
   game dashboard within seconds.

## What it does
- **Managed exceptions** → `Application.logMessageReceivedThreaded` (background threads too),
  plus `TaskScheduler.UnobservedTaskException` and `AppDomain.CurrentDomain.UnhandledException`
  → SHA-256 signature over the message + normalized top frames → `POST /api/v1/ingest/crashes`.
  Identical signatures dedupe to ≤1 report/min (repeats ride the breadcrumb trail as a counter).
- **Session log**: every log line mirrors into a rolling ~512 KB
  `persistentDataPath/Tombstack/session.log` (in-memory buffer, flushed off the main thread at
  most once per 5s + a final flush on the crash path). Crash and bug reports request a log
  upload (`"log": true`); after the 2xx the SDK PUTs the file (text/plain) to the presigned
  `data.logUpload.url` from the response — reusing the upload queue's backoff, never storing
  the presign across launches.
- **Unclean shutdowns**: Init writes `Tombstack/session.lock`; a clean quit
  (`Application.quitting`) removes it. If a marker survives to the next launch, the previous
  session died hard (native crash, OOM kill, force quit) — the SDK reports a synthetic crash
  (signature `unclean-shutdown`, previous session's buildVersion/os/arch) and uploads the
  preserved `previous-session.log`. If the write-ahead queue already held a managed crash from
  that session, no synthetic report is sent (no double-counting) — the restored crash's retry
  carries the previous log instead.
- **Session heartbeats** → `POST /api/v1/ingest/heartbeats` every N seconds (default 60,
  clamped 15–600) with `sessionId`/`buildVersion`/`os`/`arch`/`userId` — feeds the Live
  Fleet/CCU, Sessions, and Releases screens.
- **Analytics events** → `Tombstack.TrackEvent(name, props)` (flat attributes, clamped to the
  server contract: ≤32 entries, 64-char keys, 512-char values).
- **Numeric metrics** → `Tombstack.TrackMetric(name, value, unit?)` for time-series + p50/p95/p99
  (e.g. tickrate, RTT, CCU). Finite values only — NaN/Infinity are dropped. Carries the same
  correlation spine as events so a metric can be sliced by server / match / session / player.
- **Event/metric batching** (§16): events and metrics are NOT sent one request at a time. They
  accumulate in a bounded, preallocated, drop-oldest buffer (cap 256) and flush as a single
  envelope `{ "sentAtIso", "items": [...] }` to `POST /api/v1/ingest/events:batch` /
  `…/metrics:batch` on count ≥ 50, age ≥ 10s, near-full, app pause/quit, or a pre-crash flush.
  Each item keeps its own `occurredAtIso`; `sentAtIso` is the send time (used for clock-skew only).
  Sends reuse the existing queue + backoff + offline durability. Crashes, bug reports, and
  heartbeats stay individual (forensic / time-sensitive).
- **Bug reports** → `Tombstack.ReportBug(...)` → `POST /api/v1/ingest/bug-reports`.
- **Server-triggered log pull** (studio- or auto-initiated, consent-gated): a dedicated game server can
  request a player's session log when something looks wrong, without the player filing a bug report.
  - **Server side** → `Tombstack.RequestPlayerLogs(target, value, reason)` (or the
    `RequestPlayerLogs(target, reason)` / `OnAnomalousDisconnect(userId, reason)` convenience forms)
    POSTs `{ targetType, targetValue, reason }` to `POST /api/v1/pull-requests` using the configured
    **write-scoped** server token. An ingest-only client token is rejected (403) server-side — an
    extracted client token can never request anyone's logs. The pull is queued + audit-logged.
  - **Client side** (fully automatic, transparent): each heartbeat ack now carries
    `data.pendingRequests`. For every queued request that targets THIS client (by
    `userId`/`sessionId`/`matchId`/`serverId`) **and only while consent is granted**, the SDK POSTs
    `/api/v1/pull-requests/{requestId}/fulfill` and uploads its current rolling session log through the
    **same presigned-log path** used for crashes/bugs — off the main thread, fail-silent, with no
    gameplay impact. A player who has not granted telemetry consent **never** uploads. Pulled logs are
    retention-TTL'd like other telemetry and removed by a data-erasure request.
- **Breadcrumbs**: every Unity log line + manual `AddBreadcrumb(...)` entries land in a fixed
  50-entry ring buffer (zero allocations beyond the stored strings) and ride along on crashes
  and bug reports.
- **Offline-first + durable**: crashes/bugs are written to disk *before* the first upload
  attempt (write-ahead), uploads retry in-session with exponential backoff (2s→32s, 5
  attempts), poison payloads (HTTP 4xx) are dropped, and anything undelivered retries on the
  next launch from `Application.persistentDataPath/Tombstack` (server de-dups by ULID).
- **Consent-aware**: until `SetConsent(true)` (when required), *nothing* is captured or sent —
  including heartbeats.
- **Fail-silent**: the SDK never throws into game code; internal failures are logged once with
  a `[Tombstack]` prefix.
- Auto-fills `buildVersion` (`Application.version`) and `os`/`arch` (platform mapping), cached
  at Init so capture is safe from any thread.

## Public API
```csharp
Tombstack.Init(gameToken, endpoint, heartbeatIntervalSeconds = 60f);
Tombstack.SetUser(userId, steamId = null);
Tombstack.SetConsent(bool granted);
Tombstack.TrackEvent(name, Dictionary<string,string> props = null);
Tombstack.TrackMetric(name, double value, string unit = null);
Tombstack.SetSampleRate(name, float rate0to1);   // per-name keep-probability for events/metrics
Tombstack.AddBreadcrumb(message, BreadcrumbLevel level = Info, category = null);
Tombstack.ReportException(exception);
Tombstack.ReportBug(message, category = null);

// Diagnostics snapshot (support overlays / dev HUDs) — readonly struct, no steady-state alloc
TombstackDiagnostics diag = Tombstack.GetDiagnostics();
// diag.Initialized / ConsentGranted / QueuedOutbound / PersistedSidecar /
// diag.LastFlushAgeSeconds / Endpoint / MatchId / ServerId

// Multiplayer correlation context (dedicated servers)
Tombstack.SetMatchContext(serverId, matchId);
string matchId = Tombstack.StartMatch();   // server: flips role to "server", mints a match id
Tombstack.EndMatch();

// Server-triggered log pull (write-scoped server token)
Tombstack.RequestPlayerLogs(Tombstack.PullTarget target, targetValue, reason);
Tombstack.RequestPlayerLogs(target, reason);          // target = userId, or Tombstack.TARGET_ALL_ON_THIS_SERVER
Tombstack.OnAnomalousDisconnect(userId, reason);
```

## Conventions
AnkleBreaker C# standard (csharp-unity skill): private fields `_camelCase`, public
methods `PascalCase`, private methods `camelCase`, constants `UPPER_SNAKE_CASE`,
ScriptableObjects `{Name}SO`. Unity message methods stay PascalCase. The SDK keeps a thin
static facade (Sentry-style) rather than the Manager/HandlerData triad because it ships to
external studios and must not depend on AnkleBreaker's in-game framework. IL2CPP/AOT-safe:
no reflection-based serialization beyond JsonUtility, no dynamic codegen.

## Not yet
- Native crash core (Windows SEH / POSIX signals / Mach) + on-disk minidump upload —
  managed exceptions are covered today; native is the next track (`../native/`, plan written).
- Screenshot attachment on bug reports (server presign flow exists; SDK side pending).
- Breadcrumb `category` as a first-class wire field (currently folded into the message as a
  `[category] ` prefix — the ingest schema has no category field yet).

> Target engine confirmed: **Unity** (Mithrall, appId 2838890).
