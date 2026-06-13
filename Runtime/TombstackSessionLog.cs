using System;
using System.IO;
using System.Text;
using System.Threading;

namespace AnkleBreaker.Tombstack
{
    /// <summary>
    /// Bounded rolling session log. Every Unity log line is mirrored into an in-memory pending
    /// buffer (any thread, lock-protected, reused StringBuilder — no per-line concatenation) and
    /// flushed to <c>persistentDataPath/Tombstack/session.log</c> off the main thread, at most
    /// once per flush interval (driven by <see cref="TombstackBehaviour"/>) plus a synchronous
    /// final flush on the crash path and on clean quit. The file is capped at ~512 KB with a
    /// truncate-from-front trim (newest lines win). At Init the previous run's file rotates to
    /// <c>previous-session.log</c> so an unclean shutdown's log survives for next-launch upload.
    /// Fail-silent: every File.* call is wrapped; failures warn once and never loop back into
    /// the log mirror.
    /// </summary>
    internal static class TombstackSessionLog
    {
        private const string DIR_NAME = "Tombstack";
        private const string CURRENT_LOG_NAME = "session.log";
        private const string PREVIOUS_LOG_NAME = "previous-session.log";
        private const int MAX_LOG_BYTES = 512 * 1024;
        private const int TRIMMED_LOG_BYTES = MAX_LOG_BYTES / 2;
        private const int MAX_PENDING_CHARS = 64 * 1024;
        private const int PENDING_CAPACITY = 4 * 1024;
        private const string TIMESTAMP_FORMAT = "yyyy-MM-ddTHH:mm:ss.fffZ";

        private static readonly StringBuilder _pending = new StringBuilder(PENDING_CAPACITY);
        private static readonly object _pendingLock = new object();
        private static readonly object _fileLock = new object();
        // Cached delegate so the periodic flush never allocates a WaitCallback per call.
        private static readonly WaitCallback _flushWork = flushChunk;

        private static string _dirPath;
        private static string _currentPath;
        private static string _previousPath;
        private static long _fileBytes;
        private static int _droppedLines;
        private static bool _warned; // warn-once latch: a failing disk must never spam or feedback-loop

        /// <summary>
        /// Cache paths once on the main thread at Init — <c>Application.persistentDataPath</c>
        /// is not safe to read off the main thread, and the flush worker runs on the pool.
        /// </summary>
        internal static void Configure(string persistentDataPath)
        {
            try
            {
                if (string.IsNullOrEmpty(persistentDataPath)) return;
                _dirPath = Path.Combine(persistentDataPath, DIR_NAME);
                _currentPath = Path.Combine(_dirPath, CURRENT_LOG_NAME);
                _previousPath = Path.Combine(_dirPath, PREVIOUS_LOG_NAME);
            }
            catch (Exception e)
            {
                warnOnce(e.Message);
            }
        }

        /// <summary>
        /// Rotate the previous run's <c>session.log</c> to <c>previous-session.log</c> (stale
        /// previous logs are deleted either way) and reset for this session. Returns true when a
        /// previous-session log exists after rotation. Called once at Init, before any mirroring.
        /// </summary>
        internal static bool RotateForNewSession()
        {
            if (_currentPath == null) return false;
            try
            {
                lock (_fileLock)
                {
                    if (File.Exists(_previousPath)) File.Delete(_previousPath);
                    if (File.Exists(_currentPath)) File.Move(_currentPath, _previousPath);
                    _fileBytes = 0;
                    return File.Exists(_previousPath);
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

        /// <summary>Read the preserved previous-session log for an unclean-shutdown upload.</summary>
        internal static bool TryReadPreviousLog(out byte[] bytes)
        {
            return tryRead(_previousPath, out bytes);
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

        /// <summary>Append a chunk to session.log and trim from the front past the cap. Runs on
        /// the thread pool (periodic) or the crash thread (final flush); never throws.</summary>
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
