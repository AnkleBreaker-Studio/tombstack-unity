# Changelog

All notable changes to `com.anklebreaker.tombstone`.

## [0.5.1] - 2026-06-10
### Fixed — SDK hardening (audit)
- **Empty bundleVersion no longer drops all telemetry**: `buildVersion` is now guarded
  (`TombstonePlatform.BuildVersion()` returns `"0.0.0"` when `Application.version` is empty)
  at every capture/cache site (Init and the heartbeat builder). Previously an empty
  bundleVersion serialized `buildVersion:""`, failing the server's `min(1)` schema and silently
  400ing every crash, event, and heartbeat.
- **Bounded outbound queue**: the in-memory upload queue is capped at 256 (mirrors the native
  worker). Beyond the cap the OLDEST non-crash payload is dropped; crash/bug (write-ahead)
  payloads are preserved (they're persisted to disk and retry next launch). Prevents unbounded
  growth when a game calls `TrackEvent` every frame while offline. Allocation-free below the cap.
- **Monotonic crash dedupe clock**: the per-signature 60s dedupe window now times from
  `Stopwatch.GetTimestamp()` (cached epoch) instead of `DateTime.UtcNow`, so a backward
  system-clock or NTP jump can no longer suppress a genuinely new crash.
- **Breadcrumb purge on consent revoke**: `SetConsent(false)` now clears the buffered breadcrumb
  ring (true→false transition), so a pre-revoke trail can't attach to a crash captured after
  consent is re-granted.
- **Uploader 408 parity**: the standalone CLI uploader (`tools/lib/upload-classify.mjs`) now
  retries HTTP 408 (keep) instead of dropping it, matching both SDKs.
- No wire shapes changed — `tests/unity-contract.test.ts` still pins the ingest contract.

## [0.5.0] - 2026-06-10
### Added — full autonomy: after Init, "most cases" need zero further integration
- **Wider auto-capture**: in addition to `Application.logMessageReceivedThreaded` (main +
  background threads), the SDK now hooks `TaskScheduler.UnobservedTaskException` (observed +
  reported, never escalated) and `AppDomain.CurrentDomain.UnhandledException`. All auto-on at
  Init; toggle via `TombstoneConfigSO._autoCaptureExceptions` (manual `ReportException` stays
  available regardless).
- **Crash dedupe**: identical signatures report at most once per 60s window; repeats become an
  Error breadcrumb counter (`crash suppressed (duplicate ×N …)`) instead of duplicate rows.
  Bounded 64-signature map. (Doubles between the Unity log path and the AppDomain hook also
  collapse server-side via the canonical stack signature.)
- **Rolling session log + crash-time upload**: every log line mirrors into a ~512 KB
  truncate-from-front file at `persistentDataPath/Tombstone/session.log` (buffered in memory,
  flushed off the main thread at most once per 5s, plus a synchronous flush on the crash path
  and on clean quit). Crash and bug-report payloads now send `"log": true`; after the 2xx the
  SDK PUTs the log (text/plain, no auth header) to the response's presigned `data.logUpload.url`
  through the existing queue + backoff. Log PUTs retry with backoff but are never persisted
  (presigns expire); a 4xx (incl. expired presign) drops fail-soft. Toggle: `_uploadLogs`.
- **Dirty-session detection ("crashed last run")**: Init writes
  `persistentDataPath/Tombstone/session.lock` (sessionId, ISO start, buildVersion/os/arch);
  `Application.quitting` deletes it. A marker found on the next Init ⇒ the previous session
  died hard (native crash, OOM kill, force quit). `session.log` rotates to
  `previous-session.log` at Init, BEFORE the new session starts writing. Design rule — no
  double-reporting: if the write-ahead queue restored a managed crash from that session, that
  retried report carries the previous log via its own presign and no synthetic report is sent;
  only when the queue held no crash does the SDK send a synthetic crash (signature
  `unclean-shutdown`, buildVersion/os/arch from the previous marker, `"log": true`) with the
  preserved log attached. The previous log uploads at most once per launch either way.
  Toggle: `_detectUncleanShutdown`.
- **Bug reports attach the session log** (`ReportBug` sends `"log": true`) — a player filing a
  report is exactly when you want their log.
- `TombstoneConfigSO`: `_autoCaptureExceptions` / `_uploadLogs` / `_detectUncleanShutdown`
  (all default ON, consent-gated like everything else: with `RequireConsent`, nothing is
  captured, mirrored, marked, or reported until `SetConsent(true)` — which also starts the
  deferred session tracking).
- New runtime files: `TombstoneSessionLog.cs` (rolling log) and `TombstoneSessionMarker.cs`
  (session.lock), both fail-silent with every file op wrapped.
### Changed
- `CrashPayload`/`BugPayload` gained a `bool log` field (JsonUtility serializes `false` when
  unset; the server treats false as no-log). Offline-queue records now persist a `requestLog`
  flag (old records load as false and simply skip the log upload). Contract pinned additively
  in `tests/unity-contract.test.ts` — all pre-0.5 wire shapes unchanged.
- Restored offline-queue items are tagged "from previous session" so a granted log presign
  uploads `previous-session.log`, never the current session's file.
### Known limits
- If the synthetic unclean-shutdown report itself fails to deliver and the app dies again,
  its next-launch retry uploads the (newer) previous-session log — a one-session mismatch,
  fail-soft by design.
- occurredAtIso on the synthetic report is the detection time (next launch), not the unknown
  moment of death; the previous session's start time lives only in the marker.

## [0.4.0] - 2026-06-10
### Added
- **Editor plugin** (`AnkleBreaker.Tombstone.Editor`, editor-only asmdef, full UI Toolkit):
  - **Mandatory account sign-in**: the plugin is inert until you connect your Tombstone
    account (`Window ▸ Tombstone ▸ Sign In`). The editor token is stored per-user in
    EditorPrefs only — it never lands in the project folder or version control.
  - **Hub** (`Window ▸ Tombstone ▸ Hub`): studio/game picker from `/api/editor/me`,
    one-click **Link this project** (mints a `tmb_` SDK token and writes endpoint + token
    into the `TombstoneConfig` asset, creating it at `Assets/Tombstone/Resources/` if
    missing), connection status card, and a **live dashboard** tab — crash-free %, 24h/7d
    counts, spike banner, top-10 status-colored signatures (click-through to the web
    dashboard), 30-day trend bars, manual refresh + 60s auto-refresh.
  - **Project Settings page** (`Edit ▸ Project Settings ▸ Tombstone`): endpoint override,
    heartbeat interval + consent defaults (written into the config asset), unlink, sign out.
  - **First-run prompt**: a gentle, dismissible once-per-session window pointing to the Hub
    while the plugin is unconfigured.
  - Forge-dark USS theme (`TombstoneTheme.uss`) matching the web dashboard's design tokens.
  - Typed async editor API client (`TombstoneEditorApi`, `ApiResult<T>`) over UnityWebRequest;
    fail-soft everywhere — errors surface in the UI, never as editor-loop exceptions.
  - Per-project binding persisted to `ProjectSettings/TombstoneSettings.asset`
    (ScriptableSingleton); `Documentation~/index.md` for offline docs.
### Changed
- Package now targets **Unity 6** (`"unity": "6000.0"`); declared the Quick Start sample in
  `package.json`.
- Runtime: added `Runtime/AssemblyInfo.cs` (`InternalsVisibleTo` for the editor assembly so
  it reuses the AOT-safe `TombstoneJson` writer). **No wire shapes changed** —
  `tests/unity-contract.test.ts` still pins the ingest contract.

## [0.3.0] - 2026-06-10
### Added
- `Tombstone.TrackEvent(name, Dictionary<string,string> props)` → `POST /api/v1/ingest/events`
  (analytics events & funnels). Hand-built JSON (`TombstoneJson`) — JsonUtility can't serialize
  dictionaries and absent optionals must be omitted, not sent as `""`.
- `Tombstone.AddBreadcrumb(message, BreadcrumbLevel, category)` — manual breadcrumbs with
  Info/Warning/Error levels; category folds into the message as a `[category] ` prefix.
- **In-session retry with exponential backoff** (2s→32s, 5 attempts, max 4 concurrent uploads)
  + **write-ahead persistence** for crashes/bugs (persisted *before* the first attempt, so a
  quit can no longer lose them) + poison-payload drop on HTTP 4xx + a 64-file queue cap.
- Heartbeats now carry `userId` (Sessions/Fleet attribution) and respect consent.
- XML doc comments on every public member; internal `TombstoneLog` single logger.
### Changed
- All public entry points are now fail-silent (any internal exception is caught + logged,
  never propagated to game code); `handleLog` is re-entrancy-safe (own warnings filtered
  from breadcrumbs).
- Breadcrumb ring buffer entries are preallocated and mutated in place — recording a
  breadcrumb no longer allocates an entry object per crumb.
- `SetUser` clamps to the server contract (userId ≤128, steamId ≤32); `ReportBug` clamps
  `category` to 32 chars (previously an over-long category 400'd the whole report).
- Heartbeat interval clamped to 15–600s; build/os/arch cached at Init (thread-safe capture).
- Wire DTOs extracted to `TombstonePayloads.cs` (shapes unchanged — pinned by
  `tests/unity-contract.test.ts`).

## [0.2.0] - 2026-06-07
### Added
- `TombstoneConfigSO` ScriptableObject (`Create ▸ Tombstone ▸ Config`) + zero-code
  auto-init from a `Resources/TombstoneConfig` asset (`[RuntimeInitializeOnLoadMethod]`).
- **Offline-first durable queue**: failed crash/bug uploads persist to
  `Application.persistentDataPath/Tombstone` and retry on the next launch.
- `Tombstone.ReportBug(message, category)` for in-game feedback.
- `Tombstone.Init` now accepts a `heartbeatIntervalSeconds` argument.
### Changed
- Aligned all C# to the AnkleBreaker naming standard (private methods `camelCase`,
  constants `UPPER_SNAKE_CASE`, endpoints as named constants).

## [0.1.0] - 2026-06-06
### Added
- Managed C# exception capture via `Application.logMessageReceivedThreaded` → uploaded
  to `POST /api/v1/ingest/crashes` with a stable SHA-256 signature (top frames normalized).
- Session heartbeats (`POST /api/v1/ingest/heartbeats`) every 60s for CCU.
- `Tombstone.Init(gameToken, endpoint)`, `Tombstone.SetUser(userId, steamId?)`,
  `Tombstone.ReportException(ex)`, `Tombstone.SetConsent(bool)`.
- Platform mapping (os/arch) to the ingestion contract whitelist.

### Not yet
- Native crash core (Windows SEH / POSIX signals / Mach) + offline upload-on-next-launch
  queue (managed exceptions are fire-and-forget for now).
- In-game bug-report capture.
