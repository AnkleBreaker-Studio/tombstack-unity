using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

namespace AnkleBreaker.Tombstack
{
    /// <summary>How hard the SDK fights to deliver a payload.</summary>
    internal enum UploadDurability
    {
        /// <summary>Best-effort, time-sensitive (heartbeats): no retry, never persisted.</summary>
        Ephemeral,
        /// <summary>Retried with backoff in-session; persisted to disk only after the final failure (events).</summary>
        PersistOnFailure,
        /// <summary>Written to disk BEFORE the first attempt so a quit/crash can't lose it (crashes, bugs).</summary>
        WriteAhead,
    }

    /// <summary>
    /// Runtime host: drains the thread-safe outbound queue on the main thread, uploads via
    /// UnityWebRequest with in-session exponential backoff, emits periodic session heartbeats,
    /// paces the rolling session-log flush (≤ once per 5s, written off-thread), PUTs presigned
    /// session-log uploads granted by crash/bug responses,
    /// and persists crash/bug payloads to disk (write-ahead) so they survive a quit and retry
    /// on the next launch (offline-first). Created once by
    /// <see cref="Tombstack.Init(string,string,float)"/> and kept alive across scenes.
    /// Fail-silent: every internal failure is swallowed and logged via <see cref="TombstackLog"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TombstackBehaviour : MonoBehaviour
    {
        private const int REQUEST_TIMEOUT_SECONDS = 15;
        private const int MAX_RETRY_ATTEMPTS = 5;
        private const float RETRY_BASE_DELAY_SECONDS = 2f; // 2s, 4s, 8s, 16s, 32s
        private const int MAX_CONCURRENT_UPLOADS = 4;
        private const int MAX_PERSISTED_FILES = 64;
        // Offline-spool quota: event/metric batches may hold at most half of the files, so a long
        // offline stretch of analytics can never leave a crash report with nowhere to be written. When
        // the spool is full a crash/bug report evicts the OLDEST analytics file (counted as a drop);
        // analytics never evicts anything. It used to be one 64-file pool shared first-come.
        private const int MAX_PERSISTED_ANALYTICS_FILES = 32;
        // Absolute cap on crash/bug (write-ahead) items held in memory. Those items are preserved by
        // the soft-cap eviction below, so without this an exception storm grew the queue without bound.
        // Past it an item stays on disk only and goes out on the next launch.
        private const int MAX_QUEUED_WRITE_AHEAD = 64;
        private const string BUG_REPORTS_PATH = "/api/v1/ingest/bug-reports";
        // Soft cap on the in-memory outbound queue (mirrors the native worker's bounded
        // queue). A game spamming TrackEvent while offline must not grow it without bound.
        private const int MAX_OUTBOUND_QUEUE = 256;
        private const float MIN_HEARTBEAT_INTERVAL_SECONDS = 15f;
        // MUST stay comfortably below the server's session window (SESSION_WINDOW_MS in
        // src/lib/billing.ts, 5 min): a heartbeat keeps its session "alive" for that long, and consecutive
        // beats merge into one active span only while the gap does not exceed it. Configured above the
        // window, a session blinks out between its own beats — which UNDER-counts live CCU and the monthly
        // peak the studio is BILLED on, silently and in proportion to the overshoot. This was 600s, twice
        // the window, so the SDK allowed a studio to configure its own billing metric wrong. 240s leaves a
        // minute of headroom for scheduling jitter and upload latency.
        // tests/heartbeat-cadence.test.ts reads this constant and fails if the two ever drift apart.
        private const float MAX_HEARTBEAT_INTERVAL_SECONDS = 240f;
        private const long HTTP_REQUEST_TIMEOUT = 408;
        private const long HTTP_TOO_MANY_REQUESTS = 429;
        private const string QUEUE_DIR_NAME = "Tombstack";
        private const string HEARTBEATS_PATH = "/api/v1/ingest/heartbeats";
        private const string PULL_REQUESTS_PATH = "/api/v1/pull-requests";
        // Batch ingest paths (§16). The colon-suffixed wire URL maps to the server's /batch route
        // folder via a next.config rewrite (a colon is not a legal directory name on Windows).
        private const string EVENTS_BATCH_PATH = "/api/v1/ingest/events:batch";
        private const string METRICS_BATCH_PATH = "/api/v1/ingest/metrics:batch";
        private const float LOG_FLUSH_INTERVAL_SECONDS = 5f;
        private const int LOG_UPLOAD_TIMEOUT_SECONDS = 30;
        private const string CONTENT_TYPE_TEXT_PLAIN = "text/plain";
        private const string CONTENT_TYPE_IMAGE_PNG = "image/png";
        /// <summary>Header naming WHICH SDK sent an ingest POST, as <c>&lt;sdk&gt;/&lt;version&gt;</c>.
        /// Server side: src/lib/client-version.ts. Optional on the wire forever - every build already
        /// in players' hands sends nothing, and the server must keep accepting those.</summary>
        private const string CLIENT_HEADER = "X-Tombstack-Client";
        /// <summary>This package's version. MUST equal <c>unity/package.json</c>'s "version" -
        /// tests/unity-client-header.test.ts reads both and fails the suite if they drift, because a
        /// version string that lies is worse than no version string at all.</summary>
        private const string SDK_VERSION = "0.21.4";
        private const string CLIENT_HEADER_VALUE = "unity/" + SDK_VERSION;
        // §K1: name of the auto round-trip metric emitted after each successful ingest POST.
        private const string RTT_METRIC_NAME = "tombstack.rtt_ms";

        /// <summary>Keep capacity within the server item cap: small items may still fit in one envelope.</summary>
        private const int BATCH_CAPACITY = 256;
        /// <summary>Flush when this many items have accumulated. UNCHANGED at 50, deliberately: a
        /// production census (scripts/batch-fill-census.mjs, three clean days) found the count trigger
        /// fires on 52 of 119,708 flushes — 0.04% — so moving it buys nothing measurable. It is also
        /// what makes overflow unreachable: TombstackBatch clamps FlushCount to the capacity and Add
        /// flushes at FlushCount, so the buffer can never reach BATCH_CAPACITY while a flush drains.
        /// Raising this toward the capacity is the one edit that would make drop-oldest reachable.</summary>
        private const int BATCH_FLUSH_COUNT = 50;
        /// <summary>Flush when the oldest buffered item is this many seconds old.
        ///
        /// 60s, was 10s. Rate limiting is charged PER REQUEST (six counter writes per batch POST), and
        /// at a 10s window the SDK carried 3.826 rows per request — so the platform wrote 1.73
        /// accounting rows for every telemetry row it stored. The same census showed 89.5% of flushes
        /// were AGE-triggered, making this the only knob that moves anything: at 60s the same traffic
        /// makes 60.6% fewer batch requests at a fill of 9.717, with ZERO additional drops.
        ///
        /// The whole measured curve (20s / 30s / 45s / 60s / 90s / 120s) and every figure quoted here
        /// live in src/lib/sdk-batch-policy.ts, and tests/sdk-batch-policy.test.ts pins THIS constant,
        /// the README and the CHANGELOG against it.</summary>
        private const float BATCH_FLUSH_AGE_SECONDS = 60f;

        private static readonly ConcurrentQueue<PendingUpload> _outbound = new ConcurrentQueue<PendingUpload>();
        // Bounded, preallocated event/metric batch buffers (§16/§15): flush at BATCH_FLUSH_COUNT items
        // or BATCH_FLUSH_AGE_SECONDS of age (so low-volume games still report), plus pause/quit and
        // pre-crash. Drop-oldest beyond cap. Steady-state allocation-free — only a flush builds an
        // envelope string. (Those are the ONLY triggers — see the note in TombstackBatch.cs about the
        // third one these docblocks used to list and TombstackBatch.Add has never implemented.)
        private static readonly TombstackBatch _eventBatch =
            new TombstackBatch(BATCH_CAPACITY, BATCH_FLUSH_COUNT, BATCH_FLUSH_AGE_SECONDS);
        private static readonly TombstackBatch _metricBatch =
            new TombstackBatch(BATCH_CAPACITY, BATCH_FLUSH_COUNT, BATCH_FLUSH_AGE_SECONDS);
        private static readonly object _persistLock = new object();
        private static TombstackBehaviour _instance;
        private static string _queueDir;
        private static int _persistedCount;
        // Spooled analytics items, oldest first — the eviction order when a crash needs the space.
        // Guarded by _persistLock.
        private static readonly LinkedList<PendingUpload> _persistedAnalytics = new LinkedList<PendingUpload>();
        // Crash/bug items currently in _outbound (bounded by MAX_QUEUED_WRITE_AHEAD). Interlocked.
        private static int _outboundWriteAhead;
        // Set once the previous run's spool has been read (off the main thread). Work that must see the
        // restored items (the unclean-shutdown dedupe) waits in _afterQueueLoaded, drained by Update.
        private static volatile bool _queueLoaded;
        private static readonly ConcurrentQueue<Action> _afterQueueLoaded = new ConcurrentQueue<Action>();
        // True when the offline queue restored a crash report from the previous run — the
        // dirty-session detector then skips its synthetic report (no double-counting).
        private static volatile bool _hasRestoredCrash;
        // The preserved previous-session.log uploads at most once per launch, even if several
        // restored reports each get granted a presign.
        private static int _previousLogClaimed;
        // §K3 diagnostics: monotonic seconds of the last batch flush (≤0 ⇒ none yet this launch).
        private static double _lastFlushAtSeconds;

        private string _endpoint;
        private string _gameToken;
        private string _sessionId;
        private float _heartbeatIntervalSeconds;
        private int _inFlight;
        private float _nextLogFlushAt;

        /// <summary>True when the offline queue held a crash report from a previous session. Only
        /// meaningful once the spool has been read — gate on <see cref="RunAfterQueueLoaded"/>.</summary>
        internal static bool HasRestoredCrash => _hasRestoredCrash;

        /// <summary>
        /// Run <paramref name="action"/> on the main thread once the previous run's offline spool has
        /// been read (M7: the reads happen off the main thread now, so <see cref="HasRestoredCrash"/> is
        /// not final at Init). Runs immediately when already loaded or when there is no host. Main thread.
        /// </summary>
        internal static void RunAfterQueueLoaded(Action action)
        {
            if (action == null) return;
            if (_queueLoaded || _instance == null) action();
            else _afterQueueLoaded.Enqueue(action);
        }

        /// <summary>Return every static to its load-time value. Called by
        /// <c>Tombstack.resetStaticState</c> when the Editor enters Play Mode WITHOUT a domain reload,
        /// where statics survive from the previous play session (the host GameObject is already gone).
        /// Queued-but-unsent payloads of that session are discarded; spooled ones are re-read by the
        /// next Bootstrap.</summary>
        internal static void ResetStaticState()
        {
            _instance = null;
            while (_outbound.TryDequeue(out _)) { }
            while (_afterQueueLoaded.TryDequeue(out _)) { }
            _eventBatch.DrainEnvelopes(string.Empty);
            _metricBatch.DrainEnvelopes(string.Empty);
            lock (_persistLock)
            {
                _queueDir = null;
                _persistedCount = 0;
                _persistedAnalytics.Clear();
            }
            Interlocked.Exchange(ref _outboundWriteAhead, 0);
            _queueLoaded = false;
            _hasRestoredCrash = false;
            Interlocked.Exchange(ref _previousLogClaimed, 0);
            _lastFlushAtSeconds = 0;
        }

        /// <summary>§K3: count of payloads waiting in the in-memory outbound queue.</summary>
        internal static int OutboundCount => _outbound.Count;

        /// <summary>§K3: count of write-ahead payloads persisted to the offline sidecar directory.</summary>
        internal static int PersistedCount => _persistedCount;

        /// <summary>§K3: seconds since the last event/metric batch flush, or -1 when none has happened
        /// yet this launch. Uses the monotonic batch clock (never runs backward).</summary>
        internal static double LastFlushAgeSeconds =>
            _lastFlushAtSeconds <= 0 ? -1.0 : monotonic() - _lastFlushAtSeconds;

        /// <summary>§K3: the resolved ingest endpoint this host is sending to ("" before Bootstrap).</summary>
        internal static string Endpoint => _instance != null ? _instance._endpoint ?? "" : "";

        /// <summary>Create the hidden singleton host and start the heartbeat + upload loops.</summary>
        internal static void Bootstrap(string endpoint, string gameToken, string sessionId, float heartbeatIntervalSeconds)
        {
            if (_instance != null) return;
            // Stamp the main thread for the synchronous exception-screenshot guard (rendering APIs are
            // main-thread-only). Bootstrap runs on the main thread during Init.
            TombstackScreenshot.MainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            var go = new GameObject("[Tombstack]");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<TombstackBehaviour>();
            _instance._endpoint = endpoint;
            _instance._gameToken = gameToken;
            _instance._sessionId = sessionId;
            _instance._heartbeatIntervalSeconds = Mathf.Clamp(
                heartbeatIntervalSeconds, MIN_HEARTBEAT_INTERVAL_SECONDS, MAX_HEARTBEAT_INTERVAL_SECONDS);
            _queueDir = Path.Combine(Application.persistentDataPath, QUEUE_DIR_NAME);
            _instance.loadPersistedQueue();
            _instance.StartCoroutine(_instance.heartbeatLoop());
            // App-hang watchdog (0.11): background thread watching the Update pulse. ≤0 (config
            // DetectAppHangs off, or a 0 threshold) means disabled — Start no-ops. Fail-silent.
            TombstackAppHang.Start(Tombstack.AppHangThresholdSeconds);
        }

        /// <summary>
        /// Queue a payload for upload. Thread-safe (crashes can be enqueued from any thread).
        /// WriteAhead payloads are persisted to disk immediately so they survive a quit/crash.
        /// </summary>
        /// <param name="requestLog">True when the body carries <c>"log":true</c> — after the
        /// 2xx the response's <c>logUpload</c> presign is used to PUT the session log.</param>
        /// <param name="logFromPreviousSession">True when the granted presign should upload the
        /// preserved most-recent-prior session log (unclean-shutdown report) instead of the
        /// current session's log.</param>
        /// <param name="targetSessionId">v0.18: when set, a granted log presign uploads THIS specific
        /// retained session's log (a past-session pull) instead of the current session's. Null ⇒
        /// current session (unchanged behaviour). Ignored when <paramref name="logFromPreviousSession"/>
        /// is true.</param>
        internal static void Enqueue(
            string path, string json, UploadDurability durability,
            bool requestLog = false, bool logFromPreviousSession = false, string targetSessionId = null)
        {
            var item = PendingUpload.Post(path, json, durability, null, requestLog, logFromPreviousSession);
            item.TargetSessionId = targetSessionId;
            if (durability == UploadDurability.WriteAhead) persist(item);
            enqueueOutbound(item);
        }

        /// <summary>True once the upload host exists (after Init/Bootstrap).</summary>
        internal static bool HasInstance => _instance != null;

        /// <summary>Run an end-of-frame screenshot capture on the host and hand the result to
        /// <paramref name="cb"/> (null when no host or on failure). Used by the bug-report path.</summary>
        internal static void CaptureScreenshot(int maxDimension, System.Action<TombstackScreenshot.Shot?> cb)
        {
            if (_instance == null) { cb?.Invoke(null); return; }
            _instance.StartCoroutine(TombstackScreenshot.Capture(maxDimension, cb));
        }

        /// <summary>Like <see cref="Enqueue"/> but carries captured screenshot bytes: on the 2xx the
        /// response's screenshotUpload presign is chased and the PNG is PUT. Best-effort — the bytes
        /// are NOT persisted with the write-ahead record (a screenshot lost to a restart is fine).</summary>
        internal static void EnqueueWithScreenshot(
            string path, string json, UploadDurability durability, bool requestLog, byte[] screenshotBytes)
        {
            var item = PendingUpload.Post(path, json, durability, null, requestLog, false);
            if (screenshotBytes != null && screenshotBytes.Length > 0)
            {
                item.RequestedScreenshot = true;
                item.ScreenshotBytes = screenshotBytes;
            }
            if (durability == UploadDurability.WriteAhead) persist(item);
            enqueueOutbound(item);
        }

        /// <summary>
        /// Enqueue an outbound item under a soft cap (mirrors the native worker's bounded queue).
        /// At capacity the OLDEST non-crash item is dropped; crash/bug (write-ahead) payloads are
        /// preserved — they're already persisted to disk and retry on the next launch. Thread-safe.
        /// Allocation-free in steady state: no eviction work happens below the cap.
        /// <para>Crash/bug items have their own hard cap (<see cref="MAX_QUEUED_WRITE_AHEAD"/>): past it
        /// an item that is on disk simply waits there for the next launch, and one that is not (its
        /// persist failed) is counted as dropped.</para>
        /// </summary>
        private static void enqueueOutbound(PendingUpload item)
        {
            if (item.Durability == UploadDurability.WriteAhead)
            {
                if (Interlocked.Increment(ref _outboundWriteAhead) > MAX_QUEUED_WRITE_AHEAD)
                {
                    Interlocked.Decrement(ref _outboundWriteAhead);
                    if (string.IsNullOrEmpty(item.FilePath)) TombstackDrops.Record(DropReason.OutboundQueueFull);
                    return;
                }
            }
            if (_outbound.Count >= MAX_OUTBOUND_QUEUE) dropOldestNonCrash();
            _outbound.Enqueue(item);
        }

        /// <summary>Take the next outbound item, keeping the write-ahead count in step. Main thread.</summary>
        private static bool tryDequeueOutbound(out PendingUpload item)
        {
            if (!_outbound.TryDequeue(out item)) return false;
            if (item.Durability == UploadDurability.WriteAhead) Interlocked.Decrement(ref _outboundWriteAhead);
            return true;
        }

        /// <summary>Drop the oldest non-crash payload to bound the queue. Crash/bug items pulled
        /// from the front are re-enqueued (preserved, since they're durable on disk); the first
        /// non-crash item found is dropped. Bounded by the current size so it always terminates.</summary>
        private static void dropOldestNonCrash()
        {
            int scan = _outbound.Count;
            for (int i = 0; i < scan; i++)
            {
                if (!_outbound.TryDequeue(out var item)) return;
                if (item.Durability == UploadDurability.WriteAhead)
                {
                    _outbound.Enqueue(item); // preserve crashes/bugs (write-ahead persisted)
                    continue;
                }
                // Counted + reported, not just warned: the per-eviction console warning this replaced
                // only ever reached the PLAYER's machine (SDK log lines are filtered out of the
                // uploaded session log), so the studio could never learn how much it was losing.
                TombstackDrops.Record(DropReason.OutboundQueueFull);
                return;
            }
        }

        /// <summary>Append a pre-serialized event item to the batch; flush immediately when the
        /// count trigger hits. Thread-safe; allocation-free below the flush threshold.</summary>
        internal static void AddEvent(string itemJson)
        {
            if (_eventBatch.Add(itemJson, monotonic())) flushOne(_eventBatch, EVENTS_BATCH_PATH);
        }

        /// <summary>Append a pre-serialized metric item to the batch; flush immediately when the
        /// count trigger hits. Thread-safe; allocation-free below the flush threshold.</summary>
        internal static void AddMetric(string itemJson)
        {
            if (_metricBatch.Add(itemJson, monotonic())) flushOne(_metricBatch, METRICS_BATCH_PATH);
        }

        /// <summary>Force-drain both buffers (pause/quit/pre-crash). Safe to call from any thread.</summary>
        internal static void FlushBatches()
        {
            flushOne(_eventBatch, EVENTS_BATCH_PATH);
            flushOne(_metricBatch, METRICS_BATCH_PATH);
        }

        /// <summary>Drain one buffer into a batch envelope and enqueue it for off-main-thread send via
        /// the existing upload queue + backoff. PersistOnFailure — the same durability class as the
        /// single-event path it replaces (retried in-session, persisted only after the final failure).</summary>
        private static void flushOne(TombstackBatch batch, string path)
        {
            // Hold event/metric batches until the game has begun collecting (StartSession() / Init
            // auto-start) — nothing ships before identity/environment are configured. Crash/bug
            // reports bypass this (they enqueueOutbound directly), so a startup crash still sends.
            if (!Tombstack.CollectingStarted) return;
            if (!batch.HasItems) return;
            var envelopes = batch.DrainEnvelopes(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
            _lastFlushAtSeconds = monotonic(); // §K3 diagnostics: stamp the last-flush time
            foreach (var envelope in envelopes)
                enqueueOutbound(PendingUpload.Post(path, envelope, UploadDurability.PersistOnFailure, null, false, false));
        }

        /// <summary>Monotonic seconds for batch age timing (never runs backward on a clock/NTP jump).</summary>
        private static double monotonic() =>
            System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

        // Unity message — must stay PascalCase (engine-invoked). Allocates nothing when idle:
        // the queue drain no-ops and the log flush is a no-op call when the buffer is empty.
        private void Update()
        {
            try
            {
                // 0.11 per-frame pumps, both allocation-free: frame-stats accumulation (drained by
                // the next heartbeat) and the app-hang pulse (watched by the background watchdog).
                // 0.12: the sampler is gated (config CollectFrameStats / SetCaptureEnabled(FrameStats)).
                if (Tombstack.FrameStatsEnabled) TombstackFrameStats.Sample(Time.unscaledDeltaTime);
                TombstackAppHang.Pump();
                // Work deferred until the previous run's spool was read (the unclean-shutdown report).
                // Empty-queue check is one volatile read + one TryDequeue: allocation-free when idle.
                while (_queueLoaded && _afterQueueLoaded.TryDequeue(out var deferred)) deferred();
                while (_inFlight < MAX_CONCURRENT_UPLOADS && tryDequeueOutbound(out var item))
                {
                    StartCoroutine(send(item));
                }
                // Age-trigger flush so low-volume games still report within FlushAgeSeconds. Cheap locked
                // reads; no allocation when nothing is due.
                double m = monotonic();
                if (_eventBatch.ShouldFlushByAge(m)) flushOne(_eventBatch, EVENTS_BATCH_PATH);
                if (_metricBatch.ShouldFlushByAge(m)) flushOne(_metricBatch, METRICS_BATCH_PATH);
                if (Time.unscaledTime >= _nextLogFlushAt)
                {
                    _nextLogFlushAt = Time.unscaledTime + LOG_FLUSH_INTERVAL_SECONDS;
                    TombstackSessionLog.RequestFlush();
                }
            }
            // Fail-silent: an exception escaping Update is logged per-frame by Unity and would be
            // re-captured by handleLog. Swallow via the [Tombstack]-prefixed logger (filtered from
            // breadcrumbs) to honor the fail-silent contract and break the feedback loop.
            catch (System.Exception e) { TombstackLog.Warn("flush failed: " + e.Message); }
        }

        // Unity message — engine-invoked; must stay PascalCase. Two jobs on backgrounding (mobile):
        // (1) flush buffered analytics + the session log so a suspended/killed app still reports, and
        // (2) record the foreground→background transition in the session marker. That transition is
        // what lets the next launch tell a normal background kill (user closed the app / OS reclaim)
        // from a real foreground crash — without it, every backgrounded-then-killed app looked like a
        // crash. Both transitions (pause + resume) are recorded so the marker reflects the live state.
        private void OnApplicationPause(bool paused)
        {
            try
            {
                // Update the marker FIRST: it is a tiny (<1 KB) write, whereas the log flush below can be
                // slow. On mobile the OS may kill the app any time during this pause window, so the
                // backgrounded flag must be on disk before the slow flush — otherwise a kill mid-flush
                // leaves backgrounded=false and the next launch reports a phantom crash.
                Tombstack.notifyAppPause(paused);
                // A backgrounded app legitimately stops pumping frames — never an app hang.
                TombstackAppHang.NotifyPause(paused);
                if (paused)
                {
                    // Send DIRECTLY, not via the outbound queue. FlushBatches() -> flushOne() ->
                    // enqueueOutbound(), and _outbound is drained ONLY by Update(), which does not run
                    // again until RESUME (see SendHeartbeatNow's note below, which already said so). On
                    // mobile the OS usually kills a backgrounded app before any OnApplicationQuit, so the
                    // batch pending at background time — and one is always pending, since batches flush
                    // at 50 items or 10s — died with the process. Measured on a live game: 543 app_paused
                    // against 525 app_resumed, i.e. an app_paused only ever arrived when the player came
                    // BACK; the pause that ENDS a session never reported, and took its batch with it.
                    // WriteAhead rather than PersistOnFailure for the same reason the send is direct: a
                    // suspended process never spends the retry budget that PersistOnFailure waits for.
                    flushBatchDirect(_eventBatch, EVENTS_BATCH_PATH, UploadDurability.WriteAhead);
                    flushBatchDirect(_metricBatch, METRICS_BATCH_PATH, UploadDurability.WriteAhead);
                    TombstackSessionLog.FlushNow(); // persist the log tail before the OS suspends/kills us
                    // v0.19: mark the session live at background time so CCU/liveness stay tight even
                    // if this app sits minimized past the next interval tick (gated + ephemeral inside).
                    SendHeartbeatNow();
                }
            }
            catch (System.Exception e) { TombstackLog.Warn("flush failed: " + e.Message); }
        }

        // Unity message — final flush on quit (complements Tombstack.onQuitting's log flush).
        // Also stops + joins the app-hang watchdog (bounded join; the thread is IsBackground
        // anyway, so a missed join can never keep the process alive).
        private void OnApplicationQuit()
        {
            try
            {
                // Send the final batches DIRECTLY (not via the outbound queue): Update won't run again
                // after quit, so an enqueued batch would never drain. Same best-effort caveat as the
                // quit beat — a quitting process may not finish the request; a PersistOnFailure batch
                // that does fail is written to disk and retried next launch.
                flushBatchDirect(_eventBatch, EVENTS_BATCH_PATH);
                flushBatchDirect(_metricBatch, METRICS_BATCH_PATH);
                // v0.19: best-effort on-demand quit beat, sent directly (see SendHeartbeatNow). The
                // request is issued synchronously up to the first yield, so it's in flight before the
                // process dies; a quit-beat that doesn't complete is acceptable (beats are ephemeral).
                SendHeartbeatNow();
            }
            catch (System.Exception e) { TombstackLog.Warn("flush failed: " + e.Message); }
            TombstackAppHang.Stop();
        }

        /// <summary>Drain one batch and send its envelope DIRECTLY via <c>StartCoroutine(send(...))</c>
        /// (mirrors <see cref="flushOne"/> but bypasses the outbound queue for the quit and pause paths,
        /// where <see cref="Update"/> either never runs again or does not run until resume). Fail-silent.
        /// <para><paramref name="durability"/> defaults to <see cref="UploadDurability.PersistOnFailure"/>
        /// — the quit path, where a failed send persists once the in-session retry budget is spent and
        /// goes out next launch. The PAUSE path passes <see cref="UploadDurability.WriteAhead"/> instead,
        /// because PersistOnFailure only reaches disk once that budget is SPENT, and a process the OS
        /// suspends mid-pause never spends it — the batch would die in memory. WriteAhead persists BEFORE
        /// the first attempt; the record is deleted on the 2xx, so a delivered batch is never replayed.
        /// </para></summary>
        private void flushBatchDirect(
            TombstackBatch batch, string path,
            UploadDurability durability = UploadDurability.PersistOnFailure)
        {
            if (!Tombstack.CollectingStarted || !batch.HasItems) return;
            var envelopes = batch.DrainEnvelopes(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
            _lastFlushAtSeconds = monotonic();
            foreach (var envelope in envelopes)
            {
                var item = PendingUpload.Post(path, envelope, durability, null, false, false);
                // A suspended process may never spend its retry budget: persist each pause batch first.
                if (durability == UploadDurability.WriteAhead) persist(item);
                StartCoroutine(send(item));
            }
        }

        /// <summary>
        /// Restore the previous run's offline spool. Only the directory LISTING happens here on the main
        /// thread — it fixes the file count before anything new can be persisted, so the cap and the
        /// quota are right from the first frame. Reading and parsing the files (up to 64 JSON bodies)
        /// moved to the thread pool (M7); it used to stall Init. Never throws.
        /// </summary>
        private void loadPersistedQueue()
        {
            string[] files;
            try
            {
                if (!Directory.Exists(_queueDir)) { _queueLoaded = true; return; }
                files = Directory.GetFiles(_queueDir, "*.json");
                lock (_persistLock) { _persistedCount = files.Length; }
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"could not load offline queue: {e.Message}");
                _queueLoaded = true;
                return;
            }
            if (files.Length == 0) { _queueLoaded = true; return; }
            try
            {
                ThreadPool.QueueUserWorkItem(_ => readPersistedFiles(files));
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"could not schedule offline queue load; loading inline: {e.Message}");
                readPersistedFiles(files);
            }
        }

        /// <summary>Thread-pool half of <see cref="loadPersistedQueue"/>: read each spooled record
        /// oldest-first and queue it for upload. An unreadable record is deleted (it used to occupy a
        /// spool slot forever). Always ends by marking the spool loaded. Never throws.</summary>
        private static void readPersistedFiles(string[] files)
        {
            try
            {
                Array.Sort(files, compareSpoolAge);
                foreach (var file in files) restorePersisted(file);
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"could not load offline queue: {e.Message}");
            }
            finally
            {
                _queueLoaded = true;
            }
        }

        private static void restorePersisted(string file)
        {
            try
            {
                // An I/O failure leaves the file for the next launch; an unparseable one is forgotten.
                var text = File.ReadAllText(file);
                PersistedRecord record;
                try { record = JsonUtility.FromJson<PersistedRecord>(text); }
                catch (ArgumentException) { record = null; }
                if (record == null || string.IsNullOrEmpty(record.path))
                {
                    forgetPersistedFile(file);
                    return;
                }
                // Restored records came from an earlier run: a granted log presign must
                // upload that run's preserved log, not this session's fresh one.
                if (record.path == Tombstack.CRASHES_PATH) _hasRestoredCrash = true;
                var item = PendingUpload.Post(
                    record.path, record.body, UploadDurability.WriteAhead, file,
                    record.requestLog, fromPreviousSession: true);
                if (!isProtectedPath(record.path))
                {
                    lock (_persistLock) { _persistedAnalytics.AddLast(item); }
                }
                enqueueOutbound(item);
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"could not restore an offline record: {e.Message}");
            }
        }

        /// <summary>Oldest spool file first, by last write time (file names are GUIDs). Never throws.</summary>
        private static int compareSpoolAge(string a, string b)
        {
            try { return File.GetLastWriteTimeUtc(a).CompareTo(File.GetLastWriteTimeUtc(b)); }
            catch { return string.CompareOrdinal(a, b); }
        }

        /// <summary>Delete a spool file that is not backed by any live item (unreadable record).</summary>
        private static void forgetPersistedFile(string file)
        {
            lock (_persistLock)
            {
                try { File.Delete(file); }
                catch { /* best-effort */ }
                if (_persistedCount > 0) _persistedCount--;
            }
        }

        /// <summary>True for crash and bug-report payloads — the spool class analytics can never evict.</summary>
        private static bool isProtectedPath(string path) =>
            path == Tombstack.CRASHES_PATH || path == BUG_REPORTS_PATH;

        private IEnumerator heartbeatLoop()
        {
            while (true)
            {
                // Consent-gated like every other capture; resumes when SetConsent(true) is called.
                // 0.12: also gated by SendHeartbeats / SetCaptureEnabled(Heartbeats) — checked every
                // interval so a runtime flip pauses/resumes the loop on its next tick.
                if (Tombstack.CaptureAllowed && Tombstack.HeartbeatsEnabled && Tombstack.CollectingStarted)
                {
                    var json = buildHeartbeatJson(
                        out var pendingMetadataJson, out var metadataEpoch, out var carriedDevice,
                        out var pendingPriorUserId);
                    if (json != null)
                    {
                        // Heartbeats are ephemeral — a missed beat is stale data, never retried.
                        var beat = PendingUpload.Post(
                            HEARTBEATS_PATH, json, UploadDurability.Ephemeral, null, false, false);
                        // Only advance the metadata baseline once THIS beat is acked (M1) — a dropped beat
                        // must leave the change/clear pending so the next beat re-sends it.
                        beat.PendingUserMetadataJson = pendingMetadataJson;
                        beat.PendingUserMetadataEpoch = metadataEpoch;
                        // Same discipline for the one-time device snapshot (0.14): mark delivered on 2xx only.
                        beat.CarriedDevice = carriedDevice;
                        // v0.16: the one-shot identity-upgrade marker — cleared on 2xx only, so a
                        // dropped/failed beat re-carries it on the next tick.
                        beat.PendingPriorUserId = pendingPriorUserId;
                        yield return send(beat);
                    }
                }
                // Realtime, not WaitForSeconds: a pause menu sets Time.timeScale = 0, which would freeze
                // this loop and drop the player out of the server's 300s session window while they are
                // still there — understating the peak CCU the studio is billed on. Constructed per
                // iteration because WaitForSecondsRealtime latches its deadline at construction; cached
                // above the loop, every yield after the first returns instantly.
                yield return new WaitForSecondsRealtime(_heartbeatIntervalSeconds);
            }
        }

        /// <summary>
        /// v0.19: send a single on-demand heartbeat NOW (app minimize / quit), so a session is marked
        /// live at background/close time and CCU/liveness stay tight without waiting for the next
        /// interval tick. Gated by the SAME conditions as the periodic loop
        /// (CaptureAllowed &amp;&amp; HeartbeatsEnabled &amp;&amp; CollectingStarted) and enqueued
        /// <see cref="UploadDurability.Ephemeral"/> (identical to the periodic beat: a missed beat is
        /// stale data, never retried). NOT a schema change — the server treats it as a normal beat and
        /// dedupes per (game, session), so no CCU double-count. Sends the beat DIRECTLY via
        /// <c>StartCoroutine(send(beat))</c> — exactly like the periodic loop — instead of enqueueing:
        /// the outbound queue is drained ONLY by <see cref="Update"/>, which never runs again after
        /// <see cref="OnApplicationQuit"/> and only runs at RESUME after <see cref="OnApplicationPause"/>,
        /// so an enqueued lifecycle beat would be dead-on-quit and late-on-pause. StartCoroutine runs
        /// synchronously up to the first yield, so the UnityWebRequest IS issued before the app suspends.
        /// This is genuinely best-effort on quit: a quitting process may not finish the in-flight request
        /// before it dies — that lost quit-beat is acceptable (beats are ephemeral; the session already
        /// beat while live). Bypasses the _inFlight cap like the periodic loop's send. Fail-silent.
        /// Must be called on the main thread (StartCoroutine + the frame-stats read are main-thread only).
        /// </summary>
        internal static void SendHeartbeatNow()
        {
            var inst = _instance;
            if (inst == null) return;
            if (!Tombstack.CaptureAllowed || !Tombstack.HeartbeatsEnabled || !Tombstack.CollectingStarted) return;
            try
            {
                var json = inst.buildHeartbeatJson(
                    out var pendingMetadataJson, out var metadataEpoch, out var carriedDevice,
                    out var pendingPriorUserId);
                if (json == null) return;
                var beat = PendingUpload.Post(
                    HEARTBEATS_PATH, json, UploadDurability.Ephemeral, null, false, false);
                // Same ack-gated M1 delivery discipline as heartbeatLoop: only advance the metadata /
                // device / identity-upgrade baselines once THIS beat is acked (a dropped beat re-carries).
                beat.PendingUserMetadataJson = pendingMetadataJson;
                beat.PendingUserMetadataEpoch = metadataEpoch;
                beat.CarriedDevice = carriedDevice;
                beat.PendingPriorUserId = pendingPriorUserId;
                // Send directly (mirrors heartbeatLoop's `yield return send(beat)`), NOT enqueueOutbound —
                // the queue never drains after quit / drains late after pause, so an enqueued lifecycle
                // beat is dead-on-quit and late-on-pause. StartCoroutine issues the request synchronously
                // up to the first yield, so it's actually in flight before the app suspends.
                inst.StartCoroutine(inst.send(beat));
            }
            catch (System.Exception e) { TombstackLog.Warn("on-demand heartbeat failed: " + e.Message); }
        }

        // True once a heartbeat carrying the device snapshot was ACKED this session/boot — the
        // snapshot rides every beat until then (a lost beat re-sends it), then never again, so the
        // per-session cost is one ~300-byte object. Same delivery discipline as the metadata M1 path.
        private bool _deviceSentOnHeartbeat;

        /// <summary>Build the heartbeat body; userId attribution feeds the Sessions/Fleet screens. Any
        /// pending user-metadata change is spliced in AND returned via the out-params so the caller can
        /// commit the baseline only once the beat is acked (M1); both are null/0 when nothing changed.
        /// <paramref name="carriedDevice"/> is true when this beat carries the one-time device snapshot
        /// (0.14) — the caller marks it delivered only on the beat's 2xx.
        /// <paramref name="pendingPriorUserId"/> is the v0.16 identity-upgrade marker this beat is
        /// carrying (null when none pending) — committed on the beat's 2xx only, like the metadata.</summary>
        private string buildHeartbeatJson(
            out string pendingMetadataJson, out long metadataEpoch, out bool carriedDevice,
            out string pendingPriorUserId)
        {
            pendingMetadataJson = null;
            metadataEpoch = 0;
            carriedDevice = false;
            pendingPriorUserId = null;
            try
            {
                var hb = new HeartbeatPayload
                {
                    sessionId = _sessionId,
                    occurredAtIso = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    buildVersion = TombstackPlatform.BuildVersion(),
                    os = TombstackPlatform.Os(),
                    arch = TombstackPlatform.Arch(),
                    userId = Tombstack.CurrentUserId,
                    role = Tombstack.CurrentRole,
                    serverId = Tombstack.CurrentServerId,
                    matchId = Tombstack.CurrentMatchId,
                    // Server fleet metadata (SetServerInfo); "" when unset, cleaned to undefined server-side.
                    region = Tombstack.CurrentRegion,
                    hostname = Tombstack.CurrentHostname,
                    // Deployment environment (Init/SetEnvironment; default "production") — feeds the dashboard filter.
                    environment = Tombstack.CurrentEnvironment,
                };
                var json = JsonUtility.ToJson(hb);
                // JsonUtility can't serialize the per-user metadata Dictionary, so splice the pre-built
                // "metadata":{...} object in before the closing brace. Change-detected: non-null only when
                // the map changed since the last beat ("{}" when just cleared → the server deletes it).
                var metaJson = Tombstack.PeekUserMetadataForHeartbeat(out metadataEpoch);
                if (metaJson != null && json.Length >= 2 && json[json.Length - 1] == '}')
                {
                    json = json.Substring(0, json.Length - 1) + ",\"metadata\":" + metaJson + "}";
                    pendingMetadataJson = metaJson; // commit this exact value once the beat is acked (M1)
                }
                // Frame stats (0.11): OPTIONAL numeric fields, spliced like the metadata object —
                // JsonUtility serializes every declared field, so absent-when-unsampled must bypass
                // it. Null when no frame was sampled this interval (fields omitted entirely);
                // draining here also resets the accumulator for the next heartbeat interval.
                var frameJson = TombstackFrameStats.ConsumeJson();
                if (frameJson != null && json.Length >= 2 && json[json.Length - 1] == '}')
                {
                    json = json.Substring(0, json.Length - 1) + "," + frameJson + "}";
                }
                // Device snapshot (0.14): spliced like the metadata object, carried until one beat
                // is ACKED (see _deviceSentOnHeartbeat) — gives the player profile hardware specs
                // for every session, not just crashing ones.
                if (!_deviceSentOnHeartbeat && json.Length >= 2 && json[json.Length - 1] == '}')
                {
                    var deviceJson = Tombstack.DeviceJsonForHeartbeat();
                    if (deviceJson != null)
                    {
                        json = json.Substring(0, json.Length - 1) + ",\"device\":" + deviceJson + "}";
                        carriedDevice = true;
                    }
                }
                // Retained past sessions (v0.18): the session ids whose logs this client still holds,
                // so the server can target a PAST session this client was in with a log pull. Spliced
                // manually (like the metadata object) and ONLY when non-empty, so a client with no
                // retained past logs sends a byte-identical (unchanged) heartbeat. JsonUtility handles
                // string[], but auto-serializing would emit "[]" on every empty beat — hence the splice.
                var retained = TombstackSessionLog.RetainedSessionIds();
                if (retained != null && retained.Count > 0 && json.Length >= 2 && json[json.Length - 1] == '}')
                {
                    var arr = new StringBuilder(retained.Count * 34 + 20);
                    arr.Append('[');
                    for (int i = 0; i < retained.Count; i++)
                    {
                        if (i > 0) arr.Append(',');
                        TombstackJson.AppendString(arr, retained[i]);
                    }
                    arr.Append(']');
                    json = json.Substring(0, json.Length - 1) + ",\"retainedSessions\":" + arr + "}";
                }
                // Identity-upgrade marker (v0.16): spliced like the metadata object (the field is
                // absent from HeartbeatPayload so a beat with nothing pending omits it entirely).
                // Carried until a beat delivering it is ACKED — handleResult commits on the 2xx.
                var priorUserId = Tombstack.PeekPriorUserIdForHeartbeat();
                if (priorUserId != null && json.Length >= 2 && json[json.Length - 1] == '}')
                {
                    var escaped = new StringBuilder(priorUserId.Length + 2);
                    TombstackJson.AppendString(escaped, priorUserId);
                    json = json.Substring(0, json.Length - 1) + ",\"priorUserId\":" + escaped + "}";
                    pendingPriorUserId = priorUserId; // clear this exact value once the beat is acked
                }
                return json;
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"heartbeat build failed: {e.Message}");
                return null;
            }
        }

        private IEnumerator send(PendingUpload item)
        {
            _inFlight++;
            try
            {
                var req = buildRequest(item);
                if (req == null) yield break;
                // §K1: time the round trip with the monotonic Stopwatch clock (zero-alloc — no
                // Stopwatch instance), so a successful ingest POST can emit a tombstack.rtt_ms metric.
                long startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                yield return req.SendWebRequest();
                bool success = req.result == UnityWebRequest.Result.Success;
                // Learn the server clock from EVERY reply of our own endpoint, failures included — the
                // 401 a skewed clock earns is exactly the reply that must teach the fix (H1). Not from
                // presigned S3 uploads: a different host, and nothing is signed with our clock there.
                if (!item.IsLogPut)
                {
                    TombstackHttp.ObserveDateHeader(req.GetResponseHeader("Date"));
                    // The server says which per-session budget it applies to this studio ("none" for a
                    // paying one) — the SDK stops dropping client-side accordingly (TombstackSessionBudget).
                    TombstackSessionBudget.ObserveServerHeader(req.GetResponseHeader(TombstackSessionBudget.SERVER_BUDGET_HEADER));
                }
                handleResult(item, req);
                req.Dispose();
                maybeEmitRtt(item, success, startTicks);
                maybeReportDrops(item, success);
            }
            finally
            {
                _inFlight--;
            }
        }

        /// <summary>True for the ingest ingestion endpoints (crashes/bug-reports/events/heartbeats and
        /// the events:batch/metrics:batch routes) — the only paths that get signed (§S3) and RTT-timed
        /// (§K1). The editor and pull-request endpoints are deliberately excluded.</summary>
        private static bool isIngestPath(string path)
            => path != null && path.StartsWith("/api/v1/ingest/", StringComparison.Ordinal);

        /// <summary>True for a pull-request FULFIL POST (<c>/api/v1/pull-requests/{id}/fulfill</c>).
        /// These carry a short-TTL nonce + presign, so an offline-persisted retry on the next launch is
        /// guaranteed to fail (403) — they must NOT be persisted (SDK-5), exactly like log PUTs.</summary>
        private static bool isFulfilPath(string path)
            => path != null && path.StartsWith(PULL_REQUESTS_PATH, StringComparison.Ordinal)
               && path.EndsWith("/fulfill", StringComparison.Ordinal);

        /// <summary>
        /// §K1: after a successful ingest POST, emit the round-trip time as a <c>tombstack.rtt_ms</c>
        /// metric via the normal TrackMetric batch path. Opt-in via <see cref="Tombstack.AutoRttMetricEnabled"/>
        /// (default ON). Recursion guard: the metrics:batch send is skipped, so emitting the RTT metric
        /// can never trigger measuring the RTT metric's own batch. Fail-silent.
        /// </summary>
        private void maybeEmitRtt(PendingUpload item, bool success, long startTicks)
        {
            try
            {
                if (!success || !Tombstack.AutoRttMetricEnabled) return;
                if (item.IsLogPut || !isIngestPath(item.Path)) return;
                if (item.Path == METRICS_BATCH_PATH) return; // gate: don't measure the RTT metric's own batch
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - startTicks) * 1000.0
                            / System.Diagnostics.Stopwatch.Frequency;
                Tombstack.TrackMetric(RTT_METRIC_NAME, ms, "ms");
            }
            catch (System.Exception e) { TombstackLog.Warn("rtt metric failed: " + e.Message); }
        }

        /// <summary>
        /// Sentry-style client report: after a successful ingest POST, ship however many payloads the
        /// SDK has discarded since the last report as <c>tombstack.dropped_*</c> metrics, so the studio
        /// sees them as their own series in the dashboard instead of losing telemetry in silence.
        ///
        /// Tied to a SUCCESS on purpose. Drops overwhelmingly happen while a device is offline or its
        /// queues are backed up, which is precisely when a report cannot get out — sending on the drop
        /// itself would queue behind the backlog, or add to it. A success is the SDK's proof that the
        /// pipe is open again. Same recursion gate as the RTT metric: the metrics batch that carries a
        /// drop report must not be able to trigger another one. Fail-silent.
        /// </summary>
        private void maybeReportDrops(PendingUpload item, bool success)
        {
            try
            {
                if (!success || item.IsLogPut || !isIngestPath(item.Path)) return;
                if (item.Path == METRICS_BATCH_PATH) return; // gate: a drop report can't report on itself
                TombstackDrops.ReportPending();
            }
            catch (System.Exception e) { TombstackLog.Warn("drop report failed: " + e.Message); }
        }

        /// <summary>Build the request; returns null (never throws) on internal failure.
        /// Log PUTs go straight to the presigned S3 URL — text/plain, NO Authorization header
        /// (the game token must never leak to the storage host).</summary>
        private UnityWebRequest buildRequest(PendingUpload item)
        {
            try
            {
                if (item.IsLogPut)
                {
                    // Presigned S3 POST (multipart/form-data): append the server's policy fields first,
                    // then the `file` part LAST (S3 ignores any field after `file`). NO Authorization
                    // header — the game token must never reach the storage host. S3 enforces the size
                    // cap via the content-length-range condition baked into the signed policy.
                    var sections = new List<IMultipartFormSection>();
                    if (item.FormFields != null)
                    {
                        foreach (var f in item.FormFields)
                            if (f != null && !string.IsNullOrEmpty(f.k))
                                sections.Add(new MultipartFormDataSection(f.k, f.v ?? string.Empty));
                    }
                    var contentType = item.PutContentType ?? CONTENT_TYPE_TEXT_PLAIN;
                    sections.Add(new MultipartFormFileSection("file", item.RawBody, "upload", contentType));
                    var post = UnityWebRequest.Post(item.AbsoluteUrl, sections);
                    post.timeout = LOG_UPLOAD_TIMEOUT_SECONDS;
                    return post;
                }
                // Encoded ONCE: the same bytes are uploaded and signed. (Signing used to build a second
                // full-size "<t>.<body>" string and encode that too — two extra body-sized allocations
                // per send, on the main thread.) Kept on the main thread deliberately: the quit/pause
                // paths rely on send() issuing the request before its first yield, which a thread-pool
                // hop would break.
                var bodyBytes = Encoding.UTF8.GetBytes(item.Body);
                var req = new UnityWebRequest(_endpoint + item.Path, UnityWebRequest.kHttpVerbPOST);
                req.uploadHandler = new UploadHandlerRaw(bodyBytes);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Authorization", "Bearer " + _gameToken);
                // §S3: HMAC-sign ingest POSTs only (NOT editor/pull endpoints). Fail-silent — a null
                // header means signing failed and the request goes out unsigned (server allows unsigned
                // during rollout). Re-signed on every attempt, with the server-corrected clock, so a
                // retry after a clock-skew 401 carries a fresh, accepted timestamp.
                if (isIngestPath(item.Path))
                {
                    var signature = TombstackSign.BuildHeader(_gameToken, bodyBytes);
                    if (signature != null) req.SetRequestHeader("X-Tombstack-Signature", signature);
                }
                // WHICH SDK IS SPEAKING. Until this shipped, a Tombstack POST carried only Content-Type,
                // Authorization and (on ingest) the signature - no version and no User-Agent, since
                // UnityWebRequest sets none - so a wire bug could not be attributed to a release and no
                // deprecation could be scoped. Sent on EVERY request to our own endpoint, ingest and
                // pull alike: the pull/editor paths are exactly where a protocol change would land.
                // NOT on log/screenshot PUTs - those go to a presigned S3 URL, and an unsigned extra
                // header there is rejected by S3, not merely ignored.
                req.SetRequestHeader(CLIENT_HEADER, CLIENT_HEADER_VALUE);
                req.timeout = REQUEST_TIMEOUT_SECONDS;
                return req;
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"request build failed: {e.Message}");
                return null;
            }
        }

        /// <summary>Success → clean up (and chase a granted log presign); 4xx → drop poison
        /// payload; otherwise back off and retry. Log PUTs reuse the same backoff but are never
        /// persisted — a presigned URL is dead by the next launch anyway.</summary>
        private void handleResult(PendingUpload item, UnityWebRequest req)
        {
            try
            {
                if (req.result == UnityWebRequest.Result.Success)
                {
                    deletePersisted(item);
                    if (item.RequestedLog && !item.IsLogPut)
                        scheduleLogUpload(item, req.downloadHandler != null ? req.downloadHandler.text : null);
                    if (item.RequestedScreenshot && !item.IsLogPut)
                        scheduleScreenshotUpload(item, req.downloadHandler != null ? req.downloadHandler.text : null);
                    // Command channel: a heartbeat ack may carry pull requests targeting this client.
                    if (!item.IsLogPut && item.Path == HEARTBEATS_PATH)
                    {
                        handleHeartbeatAck(req.downloadHandler != null ? req.downloadHandler.text : null);
                        // The beat delivered — advance the metadata baseline now (M1). Epoch-guarded so a
                        // pre-login beat's late ack can't clobber a SetUser baseline reset (M2).
                        if (item.PendingUserMetadataJson != null)
                            Tombstack.CommitUserMetadataForHeartbeat(item.PendingUserMetadataJson, item.PendingUserMetadataEpoch);
                        // The one-time device snapshot delivered — stop carrying it (0.14).
                        if (item.CarriedDevice) _deviceSentOnHeartbeat = true;
                        // The identity-upgrade marker delivered — one-shot, stop sending it (v0.16).
                        if (item.PendingPriorUserId != null)
                            Tombstack.CommitPriorUserIdSent(item.PendingPriorUserId);
                    }
                    return;
                }

                long code = req.responseCode;
                // H1: a 401 is NOT automatically poison. invalid_signature (and a bare 401) is almost
                // always a device clock more than 300s off; the Date header of this very reply has
                // already corrected the signing clock (send()), so a retry succeeds. It used to be
                // treated as poison, which DELETED the spooled crash report of every player whose
                // clock was wrong. invalid_api_key is a real credential failure: analytics drop as
                // before, but a crash/bug report is never deleted over it.
                var unauthorized = item.IsLogPut
                    ? UnauthorizedKind.None
                    : TombstackHttp.ClassifyUnauthorized(code, req.downloadHandler != null ? req.downloadHandler.text : null);
                if (unauthorized == UnauthorizedKind.Key && item.Durability == UploadDurability.WriteAhead)
                {
                    // Retrying this session cannot help; the record stays on disk and is tried once per
                    // launch, so a restored or re-minted key still delivers it.
                    TombstackLog.Warn($"the SDK token was refused (HTTP 401) for {item.Path}; the report stays on disk for the next launch.");
                    // Only an item whose spool write failed has nowhere to wait — that one is lost.
                    if (string.IsNullOrEmpty(item.FilePath)) TombstackDrops.Record(DropReason.Rejected);
                    return;
                }
                bool poison = code >= 400 && code < 500
                              && code != HTTP_REQUEST_TIMEOUT && code != HTTP_TOO_MANY_REQUESTS
                              && unauthorized != UnauthorizedKind.Signature;
                if (poison)
                {
                    // Rejected by validation/auth (or an expired presign) — retrying forever
                    // would just burn quota.
                    TombstackLog.Warn($"payload rejected with HTTP {code}; dropping ({(item.IsLogPut ? "log upload" : item.Path)}).");
                    TombstackDrops.Record(DropReason.Rejected);
                    deletePersisted(item);
                    return;
                }

                if (item.Durability == UploadDurability.Ephemeral) return;

                if (item.Attempt < MAX_RETRY_ATTEMPTS)
                {
                    // Honour the rate limiter's Retry-After (it is set on every 429): retrying sooner
                    // only earns another 429 and spends an attempt. Parsed against the server clock.
                    float retryAfter = code == HTTP_TOO_MANY_REQUESTS
                        ? TombstackHttp.ParseRetryAfterSeconds(
                            req.GetResponseHeader("Retry-After"),
                            DateTimeOffset.UtcNow.AddSeconds(TombstackHttp.OffsetSeconds))
                        : -1f;
                    StartCoroutine(retryLater(item, retryAfter));
                    return;
                }

                // Final in-session failure: make sure it survives to the next launch.
                // (Log PUTs are exempt: the presigned URL will have expired by then. Pull-request
                // FULFIL POSTs are exempt for the same reason — the fulfil nonce has a short TTL
                // (~120s), so a next-launch retry is guaranteed a 403 → poison drop; and PersistedRecord
                // carries no TargetSessionId, so a restored past-session fulfil would read the wrong
                // log. SDK-5: don't persist fulfils.)
                if (item.FilePath == null && !item.IsLogPut && !isFulfilPath(item.Path)) persist(item);
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"upload bookkeeping failed: {e.Message}");
            }
        }

        /// <summary>
        /// A crash/bug report that asked for a log upload got its 2xx — parse the presign and
        /// queue the PUT. The file read (≤512 KB) happens on the thread pool, never the main
        /// thread; the resulting raw-bytes item joins the normal outbound queue. Fail-soft:
        /// any parse/read failure drops the log, never the (already delivered) report.
        /// </summary>
        private void scheduleLogUpload(PendingUpload item, string responseText)
        {
            try
            {
                if (string.IsNullOrEmpty(responseText)) return;
                var response = JsonUtility.FromJson<IngestResponse>(responseText);
                var target = response != null && response.data != null ? response.data.logUpload : null;
                // JsonUtility may default-construct absent nested objects — the url is the
                // only reliable presence signal.
                if (target == null || string.IsNullOrEmpty(target.url)) return;

                bool fromPrevious = item.FromPreviousSession;
                if (fromPrevious && Interlocked.Exchange(ref _previousLogClaimed, 1) == 1)
                    return; // the most-recent-prior session log already uploaded (or in flight) this launch

                var url = target.url;
                var formFields = target.formFields;
                // v0.18: a past-session pull reads THAT specific retained session's log; a
                // most-recent-prior (unclean-shutdown) upload reads the previous log; otherwise the
                // current session's log. targetSessionId wins only when it is a genuine PAST session.
                string targetSessionId = item.TargetSessionId;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        byte[] bytes;
                        bool ok;
                        if (fromPrevious)
                            ok = TombstackSessionLog.TryReadPreviousLog(out bytes);
                        else if (!string.IsNullOrEmpty(targetSessionId))
                            ok = TombstackSessionLog.TryReadSessionLog(targetSessionId, out bytes);
                        else
                            ok = TombstackSessionLog.TryReadCurrentLog(out bytes);
                        if (!ok) return;
                        enqueueOutbound(PendingUpload.LogPut(url, bytes, formFields));
                    }
                    catch (Exception e)
                    {
                        TombstackLog.Warn($"log upload prep failed: {e.Message}");
                    }
                });
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"log presign handling failed: {e.Message}");
            }
        }

        /// <summary>
        /// A crash/bug that carried a screenshot got its 2xx — parse data.screenshotUpload and queue
        /// the PUT of the captured PNG (already in memory; no file read). Best-effort: any failure
        /// drops the screenshot, never the already-delivered report.
        /// </summary>
        private void scheduleScreenshotUpload(PendingUpload item, string responseText)
        {
            try
            {
                if (item.ScreenshotBytes == null || item.ScreenshotBytes.Length == 0) return;
                if (string.IsNullOrEmpty(responseText)) return;
                var response = JsonUtility.FromJson<IngestResponse>(responseText);
                var target = response != null && response.data != null ? response.data.screenshotUpload : null;
                // JsonUtility default-constructs absent nested objects — the url is the only reliable signal.
                if (target == null || string.IsNullOrEmpty(target.url)) return;
                enqueueOutbound(PendingUpload.ScreenshotPut(target.url, item.ScreenshotBytes, target.formFields));
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"screenshot presign handling failed: {e.Message}");
            }
        }

        /// <summary>
        /// Parse the heartbeat ack and, for each pending pull request that targets THIS client (and
        /// only while consent is granted), POST a fulfilment that requests a log presign — the
        /// existing logUpload chase then PUTs the current session log off-thread (§15). Fail-soft
        /// throughout: a non-targeted or non-consented client uploads nothing. Allocation-light: a
        /// beat with no pending requests does a single ordinal substring check and returns without
        /// deserializing (the empty-list ack carries no <c>requestId</c>).
        /// </summary>
        private void handleHeartbeatAck(string responseText)
        {
            try
            {
                // Consent gate FIRST: a non-consented client never uploads its log, even when targeted.
                if (string.IsNullOrEmpty(responseText) || !Tombstack.CaptureAllowed) return;
                // Cheap fast-path: skip JsonUtility entirely when there is nothing to honour. An empty
                // pendingRequests list has no "requestId" key, so most heartbeats short-circuit here.
                if (responseText.IndexOf("\"requestId\"", StringComparison.Ordinal) < 0) return;

                var ack = JsonUtility.FromJson<HeartbeatAck>(responseText);
                var pending = ack != null && ack.data != null ? ack.data.pendingRequests : null;
                if (pending == null || pending.Length == 0) return;

                string userId = Tombstack.CurrentUserId ?? "";
                string sessionId = _sessionId ?? "";
                string matchId = Tombstack.CurrentMatchId ?? "";
                string serverId = Tombstack.CurrentServerId ?? "";

                foreach (var p in pending)
                {
                    if (p == null || string.IsNullOrEmpty(p.requestId)) continue;
                    if (!targetsThisClient(p, userId, sessionId, matchId, serverId)) continue;

                    // v0.18: a sessionId pull may target a PAST session this client retains (not the
                    // current one). When so, the fulfil's asserted sessionId + the uploaded log must be
                    // that PAST session's, while the nonce still belongs to the CURRENT beat session.
                    bool isPastSession = p.targetType == "sessionId"
                                         && !string.Equals(p.targetValue, sessionId, StringComparison.Ordinal)
                                         && TombstackSessionLog.HasRetainedSession(p.targetValue);
                    string uploadSessionId = isPastSession ? p.targetValue : _sessionId;

                    var payload = new PullFulfillPayload
                    {
                        userId = Tombstack.CurrentUserId,                          // null → "" via JsonUtility; server cleans it
                        // For a past-session pull this is the PAST session being uploaded for; otherwise
                        // the session this client heartbeated with. (The nonce below stays bound to the
                        // CURRENT beat session regardless.)
                        sessionId = uploadSessionId,
                        // Always the CURRENT beat session — the nonce was minted over it, so the server
                        // verifies against this (never the possibly-past upload session). Non-empty so
                        // the server's optional min(1) field is satisfied on every fulfil.
                        nonceSessionId = _sessionId,
                        matchId = string.IsNullOrEmpty(matchId) ? null : matchId,
                        serverId = string.IsNullOrEmpty(serverId) ? null : serverId,
                        nonce = p.fulfillNonce,                                    // present the fulfilment nonce from the ack
                        nonceExpiry = p.nonceExpiry,
                    };
                    // requestLog:true → the fulfil 2xx's data.logUpload is chased exactly like a crash/bug,
                    // reusing scheduleLogUpload (log read ≤512 KB on the ThreadPool, never the main thread).
                    // targetSessionId (past-session pulls only) tells scheduleLogUpload which retained
                    // session's bytes to read; null ⇒ the current session log (unchanged behaviour).
                    Enqueue($"{PULL_REQUESTS_PATH}/{p.requestId}/fulfill",
                        JsonUtility.ToJson(payload), UploadDurability.PersistOnFailure, requestLog: true,
                        targetSessionId: isPastSession ? p.targetValue : null);
                }
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"heartbeat ack handling failed: {e.Message}");
            }
        }

        /// <summary>Mirror of the server's heartbeatMatchesRequest — the client uploads only when it
        /// is genuinely targeted (and only ever its OWN log). An empty asserted id never matches.
        /// v0.18: a <c>sessionId</c> pull ALSO matches when the target is one of this client's RETAINED
        /// PAST session ids — a client that was on a past server boot reconnects in a NEW session, so
        /// without this a session-targeted pull for that past boot would never match.</summary>
        private static bool targetsThisClient(
            PullRequestDto p, string userId, string sessionId, string matchId, string serverId)
        {
            switch (p.targetType)
            {
                case "userId": return !string.IsNullOrEmpty(userId) && userId == p.targetValue;
                case "sessionId":
                    if (!string.IsNullOrEmpty(sessionId) && sessionId == p.targetValue) return true;
                    // Past-session pull: match a retained session id (log still on disk to upload).
                    return TombstackSessionLog.HasRetainedSession(p.targetValue);
                case "matchId": return !string.IsNullOrEmpty(matchId) && matchId == p.targetValue;
                case "server": return !string.IsNullOrEmpty(serverId) && serverId == p.targetValue;
                default: return false;
            }
        }

        /// <summary>Re-enqueue after an exponential backoff delay (2s → 32s), or after the server's
        /// <c>Retry-After</c> when that is longer (<paramref name="minDelaySeconds"/>; negative = none).</summary>
        private IEnumerator retryLater(PendingUpload item, float minDelaySeconds)
        {
            float delay = Mathf.Max(RETRY_BASE_DELAY_SECONDS * (1 << item.Attempt), minDelaySeconds);
            item.Attempt++;
            // Realtime for the same reason as the heartbeat loop: an upload backoff is wall-clock, and a
            // paused game must not park a pending crash report indefinitely.
            yield return new WaitForSecondsRealtime(delay);
            enqueueOutbound(item);
        }

        /// <summary>Write a payload to the offline queue (bounded, with a crash/bug reserve — see
        /// <see cref="MAX_PERSISTED_ANALYTICS_FILES"/>). Thread-safe, never throws.</summary>
        private static void persist(PendingUpload item)
        {
            try
            {
                lock (_persistLock)
                {
                    if (string.IsNullOrEmpty(_queueDir)) return;
                    bool analytics = !isProtectedPath(item.Path);
                    // H3: analytics may fill only its share of the spool; the rest is reserved for crashes.
                    if (analytics && _persistedAnalytics.Count >= MAX_PERSISTED_ANALYTICS_FILES)
                    {
                        TombstackDrops.Record(DropReason.OfflineQueueFull);
                        return;
                    }
                    if (_persistedCount >= MAX_PERSISTED_FILES && (analytics || !evictOldestAnalyticsLocked()))
                    {
                        // The offline sidecar is full (and, for a crash/bug report, holds no analytics
                        // file to make room with). This is NOT harmless: the write-ahead call above
                        // loses its durability (a crash report enqueued now dies with the process), and
                        // the last-resort persist in handleResult — the one that runs after five failed
                        // in-session attempts — discards the payload outright. It used to return in
                        // silence. Now it is counted, warned once, written into the session log that
                        // ships with crash reports, and reported as a metric on the next good upload.
                        TombstackDrops.Record(DropReason.OfflineQueueFull);
                        return;
                    }
                    Directory.CreateDirectory(_queueDir);
                    var record = new PersistedRecord { path = item.Path, body = item.Body, requestLog = item.RequestedLog };
                    var file = Path.Combine(_queueDir, Guid.NewGuid().ToString("N") + ".json");
                    File.WriteAllText(file, JsonUtility.ToJson(record));
                    item.FilePath = file;
                    _persistedCount++;
                    if (analytics) _persistedAnalytics.AddLast(item);
                }
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"could not persist payload for retry: {e.Message}");
            }
        }

        /// <summary>
        /// Make room for a crash/bug report in a full spool by deleting the OLDEST analytics file.
        /// That batch loses its durability (it may still be in memory and deliver this session), so
        /// it is counted as an offline-queue drop. False when no analytics file is spooled. Caller
        /// holds <see cref="_persistLock"/>.
        /// </summary>
        private static bool evictOldestAnalyticsLocked()
        {
            while (_persistedAnalytics.Count > 0)
            {
                var victim = _persistedAnalytics.First.Value;
                _persistedAnalytics.RemoveFirst();
                var path = victim.FilePath;
                if (string.IsNullOrEmpty(path)) continue; // already delivered
                try { File.Delete(path); }
                catch { /* best-effort; the count below still frees the slot */ }
                victim.FilePath = null;
                if (_persistedCount > 0) _persistedCount--;
                TombstackDrops.Record(DropReason.OfflineQueueFull);
                return true;
            }
            return false;
        }

        /// <summary>Remove a delivered (or poison) payload's backing file, if any. The path is
        /// re-read under the lock: an eviction may have taken the file in the meantime, and deleting
        /// (and un-counting) it twice would under-count the spool.</summary>
        private static void deletePersisted(PendingUpload item)
        {
            if (string.IsNullOrEmpty(item.FilePath)) return;
            try
            {
                lock (_persistLock)
                {
                    var path = item.FilePath;
                    if (string.IsNullOrEmpty(path)) return;
                    item.FilePath = null;
                    _persistedAnalytics.Remove(item);
                    if (_persistedCount > 0) _persistedCount--;
                    File.Delete(path);
                }
            }
            catch { /* best-effort; a leftover file is retried and de-duplicated server-side by ULID */ }
        }

        /// <summary>One outbound item: either a JSON POST to an ingest path, or a raw-bytes
        /// PUT of the session log to a presigned URL. Built via the factories only.</summary>
        private sealed class PendingUpload
        {
            public readonly string Path;
            public readonly string Body;
            public readonly UploadDurability Durability;
            public readonly bool RequestedLog;        // body carries "log":true → chase the presign on 2xx
            public readonly bool FromPreviousSession; // a granted presign uploads the most-recent-prior session log
            // v0.18: when set, a granted presign uploads THIS specific retained session's log (a
            // past-session pull) instead of the current session's. Null ⇒ current session. Mutable
            // (set right after Post by the pull-fulfil path); never persisted (a presign is dead by
            // the next launch anyway).
            public string TargetSessionId;
            public readonly bool IsLogPut;            // raw PUT to AbsoluteUrl instead of a JSON POST
            public readonly string AbsoluteUrl;
            public readonly byte[] RawBody;
            public readonly string PutContentType;    // content-type for the upload (null ⇒ text/plain)
            public readonly FormField[] FormFields;    // presigned-POST policy fields (log/screenshot uploads)
            // Screenshot carried with an ingest POST: chase data.screenshotUpload on 2xx and PUT these
            // bytes. Mutable (set right after Post by the capture path); best-effort, never persisted.
            public bool RequestedScreenshot;
            public byte[] ScreenshotBytes;
            // Heartbeat metadata delivery (M1): the metadata JSON this beat is carrying + the epoch it was
            // built under. On a 2xx, the baseline is advanced to this value (epoch-guarded). Null when the
            // beat carries no metadata change. Mutable, set right after Post; never persisted.
            public string PendingUserMetadataJson;
            public long PendingUserMetadataEpoch;
            // True when this heartbeat carries the one-time device snapshot (0.14) — on its 2xx the
            // behaviour stops attaching it for the rest of the session. Mutable, never persisted.
            public bool CarriedDevice;
            // v0.16: the identity-upgrade marker (priorUserId) this heartbeat is carrying; on its 2xx
            // the pending marker is cleared (ordinal-matched). Null when none. Mutable, never persisted.
            public string PendingPriorUserId;
            public string FilePath; // non-null when the item is backed by a persisted file
            public int Attempt;     // in-session retry counter

            private PendingUpload(
                string path, string body, UploadDurability durability, string filePath,
                bool requestedLog, bool fromPreviousSession, bool isLogPut, string absoluteUrl, byte[] rawBody,
                string putContentType, FormField[] formFields)
            {
                Path = path;
                Body = body;
                Durability = durability;
                FilePath = filePath;
                RequestedLog = requestedLog;
                FromPreviousSession = fromPreviousSession;
                IsLogPut = isLogPut;
                AbsoluteUrl = absoluteUrl;
                RawBody = rawBody;
                PutContentType = putContentType;
                FormFields = formFields;
            }

            /// <summary>A JSON ingest POST (crash, bug, event, heartbeat).</summary>
            public static PendingUpload Post(
                string path, string body, UploadDurability durability, string filePath,
                bool requestedLog, bool fromPreviousSession)
            {
                return new PendingUpload(
                    path, body, durability, filePath, requestedLog, fromPreviousSession, false, null, null, null, null);
            }

            /// <summary>A presigned session-log upload — a multipart/form-data POST to the presigned S3
            /// URL (policy fields + file). Retries with the shared backoff but is never persisted (the
            /// presigned URL is dead by the next launch anyway).</summary>
            public static PendingUpload LogPut(string absoluteUrl, byte[] bytes, FormField[] formFields)
            {
                return new PendingUpload(
                    null, null, UploadDurability.PersistOnFailure, null, false, false, true, absoluteUrl, bytes, null, formFields);
            }

            /// <summary>A presigned screenshot upload (image/png), multipart POST. Best-effort like a log upload.</summary>
            public static PendingUpload ScreenshotPut(string absoluteUrl, byte[] bytes, FormField[] formFields)
            {
                return new PendingUpload(
                    null, null, UploadDurability.PersistOnFailure, null, false, false, true, absoluteUrl, bytes,
                    CONTENT_TYPE_IMAGE_PNG, formFields);
            }
        }

        [Serializable]
        private sealed class PersistedRecord
        {
            public string path;
            public string body;
            // True when the body carries "log":true. Old (pre-0.5.0) records deserialize to
            // false — their retry simply skips the log upload. JsonUtility-safe by design.
            public bool requestLog;
        }
    }
}
