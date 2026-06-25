using System;
using System.Collections;
using System.Collections.Concurrent;
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
        // Soft cap on the in-memory outbound queue (mirrors the native worker's bounded
        // queue). A game spamming TrackEvent while offline must not grow it without bound.
        private const int MAX_OUTBOUND_QUEUE = 256;
        private const float MIN_HEARTBEAT_INTERVAL_SECONDS = 15f;
        private const float MAX_HEARTBEAT_INTERVAL_SECONDS = 600f;
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
        // §K1: name of the auto round-trip metric emitted after each successful ingest POST.
        private const string RTT_METRIC_NAME = "tombstack.rtt_ms";

        private static readonly ConcurrentQueue<PendingUpload> _outbound = new ConcurrentQueue<PendingUpload>();
        // Bounded, preallocated event/metric batch buffers (§16/§15): cap 256, flush at 50 items or
        // 10s age (so low-volume games still report), near-full, pause/quit, and pre-crash. Drop-oldest
        // beyond cap. Steady-state allocation-free — only a flush (rare) builds an envelope string.
        private static readonly TombstackBatch _eventBatch = new TombstackBatch(256, 50, 10f);
        private static readonly TombstackBatch _metricBatch = new TombstackBatch(256, 50, 10f);
        private static readonly object _persistLock = new object();
        private static TombstackBehaviour _instance;
        private static string _queueDir;
        private static int _persistedCount;
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

        /// <summary>True when the offline queue held a crash report from a previous session.</summary>
        internal static bool HasRestoredCrash => _hasRestoredCrash;

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
        }

        /// <summary>
        /// Queue a payload for upload. Thread-safe (crashes can be enqueued from any thread).
        /// WriteAhead payloads are persisted to disk immediately so they survive a quit/crash.
        /// </summary>
        /// <param name="requestLog">True when the body carries <c>"log":true</c> — after the
        /// 2xx the response's <c>logUpload</c> presign is used to PUT the session log.</param>
        /// <param name="logFromPreviousSession">True when the granted presign should upload the
        /// preserved <c>previous-session.log</c> (unclean-shutdown report) instead of the
        /// current session's log.</param>
        internal static void Enqueue(
            string path, string json, UploadDurability durability,
            bool requestLog = false, bool logFromPreviousSession = false)
        {
            var item = PendingUpload.Post(path, json, durability, null, requestLog, logFromPreviousSession);
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
        /// </summary>
        private static void enqueueOutbound(PendingUpload item)
        {
            if (_outbound.Count >= MAX_OUTBOUND_QUEUE) dropOldestNonCrash();
            _outbound.Enqueue(item);
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
                TombstackLog.Warn("outbound queue full; dropped oldest non-crash payload.");
                return;
            }
        }

        /// <summary>Append a pre-serialized event item to the batch; flush immediately when the
        /// count/near-full trigger hits. Thread-safe; allocation-free below the flush threshold.</summary>
        internal static void AddEvent(string itemJson)
        {
            if (_eventBatch.Add(itemJson, monotonic())) flushOne(_eventBatch, EVENTS_BATCH_PATH);
        }

        /// <summary>Append a pre-serialized metric item to the batch; flush immediately when the
        /// count/near-full trigger hits. Thread-safe; allocation-free below the flush threshold.</summary>
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
            if (!batch.HasItems) return;
            var envelope = batch.DrainEnvelope(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
            if (envelope == null) return;
            _lastFlushAtSeconds = monotonic(); // §K3 diagnostics: stamp the last-flush time
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
                while (_inFlight < MAX_CONCURRENT_UPLOADS && _outbound.TryDequeue(out var item))
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

        // Unity message — flush buffered analytics when backgrounded (mobile) so a suspended/killed
        // app still reports. Engine-invoked; must stay PascalCase.
        private void OnApplicationPause(bool paused)
        {
            try { if (paused) FlushBatches(); }
            catch (System.Exception e) { TombstackLog.Warn("flush failed: " + e.Message); }
        }

        // Unity message — final flush on quit (complements Tombstack.onQuitting's log flush).
        private void OnApplicationQuit()
        {
            try { FlushBatches(); }
            catch (System.Exception e) { TombstackLog.Warn("flush failed: " + e.Message); }
        }

        private void loadPersistedQueue()
        {
            try
            {
                if (!Directory.Exists(_queueDir)) return;
                var files = Directory.GetFiles(_queueDir, "*.json");
                _persistedCount = files.Length;
                foreach (var file in files)
                {
                    var record = JsonUtility.FromJson<PersistedRecord>(File.ReadAllText(file));
                    if (record != null && !string.IsNullOrEmpty(record.path))
                    {
                        // Restored records came from an earlier run: a granted log presign must
                        // upload that run's preserved log, not this session's fresh one.
                        if (record.path == Tombstack.CRASHES_PATH) _hasRestoredCrash = true;
                        enqueueOutbound(PendingUpload.Post(
                            record.path, record.body, UploadDurability.WriteAhead, file,
                            record.requestLog, fromPreviousSession: true));
                    }
                }
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"could not load offline queue: {e.Message}");
            }
        }

        private IEnumerator heartbeatLoop()
        {
            var wait = new WaitForSeconds(_heartbeatIntervalSeconds);
            while (true)
            {
                // Consent-gated like every other capture; resumes when SetConsent(true) is called.
                if (Tombstack.CaptureAllowed)
                {
                    var json = buildHeartbeatJson();
                    if (json != null)
                    {
                        // Heartbeats are ephemeral — a missed beat is stale data, never retried.
                        yield return send(PendingUpload.Post(
                            HEARTBEATS_PATH, json, UploadDurability.Ephemeral, null, false, false));
                    }
                }
                yield return wait;
            }
        }

        /// <summary>Build the heartbeat body; userId attribution feeds the Sessions/Fleet screens.</summary>
        private string buildHeartbeatJson()
        {
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
                };
                return JsonUtility.ToJson(hb);
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
                handleResult(item, req);
                req.Dispose();
                maybeEmitRtt(item, success, startTicks);
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

        /// <summary>Build the request; returns null (never throws) on internal failure.
        /// Log PUTs go straight to the presigned S3 URL — text/plain, NO Authorization header
        /// (the game token must never leak to the storage host).</summary>
        private UnityWebRequest buildRequest(PendingUpload item)
        {
            try
            {
                if (item.IsLogPut)
                {
                    var put = new UnityWebRequest(item.AbsoluteUrl, UnityWebRequest.kHttpVerbPUT);
                    put.uploadHandler = new UploadHandlerRaw(item.RawBody);
                    put.downloadHandler = new DownloadHandlerBuffer();
                    put.SetRequestHeader("Content-Type", item.PutContentType ?? CONTENT_TYPE_TEXT_PLAIN);
                    put.timeout = LOG_UPLOAD_TIMEOUT_SECONDS;
                    return put;
                }
                var req = new UnityWebRequest(_endpoint + item.Path, UnityWebRequest.kHttpVerbPOST);
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(item.Body));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Authorization", "Bearer " + _gameToken);
                // §S3: HMAC-sign ingest POSTs only (NOT editor/pull endpoints). Fail-silent — a null
                // header means signing failed and the request goes out unsigned (server allows unsigned
                // during rollout). The HMAC is computed over the serialized body at send time (already
                // off the main game-frame path).
                if (isIngestPath(item.Path))
                {
                    var signature = TombstackSign.BuildHeader(_gameToken, item.Body);
                    if (signature != null) req.SetRequestHeader("X-Tombstack-Signature", signature);
                }
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
                        handleHeartbeatAck(req.downloadHandler != null ? req.downloadHandler.text : null);
                    return;
                }

                long code = req.responseCode;
                bool poison = code >= 400 && code < 500
                              && code != HTTP_REQUEST_TIMEOUT && code != HTTP_TOO_MANY_REQUESTS;
                if (poison)
                {
                    // Rejected by validation/auth (or an expired presign) — retrying forever
                    // would just burn quota.
                    TombstackLog.Warn($"payload rejected with HTTP {code}; dropping ({(item.IsLogPut ? "log upload" : item.Path)}).");
                    deletePersisted(item);
                    return;
                }

                if (item.Durability == UploadDurability.Ephemeral) return;

                if (item.Attempt < MAX_RETRY_ATTEMPTS)
                {
                    StartCoroutine(retryLater(item));
                    return;
                }

                // Final in-session failure: make sure it survives to the next launch.
                // (Log PUTs are exempt: the presigned URL will have expired by then.)
                if (item.FilePath == null && !item.IsLogPut) persist(item);
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
                    return; // previous-session.log already uploaded (or in flight) this launch

                var url = target.url;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        byte[] bytes;
                        bool ok = fromPrevious
                            ? TombstackSessionLog.TryReadPreviousLog(out bytes)
                            : TombstackSessionLog.TryReadCurrentLog(out bytes);
                        if (!ok) return;
                        enqueueOutbound(PendingUpload.LogPut(url, bytes));
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
                enqueueOutbound(PendingUpload.ScreenshotPut(target.url, item.ScreenshotBytes));
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

                    var payload = new PullFulfillPayload
                    {
                        userId = Tombstack.CurrentUserId,                          // null → "" via JsonUtility; server cleans it
                        sessionId = _sessionId,                                    // the session this client heartbeated with
                        matchId = string.IsNullOrEmpty(matchId) ? null : matchId,
                        serverId = string.IsNullOrEmpty(serverId) ? null : serverId,
                        nonce = p.fulfillNonce,                                    // present the fulfilment nonce from the ack
                        nonceExpiry = p.nonceExpiry,
                    };
                    // requestLog:true → the fulfil 2xx's data.logUpload is chased exactly like a crash/bug,
                    // reusing scheduleLogUpload (log read ≤512 KB on the ThreadPool, never the main thread).
                    Enqueue($"{PULL_REQUESTS_PATH}/{p.requestId}/fulfill",
                        JsonUtility.ToJson(payload), UploadDurability.PersistOnFailure, requestLog: true);
                }
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"heartbeat ack handling failed: {e.Message}");
            }
        }

        /// <summary>Mirror of the server's heartbeatMatchesRequest — the client uploads only when it
        /// is genuinely targeted (and only ever its OWN log). An empty asserted id never matches.</summary>
        private static bool targetsThisClient(
            PullRequestDto p, string userId, string sessionId, string matchId, string serverId)
        {
            switch (p.targetType)
            {
                case "userId": return !string.IsNullOrEmpty(userId) && userId == p.targetValue;
                case "sessionId": return !string.IsNullOrEmpty(sessionId) && sessionId == p.targetValue;
                case "matchId": return !string.IsNullOrEmpty(matchId) && matchId == p.targetValue;
                case "server": return !string.IsNullOrEmpty(serverId) && serverId == p.targetValue;
                default: return false;
            }
        }

        /// <summary>Re-enqueue after an exponential backoff delay (2s → 32s).</summary>
        private IEnumerator retryLater(PendingUpload item)
        {
            float delay = RETRY_BASE_DELAY_SECONDS * (1 << item.Attempt);
            item.Attempt++;
            yield return new WaitForSeconds(delay);
            enqueueOutbound(item);
        }

        /// <summary>Write a payload to the offline queue (bounded). Thread-safe, never throws.</summary>
        private static void persist(PendingUpload item)
        {
            try
            {
                lock (_persistLock)
                {
                    if (string.IsNullOrEmpty(_queueDir) || _persistedCount >= MAX_PERSISTED_FILES) return;
                    Directory.CreateDirectory(_queueDir);
                    var record = new PersistedRecord { path = item.Path, body = item.Body, requestLog = item.RequestedLog };
                    var file = Path.Combine(_queueDir, Guid.NewGuid().ToString("N") + ".json");
                    File.WriteAllText(file, JsonUtility.ToJson(record));
                    item.FilePath = file;
                    _persistedCount++;
                }
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"could not persist payload for retry: {e.Message}");
            }
        }

        /// <summary>Remove a delivered (or poison) payload's backing file, if any.</summary>
        private static void deletePersisted(PendingUpload item)
        {
            if (string.IsNullOrEmpty(item.FilePath)) return;
            try
            {
                lock (_persistLock)
                {
                    File.Delete(item.FilePath);
                    if (_persistedCount > 0) _persistedCount--;
                }
                item.FilePath = null;
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
            public readonly bool FromPreviousSession; // a granted presign uploads previous-session.log
            public readonly bool IsLogPut;            // raw PUT to AbsoluteUrl instead of a JSON POST
            public readonly string AbsoluteUrl;
            public readonly byte[] RawBody;
            public readonly string PutContentType;    // content-type for a raw PUT (null ⇒ text/plain)
            // Screenshot carried with an ingest POST: chase data.screenshotUpload on 2xx and PUT these
            // bytes. Mutable (set right after Post by the capture path); best-effort, never persisted.
            public bool RequestedScreenshot;
            public byte[] ScreenshotBytes;
            public string FilePath; // non-null when the item is backed by a persisted file
            public int Attempt;     // in-session retry counter

            private PendingUpload(
                string path, string body, UploadDurability durability, string filePath,
                bool requestedLog, bool fromPreviousSession, bool isLogPut, string absoluteUrl, byte[] rawBody,
                string putContentType)
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
            }

            /// <summary>A JSON ingest POST (crash, bug, event, heartbeat).</summary>
            public static PendingUpload Post(
                string path, string body, UploadDurability durability, string filePath,
                bool requestedLog, bool fromPreviousSession)
            {
                return new PendingUpload(
                    path, body, durability, filePath, requestedLog, fromPreviousSession, false, null, null, null);
            }

            /// <summary>A presigned session-log PUT. Retries with the shared backoff but is
            /// never persisted (PersistOnFailure is bypassed for log PUTs in handleResult).</summary>
            public static PendingUpload LogPut(string absoluteUrl, byte[] bytes)
            {
                return new PendingUpload(
                    null, null, UploadDurability.PersistOnFailure, null, false, false, true, absoluteUrl, bytes, null);
            }

            /// <summary>A presigned screenshot PUT (image/png). Best-effort like a log PUT.</summary>
            public static PendingUpload ScreenshotPut(string absoluteUrl, byte[] bytes)
            {
                return new PendingUpload(
                    null, null, UploadDurability.PersistOnFailure, null, false, false, true, absoluteUrl, bytes,
                    CONTENT_TYPE_IMAGE_PNG);
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
