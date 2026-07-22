using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace AnkleBreaker.Tombstack
{
    /// <summary>
    /// Bounded rolling session log. Every Unity log line is mirrored into an in-memory pending
    /// buffer (any thread, lock-protected, reused StringBuilder — no per-line concatenation) and
    /// flushed to <c>persistentDataPath/Tombstack/session-&lt;sessionId&gt;.log</c> off the main
    /// thread, at most once per flush interval (driven by <see cref="TombstackBehaviour"/>) plus a
    /// synchronous final flush on the crash path and on clean quit. The file is capped at ~512 KB
    /// with a truncate-from-front trim (newest lines win).
    ///
    /// v0.18: the log is keyed per SESSION. Each launch writes its own
    /// <c>session-&lt;sessionId&gt;.log</c>; at Init we keep only the newest N session logs
    /// (<see cref="Configure"/> retention count, default 3) and delete the rest, so a SPECIFIC past
    /// session's log survives for an on-demand server-side pull (<see cref="TryReadSessionLog"/>).
    /// The single most-recent PRIOR session log is still "the previous session" for unclean-shutdown
    /// upload (<see cref="RotateForNewSession"/> / <see cref="TryReadPreviousLog"/>). Legacy
    /// <c>session.log</c> / <c>previous-session.log</c> files from an older SDK are migrated to keyed
    /// names so nothing is lost.
    ///
    /// Fail-silent: every File.* call is wrapped; failures warn once and never loop back into
    /// the log mirror.
    /// </summary>
    internal static class TombstackSessionLog
    {
        private const string DIR_NAME = "Tombstack";
        // Legacy fixed names from pre-v0.18 SDKs — migrated to keyed names at RotateForNewSession.
        private const string LEGACY_CURRENT_LOG_NAME = "session.log";
        private const string LEGACY_PREVIOUS_LOG_NAME = "previous-session.log";
        private const string SESSION_LOG_PREFIX = "session-";
        private const string SESSION_LOG_EXTENSION = ".log";
        private const string SESSION_LOG_GLOB = SESSION_LOG_PREFIX + "*" + SESSION_LOG_EXTENSION;
        private const int MAX_LOG_BYTES = 512 * 1024;
        private const int TRIMMED_LOG_BYTES = MAX_LOG_BYTES / 2;
        private const int MAX_PENDING_CHARS = 64 * 1024;
        private const int PENDING_CAPACITY = 4 * 1024;
        private const string TIMESTAMP_FORMAT = "yyyy-MM-ddTHH:mm:ss.fffZ";
        // Sanitized session id in a filename: keep [A-Za-z0-9_-], capped so a hostile/huge id can't
        // blow the path length. Session ids are GUID "N" (32 hex) in practice — the cap is slack.
        private const int MAX_SESSION_ID_FILENAME = 64;
        // Retention: keep the newest N session logs. Clamped to this sane range (mirrors the config knob).
        private const int MIN_RETAINED_LOGS = 1;
        private const int MAX_RETAINED_LOGS = 10;
        private const int DEFAULT_RETAINED_LOGS = 3;

        private static readonly StringBuilder _pending = new StringBuilder(PENDING_CAPACITY);
        private static readonly object _pendingLock = new object();
        private static readonly object _fileLock = new object();
        // Cached delegate so the periodic flush never allocates a WaitCallback per call.
        private static readonly WaitCallback _flushWork = flushChunk;

        private static string _dirPath;
        private static string _sessionId;         // raw current session id (for TryReadSessionLog identity checks)
        private static string _currentPath;        // session-<sanitized current id>.log
        private static string _previousPath;       // most-recent PRIOR session log after RotateForNewSession (null when none)
        private static int _retainedLogs = DEFAULT_RETAINED_LOGS;
        // Snapshot of the retained PRIOR session ids (newest first, excluding the current session),
        // built at RotateForNewSession. Advertised on heartbeats + matched against session-targeted
        // pulls. Immutable after assignment (a new array is swapped in), so reads are lock-free.
        private static string[] _retainedPriorSessionIds = Array.Empty<string>();
        private static long _fileBytes;
        private static int _droppedLines;
        private static bool _warned; // warn-once latch: a failing disk must never spam or feedback-loop

        /// <summary>
        /// Cache paths once on the main thread at Init — <c>Application.persistentDataPath</c>
        /// is not safe to read off the main thread, and the flush worker runs on the pool. The
        /// current session's log is <c>session-&lt;sessionId&gt;.log</c>; <paramref name="retainedLogs"/>
        /// is how many recent session logs to keep (clamped to 1..10).
        /// </summary>
        internal static void Configure(string persistentDataPath, string sessionId, int retainedLogs)
        {
            try
            {
                if (string.IsNullOrEmpty(persistentDataPath) || string.IsNullOrEmpty(sessionId)) return;
                _dirPath = Path.Combine(persistentDataPath, DIR_NAME);
                _sessionId = sessionId;
                _retainedLogs = clampRetained(retainedLogs);
                _currentPath = Path.Combine(_dirPath, sessionLogFileName(sessionId));
                _fileBytes = 0;
            }
            catch (Exception e)
            {
                warnOnce(e.Message);
            }
        }

        /// <summary>
        /// Establish this session as the current log and prune retained logs to the newest N. Legacy
        /// <c>session.log</c> / <c>previous-session.log</c> from an older SDK are first migrated to keyed
        /// names (so their contents survive). The most-recent PRIOR <c>session-*.log</c> (not the current)
        /// becomes "the previous session" for the unclean-shutdown upload; returns true when such a prior
        /// log exists. Called once at Init, before any mirroring.
        /// </summary>
        internal static bool RotateForNewSession()
        {
            if (_currentPath == null) return false;
            try
            {
                lock (_fileLock)
                {
                    Directory.CreateDirectory(_dirPath);
                    migrateLegacyLogsLocked();

                    // All session logs EXCLUDING the current one, newest write first.
                    var priorFiles = enumeratePriorLogsNewestFirstLocked();

                    // The single most-recent prior log is "the previous session".
                    _previousPath = priorFiles.Count > 0 ? priorFiles[0] : null;

                    // Keep only the newest (N-1) PRIOR logs alongside the current one → at most N files.
                    // (The current session's own log counts toward the N budget.)
                    int keepPrior = _retainedLogs - 1;
                    if (keepPrior < 0) keepPrior = 0;
                    for (int i = keepPrior; i < priorFiles.Count; i++)
                    {
                        try { File.Delete(priorFiles[i]); }
                        catch (Exception e) { warnOnce(e.Message); }
                    }

                    // Snapshot the retained prior session ids (newest first) for heartbeat advertisement
                    // + session-pull matching. Capped at the kept count.
                    int keptCount = Math.Min(keepPrior, priorFiles.Count);
                    var ids = new List<string>(keptCount);
                    for (int i = 0; i < keptCount; i++)
                    {
                        var id = sessionIdFromPath(priorFiles[i]);
                        if (!string.IsNullOrEmpty(id)) ids.Add(id);
                    }
                    _retainedPriorSessionIds = ids.ToArray();

                    return _previousPath != null;
                }
            }
            catch (Exception e)
            {
                warnOnce(e.Message);
                return false;
            }
        }

        /// <summary>
        /// Buffer one log line (any thread). Allocates only the timestamp string — the line
        /// pieces are appended to the reused pending StringBuilder, never concatenated. When the
        /// pending buffer is full (flush stalled), lines are counted and dropped, not grown.
        /// </summary>
        internal static void Append(string level, string message, string stackTrace)
        {
            if (_currentPath == null || string.IsNullOrEmpty(message)) return;
            var ts = DateTime.UtcNow.ToString(TIMESTAMP_FORMAT);
            lock (_pendingLock)
            {
                if (_pending.Length >= MAX_PENDING_CHARS)
                {
                    _droppedLines++;
                    return;
                }
                _pending.Append(ts).Append(" [").Append(level).Append("] ").Append(message).Append('\n');
                if (!string.IsNullOrEmpty(stackTrace))
                {
                    _pending.Append(stackTrace);
                    if (stackTrace[stackTrace.Length - 1] != '\n') _pending.Append('\n');
                }
            }
        }

        /// <summary>
        /// Hand the pending buffer to the thread pool (no main-thread file I/O). Called by the
        /// behaviour's Update at most once per flush interval; no-op (zero alloc) when empty.
        /// </summary>
        internal static void RequestFlush()
        {
            var chunk = takePendingChunk();
            if (chunk == null) return;
            try
            {
                ThreadPool.QueueUserWorkItem(_flushWork, chunk);
            }
            catch (Exception e)
            {
                warnOnce(e.Message);
            }
        }

        /// <summary>
        /// Synchronous flush for the crash path and clean quit — the process may be about to
        /// die, so the on-disk file must include the final lines before we return.
        /// </summary>
        internal static void FlushNow()
        {
            var chunk = takePendingChunk();
            if (chunk != null) flushChunk(chunk);
        }

        /// <summary>Read the current session log (flushed first) for a crash/bug log upload.</summary>
        internal static bool TryReadCurrentLog(out byte[] bytes)
        {
            FlushNow();
            return tryRead(_currentPath, out bytes);
        }

        /// <summary>Read the most-recent PRIOR session log for an unclean-shutdown upload.</summary>
        internal static bool TryReadPreviousLog(out byte[] bytes)
        {
            return tryRead(_previousPath, out bytes);
        }

        /// <summary>
        /// Read a SPECIFIC session's retained log by id (for a past-session log pull). When the id is the
        /// CURRENT session, the pending buffer is flushed first so the returned bytes are up to date.
        /// Returns false when no such log is held (or on any read failure). Fail-silent.
        /// </summary>
        internal static bool TryReadSessionLog(string sessionId, out byte[] bytes)
        {
            bytes = null;
            if (string.IsNullOrEmpty(sessionId) || _dirPath == null) return false;
            // If they asked for the current session, flush first (identical to TryReadCurrentLog).
            if (string.Equals(sessionId, _sessionId, StringComparison.Ordinal))
                return TryReadCurrentLog(out bytes);
            try
            {
                var path = Path.Combine(_dirPath, sessionLogFileName(sessionId));
                return tryRead(path, out bytes);
            }
            catch (Exception e)
            {
                warnOnce(e.Message);
                return false;
            }
        }

        /// <summary>
        /// The session ids whose logs we currently retain, newest first, EXCLUDING the current session —
        /// advertised on heartbeats so a server-side pull can target a past session this client was in.
        /// Returns the snapshot built at <see cref="RotateForNewSession"/>; empty when none.
        /// </summary>
        internal static IReadOnlyList<string> RetainedSessionIds()
        {
            return _retainedPriorSessionIds;
        }

        /// <summary>True when <paramref name="sessionId"/> is one of the retained PRIOR session ids
        /// (used to match a past-session log pull). Ordinal, allocation-free.</summary>
        internal static bool HasRetainedSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return false;
            var ids = _retainedPriorSessionIds;
            for (int i = 0; i < ids.Length; i++)
                if (string.Equals(ids[i], sessionId, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>Swap out the pending text (and a drop marker, if any) under the lock.</summary>
        private static string takePendingChunk()
        {
            lock (_pendingLock)
            {
                if (_pending.Length == 0) return null;
                if (_droppedLines > 0)
                {
                    _pending.Append("[Tombstack] session log buffer overflow: ")
                        .Append(_droppedLines).Append(" lines dropped\n");
                    _droppedLines = 0;
                }
                var chunk = _pending.ToString();
                _pending.Length = 0;
                return chunk;
            }
        }

        /// <summary>Append a chunk to the current session log and trim from the front past the cap. Runs
        /// on the thread pool (periodic) or the crash thread (final flush); never throws.</summary>
        private static void flushChunk(object state)
        {
            try
            {
                var chunk = (string)state;
                lock (_fileLock)
                {
                    Directory.CreateDirectory(_dirPath);
                    File.AppendAllText(_currentPath, chunk);
                    _fileBytes += Encoding.UTF8.GetByteCount(chunk);
                    if (_fileBytes > MAX_LOG_BYTES) trimFromFront();
                }
            }
            catch (Exception e)
            {
                warnOnce(e.Message);
            }
        }

        /// <summary>Keep the newest ~256 KB, aligned to a whole line. Rare (cap-crossing) path.</summary>
        private static void trimFromFront()
        {
            var all = File.ReadAllBytes(_currentPath);
            if (all.Length <= TRIMMED_LOG_BYTES)
            {
                _fileBytes = all.Length;
                return;
            }
            int start = all.Length - TRIMMED_LOG_BYTES;
            while (start < all.Length && all[start] != (byte)'\n') start++;
            if (start < all.Length) start++;
            var kept = new byte[all.Length - start];
            Buffer.BlockCopy(all, start, kept, 0, kept.Length);
            File.WriteAllBytes(_currentPath, kept);
            _fileBytes = kept.Length;
        }

        private static bool tryRead(string path, out byte[] bytes)
        {
            bytes = null;
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
                bytes = File.ReadAllBytes(path);
                return bytes.Length > 0;
            }
            catch (Exception e)
            {
                warnOnce(e.Message);
                return false;
            }
        }

        /// <summary>Migrate pre-v0.18 fixed-name logs to keyed names so their contents are retained as
        /// prior sessions. The legacy files have no session id, so they're keyed to synthetic
        /// <c>legacy…</c> ids (their file times still order them correctly). Caller holds _fileLock.</summary>
        private static void migrateLegacyLogsLocked()
        {
            migrateOneLegacyLocked(LEGACY_PREVIOUS_LOG_NAME, "legacy-previous");
            migrateOneLegacyLocked(LEGACY_CURRENT_LOG_NAME, "legacy-current");
        }

        private static void migrateOneLegacyLocked(string legacyName, string syntheticId)
        {
            try
            {
                var legacyPath = Path.Combine(_dirPath, legacyName);
                if (!File.Exists(legacyPath)) return;
                var target = Path.Combine(_dirPath, sessionLogFileName(syntheticId));
                // Never clobber a real keyed log or overwrite a prior migration; drop the legacy file then.
                if (File.Exists(target)) { File.Delete(legacyPath); return; }
                File.Move(legacyPath, target);
            }
            catch (Exception e)
            {
                warnOnce(e.Message);
            }
        }

        /// <summary>All <c>session-*.log</c> files except the current session's, newest write-time first.
        /// Caller holds _fileLock.</summary>
        private static List<string> enumeratePriorLogsNewestFirstLocked()
        {
            var result = new List<string>();
            string[] files;
            try { files = Directory.GetFiles(_dirPath, SESSION_LOG_GLOB); }
            catch (Exception e) { warnOnce(e.Message); return result; }

            foreach (var f in files)
            {
                // Exclude the current session's own file (ordinal path compare; both are Path.Combine'd).
                if (string.Equals(f, _currentPath, StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(f);
            }
            // Newest last-write first. A sort by DateTime is fine at this small N (≤ dozens).
            result.Sort((a, b) => lastWriteTicks(b).CompareTo(lastWriteTicks(a)));
            return result;
        }

        private static long lastWriteTicks(string path)
        {
            try { return File.GetLastWriteTimeUtc(path).Ticks; }
            catch { return 0L; }
        }

        /// <summary>Build the on-disk file name for a session id (sanitized + capped).</summary>
        private static string sessionLogFileName(string sessionId)
        {
            return SESSION_LOG_PREFIX + sanitizeSessionId(sessionId) + SESSION_LOG_EXTENSION;
        }

        /// <summary>Recover a session id from a <c>session-&lt;id&gt;.log</c> path (best-effort; the id is
        /// the SANITIZED id, which for the GUID "N" ids used in practice is identical to the raw id).</summary>
        private static string sessionIdFromPath(string path)
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(path); // "session-<id>"
                if (string.IsNullOrEmpty(name) || !name.StartsWith(SESSION_LOG_PREFIX, StringComparison.Ordinal))
                    return null;
                return name.Substring(SESSION_LOG_PREFIX.Length);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Keep only [A-Za-z0-9_-] and cap the length, so an id can never escape the directory
        /// or blow the path length. Empty/invalid → "unknown".</summary>
        private static string sanitizeSessionId(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return "unknown";
            var sb = new StringBuilder(Math.Min(sessionId.Length, MAX_SESSION_ID_FILENAME));
            foreach (var c in sessionId)
            {
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                          || (c >= '0' && c <= '9') || c == '_' || c == '-';
                if (ok) sb.Append(c);
                if (sb.Length >= MAX_SESSION_ID_FILENAME) break;
            }
            return sb.Length == 0 ? "unknown" : sb.ToString();
        }

        private static int clampRetained(int value)
        {
            if (value < MIN_RETAINED_LOGS) return MIN_RETAINED_LOGS;
            if (value > MAX_RETAINED_LOGS) return MAX_RETAINED_LOGS;
            return value;
        }

        /// <summary>Warn exactly once. TombstackLog lines are excluded from mirroring, but a
        /// persistently failing disk (locked file, antivirus) must still never spam.</summary>
        private static void warnOnce(string message)
        {
            if (_warned) return;
            _warned = true;
            TombstackLog.Warn($"session log unavailable: {message}");
        }
    }
}
