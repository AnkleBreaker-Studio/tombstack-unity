using System;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

namespace AnkleBreaker.Tombstone
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
    /// <see cref="Tombstone.Init(string,string,float)"/> and kept alive across scenes.
    /// Fail-silent: every internal failure is swallowed and logged via <see cref="TombstoneLog"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TombstoneBehaviour : MonoBehaviour
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
        private const string QUEUE_DIR_NAME = "Tombstone";
        private const string HEARTBEATS_PATH = "/api/v1/ingest/heartbeats";
        private const float LOG_FLUSH_INTERVAL_SECONDS = 5f;
        private const int LOG_UPLOAD_TIMEOUT_SECONDS = 30;
        private const string CONTENT_TYPE_TEXT_PLAIN = "text/plain";

        private static readonly ConcurrentQueue<PendingUpload> _outbound = new ConcurrentQueue<PendingUpload>();
        private static readonly object _persistLock = new object();
        private static TombstoneBehaviour _instance;
        private static string _queueDir;
        private static int _persistedCount;
        // True when the offline queue restored a crash report from the previous run — the
        // dirty-session detector then skips its synthetic report (no double-counting).
        private static volatile bool _hasRestoredCrash;
        // The preserved previous-session.log uploads at most once per launch, even if several
        // restored reports each get granted a presign.
        private static int _previousLogClaimed;

        private string _endpoint;
        private string _gameToken;
        private string _sessionId;
        private float _heartbeatIntervalSeconds;
        private int _inFlight;
        private float _nextLogFlushAt;

        /// <summary>True when the offline queue held a crash report from a previous session.</summary>
        internal static bool HasRestoredCrash => _hasRestoredCrash;

        /// <summary>Create the hidden singleton host and start the heartbeat + upload loops.</summary>
        internal static void Bootstrap(string endpoint, string gameToken, string sessionId, float heartbeatIntervalSeconds)
        {
            if (_instance != null) return;
            var go = new GameObject("[Tombstone]");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<TombstoneBehaviour>();
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
                TombstoneLog.Warn("outbound queue full; dropped oldest non-crash payload.");
                return;
            }
        }

        // Unity message — must stay PascalCase (engine-invoked). Allocates nothing when idle:
        // the queue drain no-ops and the log flush is a no-op call when the buffer is empty.
        private void Update()
        {
            while (_inFlight < MAX_CONCURRENT_UPLOADS && _outbound.TryDequeue(out var item))
            {
                StartCoroutine(send(item));
            }
            if (Time.unscaledTime >= _nextLogFlushAt)
            {
                _nextLogFlushAt = Time.unscaledTime + LOG_FLUSH_INTERVAL_SECONDS;
                TombstoneSessionLog.RequestFlush();
            }
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
                        if (record.path == Tombstone.CRASHES_PATH) _hasRestoredCrash = true;
                        enqueueOutbound(PendingUpload.Post(
                            record.path, record.body, UploadDurability.WriteAhead, file,
                            record.requestLog, logFromPreviousSession: true));
                    }
                }
            }
            catch (Exception e)
            {
                TombstoneLog.Warn($"could not load offline queue: {e.Message}");
            }
        }

        private IEnumerator heartbeatLoop()
        {
            var wait = new WaitForSeconds(_heartbeatIntervalSeconds);
            while (true)
            {
                // Consent-gated like every other capture; resumes when SetConsent(true) is called.
                if (Tombstone.CaptureAllowed)
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
                    buildVersion = TombstonePlatform.BuildVersion(),
                    os = TombstonePlatform.Os(),
                    arch = TombstonePlatform.Arch(),
                    userId = Tombstone.CurrentUserId,
                };
                return JsonUtility.ToJson(hb);
            }
            catch (Exception e)
            {
                TombstoneLog.Warn($"heartbeat build failed: {e.Message}");
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
                yield return req.SendWebRequest();
                handleResult(item, req);
                req.Dispose();
            }
            finally
            {
                _inFlight--;
            }
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
                    put.SetRequestHeader("Content-Type", CONTENT_TYPE_TEXT_PLAIN);
                    put.timeout = LOG_UPLOAD_TIMEOUT_SECONDS;
                    return put;
                }
                var req = new UnityWebRequest(_endpoint + item.Path, UnityWebRequest.kHttpVerbPOST);
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(item.Body));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Authorization", "Bearer " + _gameToken);
                req.timeout = REQUEST_TIMEOUT_SECONDS;
                return req;
            }
            catch (Exception e)
            {
                TombstoneLog.Warn($"request build failed: {e.Message}");
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
                    return;
                }

                long code = req.responseCode;
                bool poison = code >= 400 && code < 500
                              && code != HTTP_REQUEST_TIMEOUT && code != HTTP_TOO_MANY_REQUESTS;
                if (poison)
                {
                    // Rejected by validation/auth (or an expired presign) — retrying forever
                    // would just burn quota.
                    TombstoneLog.Warn($"payload rejected with HTTP {code}; dropping ({(item.IsLogPut ? "log upload" : item.Path)}).");
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
                TombstoneLog.Warn($"upload bookkeeping failed: {e.Message}");
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
                            ? TombstoneSessionLog.TryReadPreviousLog(out bytes)
                            : TombstoneSessionLog.TryReadCurrentLog(out bytes);
                        if (!ok) return;
                        enqueueOutbound(PendingUpload.LogPut(url, bytes));
                    }
                    catch (Exception e)
                    {
                        TombstoneLog.Warn($"log upload prep failed: {e.Message}");
                    }
                });
            }
            catch (Exception e)
            {
                TombstoneLog.Warn($"log presign handling failed: {e.Message}");
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
                TombstoneLog.Warn($"could not persist payload for retry: {e.Message}");
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
            public string FilePath; // non-null when the item is backed by a persisted file
            public int Attempt;     // in-session retry counter

            private PendingUpload(
                string path, string body, UploadDurability durability, string filePath,
                bool requestedLog, bool fromPreviousSession, bool isLogPut, string absoluteUrl, byte[] rawBody)
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
            }

            /// <summary>A JSON ingest POST (crash, bug, event, heartbeat).</summary>
            public static PendingUpload Post(
                string path, string body, UploadDurability durability, string filePath,
                bool requestedLog, bool fromPreviousSession)
            {
                return new PendingUpload(
                    path, body, durability, filePath, requestedLog, fromPreviousSession, false, null, null);
            }

            /// <summary>A presigned session-log PUT. Retries with the shared backoff but is
            /// never persisted (PersistOnFailure is bypassed for log PUTs in handleResult).</summary>
            public static PendingUpload LogPut(string absoluteUrl, byte[] bytes)
            {
                return new PendingUpload(
                    null, null, UploadDurability.PersistOnFailure, null, false, false, true, absoluteUrl, bytes);
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
