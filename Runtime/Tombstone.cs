using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace AnkleBreaker.Tombstone
{
    /// <summary>Severity of a manually recorded breadcrumb (see <see cref="Tombstone.AddBreadcrumb"/>).</summary>
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
    /// Public entry point for the Tombstone Unity SDK. After Init (or zero-code auto-init) it
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
    /// are swallowed and logged once through <see cref="TombstoneLog"/>.
    /// </summary>
    public static class Tombstone
    {
        private const int MAX_STACK_HINT = 512;
        private const int MAX_STACK_TRACE = 8192;
        private const int MAX_BUG_MESSAGE = 4000;
        private const int MAX_CATEGORY = 32;
        private const int MAX_USER_ID = 128;
        private const int MAX_STEAM_ID = 32;
        private const int SIGNATURE_FRAMES = 8;
        private const int SIGNATURE_HEX_LENGTH = 32;
        private const int MAX_BREADCRUMBS = 50;
        private const int MAX_BREADCRUMB_MESSAGE = 512;
        private const int MAX_EVENT_NAME = 64;
        private const int MAX_EVENT_ATTRIBUTES = 32;
        private const int MAX_EVENT_ATTRIBUTE_KEY = 64;
        private const int MAX_EVENT_ATTRIBUTE_VALUE = 512;
        private const int EVENT_JSON_CAPACITY = 256;
        private const int CRASH_DEDUPE_WINDOW_SECONDS = 60;
        private const int MAX_TRACKED_SIGNATURES = 64;

        private const string UNCLEAN_SIGNATURE = "unclean-shutdown";
        private const string UNCLEAN_STACK_HINT =
            "Previous session ended without a clean shutdown (hard crash, OOM kill, or force quit)";

        // Internal (not private): TombstoneBehaviour identifies restored crash records by path.
        internal const string CRASHES_PATH = "/api/v1/ingest/crashes";
        private const string BUG_REPORTS_PATH = "/api/v1/ingest/bug-reports";
        private const string EVENTS_PATH = "/api/v1/ingest/events";

        private static volatile bool _initialized;
        private static volatile bool _consent = true;
        // v0.5 autonomy toggles — default ON; overridden from TombstoneConfigSO at auto-init.
        private static bool _autoCaptureExceptions = true;
        private static bool _uploadLogs = true;
        private static bool _detectUncleanShutdown = true;
        private static string _endpoint;
        private static string _gameToken;
        private static string _sessionId;
        private static string _userId;
        private static string _steamId;

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

        // Ring buffer of recent log lines, attached to crashes/bugs as the "breadcrumb" trail.
        // Entries are preallocated once and mutated in place: recording a breadcrumb allocates
        // nothing beyond the stored strings. logMessageReceivedThreaded fires off-thread, so
        // every access is locked.
        private static readonly Breadcrumb[] _breadcrumbs = createRing();
        private static int _breadcrumbHead;
        private static int _breadcrumbCount;
        private static readonly object _breadcrumbLock = new object();

        /// <summary>True while capture + upload is permitted (Init done and consent granted).</summary>
        internal static bool CaptureAllowed => _initialized && _consent;

        /// <summary>Current user id ("" or null when anonymous) for heartbeat attribution.</summary>
        internal static string CurrentUserId => _userId;

        /// <summary>Auto-init from a <c>Resources/TombstoneConfig</c> asset, if present and enabled.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void autoInit()
        {
            try
            {
                var config = Resources.Load<TombstoneConfigSO>("TombstoneConfig");
                if (config == null || !config.AutoInitOnLoad) return;
                // When consent is required, start disabled until the game calls SetConsent(true).
                _consent = !config.RequireConsent;
                _autoCaptureExceptions = config.AutoCaptureExceptions;
                _uploadLogs = config.UploadLogs;
                _detectUncleanShutdown = config.DetectUncleanShutdown;
                Init(config.GameToken, config.Endpoint, config.HeartbeatIntervalSeconds);
            }
            catch (Exception e)
            {
                TombstoneLog.Warn($"auto-init failed: {e.Message}");
            }
        }

        /// <summary>
        /// Initialize the SDK. Idempotent — the first successful call wins. Starts exception
        /// capture, the session heartbeat loop, and the durable upload queue.
        /// </summary>
        /// <param name="gameToken">Per-game SDK token (tmb_...). Treat as a build secret.</param>
        /// <param name="endpoint">Tombstone base URL, e.g. https://your-tenant.example.com</param>
        /// <param name="heartbeatIntervalSeconds">Seconds between session heartbeats (clamped to a sane range).</param>
        public static void Init(string gameToken, string endpoint, float heartbeatIntervalSeconds = 60f)
        {
            try
            {
                if (_initialized) return;
                if (string.IsNullOrEmpty(gameToken) || string.IsNullOrEmpty(endpoint))
                {
                    TombstoneLog.Warn("Init skipped: missing token or endpoint.");
                    return;
                }
                _gameToken = gameToken;
                _endpoint = endpoint.TrimEnd('/');
                _buildVersion = TombstonePlatform.BuildVersion();
                _os = TombstonePlatform.Os();
                _arch = TombstonePlatform.Arch();
                _sessionId = Guid.NewGuid().ToString("N");
                _initialized = true;

                // Main-thread-only values (persistentDataPath) are cached here, like
                // version/os/arch above — everything after this point may run off-thread.
                var persistentDataPath = Application.persistentDataPath;
                TombstoneSessionLog.Configure(persistentDataPath);
                TombstoneSessionMarker.Configure(persistentDataPath);

                // Local file bookkeeping (not capture, so not consent-gated): preserve the
                // previous run's log for next-launch upload and read its dirty-session marker.
                // REPORTING from these only happens once capture is allowed (session tracking).
                _hadPreviousLog = TombstoneSessionLog.RotateForNewSession();
                if (_detectUncleanShutdown) _previousMarker = TombstoneSessionMarker.TakePrevious();
                else TombstoneSessionMarker.Delete(); // never leave a stale marker behind

                // Threaded so exceptions on background threads are captured too.
                Application.logMessageReceivedThreaded += handleLog;
                if (_autoCaptureExceptions) hookBackgroundExceptionSources();
                Application.quitting += onQuitting;

                TombstoneBehaviour.Bootstrap(_endpoint, _gameToken, _sessionId, heartbeatIntervalSeconds);
                if (CaptureAllowed) startSessionTracking();
            }
            catch (Exception e)
            {
                TombstoneLog.Warn($"Init failed: {e.Message}");
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
                TombstoneLog.Warn($"SetConsent follow-up failed: {e.Message}");
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
                if (_uploadLogs) TombstoneSessionLog.Append("Exception", message, stack);
                captureException(message, stack);
            }
            catch (Exception e)
            {
                TombstoneLog.Warn($"ReportException failed: {e.Message}");
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
                };
                TombstoneBehaviour.Enqueue(
                    BUG_REPORTS_PATH, JsonUtility.ToJson(payload), UploadDurability.WriteAhead, payload.log);
            }
            catch (Exception e)
            {
                TombstoneLog.Warn($"ReportBug failed: {e.Message}");
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
                // Hand-built JSON: JsonUtility can't serialize dictionaries, and absent optionals
                // must be OMITTED (an empty-string `level` would fail the server's enum).
                var sb = new StringBuilder(EVENT_JSON_CAPACITY);
                sb.Append('{');
                bool first = true;
                TombstoneJson.AppendField(sb, "occurredAtIso", nowIso(), ref first);
                TombstoneJson.AppendField(sb, "buildVersion", _buildVersion, ref first);
                TombstoneJson.AppendField(sb, "os", _os, ref first);
                TombstoneJson.AppendField(sb, "arch", _arch, ref first);
                TombstoneJson.AppendField(sb, "name", truncate(name, MAX_EVENT_NAME), ref first);
                if (!string.IsNullOrEmpty(_userId))
                    TombstoneJson.AppendField(sb, "userId", _userId, ref first);
                TombstoneJson.AppendAttributes(
                    sb, props, MAX_EVENT_ATTRIBUTES, MAX_EVENT_ATTRIBUTE_KEY, MAX_EVENT_ATTRIBUTE_VALUE, ref first);
                sb.Append('}');
                TombstoneBehaviour.Enqueue(EVENTS_PATH, sb.ToString(), UploadDurability.PersistOnFailure);
            }
            catch (Exception e)
            {
                TombstoneLog.Warn($"TrackEvent failed: {e.Message}");
            }
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
                TombstoneLog.Warn($"AddBreadcrumb failed: {e.Message}");
            }
        }

        private static void handleLog(string condition, string stackTrace, LogType type)
        {
            // Invoked by Unity's log dispatch (any thread) — must never throw or re-enter.
            try
            {
                if (!CaptureAllowed) return;
                // Our own [Tombstone] warnings never feed back into the trail or the log
                // mirror (re-entrancy / feedback-loop guard).
                bool sdkLine = condition != null
                               && condition.StartsWith(TombstoneLog.PREFIX, StringComparison.Ordinal);
                if (sdkLine) return;

                if (_uploadLogs)
                {
                    TombstoneSessionLog.Append(
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
                TombstoneLog.Warn($"capture failed: {e.Message}");
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
            };
            TombstoneBehaviour.Enqueue(
                CRASHES_PATH, JsonUtility.ToJson(payload), UploadDurability.WriteAhead, payload.log);
            // Final flush in the crash path: the on-disk log must include this crash even if
            // the process dies before the upload (the write-ahead record retries next launch
            // and uploads previous-session.log).
            if (_uploadLogs) TombstoneSessionLog.FlushNow();
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
                if (_uploadLogs) TombstoneSessionLog.Append("Exception", message, stack);
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
                if (_uploadLogs) TombstoneSessionLog.Append("Exception", message, stack);
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
                TombstoneSessionLog.FlushNow();
                TombstoneSessionMarker.Delete();
            }
            catch { /* quitting path must never throw */ }
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
            TombstoneSessionMarker.Write(_sessionId, nowIso(), _buildVersion, _os, _arch);
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
            if (TombstoneBehaviour.HasRestoredCrash) return;
            var payload = new CrashPayload
            {
                occurredAtIso = nowIso(), // detection time; the previous start rides in the marker only
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
            };
            TombstoneBehaviour.Enqueue(
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
