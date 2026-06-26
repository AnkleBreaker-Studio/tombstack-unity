using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace AnkleBreaker.Tombstack
{
    /// <summary>Severity of a manually recorded breadcrumb (see <see cref="Tombstack.AddBreadcrumb"/>).</summary>
    public enum BreadcrumbLevel
    {
        /// <summary>Informational marker (default).</summary>
        Info,
        /// <summary>Something unexpected but recoverable.</summary>
        Warning,
        /// <summary>A handled error worth seeing in the trail.</summary>
        Error,
    }

    /// <summary>
    /// Immutable snapshot of SDK state returned by <see cref="Tombstack.GetDiagnostics"/> (§K3) — for
    /// support overlays / dev HUDs. A value type: reading it allocates nothing beyond the struct itself.
    /// </summary>
    public readonly struct TombstackDiagnostics
    {
        /// <summary>True once <see cref="Tombstack.Init"/> has completed.</summary>
        public readonly bool Initialized;
        /// <summary>Current telemetry consent state.</summary>
        public readonly bool ConsentGranted;
        /// <summary>Payloads waiting in the in-memory outbound queue.</summary>
        public readonly int QueuedOutbound;
        /// <summary>Write-ahead payloads persisted to the offline sidecar directory.</summary>
        public readonly int PersistedSidecar;
        /// <summary>Seconds since the last event/metric batch flush, or -1 when none yet this launch.</summary>
        public readonly double LastFlushAgeSeconds;
        /// <summary>The resolved ingest endpoint ("" before Init/Bootstrap).</summary>
        public readonly string Endpoint;
        /// <summary>Current match id ("" when unset).</summary>
        public readonly string MatchId;
        /// <summary>Current server id ("" when unset).</summary>
        public readonly string ServerId;

        /// <summary>Construct a diagnostics snapshot (called by <see cref="Tombstack.GetDiagnostics"/>).</summary>
        public TombstackDiagnostics(
            bool initialized, bool consentGranted, int queuedOutbound, int persistedSidecar,
            double lastFlushAgeSeconds, string endpoint, string matchId, string serverId)
        {
            Initialized = initialized;
            ConsentGranted = consentGranted;
            QueuedOutbound = queuedOutbound;
            PersistedSidecar = persistedSidecar;
            LastFlushAgeSeconds = lastFlushAgeSeconds;
            Endpoint = endpoint;
            MatchId = matchId;
            ServerId = serverId;
        }
    }

    /// <summary>
    /// Public entry point for the Tombstack Unity SDK. After Init (or zero-code auto-init) it
    /// runs autonomously: captures managed C# exceptions (Unity log, unobserved Tasks,
    /// AppDomain — deduped per signature), session heartbeats, breadcrumbs, a rolling session
    /// log uploaded with crashes and bug reports, and detects unclean shutdowns (hard crash /
    /// OOM kill / force quit) on the next launch. Analytics events and player bug reports are
    /// one-line calls. Everything uploads to the studio's ingestion endpoint with a per-game
    /// SDK token (tmb_...). The native crash core (SEH / signals / Mach) reports through the
    /// same endpoints once integrated.
    ///
    /// This is a standalone UPM package consumed by external studios, so it exposes a thin
    /// static facade (Sentry-style) rather than the AnkleBreaker Manager/HandlerData triad —
    /// but it follows the AnkleBreaker C# naming standard throughout.
    ///
    /// Fail-silent guarantee: no public member ever throws into game code; internal failures
    /// are swallowed and logged once through <see cref="TombstackLog"/>.
    /// </summary>
    public static class Tombstack
    {
        private const int MAX_STACK_HINT = 512;
        private const int MAX_STACK_TRACE = 8192;
        private const int MAX_BUG_MESSAGE = 4000;
        private const int MAX_CATEGORY = 32;
        private const int MAX_USER_ID = 128;
        private const int MAX_STEAM_ID = 32;
        // A pull-request reason is clamped to the server contract's MAX_PULL_REASON (280) — NOT
        // MAX_BUG_MESSAGE: a longer reason would be rejected (400) and dropped server-side.
        private const int MAX_PULL_REASON = 280;
        // Correlation ids (serverId / matchId) clamp to the server contract's max(128).
        private const int MAX_CONTEXT_ID = 128;
        private const int SIGNATURE_FRAMES = 8;
        private const int SIGNATURE_HEX_LENGTH = 32;
        private const int MAX_BREADCRUMBS = 50;
        private const int MAX_BREADCRUMB_MESSAGE = 512;
        private const int MAX_EVENT_NAME = 64;
        private const int MAX_METRIC_NAME = 64;
        private const int MAX_METRIC_UNIT = 16;
        private const int MAX_EVENT_ATTRIBUTES = 32;
        private const int MAX_EVENT_ATTRIBUTE_KEY = 64;
        private const int MAX_EVENT_ATTRIBUTE_VALUE = 512;
        private const int EVENT_JSON_CAPACITY = 256;
        private const int CRASH_DEDUPE_WINDOW_SECONDS = 60;
        private const int MAX_TRACKED_SIGNATURES = 64;
        // §K1: bound on the per-name sample-rate map so a misbehaving game can't grow it unbounded.
        private const int MAX_SAMPLE_RATES = 128;

        // Signature is kept stable so existing reports keep grouping. The hint is now accurate for the
        // ONLY case we still report: the app died while foreground-active (a clean background kill, a
        // user close, and an Editor Play Mode stop are classified out and never reach this report).
        private const string UNCLEAN_SIGNATURE = "unclean-shutdown";
        private const string UNCLEAN_STACK_HINT =
            "App terminated unexpectedly while running — no clean shutdown (likely a native crash or out-of-memory)";

        // Internal (not private): TombstackBehaviour identifies restored crash records by path.
        internal const string CRASHES_PATH = "/api/v1/ingest/crashes";
        private const string BUG_REPORTS_PATH = "/api/v1/ingest/bug-reports";
        private const string EVENTS_PATH = "/api/v1/ingest/events";
        private const string PULL_REQUESTS_PATH = "/api/v1/pull-requests";
        /// <summary>Sentinel for the <see cref="RequestPlayerLogs(string,string)"/> convenience
        /// overload: pull every player connected to THIS dedicated server (uses the current serverId).</summary>
        public const string TARGET_ALL_ON_THIS_SERVER = "all-on-this-server";

        private static volatile bool _initialized;
        private static volatile bool _consent = true;
        // v0.5 autonomy toggles — default ON; overridden from TombstackConfigSO at auto-init.
        private static bool _autoCaptureExceptions = true;
        private static bool _uploadLogs = true;
        private static bool _detectUncleanShutdown = true;
        // Screenshot auto-capture (see TombstackScreenshot). Bug capture defaults ON; exception
        // capture is opt-in. Overridden from TombstackConfigSO at auto-init.
        private static bool _captureScreenshotOnBugReport = true;
        private static bool _captureScreenshotOnException = false;
        private static int _screenshotMaxDimension = 1280;
        private static float _exceptionScreenshotThrottleSeconds = 10f;
        // v0.9 autonomy toggles — default ON; overridden from TombstackConfigSO at auto-init.
        private static bool _autoRttMetric = true;        // §K1: auto tombstack.rtt_ms after each ingest POST
        private static bool _autoSceneBreadcrumbs = true; // §K2: auto breadcrumb on scene load / active change
        private static string _endpoint;
        private static string _gameToken;
        private static string _sessionId;
        private static string _userId;
        private static string _steamId;
        // Correlation context: stamped on every payload so server<->session<->match<->player
        // linking is exact. Defaults make a plain client send role="client" + empty ids ("" is
        // cleaned to undefined server-side, like userId). Set via SetMatchContext/StartMatch.
        private static string _role = "client";
        private static string _serverId = "";
        private static string _matchId = "";

        // Dirty-session state captured at Init, consumed when capture first becomes allowed.
        private static SessionMarkerData _previousMarker;
        private static bool _hadPreviousLog;
        private static bool _sessionTrackingStarted;
        private static readonly object _sessionTrackingLock = new object();

        // Per-signature dedupe: same crash signature reports at most once per window; repeats
        // become a counter breadcrumb instead of another report. Bounded at 64 signatures.
        private static readonly Dictionary<string, SignatureWindow> _recentSignatures =
            new Dictionary<string, SignatureWindow>(StringComparer.Ordinal);
        private static readonly object _dedupeLock = new object();

        // Dedupe timing uses a monotonic clock, not wall time: a backward system-clock or NTP
        // jump must never suppress a genuinely new crash. Stopwatch ticks never run backward.
        private static readonly long _dedupeEpochTicks = System.Diagnostics.Stopwatch.GetTimestamp();

        // Device/build context is cached on the main thread at Init: handleLog can run on any
        // thread (logMessageReceivedThreaded) and Unity APIs are not safe off the main thread.
        private static string _buildVersion = "unknown";
        private static string _os = "other";
        private static string _arch = "other";
        // Cached at Init (main thread): whether this run is the Unity Editor. A Play Mode stop leaves a
        // marker like any unclean exit, but it is NOT a player crash — the reporter suppresses it.
        private static bool _isEditor;
        // Static device/runtime context, snapshotted once at Init on the main thread (SystemInfo/Screen
        // are main-thread-only) and attached to every crash/bug report. Null if capture failed.
        private static DevicePayload _device;

        // Ring buffer of recent log lines, attached to crashes/bugs as the "breadcrumb" trail.
        // Entries are preallocated once and mutated in place: recording a breadcrumb allocates
        // nothing beyond the stored strings. logMessageReceivedThreaded fires off-thread, so
        // every access is locked.
        private static readonly Breadcrumb[] _breadcrumbs = createRing();
        private static int _breadcrumbHead;
        private static int _breadcrumbCount;
        private static readonly object _breadcrumbLock = new object();

        // §K1 per-name sampling: a bounded map of name → keep-probability [0,1], applied before an
        // event/metric is buffered so a high-frequency name can't saturate the batch. Guarded by a
        // lock (TrackEvent/TrackMetric can be called off the main thread). The RNG is thread-static
        // so the sampler never contends or shares mutable state across threads.
        private static readonly Dictionary<string, float> _sampleRates =
            new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly object _sampleLock = new object();
        [ThreadStatic] private static System.Random _sampleRng;

        // §K3 editor live-tail: raised fail-silently on each captured crumb/event/metric/crash so the
        // editor-only Live Tail window can subscribe. Null (no subscriber) in shipped builds → the
        // guard is a single reference read and nothing is formatted/allocated on the hot path.
        internal static event Action<string> OnTelemetry;

        /// <summary>True while capture + upload is permitted (Init done and consent granted).</summary>
        internal static bool CaptureAllowed => _initialized && _consent;

        /// <summary>§K1: whether the auto round-trip metric (tombstack.rtt_ms) is enabled (read by the upload host).</summary>
        internal static bool AutoRttMetricEnabled => _autoRttMetric;

        /// <summary>Current user id ("" or null when anonymous) for heartbeat attribution.</summary>
        internal static string CurrentUserId => _userId;

        /// <summary>Current emitter role ("client" | "server") for heartbeat correlation.</summary>
        internal static string CurrentRole => _role;

        /// <summary>Current server id ("" when unset) for heartbeat correlation.</summary>
        internal static string CurrentServerId => _serverId;

        /// <summary>Current match id ("" when unset) for heartbeat correlation.</summary>
        internal static string CurrentMatchId => _matchId;

        /// <summary>Auto-init from a <c>Resources/TombstackConfig</c> asset, if present and enabled.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void autoInit()
        {
            try
            {
                var config = Resources.Load<TombstackConfigSO>("TombstackConfig");
                if (config == null || !config.AutoInitOnLoad) return;
                // When consent is required, start disabled until the game calls SetConsent(true).
                _consent = !config.RequireConsent;
                _autoCaptureExceptions = config.AutoCaptureExceptions;
                _uploadLogs = config.UploadLogs;
                _detectUncleanShutdown = config.DetectUncleanShutdown;
                _captureScreenshotOnBugReport = config.CaptureScreenshotOnBugReport;
                _captureScreenshotOnException = config.CaptureScreenshotOnException;
                _screenshotMaxDimension = config.ScreenshotMaxDimension;
                _exceptionScreenshotThrottleSeconds = config.ExceptionScreenshotThrottleSeconds;
                _autoRttMetric = config.AutoRttMetric;
                _autoSceneBreadcrumbs = config.AutoSceneBreadcrumbs;
                Init(config.GameToken, config.Endpoint, config.HeartbeatIntervalSeconds);
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"auto-init failed: {e.Message}");
            }
        }

        /// <summary>
        /// Initialize the SDK. Idempotent — the first successful call wins. Starts exception
        /// capture, the session heartbeat loop, and the durable upload queue.
        /// </summary>
        /// <param name="gameToken">Per-game SDK token (tmb_...). Treat as a build secret.</param>
        /// <param name="endpoint">Tombstack base URL, e.g. https://your-tenant.example.com</param>
        /// <param name="heartbeatIntervalSeconds">Seconds between session heartbeats (clamped to a sane range).</param>
        public static void Init(string gameToken, string endpoint, float heartbeatIntervalSeconds = 60f)
        {
            try
            {
                if (_initialized) return;
                if (string.IsNullOrEmpty(gameToken) || string.IsNullOrEmpty(endpoint))
                {
                    TombstackLog.Warn("Init skipped: missing token or endpoint.");
                    return;
                }
                _gameToken = gameToken;
                _endpoint = endpoint.TrimEnd('/');
                _buildVersion = TombstackPlatform.BuildVersion();
                _os = TombstackPlatform.Os();
                _arch = TombstackPlatform.Arch();
                // Snapshot device/runtime context on the main thread (SystemInfo/Screen are main-thread
                // only); cached + attached to crash/bug reports. Fail-soft — never blocks Init.
                try { _device = TombstackDevice.Capture(); }
                catch (Exception e) { TombstackLog.Warn($"device capture failed: {e.Message}"); }
                _isEditor = Application.isEditor;
                _sessionId = newId();
                _initialized = true;

                // Main-thread-only values (persistentDataPath) are cached here, like
                // version/os/arch above — everything after this point may run off-thread.
                var persistentDataPath = Application.persistentDataPath;
                TombstackSessionLog.Configure(persistentDataPath);
                TombstackSessionMarker.Configure(persistentDataPath);

                // Local file bookkeeping (not capture, so not consent-gated): preserve the
                // previous run's log for next-launch upload and read its dirty-session marker.
                // REPORTING from these only happens once capture is allowed (session tracking).
                _hadPreviousLog = TombstackSessionLog.RotateForNewSession();
                if (_detectUncleanShutdown) _previousMarker = TombstackSessionMarker.TakePrevious();
                else TombstackSessionMarker.Delete(); // never leave a stale marker behind

                // Threaded so exceptions on background threads are captured too.
                Application.logMessageReceivedThreaded += handleLog;
                if (_autoCaptureExceptions) hookBackgroundExceptionSources();
                Application.quitting += onQuitting;

                TombstackBehaviour.Bootstrap(_endpoint, _gameToken, _sessionId, heartbeatIntervalSeconds);
                if (_autoSceneBreadcrumbs) hookSceneBreadcrumbs();
                if (CaptureAllowed) startSessionTracking();
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"Init failed: {e.Message}");
            }
        }

        /// <summary>
        /// Associate subsequent reports and heartbeats with a player (and optional Steam64 id).
        /// Values are clamped to the server contract (128 / 32 chars). Pass null to clear.
        /// </summary>
        /// <param name="userId">Your stable player identifier.</param>
        /// <param name="steamId">Optional Steam64 id (e.g. "7656119...").</param>
        public static void SetUser(string userId, string steamId = null)
        {
            _userId = truncate(userId, MAX_USER_ID);
            _steamId = truncate(steamId, MAX_STEAM_ID);
        }

        /// <summary>
        /// Tag subsequent telemetry with the server + match it belongs to, so crashes, events,
        /// bug reports, and heartbeats correlate to a specific dedicated server and match.
        /// Both ids are clamped to the server contract (128 chars); pass null/"" to clear one.
        /// Does not change the role — call <see cref="StartMatch"/> on a dedicated server.
        /// </summary>
        /// <param name="serverId">Your stable dedicated-server identifier (e.g. "srv-eu-1").</param>
        /// <param name="matchId">The current match/session identifier (e.g. "m-42").</param>
        public static void SetMatchContext(string serverId, string matchId)
        {
            try
            {
                _serverId = truncate(serverId, MAX_CONTEXT_ID) ?? "";
                _matchId = truncate(matchId, MAX_CONTEXT_ID) ?? "";
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"SetMatchContext failed: {e.Message}");
            }
        }

        /// <summary>
        /// Begin a match on a dedicated server: flips the emitter role to "server" and mints a
        /// fresh match id (returned so the caller can broadcast it to clients via
        /// <see cref="SetMatchContext"/>). All telemetry from now until <see cref="EndMatch"/>
        /// carries this match id and role.
        /// </summary>
        /// <returns>The newly minted match id (a GUID "N"); "" only if minting failed.</returns>
        public static string StartMatch()
        {
            try
            {
                _role = "server";
                _matchId = newId();
                return _matchId;
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"StartMatch failed: {e.Message}");
                return _matchId;
            }
        }

        /// <summary>Clear the current match id (the role and server id are left intact — a
        /// dedicated server keeps its identity between matches). Telemetry sent after this no
        /// longer correlates to a match until the next <see cref="StartMatch"/>/<see cref="SetMatchContext"/>.</summary>
        public static void EndMatch()
        {
            try
            {
                _matchId = "";
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"EndMatch failed: {e.Message}");
            }
        }

        /// <summary>What a server-side log pull targets (maps 1:1 to the wire <c>targetType</c>).</summary>
        public enum PullTarget
        {
            /// <summary>A single player id (<c>SetUser</c> userId).</summary>
            UserId,
            /// <summary>A single session id.</summary>
            SessionId,
            /// <summary>Every client correlated to a match id.</summary>
            MatchId,
            /// <summary>Every client connected to a server id (fan-out to the whole box).</summary>
            Server,
        }

        /// <summary>
        /// Server-side: request the session logs of a player / session / match / whole server. Requires
        /// a WRITE-scoped server token (an ingest-only client token is rejected server-side with 403 —
        /// surfaced via <see cref="TombstackLog"/>, never thrown). The pull is queued; targeted clients
        /// upload on their next heartbeat (consent-gated, client-side). Fail-silent — safe to call from
        /// game-server code. Wire body: <c>{ "targetType", "targetValue", "reason" }</c>.
        /// </summary>
        /// <param name="target">What <paramref name="targetValue"/> identifies.</param>
        /// <param name="targetValue">The userId / sessionId / matchId / serverId to pull.</param>
        /// <param name="reason">Why (audit trail), e.g. "anomalous disconnect".</param>
        public static void RequestPlayerLogs(PullTarget target, string targetValue, string reason)
        {
            try
            {
                // Server action, NOT player capture: gated on Init only (not consent). The consent
                // gate is enforced on the CLIENT honouring side, where the player's bytes are read.
                if (!_initialized || string.IsNullOrEmpty(targetValue) || string.IsNullOrEmpty(reason)) return;
                var sb = new StringBuilder(EVENT_JSON_CAPACITY);
                sb.Append('{');
                bool first = true;
                TombstackJson.AppendField(sb, "targetType", pullTargetName(target), ref first);
                TombstackJson.AppendField(sb, "targetValue", truncate(targetValue, MAX_USER_ID), ref first);
                TombstackJson.AppendField(sb, "reason", truncate(reason, MAX_PULL_REASON), ref first);
                sb.Append('}');
                TombstackBehaviour.Enqueue(PULL_REQUESTS_PATH, sb.ToString(), UploadDurability.PersistOnFailure);
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"RequestPlayerLogs failed: {e.Message}");
            }
        }

        /// <summary>
        /// Server-side convenience: pull a single player's logs by id, or — with
        /// <see cref="TARGET_ALL_ON_THIS_SERVER"/> — every player connected to this dedicated server
        /// (resolved to the current serverId). For sessionId/matchId pulls use the typed overload.
        /// </summary>
        /// <param name="target">A player userId, or <see cref="TARGET_ALL_ON_THIS_SERVER"/>.</param>
        /// <param name="reason">Why (audit trail).</param>
        public static void RequestPlayerLogs(string target, string reason)
        {
            try
            {
                if (string.Equals(target, TARGET_ALL_ON_THIS_SERVER, StringComparison.Ordinal))
                    RequestPlayerLogs(PullTarget.Server, _serverId, reason);
                else
                    RequestPlayerLogs(PullTarget.UserId, target, reason);
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"RequestPlayerLogs failed: {e.Message}");
            }
        }

        /// <summary>Server-side convenience: auto-pull a player's logs after a weird disconnect.</summary>
        /// <param name="userId">The player whose session log to pull.</param>
        /// <param name="reason">Why (audit trail); defaults to "anomalous disconnect" when empty.</param>
        public static void OnAnomalousDisconnect(string userId, string reason)
        {
            RequestPlayerLogs(
                PullTarget.UserId, userId, string.IsNullOrEmpty(reason) ? "anomalous disconnect" : reason);
        }

        /// <summary>Map a <see cref="PullTarget"/> to its wire <c>targetType</c> string (no enum.ToString() alloc).</summary>
        private static string pullTargetName(PullTarget t)
        {
            switch (t)
            {
                case PullTarget.SessionId: return "sessionId";
                case PullTarget.MatchId: return "matchId";
                case PullTarget.Server: return "server";
                default: return "userId";
            }
        }

        /// <summary>
        /// Toggle capture + upload (store-policy / GDPR consent). While false, nothing is
        /// recorded or sent — including heartbeats, breadcrumbs, and analytics events.
        /// </summary>
        /// <param name="granted">True once the player has accepted telemetry.</param>
        public static void SetConsent(bool granted)
        {
            bool wasGranted = _consent;
            _consent = granted;
            try
            {
                // Consent arriving after Init starts the deferred session tracking (marker
                // write + unclean-shutdown report) exactly once.
                if (granted && _initialized) startSessionTracking();
                // Consent revoked: purge buffered breadcrumbs so the pre-revoke trail can't
                // attach to a crash captured after consent is re-granted (GDPR scoping).
                else if (!granted && wasGranted) clearBreadcrumbs();
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"SetConsent follow-up failed: {e.Message}");
            }
        }

        /// <summary>Manually report a caught exception (it is uploaded like an uncaught one).
        /// Always available, even with auto-capture disabled. Deduped like auto captures.</summary>
        /// <param name="ex">The exception to report; null is ignored.</param>
        public static void ReportException(Exception ex)
        {
            try
            {
                if (ex == null || !CaptureAllowed) return;
                var message = string.IsNullOrEmpty(ex.Message) ? "Exception" : ex.Message;
                var stack = ex.StackTrace ?? string.Empty;
                if (_uploadLogs) TombstackSessionLog.Append("Exception", message, stack);
                captureException(message, stack);
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"ReportException failed: {e.Message}");
            }
        }

        /// <summary>
        /// Submit a player bug report (e.g. from an in-game feedback form). The current
        /// breadcrumb trail is attached. Durable: persisted to disk until delivered.
        /// </summary>
        /// <param name="message">Player-written report body (required, clamped to 4000 chars).</param>
        /// <param name="category">Optional category label (clamped to 32 chars), e.g. "ui".</param>
        public static void ReportBug(string message, string category = null)
        {
            try
            {
                if (!CaptureAllowed || string.IsNullOrEmpty(message)) return;
                var payload = new BugPayload
                {
                    occurredAtIso = nowIso(),
                    buildVersion = _buildVersion,
                    os = _os,
                    arch = _arch,
                    category = truncate(category, MAX_CATEGORY),
                    message = truncate(message, MAX_BUG_MESSAGE),
                    userId = nullIfEmpty(_userId),
                    steamId = nullIfEmpty(_steamId),
                    breadcrumbs = snapshotBreadcrumbs(),
                    // A player writing a bug report is exactly when you want their log.
                    log = _uploadLogs,
                    role = _role,
                    serverId = _serverId,
                    matchId = _matchId,
                    sessionId = _sessionId,
                    device = _device,
                };
                if (_captureScreenshotOnBugReport && TombstackBehaviour.HasInstance)
                {
                    // Capture at end-of-frame on the host, then attach + enqueue + PUT the bytes.
                    // The report is never blocked or lost — capture failure just ships no screenshot.
                    TombstackBehaviour.CaptureScreenshot(_screenshotMaxDimension, shot =>
                    {
                        if (shot.HasValue)
                        {
                            payload.screenshot = new ScreenshotMeta { size = shot.Value.Size, sha256 = shot.Value.Sha256 };
                            TombstackBehaviour.EnqueueWithScreenshot(
                                BUG_REPORTS_PATH, JsonUtility.ToJson(payload), UploadDurability.WriteAhead,
                                payload.log, shot.Value.Bytes);
                        }
                        else
                        {
                            payload.screenshot = new ScreenshotMeta();
                            TombstackBehaviour.Enqueue(
                                BUG_REPORTS_PATH, JsonUtility.ToJson(payload), UploadDurability.WriteAhead, payload.log);
                        }
                    });
                }
                else
                {
                    payload.screenshot = new ScreenshotMeta();
                    TombstackBehaviour.Enqueue(
                        BUG_REPORTS_PATH, JsonUtility.ToJson(payload), UploadDurability.WriteAhead, payload.log);
                }
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"ReportBug failed: {e.Message}");
            }
        }

        /// <summary>
        /// Track a named analytics event (feeds the Analytics events &amp; funnels screens).
        /// Properties are flat string key/values, clamped to the server contract (32 entries,
        /// 64-char keys, 512-char values). Retried with backoff and persisted offline on failure.
        /// </summary>
        /// <param name="name">Event name (required, clamped to 64 chars), e.g. "level_complete".</param>
        /// <param name="props">Optional flat properties, e.g. { "level": "3" }.</param>
        public static void TrackEvent(string name, Dictionary<string, string> props = null)
        {
            try
            {
                if (!CaptureAllowed || string.IsNullOrEmpty(name)) return;
                // §K1: per-name sampling, applied BEFORE building/buffering so a dropped item costs
                // nothing beyond the cheap sampler check (no JSON allocation).
                if (!passesSample(name)) return;
                // Hand-built JSON: JsonUtility can't serialize dictionaries, and absent optionals
                // must be OMITTED (an empty-string `level` would fail the server's enum).
                var sb = new StringBuilder(EVENT_JSON_CAPACITY);
                sb.Append('{');
                bool first = true;
                TombstackJson.AppendField(sb, "occurredAtIso", nowIso(), ref first);
                TombstackJson.AppendField(sb, "buildVersion", _buildVersion, ref first);
                TombstackJson.AppendField(sb, "os", _os, ref first);
                TombstackJson.AppendField(sb, "arch", _arch, ref first);
                TombstackJson.AppendField(sb, "name", truncate(name, MAX_EVENT_NAME), ref first);
                // Correlation spine (role/serverId/matchId/userId/sessionId), omitting empty ids —
                // shared with TrackMetric so segmentation + cross-actor funnels line up server-side.
                appendCorrelation(sb, ref first);
                TombstackJson.AppendAttributes(
                    sb, props, MAX_EVENT_ATTRIBUTES, MAX_EVENT_ATTRIBUTE_KEY, MAX_EVENT_ATTRIBUTE_VALUE, ref first);
                sb.Append('}');
                // Batched (§16): accumulated into the bounded event buffer and flushed on
                // count/age/near-full/pause/quit/pre-crash, instead of one POST per event.
                TombstackBehaviour.AddEvent(sb.ToString());
                raiseTelemetry("event", name);
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"TrackEvent failed: {e.Message}");
            }
        }

        /// <summary>
        /// Record a numeric metric (e.g. tickrate, RTT, CCU). Batched client-side (§16) and flushed
        /// on count/age/pause/quit/pre-crash. <paramref name="value"/> must be finite. Fail-silent;
        /// never blocks the calling thread and never throws into game code.
        /// </summary>
        /// <param name="name">Metric name (required, clamped to 64 chars), e.g. "tickrate".</param>
        /// <param name="value">Finite numeric sample (NaN/Infinity are dropped, never shipped).</param>
        /// <param name="unit">Optional unit label (clamped to 16 chars), e.g. "ms".</param>
        public static void TrackMetric(string name, double value, string unit = null)
        {
            try
            {
                if (!CaptureAllowed || string.IsNullOrEmpty(name)) return;
                if (double.IsNaN(value) || double.IsInfinity(value)) return; // never ship a bad sample
                // §K1: per-name sampling, applied before building/buffering (see TrackEvent).
                if (!passesSample(name)) return;
                // Hand-built JSON (like TrackEvent): numbers are unquoted and absent optionals are
                // OMITTED. Each metric carries its OWN occurredAtIso — the batch's sentAtIso is added
                // only at flush time and is never used as the sample's timestamp.
                var sb = new StringBuilder(EVENT_JSON_CAPACITY);
                sb.Append('{');
                bool first = true;
                TombstackJson.AppendField(sb, "name", truncate(name, MAX_METRIC_NAME), ref first);
                TombstackJson.AppendNumberField(sb, "value", value, ref first);
                if (!string.IsNullOrEmpty(unit))
                    TombstackJson.AppendField(sb, "unit", truncate(unit, MAX_METRIC_UNIT), ref first);
                TombstackJson.AppendField(sb, "occurredAtIso", nowIso(), ref first);
                TombstackJson.AppendField(sb, "buildVersion", _buildVersion, ref first);
                TombstackJson.AppendField(sb, "os", _os, ref first);
                TombstackJson.AppendField(sb, "arch", _arch, ref first);
                appendCorrelation(sb, ref first);
                sb.Append('}');
                TombstackBehaviour.AddMetric(sb.ToString());
                raiseTelemetry("metric", name);
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"TrackMetric failed: {e.Message}");
            }
        }

        /// <summary>Append the Plan 1 correlation spine (role/serverId/matchId/userId/sessionId),
        /// omitting empty values. Shared by metrics and events so segmentation + cross-actor funnels
        /// work server-side. Empty strings are never emitted (cleanOptionalId-safe). The role is the
        /// contract-valid default ("client"/"server"), so it is always present.</summary>
        private static void appendCorrelation(StringBuilder sb, ref bool first)
        {
            if (!string.IsNullOrEmpty(_role)) TombstackJson.AppendField(sb, "role", _role, ref first);
            if (!string.IsNullOrEmpty(_serverId)) TombstackJson.AppendField(sb, "serverId", _serverId, ref first);
            if (!string.IsNullOrEmpty(_matchId)) TombstackJson.AppendField(sb, "matchId", _matchId, ref first);
            if (!string.IsNullOrEmpty(_userId)) TombstackJson.AppendField(sb, "userId", _userId, ref first);
            if (!string.IsNullOrEmpty(_sessionId)) TombstackJson.AppendField(sb, "sessionId", _sessionId, ref first);
        }

        /// <summary>
        /// Manually add a breadcrumb to the trail attached to future crashes and bug reports.
        /// The buffer is a fixed 50-entry ring — oldest entries are overwritten, and recording
        /// allocates nothing beyond the stored strings.
        /// </summary>
        /// <param name="message">Breadcrumb text (required, clamped to 512 chars).</param>
        /// <param name="level">Severity shown in the dashboard trail (default Info).</param>
        /// <param name="category">Optional category, folded into the message as a "[category] " prefix.</param>
        public static void AddBreadcrumb(string message, BreadcrumbLevel level = BreadcrumbLevel.Info, string category = null)
        {
            try
            {
                if (!CaptureAllowed || string.IsNullOrEmpty(message)) return;
                // The wire schema has only {tsIso, level, message} — category rides in the message.
                var text = string.IsNullOrEmpty(category) ? message : "[" + category + "] " + message;
                recordBreadcrumb(truncate(text, MAX_BREADCRUMB_MESSAGE), manualLevelName(level));
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"AddBreadcrumb failed: {e.Message}");
            }
        }

        /// <summary>
        /// §K1: set a per-name keep-probability for <see cref="TrackMetric"/> / <see cref="TrackEvent"/>.
        /// A high-frequency name can be sampled down so it can't saturate the batch buffer. The rate is
        /// clamped to [0,1] (1 = keep all, the default for any unset name; 0 = drop all). Sampling is
        /// applied before buffering. The map is bounded (<see cref="MAX_SAMPLE_RATES"/> names); once full,
        /// new names are ignored (existing rates can still be updated). Fail-silent; thread-safe.
        /// </summary>
        /// <param name="name">The exact metric/event name to sample (case-sensitive).</param>
        /// <param name="rate0to1">Keep-probability in [0,1]; values outside the range are clamped.</param>
        public static void SetSampleRate(string name, float rate0to1)
        {
            try
            {
                if (string.IsNullOrEmpty(name)) return;
                float clamped = rate0to1 < 0f ? 0f : (rate0to1 > 1f ? 1f : rate0to1);
                lock (_sampleLock)
                {
                    if (!_sampleRates.ContainsKey(name) && _sampleRates.Count >= MAX_SAMPLE_RATES) return;
                    _sampleRates[name] = clamped;
                }
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"SetSampleRate failed: {e.Message}");
            }
        }

        /// <summary>Deterministic-per-call sampler: keep when no rate is set (default), keep/drop by a
        /// random draw otherwise. Allocation-free on the no-rate fast path. Thread-safe.</summary>
        private static bool passesSample(string name)
        {
            float rate;
            lock (_sampleLock)
            {
                if (!_sampleRates.TryGetValue(name, out rate)) return true; // no rate → always keep
            }
            if (rate >= 1f) return true;
            if (rate <= 0f) return false;
            var rng = _sampleRng ?? (_sampleRng = new System.Random());
            return rng.NextDouble() < rate;
        }

        /// <summary>
        /// §K3: snapshot the current SDK state for diagnostics overlays / support tooling. Returns a
        /// small value struct; allocates nothing in steady state beyond the returned struct on call.
        /// Fail-silent — returns <c>default</c> if anything goes wrong.
        /// </summary>
        public static TombstackDiagnostics GetDiagnostics()
        {
            try
            {
                return new TombstackDiagnostics(
                    _initialized,
                    _consent,
                    TombstackBehaviour.OutboundCount,
                    TombstackBehaviour.PersistedCount,
                    TombstackBehaviour.LastFlushAgeSeconds,
                    TombstackBehaviour.Endpoint,
                    _matchId ?? string.Empty,
                    _serverId ?? string.Empty);
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"GetDiagnostics failed: {e.Message}");
                return default;
            }
        }

        /// <summary>§K2: subscribe scene-change breadcrumbs (idempotent; main thread, at Init).</summary>
        private static bool _sceneHooksInstalled;
        private static void hookSceneBreadcrumbs()
        {
            try
            {
                if (_sceneHooksInstalled) return;
                UnityEngine.SceneManagement.SceneManager.sceneLoaded += onSceneLoaded;
                UnityEngine.SceneManagement.SceneManager.activeSceneChanged += onActiveSceneChanged;
                _sceneHooksInstalled = true;
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"scene breadcrumb hook failed: {e.Message}");
            }
        }

        private static void unhookSceneBreadcrumbs()
        {
            if (!_sceneHooksInstalled) return;
            try
            {
                UnityEngine.SceneManagement.SceneManager.sceneLoaded -= onSceneLoaded;
                UnityEngine.SceneManagement.SceneManager.activeSceneChanged -= onActiveSceneChanged;
            }
            catch { /* shutdown path must never throw */ }
            _sceneHooksInstalled = false;
        }

        private static void onSceneLoaded(
            UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            try { AddBreadcrumb($"scene loaded: {scene.name}", BreadcrumbLevel.Info, "scene"); }
            catch { /* scene callback must never throw into engine code */ }
        }

        private static void onActiveSceneChanged(
            UnityEngine.SceneManagement.Scene from, UnityEngine.SceneManagement.Scene to)
        {
            try { AddBreadcrumb($"active scene changed: {from.name} -> {to.name}", BreadcrumbLevel.Info, "scene"); }
            catch { /* scene callback must never throw into engine code */ }
        }

        private static void handleLog(string condition, string stackTrace, LogType type)
        {
            // Invoked by Unity's log dispatch (any thread) — must never throw or re-enter.
            try
            {
                if (!CaptureAllowed) return;
                // Our own [Tombstack] warnings never feed back into the trail or the log
                // mirror (re-entrancy / feedback-loop guard).
                bool sdkLine = condition != null
                               && condition.StartsWith(TombstackLog.PREFIX, StringComparison.Ordinal);
                if (sdkLine) return;

                if (_uploadLogs)
                {
                    TombstackSessionLog.Append(
                        type == LogType.Exception ? "Exception" : logTypeName(type),
                        condition,
                        type == LogType.Exception ? stackTrace : null);
                }

                if (type != LogType.Exception)
                {
                    // Every non-fatal log becomes a breadcrumb.
                    recordBreadcrumb(truncate(condition, MAX_BREADCRUMB_MESSAGE), logTypeName(type));
                    return;
                }

                if (!_autoCaptureExceptions) return;
                captureException(condition, stackTrace);
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"capture failed: {e.Message}");
            }
        }

        /// <summary>
        /// Shared crash path for Unity-logged exceptions, manual ReportException, unobserved
        /// Task exceptions, and AppDomain unhandled exceptions. Deduped (≤1 report per
        /// signature per window — repeats become a counter breadcrumb), write-ahead durable,
        /// and ends with a synchronous log flush so a dying process leaves the crash line on
        /// disk for next-launch upload. Safe from any thread.
        /// </summary>
        private static void captureException(string condition, string stackTrace)
        {
            var signature = computeSignature(condition, stackTrace);
            if (isDuplicateCrash(signature, condition)) return;

            var payload = new CrashPayload
            {
                occurredAtIso = nowIso(),
                buildVersion = _buildVersion,
                os = _os,
                arch = _arch,
                signature = signature,
                // stackHint has a server-side min(1): never send it empty.
                stackHint = truncate(string.IsNullOrEmpty(condition) ? "Exception" : condition, MAX_STACK_HINT),
                stackTrace = truncate(stackTrace, MAX_STACK_TRACE),
                userId = nullIfEmpty(_userId),
                steamId = nullIfEmpty(_steamId),
                breadcrumbs = snapshotBreadcrumbs(),
                log = _uploadLogs,
                role = _role,
                serverId = _serverId,
                matchId = _matchId,
                sessionId = _sessionId,
                device = _device,
            };
            // Opt-in exception screenshot: synchronous best-effort grab (main-thread only, throttled).
            // Never delays or drops the durable crash — on any miss the crash ships with no screenshot
            // (payload.screenshot stays null → JsonUtility emits {size:0} → server presigns nothing).
            byte[] shotBytes = null;
            if (_captureScreenshotOnException && TombstackScreenshot.OnMainThread
                && TombstackScreenshot.ThrottleAllows(_exceptionScreenshotThrottleSeconds))
            {
                var shot = TombstackScreenshot.CaptureSync(_screenshotMaxDimension);
                if (shot.HasValue)
                {
                    payload.screenshot = new ScreenshotMeta { size = shot.Value.Size, sha256 = shot.Value.Sha256 };
                    shotBytes = shot.Value.Bytes;
                }
            }

            // Pre-crash flush: deliver buffered events/metrics before the (possibly fatal) crash
            // path may end the process, so a final batch isn't lost when the app dies here.
            TombstackBehaviour.FlushBatches();
            if (shotBytes != null)
                TombstackBehaviour.EnqueueWithScreenshot(
                    CRASHES_PATH, JsonUtility.ToJson(payload), UploadDurability.WriteAhead, payload.log, shotBytes);
            else
                TombstackBehaviour.Enqueue(
                    CRASHES_PATH, JsonUtility.ToJson(payload), UploadDurability.WriteAhead, payload.log);
            raiseTelemetry("crash", payload.stackHint);
            // Final flush in the crash path: the on-disk log must include this crash even if
            // the process dies before the upload (the write-ahead record retries next launch
            // and uploads previous-session.log).
            if (_uploadLogs) TombstackSessionLog.FlushNow();
        }

        /// <summary>
        /// True when this signature already reported inside the dedupe window. Repeats are
        /// counted as an Error breadcrumb (visible on the next report) instead of burning
        /// quota with identical crash rows. The map is bounded: at capacity it resets, which
        /// at worst re-allows one early report per signature — never drops a new crash.
        /// </summary>
        private static bool isDuplicateCrash(string signature, string condition)
        {
            var now = monotonicSeconds();
            int suppressedCount;
            lock (_dedupeLock)
            {
                if (_recentSignatures.TryGetValue(signature, out var window))
                {
                    if (now - window.LastSentSeconds < CRASH_DEDUPE_WINDOW_SECONDS)
                    {
                        window.Suppressed++;
                        suppressedCount = window.Suppressed;
                    }
                    else
                    {
                        window.LastSentSeconds = now;
                        window.Suppressed = 0;
                        return false;
                    }
                }
                else
                {
                    if (_recentSignatures.Count >= MAX_TRACKED_SIGNATURES) _recentSignatures.Clear();
                    _recentSignatures[signature] = new SignatureWindow { LastSentSeconds = now };
                    return false;
                }
            }
            // Crash-path-only allocation; the counter rides the breadcrumb trail instead.
            recordBreadcrumb(
                truncate($"crash suppressed (duplicate ×{suppressedCount} within {CRASH_DEDUPE_WINDOW_SECONDS}s): {condition}",
                    MAX_BREADCRUMB_MESSAGE),
                "Error");
            return true;
        }

        /// <summary>Capture exceptions Unity's log dispatch can miss: unobserved Task faults
        /// and raw AppDomain unhandled exceptions. Doubles with the Unity log path collapse
        /// via the dedupe window client-side and the canonical stack signature server-side.</summary>
        private static void hookBackgroundExceptionSources()
        {
            TaskScheduler.UnobservedTaskException += onUnobservedTaskException;
            AppDomain.CurrentDomain.UnhandledException += onUnhandledException;
        }

        private static void onUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            try
            {
                e.SetObserved(); // never escalate to a finalizer-thread rethrow
                if (!CaptureAllowed || e.Exception == null) return;
                var inner = e.Exception.InnerException ?? e.Exception;
                var message = "Unobserved task exception: " + inner.Message;
                var stack = inner.StackTrace ?? string.Empty;
                if (_uploadLogs) TombstackSessionLog.Append("Exception", message, stack);
                captureException(message, stack);
            }
            catch { /* crash hook must never throw */ }
        }

        private static void onUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                if (!CaptureAllowed) return;
                var ex = e.ExceptionObject as Exception;
                var message = ex != null && !string.IsNullOrEmpty(ex.Message) ? ex.Message : "Unhandled exception";
                var stack = ex != null ? ex.StackTrace ?? string.Empty : string.Empty;
                if (_uploadLogs) TombstackSessionLog.Append("Exception", message, stack);
                captureException(message, stack); // write-ahead + FlushNow: survives the process dying here
            }
            catch { /* crash hook must never throw */ }
        }

        /// <summary>Clean shutdown: flush the tail of the session log and remove the dirty-
        /// session marker so the next launch knows this run ended on purpose.</summary>
        private static void onQuitting()
        {
            try
            {
                unhookSceneBreadcrumbs();
                TombstackSessionLog.FlushNow();
                TombstackSessionMarker.Delete();
            }
            catch { /* quitting path must never throw */ }
        }

        /// <summary>
        /// Record an app foreground/background transition into the session marker. Called from
        /// TombstackBehaviour.OnApplicationPause. This is the signal that lets the next launch tell a
        /// normal background kill (user closed the app / OS suspended then reclaimed it) from a real
        /// foreground crash. No-op until the marker has been written (capture allowed).
        /// </summary>
        internal static void notifyAppPause(bool paused)
        {
            TombstackSessionMarker.UpdateState(paused, nowIso());
        }

        /// <summary>How a previous session ended, inferred from its surviving marker.</summary>
        private enum TerminationKind { Foreground, Backgrounded, Editor }

        /// <summary>
        /// Pure classification of a surviving marker. Only a death while FOREGROUND-ACTIVE is treated as
        /// a crash. An Editor Play Mode stop and a backgrounded-then-killed app (the normal mobile close /
        /// OS reclaim path) are normal terminations, not crashes — mirrors how Sentry/Unity only count a
        /// session as "crashed" with real crash evidence.
        /// </summary>
        private static TerminationKind classifyTermination(SessionMarkerData m)
        {
            if (m.isEditor) return TerminationKind.Editor;
            if (m.backgrounded) return TerminationKind.Backgrounded;
            return TerminationKind.Foreground;
        }

        /// <summary>
        /// Start dirty-session tracking exactly once per launch, the first time capture is
        /// allowed (at Init, or at SetConsent(true) when consent was required): write this
        /// session's marker and report the previous session's unclean shutdown, if any.
        /// </summary>
        private static void startSessionTracking()
        {
            lock (_sessionTrackingLock)
            {
                if (_sessionTrackingStarted || !_detectUncleanShutdown) return;
                _sessionTrackingStarted = true;
            }
            TombstackSessionMarker.Write(_sessionId, nowIso(), _buildVersion, _os, _arch, _isEditor);
            var previous = _previousMarker;
            _previousMarker = null;
            if (previous != null) reportUncleanShutdown(previous);
        }

        /// <summary>
        /// Previous run left its marker behind → it died hard. Design rule (no double
        /// reporting): when the write-ahead queue restored a managed crash from that session,
        /// the death is already represented — that restored report retries now and its own
        /// logUpload presign carries previous-session.log. Only when the queue held NO crash
        /// (native crash, OOM kill, force quit) do we send this synthetic report, attaching
        /// the preserved log to it instead.
        /// </summary>
        private static void reportUncleanShutdown(SessionMarkerData previous)
        {
            if (TombstackBehaviour.HasRestoredCrash) return;

            // Only a death while FOREGROUND-ACTIVE is a crash. A Play Mode stop in the Editor and a
            // backgrounded-then-killed app (user closed it / OS reclaimed a suspended app) are normal
            // terminations — suppress them so they don't show up as phantom crashes.
            var kind = classifyTermination(previous);
            if (kind != TerminationKind.Foreground)
            {
                TombstackLog.Info($"previous session ended normally ({kind.ToString().ToLowerInvariant()}); not reporting a crash");
                return;
            }

            var payload = new CrashPayload
            {
                // Detection time (this launch). The dead session's own timestamps ride in the marker,
                // but they are NOT used here: a crash whose session started/last-lived outside the
                // server's retention window would be rejected, so we date it at the (always-valid)
                // moment of detection — the marker's lastAliveIso remains available for future use.
                occurredAtIso = nowIso(),
                buildVersion = string.IsNullOrEmpty(previous.buildVersion) ? _buildVersion : previous.buildVersion,
                os = string.IsNullOrEmpty(previous.os) ? _os : previous.os,
                arch = string.IsNullOrEmpty(previous.arch) ? _arch : previous.arch,
                signature = UNCLEAN_SIGNATURE, // constant → all unclean shutdowns group together
                stackHint = UNCLEAN_STACK_HINT,
                stackTrace = string.Empty,
                userId = nullIfEmpty(_userId),
                steamId = nullIfEmpty(_steamId),
                breadcrumbs = null, // this launch's crumbs belong to this session, not the dead one
                log = _uploadLogs && _hadPreviousLog,
                // Correlation belongs to the DEAD session: its sessionId rides in the marker; the
                // match context wasn't persisted, so role stays the contract-valid default ("" would
                // fail the role enum) and the ids stay empty (cleaned to undefined server-side).
                role = _role,
                serverId = _serverId,
                matchId = _matchId,
                sessionId = previous.sessionId,
                device = _device, // same physical device; captured this launch (dead session's wasn't persisted)
            };
            TombstackBehaviour.Enqueue(
                CRASHES_PATH, JsonUtility.ToJson(payload), UploadDurability.WriteAhead,
                payload.log, logFromPreviousSession: true);
        }

        /// <summary>Preallocate the ring so recording never allocates entry objects.</summary>
        private static Breadcrumb[] createRing()
        {
            var ring = new Breadcrumb[MAX_BREADCRUMBS];
            for (int i = 0; i < ring.Length; i++) ring[i] = new Breadcrumb();
            return ring;
        }

        /// <summary>Overwrite the next ring slot in place (no per-breadcrumb object allocation).</summary>
        private static void recordBreadcrumb(string message, string level)
        {
            if (string.IsNullOrEmpty(message)) return;
            var ts = nowIso();
            lock (_breadcrumbLock)
            {
                var slot = _breadcrumbs[_breadcrumbHead];
                slot.tsIso = ts;
                slot.level = level;
                slot.message = message;
                _breadcrumbHead = (_breadcrumbHead + 1) % MAX_BREADCRUMBS;
                if (_breadcrumbCount < MAX_BREADCRUMBS) _breadcrumbCount++;
            }
            // Raise OUTSIDE the lock so a slow editor subscriber never stalls capture.
            raiseTelemetry("crumb", message);
        }

        /// <summary>§K3: raise the editor live-tail telemetry event, fail-silently. Allocation-free when
        /// there is no subscriber (shipped builds): the handler reference read short-circuits before any
        /// string is built, and the kind/detail concat happens only when the editor window is listening.</summary>
        private static void raiseTelemetry(string kind, string detail)
        {
            var handler = OnTelemetry;
            if (handler == null) return;
            try { handler(kind + ": " + detail); }
            catch { /* a faulty subscriber must never throw into capture/game code */ }
        }

        /// <summary>Drop the buffered breadcrumb trail (consent revoked). Resets the ring without
        /// allocating; preallocated slots are reused on the next record.</summary>
        private static void clearBreadcrumbs()
        {
            lock (_breadcrumbLock)
            {
                _breadcrumbHead = 0;
                _breadcrumbCount = 0;
            }
        }

        /// <summary>Snapshot the buffered breadcrumbs oldest→newest (null when empty). Copies the
        /// entries so the ring can keep mutating; only runs on the rare crash/bug path.</summary>
        private static Breadcrumb[] snapshotBreadcrumbs()
        {
            lock (_breadcrumbLock)
            {
                if (_breadcrumbCount == 0) return null;
                var snapshot = new Breadcrumb[_breadcrumbCount];
                int start = (_breadcrumbHead - _breadcrumbCount + MAX_BREADCRUMBS) % MAX_BREADCRUMBS;
                for (int i = 0; i < _breadcrumbCount; i++)
                {
                    var src = _breadcrumbs[(start + i) % MAX_BREADCRUMBS];
                    snapshot[i] = new Breadcrumb { tsIso = src.tsIso, level = src.level, message = src.message };
                }
                return snapshot;
            }
        }

        /// <summary>Interned level label for a Unity LogType (no enum.ToString() allocation).</summary>
        private static string logTypeName(LogType type)
        {
            switch (type)
            {
                case LogType.Log: return "Log";
                case LogType.Warning: return "Warning";
                case LogType.Error: return "Error";
                case LogType.Assert: return "Assert";
                default: return "Log";
            }
        }

        /// <summary>Interned level label for a manual breadcrumb (no enum.ToString() allocation).</summary>
        private static string manualLevelName(BreadcrumbLevel level)
        {
            switch (level)
            {
                case BreadcrumbLevel.Warning: return "Warning";
                case BreadcrumbLevel.Error: return "Error";
                default: return "Info";
            }
        }

        /// <summary>Stable fingerprint: SHA-256 over the exception message + normalized top frames.</summary>
        private static string computeSignature(string condition, string stackTrace)
        {
            var sb = new StringBuilder();
            sb.Append(condition ?? string.Empty);
            if (!string.IsNullOrEmpty(stackTrace))
            {
                var lines = stackTrace.Split('\n');
                for (int i = 0; i < lines.Length && i < SIGNATURE_FRAMES; i++)
                {
                    // Drop file paths / line numbers so the same bug hashes the same.
                    var frame = lines[i];
                    int at = frame.IndexOf(" (at ", StringComparison.Ordinal);
                    if (at >= 0) frame = frame.Substring(0, at);
                    sb.Append('\n').Append(frame.Trim());
                }
            }

            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
            var hex = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) hex.Append(b.ToString("x2"));
            return hex.ToString().Substring(0, SIGNATURE_HEX_LENGTH);
        }

        private static string nowIso() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        /// <summary>Mint a fresh id (GUID "N") — used for the session id and match ids.</summary>
        private static string newId() => Guid.NewGuid().ToString("N");

        /// <summary>Seconds elapsed on the monotonic clock since the dedupe epoch (cached at load).</summary>
        private static double monotonicSeconds() =>
            (System.Diagnostics.Stopwatch.GetTimestamp() - _dedupeEpochTicks)
            / (double)System.Diagnostics.Stopwatch.Frequency;

        private static string truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= max ? value : value.Substring(0, max);
        }

        private static string nullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

        /// <summary>Mutable dedupe slot: monotonic seconds when this signature last reported + repeats since.</summary>
        private sealed class SignatureWindow
        {
            public double LastSentSeconds;
            public int Suppressed;
        }
    }
}
