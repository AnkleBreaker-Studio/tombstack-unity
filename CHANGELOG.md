# Changelog

All notable changes to `com.anklebreaker.tombstack`.

## [0.9.3] - 2026-06-25
### Added — device / runtime context on crash + bug reports
- Crash and bug reports now carry a **device context** block: model, form factor, OS, CPU (+ core
  count), system RAM, GPU (+ graphics API), VRAM, display resolution + refresh rate, system language,
  Unity version, scripting backend (IL2CPP/Mono), and platform. It is snapshotted **once at `Init` on
  the main thread** (`SystemInfo`/`Screen` are main-thread-only), cached, and attached to every crash
  + bug report — including unclean-shutdown reports recovered on the next launch. Heartbeats stay lean
  for CCU and do **not** carry it. **No hardware-unique or personally-identifying id is collected.**
  Fail-soft: if capture throws, reporting continues without it. New file `TombstackDevice.cs`; new
  `DevicePayload`. (Wire: crash/bug bodies gain an optional `device` object; every field is optional
  and the server stores only the non-empty values.) The dashboard shows a **Device** panel on the
  crash-signature and bug detail pages.

## [0.9.2] - 2026-06-25
### Fixed — session-log + screenshot uploads (HTTP 403)
- The session-log and bug-report-screenshot uploads now use a **presigned S3 multipart POST**
  instead of a PUT. The server presigns a POST (to enforce an upload size cap via
  `content-length-range`); the SDK was still doing a PUT, so S3 rejected the upload with **HTTP 403**
  and the log/screenshot was dropped ("payload rejected with HTTP 403; dropping (log upload)"). The
  crash/bug report itself was unaffected — only the attached log/screenshot failed. The upload path
  now appends the presigned-POST policy fields (sent as a JsonUtility-parseable `formFields` array)
  and the file part last, with no Authorization header. Verified end-to-end: a crash's session log
  now uploads with a 2xx. (Internal wire: ingest responses' `logUpload`/`screenshotUpload` now carry
  `formFields`.)

## [0.9.1] - 2026-06-25
### Fixed — package now imports correctly via the UPM git URL
- The published package now includes Unity `.meta` files. Without them, a git-URL / tarball install
  had no asset GUIDs, so Unity silently ignored every script and asmdef in the package. Re-published
  with the full `.meta` set so the package imports and compiles on install.
- `Tombstack.cs`: qualified `Random` as `System.Random` (was an ambiguous reference between
  `UnityEngine.Random` and `System.Random` — compile error CS0104).
- `TombstackBehaviour.cs`: corrected a `PendingUpload.Post` call to the real parameter name
  `fromPreviousSession` (was `logFromPreviousSession` — compile error CS1739).
- Default endpoint is now `https://tombstack.com` (the canonical custom domain; the raw
  `*.cloudfront.net` origin is not directly reachable and returns 403 via host-based routing).

## [0.9.0] - 2026-06-11
### Added — fulfilment nonce (log-pull authenticity)
- The heartbeat ack now carries, per pending request, a `fulfillNonce` (string) and `nonceExpiry`
  (number) alongside `requestId`/`targetType`/`targetValue`. When a targeted, consenting client
  honours a pull, the fulfil POST body now echoes `nonce` + `nonceExpiry` (and the `sessionId` it
  heartbeated with) so the server can authenticate the fulfilment and reject a stale nonce. Wire
  body: `{ userId?, sessionId, matchId?, serverId?, nonce, nonceExpiry }`. `PullRequestDto` /
  `PullFulfillPayload` updated (additive — older servers leave the nonce fields empty/`0`).
### Added — request signing (HMAC, §S3)
- Every **ingest** POST (crashes, bug-reports, events, heartbeats, events:batch, metrics:batch — NOT
  the editor/pull endpoints) now carries an `X-Tombstack-Signature` header:
  `t=<unixSec>,v1=<hex HMAC_SHA256(ingestKey, t + "." + rawBody)>`, keyed with the SDK token already
  sent as the Bearer ingest key (`System.Security.Cryptography.HMACSHA256`). Computed at send time
  (off the main game-frame path); the HMAC primitive + hex buffer are reused across requests.
  **Fail-silent** — if signing throws, the request is sent unsigned (the server accepts unsigned
  during rollout). New file `TombstackSign.cs`.
### Added — auto network-RTT metric + per-name sampling (§K1)
- After each successful **ingest** POST the SDK measures the round trip with `Stopwatch` and emits a
  `tombstack.rtt_ms` metric (unit `ms`) via the normal `TrackMetric` batch path. Opt-in via the
  config flag `_autoRttMetric` (default ON). Recursion-guarded — the metrics:batch send is never
  measured, so the RTT metric can't measure its own batch.
- **`Tombstack.SetSampleRate(string name, float rate0to1)`** — per-name keep-probability [0,1]
  (deterministic/random sampler) applied **before** an event/metric is buffered, so a high-frequency
  name can't saturate the batch. Bounded map (128 names); unset names always keep. Fail-silent.
### Added — auto scene breadcrumbs (§K2)
- Hooks `SceneManager.sceneLoaded` and `activeSceneChanged` at Init to auto-`AddBreadcrumb`
  (`"scene loaded: {name}"` / `"active scene changed: {from} -> {to}"`, category `scene`). Config
  flag `_autoSceneBreadcrumbs` (default ON); unhooked on shutdown. Zero alloc beyond the breadcrumb string.
### Added — diagnostics API + editor live-tail (§K3)
- **`Tombstack.GetDiagnostics()`** → `TombstackDiagnostics` (readonly struct): `Initialized`,
  `ConsentGranted`, `QueuedOutbound`, `PersistedSidecar`, `LastFlushAgeSeconds`, `Endpoint`,
  `MatchId`, `ServerId`. No steady-state allocation beyond the returned struct.
- **Live Tail window** (`Window ▸ Tombstack ▸ Live Tail`, UI Toolkit, forge-dark USS, editor-only):
  in Play mode it subscribes to a new lightweight `internal static event Action<string>
  Tombstack.OnTelemetry` (raised fail-silently on each captured crumb/event/metric/crash) and shows
  a bounded scrolling list. The event is null (no subscriber) in shipped builds, so the hot path
  stays allocation-free.
### Changed
- `TombstackConfigSO` gains `_autoRttMetric` and `_autoSceneBreadcrumbs` (both default ON). Wire
  shapes are additive — `tests/unity-contract.test.ts` stays authoritative for the ingest contract.

## [0.8.1] - 2026-06-11
### Fixed
- **Fail-silent flush guards (§15)** — wrapped the age-trigger flush in `Update()` and the
  `OnApplicationPause`/`OnApplicationQuit` flush handlers in try/catch, matching `TrackEvent`/
  `TrackMetric`. An exception escaping `Update` was previously logged per-frame by Unity and
  re-captured by the log hook, risking a breadcrumb feedback loop and breaching the fail-silent
  contract. Failures now funnel through the `[Tombstack]`-prefixed internal logger (filtered from
  breadcrumbs); behaviour is otherwise unchanged.
### Changed
- **G17 numeric round-trip** — `TombstackJson.AppendNumberField` now formats doubles with `"G17"`
  (Microsoft's recommended round-trip specifier) instead of `"R"`, keeping `CultureInfo.InvariantCulture`.
  Still emits a valid finite JSON number; the wire shape and contract test are unchanged.

## [0.8.0] - 2026-06-11
### Added — log-pull control plane (server-triggered session-log retrieval)
- **`Tombstack.RequestPlayerLogs(PullTarget target, string targetValue, string reason)`** — server-side
  call (dedicated game server) to queue a pull of a player's / session's / match's / whole-server's
  session log. Requires a **WRITE-scoped** server token; an ingest-only client token is rejected
  server-side (403) and surfaced via `[Tombstack]` log, never thrown. Fail-silent; gated on `Init`
  only (NOT consent — consent is enforced on the client honouring side). Wire body:
  `{ "targetType", "targetValue", "reason" }` (`targetType` ∈ `userId|sessionId|matchId|server`;
  `targetValue` clamped to 128, `reason` clamped to 280 to match the server contract).
- **`Tombstack.RequestPlayerLogs(string target, string reason)`** — convenience overload: `target` is a
  player userId, or the sentinel `Tombstack.TARGET_ALL_ON_THIS_SERVER` ("all-on-this-server") to pull
  every player on this dedicated server (resolved to the current serverId).
- **`Tombstack.OnAnomalousDisconnect(string userId, string reason)`** — convenience auto-pull after a
  weird disconnect (defaults `reason` to "anomalous disconnect"). Targets the player's userId.
- **Automatic, transparent client honouring** — the heartbeat 202 ack now carries
  `data.pendingRequests`. The heartbeat loop parses the ack and, for each request that targets THIS
  client (by `userId`/`sessionId`/`matchId`/`serverId`) **and only while consent is granted**, POSTs
  `/api/v1/pull-requests/{requestId}/fulfill` with its asserted identity
  (`{ userId, sessionId, matchId, serverId }`) and reuses the existing
  `Enqueue(requestLog:true)` → `scheduleLogUpload` path: the fulfil 2xx's `data.logUpload` presign is
  chased exactly like a crash/bug, PUTting the rolling session log **off the main thread** (≤512 KB on
  the ThreadPool). A non-consented or non-targeted client uploads **nothing**.
- **Perf (§15)**: ack handling adds no per-frame allocation — heartbeats run on an interval, and an ack
  with an empty `pendingRequests` list short-circuits on a single ordinal substring check **without
  deserializing** (the empty list carries no `requestId`). All upload work stays off the calling thread;
  fail-silent throughout; a registry/parse failure degrades to honouring nothing, never affecting gameplay.
### Added — DTOs
- `HeartbeatAck` / `HeartbeatAckData` / `PullRequestDto` (parse-only ack) and `PullFulfillPayload`
  (fulfil POST body). `tests/unity-contract.test.ts` pins the create + fulfil byte shapes against the
  server's pull-request schemas (additive, backward-compatible — older SDKs ignored the ack body).

## [0.7.0] - 2026-06-11
### Added — numeric metrics + client-side event/metric batching
- **`Tombstack.TrackMetric(name, value, unit = null)`** — record a numeric sample (tickrate, RTT,
  CCU, …). Fail-silent; non-finite values (NaN/Infinity) are dropped, never shipped. Each metric is
  stamped with the cached correlation spine (`role`/`serverId`/`matchId`/`userId`/`sessionId`) plus
  `buildVersion`/`os`/`arch` and its own `occurredAtIso`. Hand-built JSON (`TombstackJson`,
  AOT-safe): numbers are unquoted + invariant-culture round-trip ("R") formatted; empty ids are
  omitted. Wire item: `{ name, value, unit?, occurredAtIso, buildVersion, os, arch, role,
  serverId?, matchId?, userId?, sessionId? }`.
- **Event + metric batching (§16)** — `TrackEvent` and `TrackMetric` no longer POST once per call.
  Items accumulate into a **bounded, preallocated, drop-oldest** ring (`TombstackBatch`, cap 256)
  and flush as a batch envelope `{ "sentAtIso", "items": [...] }` to
  `POST /api/v1/ingest/events:batch` / `…/metrics:batch`. Each item keeps its OWN `occurredAtIso`
  (when it happened); `sentAtIso` is added only at flush time. Flush triggers: **count ≥ 50**,
  **age ≥ 10s** (low-volume games still report), **near-full**, **`OnApplicationPause`/
  `OnApplicationQuit`**, and a **pre-crash flush** (before the possibly-fatal write-ahead crash
  upload). Sends reuse the existing outbound queue, in-session exponential backoff, and
  `PersistOnFailure` durability — the same class the single-event path used.
- **Perf budget (§15)**: the batch buffer's backing array is allocated once and never grows; adding
  an item reuses a ring slot (no per-frame/per-item allocation); the age check is a cheap locked
  read; an envelope string is built only on the rare flush; all sending stays off the calling
  thread; the buffer is bounded with drop-oldest; fail-silent throughout.
### Changed
- `TrackEvent` now stamps correlation via the shared `appendCorrelation` helper (same keys/omit-empty
  rules as before) and routes through the event batch buffer instead of a per-event POST.
- New runtime file `TombstackBatch.cs` (bounded batch buffer); `TombstackJson` gains `AppendNumberField`
  (unquoted, invariant-culture numbers). Wire shapes are additive — `tests/unity-contract.test.ts`
  pins the batch envelope + metric item; crashes/bug-reports/heartbeats remain **un-batched**
  (forensic/time-sensitive, sent individually).

## [0.6.0] - 2026-06-11
### Added — multiplayer correlation: server ↔ session ↔ match ↔ player linking
- **Match-context API**: three new fail-silent static calls —
  - `Tombstack.SetMatchContext(serverId, matchId)` tags subsequent telemetry with a dedicated
    server + match (both clamped to 128 chars; null/"" clears).
  - `Tombstack.StartMatch()` flips the emitter role to `"server"`, mints + returns a fresh match
    id (GUID "N") to broadcast to clients.
  - `Tombstack.EndMatch()` clears the match id (role + server id are kept between matches).
- **Correlation stamping on every payload**: crashes, bug reports, analytics events, and session
  heartbeats now carry `role` (`"client"` default / `"server"`), `serverId`, `matchId`, and
  `sessionId`. Crash/bug/heartbeat stamp via `JsonUtility` (empty ids serialize as `""`, cleaned
  to `undefined` server-side via `cleanOptionalId`, exactly like `userId`); the hand-built event
  JSON always emits the enum-valid `role` and omits empty ids. The synthetic unclean-shutdown
  crash carries the **dead** session's `sessionId` (from the session marker).
- Wire shape is additive + backward-compatible — `tests/unity-contract.test.ts` pins the
  `role`/`serverId`/`matchId`/`sessionId` keys against the server's ingest schemas.

## [0.5.1] - 2026-06-10
### Fixed — SDK hardening (audit)
- **Empty bundleVersion no longer drops all telemetry**: `buildVersion` is now guarded
  (`TombstackPlatform.BuildVersion()` returns `"0.0.0"` when `Application.version` is empty)
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
  Init; toggle via `TombstackConfigSO._autoCaptureExceptions` (manual `ReportException` stays
  available regardless).
- **Crash dedupe**: identical signatures report at most once per 60s window; repeats become an
  Error breadcrumb counter (`crash suppressed (duplicate ×N …)`) instead of duplicate rows.
  Bounded 64-signature map. (Doubles between the Unity log path and the AppDomain hook also
  collapse server-side via the canonical stack signature.)
- **Rolling session log + crash-time upload**: every log line mirrors into a ~512 KB
  truncate-from-front file at `persistentDataPath/Tombstack/session.log` (buffered in memory,
  flushed off the main thread at most once per 5s, plus a synchronous flush on the crash path
  and on clean quit). Crash and bug-report payloads now send `"log": true`; after the 2xx the
  SDK PUTs the log (text/plain, no auth header) to the response's presigned `data.logUpload.url`
  through the existing queue + backoff. Log PUTs retry with backoff but are never persisted
  (presigns expire); a 4xx (incl. expired presign) drops fail-soft. Toggle: `_uploadLogs`.
- **Dirty-session detection ("crashed last run")**: Init writes
  `persistentDataPath/Tombstack/session.lock` (sessionId, ISO start, buildVersion/os/arch);
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
- `TombstackConfigSO`: `_autoCaptureExceptions` / `_uploadLogs` / `_detectUncleanShutdown`
  (all default ON, consent-gated like everything else: with `RequireConsent`, nothing is
  captured, mirrored, marked, or reported until `SetConsent(true)` — which also starts the
  deferred session tracking).
- New runtime files: `TombstackSessionLog.cs` (rolling log) and `TombstackSessionMarker.cs`
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
- **Editor plugin** (`AnkleBreaker.Tombstack.Editor`, editor-only asmdef, full UI Toolkit):
  - **Mandatory account sign-in**: the plugin is inert until you connect your Tombstack
    account (`Window ▸ Tombstack ▸ Sign In`). The editor token is stored per-user in
    EditorPrefs only — it never lands in the project folder or version control.
  - **Hub** (`Window ▸ Tombstack ▸ Hub`): studio/game picker from `/api/editor/me`,
    one-click **Link this project** (mints a `tmb_` SDK token and writes endpoint + token
    into the `TombstackConfig` asset, creating it at `Assets/Tombstack/Resources/` if
    missing), connection status card, and a **live dashboard** tab — crash-free %, 24h/7d
    counts, spike banner, top-10 status-colored signatures (click-through to the web
    dashboard), 30-day trend bars, manual refresh + 60s auto-refresh.
  - **Project Settings page** (`Edit ▸ Project Settings ▸ Tombstack`): endpoint override,
    heartbeat interval + consent defaults (written into the config asset), unlink, sign out.
  - **First-run prompt**: a gentle, dismissible once-per-session window pointing to the Hub
    while the plugin is unconfigured.
  - Forge-dark USS theme (`TombstackTheme.uss`) matching the web dashboard's design tokens.
  - Typed async editor API client (`TombstackEditorApi`, `ApiResult<T>`) over UnityWebRequest;
    fail-soft everywhere — errors surface in the UI, never as editor-loop exceptions.
  - Per-project binding persisted to `ProjectSettings/TombstackSettings.asset`
    (ScriptableSingleton); `Documentation~/index.md` for offline docs.
### Changed
- Package now targets **Unity 6** (`"unity": "6000.0"`); declared the Quick Start sample in
  `package.json`.
- Runtime: added `Runtime/AssemblyInfo.cs` (`InternalsVisibleTo` for the editor assembly so
  it reuses the AOT-safe `TombstackJson` writer). **No wire shapes changed** —
  `tests/unity-contract.test.ts` still pins the ingest contract.

## [0.3.0] - 2026-06-10
### Added
- `Tombstack.TrackEvent(name, Dictionary<string,string> props)` → `POST /api/v1/ingest/events`
  (analytics events & funnels). Hand-built JSON (`TombstackJson`) — JsonUtility can't serialize
  dictionaries and absent optionals must be omitted, not sent as `""`.
- `Tombstack.AddBreadcrumb(message, BreadcrumbLevel, category)` — manual breadcrumbs with
  Info/Warning/Error levels; category folds into the message as a `[category] ` prefix.
- **In-session retry with exponential backoff** (2s→32s, 5 attempts, max 4 concurrent uploads)
  + **write-ahead persistence** for crashes/bugs (persisted *before* the first attempt, so a
  quit can no longer lose them) + poison-payload drop on HTTP 4xx + a 64-file queue cap.
- Heartbeats now carry `userId` (Sessions/Fleet attribution) and respect consent.
- XML doc comments on every public member; internal `TombstackLog` single logger.
### Changed
- All public entry points are now fail-silent (any internal exception is caught + logged,
  never propagated to game code); `handleLog` is re-entrancy-safe (own warnings filtered
  from breadcrumbs).
- Breadcrumb ring buffer entries are preallocated and mutated in place — recording a
  breadcrumb no longer allocates an entry object per crumb.
- `SetUser` clamps to the server contract (userId ≤128, steamId ≤32); `ReportBug` clamps
  `category` to 32 chars (previously an over-long category 400'd the whole report).
- Heartbeat interval clamped to 15–600s; build/os/arch cached at Init (thread-safe capture).
- Wire DTOs extracted to `TombstackPayloads.cs` (shapes unchanged — pinned by
  `tests/unity-contract.test.ts`).

## [0.2.0] - 2026-06-07
### Added
- `TombstackConfigSO` ScriptableObject (`Create ▸ Tombstack ▸ Config`) + zero-code
  auto-init from a `Resources/TombstackConfig` asset (`[RuntimeInitializeOnLoadMethod]`).
- **Offline-first durable queue**: failed crash/bug uploads persist to
  `Application.persistentDataPath/Tombstack` and retry on the next launch.
- `Tombstack.ReportBug(message, category)` for in-game feedback.
- `Tombstack.Init` now accepts a `heartbeatIntervalSeconds` argument.
### Changed
- Aligned all C# to the AnkleBreaker naming standard (private methods `camelCase`,
  constants `UPPER_SNAKE_CASE`, endpoints as named constants).

## [0.1.0] - 2026-06-06
### Added
- Managed C# exception capture via `Application.logMessageReceivedThreaded` → uploaded
  to `POST /api/v1/ingest/crashes` with a stable SHA-256 signature (top frames normalized).
- Session heartbeats (`POST /api/v1/ingest/heartbeats`) every 60s for CCU.
- `Tombstack.Init(gameToken, endpoint)`, `Tombstack.SetUser(userId, steamId?)`,
  `Tombstack.ReportException(ex)`, `Tombstack.SetConsent(bool)`.
- Platform mapping (os/arch) to the ingestion contract whitelist.

### Not yet
- Native crash core (Windows SEH / POSIX signals / Mach) + offline upload-on-next-launch
  queue (managed exceptions are fire-and-forget for now).
- In-game bug-report capture.
