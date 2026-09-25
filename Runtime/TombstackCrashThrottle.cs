using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace AnkleBreaker.Tombstack
{
    /// <summary>
    /// Bounds what an exception STORM can cost a player's device. Three guards, all pure and
    /// thread-safe (exceptions arrive on any thread via <c>logMessageReceivedThreaded</c>):
    /// <list type="bullet">
    /// <item><b>Dedupe</b> — one report per crash KEY per window. The key is the exception type plus
    /// the normalized top frames (the message only when there is no stack, and then with its digits,
    /// ids and quoted literals collapsed). It used to be a hash of the full message, so an exception
    /// whose message interpolates a value (<c>"Index 4817 out of range"</c>) produced a new key per
    /// occurrence and bypassed dedupe entirely. The map is bounded by evicting expired, then oldest,
    /// entries; it used to be CLEARED at 64, which re-admitted every hot signature at once.</item>
    /// <item><b>Rate limit</b> — a global token bucket (<see cref="BUCKET_CAPACITY"/> reports, refilled
    /// at one per <see cref="REFILL_SECONDS"/>s, i.e. ten a minute) over everything that passed dedupe,
    /// so a storm of genuinely distinct exceptions cannot write a report file and a network request
    /// per frame. What the bucket refuses is counted and surfaced (breadcrumb, session log).</item>
    /// <item><b>Synchronous log flush pacing</b> — a non-fatal capture flushes the session log to
    /// disk synchronously at most once per <see cref="SYNC_FLUSH_MIN_INTERVAL_SECONDS"/>s.</item>
    /// </list>
    /// This key is CLIENT-SIDE only. The <c>signature</c> sent on the wire is unchanged: the server
    /// derives its own grouping from the stack, and its deterministic crash id includes the raw
    /// client signature, so changing that would re-key retried reports.
    /// </summary>
    internal static class TombstackCrashThrottle
    {
        internal const int DEDUPE_WINDOW_SECONDS = 60;
        internal const int MAX_TRACKED_KEYS = 64;
        internal const int BUCKET_CAPACITY = 10;
        internal const double REFILL_SECONDS = 6.0;
        internal const double SYNC_FLUSH_MIN_INTERVAL_SECONDS = 3.0;
        private const int KEY_FRAMES = 8;
        private const int MAX_KEY_LENGTH = 2048;
        private const char NUMBER_MARK = '#';
        private const string LITERAL_MARK = "<s>";
        // A mixed letter/digit hex run this long is an id (GUID segment, address, hash), not a word.
        private const int MIN_HEX_ID_LENGTH = 6;

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, KeyWindow> _recent =
            new Dictionary<string, KeyWindow>(StringComparer.Ordinal);
        private static double _tokens = BUCKET_CAPACITY;
        private static double _lastRefillSeconds = double.NaN;
        private static double _lastSyncFlushSeconds = double.NegativeInfinity;
        // Reports the bucket refused since the last one it admitted (surfaced on that next report).
        private static int _rateLimitedPending;
        // Reports the bucket refused this launch (diagnostics + the log-once latch).
        private static int _rateLimitedTotal;

        /// <summary>Reports the rate limiter refused this launch.</summary>
        internal static int RateLimitedTotal => Volatile.Read(ref _rateLimitedTotal);

        /// <summary>Forget all state (new session / static reset between editor play sessions).</summary>
        internal static void Reset()
        {
            lock (_lock)
            {
                _recent.Clear();
                _tokens = BUCKET_CAPACITY;
                _lastRefillSeconds = double.NaN;
                _lastSyncFlushSeconds = double.NegativeInfinity;
            }
            Interlocked.Exchange(ref _rateLimitedPending, 0);
            Interlocked.Exchange(ref _rateLimitedTotal, 0);
        }

        /// <summary>
        /// True when <paramref name="key"/> already reported inside the dedupe window;
        /// <paramref name="suppressedCount"/> is then the number of repeats since that report.
        /// A first sighting (or one past the window) claims the slot and returns false.
        /// </summary>
        internal static bool IsDuplicate(string key, double nowSeconds, out int suppressedCount)
        {
            suppressedCount = 0;
            lock (_lock)
            {
                if (_recent.TryGetValue(key, out var window))
                {
                    if (nowSeconds - window.LastSentSeconds < DEDUPE_WINDOW_SECONDS)
                    {
                        window.Suppressed++;
                        suppressedCount = window.Suppressed;
                        return true;
                    }
                    window.LastSentSeconds = nowSeconds;
                    window.Suppressed = 0;
                    return false;
                }
                if (_recent.Count >= MAX_TRACKED_KEYS) evictLocked(nowSeconds);
                _recent[key] = new KeyWindow { LastSentSeconds = nowSeconds };
                return false;
            }
        }

        /// <summary>
        /// Take one report token. False when the bucket is empty: the caller must not report, and
        /// the refusal is counted. On success <paramref name="refusedBefore"/> is how many reports
        /// were refused since the previous admitted one (so it can ride this report's breadcrumbs).
        /// </summary>
        internal static bool TryAcquire(double nowSeconds, out int refusedBefore)
        {
            refusedBefore = 0;
            lock (_lock)
            {
                if (double.IsNaN(_lastRefillSeconds)) _lastRefillSeconds = nowSeconds;
                double elapsed = nowSeconds - _lastRefillSeconds;
                if (elapsed > 0)
                {
                    _tokens = Math.Min(BUCKET_CAPACITY, _tokens + elapsed / REFILL_SECONDS);
                    _lastRefillSeconds = nowSeconds;
                }
                if (_tokens < 1.0)
                {
                    Interlocked.Increment(ref _rateLimitedPending);
                    Interlocked.Increment(ref _rateLimitedTotal);
                    return false;
                }
                _tokens -= 1.0;
            }
            refusedBefore = Interlocked.Exchange(ref _rateLimitedPending, 0);
            return true;
        }

        /// <summary>True when a non-fatal capture may flush the session log synchronously now (and
        /// claims the slot); false means "request an async flush instead".</summary>
        internal static bool TryClaimSyncFlush(double nowSeconds)
        {
            lock (_lock)
            {
                if (nowSeconds - _lastSyncFlushSeconds < SYNC_FLUSH_MIN_INTERVAL_SECONDS) return false;
                _lastSyncFlushSeconds = nowSeconds;
                return true;
            }
        }

        /// <summary>
        /// Client-side crash key: exception type + normalized top frames, or + the normalized message
        /// when there is no stack. <paramref name="exceptionType"/> may be null; the type is then read
        /// from a Unity-style condition (<c>"NullReferenceException: ..."</c>).
        /// </summary>
        internal static string BuildKey(string condition, string stackTrace, string exceptionType)
        {
            var sb = new StringBuilder(256);
            sb.Append(string.IsNullOrEmpty(exceptionType) ? typeFromCondition(condition) : exceptionType);
            sb.Append('|');
            int frames = appendFrames(sb, stackTrace);
            if (frames == 0) appendNormalizedMessage(sb, messageAfterType(condition));
            return sb.Length <= MAX_KEY_LENGTH ? sb.ToString() : sb.ToString(0, MAX_KEY_LENGTH);
        }

        /// <summary>Drop expired entries; if none expired, drop the single oldest. Caller holds _lock.</summary>
        private static void evictLocked(double nowSeconds)
        {
            string oldestKey = null;
            double oldest = double.PositiveInfinity;
            List<string> expired = null;
            foreach (var pair in _recent)
            {
                if (nowSeconds - pair.Value.LastSentSeconds >= DEDUPE_WINDOW_SECONDS)
                    (expired ??= new List<string>()).Add(pair.Key);
                if (pair.Value.LastSentSeconds < oldest)
                {
                    oldest = pair.Value.LastSentSeconds;
                    oldestKey = pair.Key;
                }
            }
            if (expired != null)
            {
                foreach (var k in expired) _recent.Remove(k);
                return;
            }
            if (oldestKey != null) _recent.Remove(oldestKey);
        }

        /// <summary>The leading type token of a Unity condition ("Foo.BarException: msg" → "Foo.BarException"),
        /// or "" when the condition does not start with an identifier followed by ": ".</summary>
        private static string typeFromCondition(string condition)
        {
            if (string.IsNullOrEmpty(condition)) return string.Empty;
            int colon = condition.IndexOf(": ", StringComparison.Ordinal);
            if (colon <= 0) return string.Empty;
            for (int i = 0; i < colon; i++)
            {
                char c = condition[i];
                if (!(char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '`' || c == '+')) return string.Empty;
            }
            return condition.Substring(0, colon);
        }

        /// <summary>The message part of a condition whose type prefix was recognised, else the whole condition.</summary>
        private static string messageAfterType(string condition)
        {
            var type = typeFromCondition(condition);
            return type.Length == 0 ? condition : condition.Substring(type.Length + 2);
        }

        /// <summary>Append up to KEY_FRAMES frames with file/line/IL-offset noise removed. Returns the count.</summary>
        private static int appendFrames(StringBuilder sb, string stackTrace)
        {
            if (string.IsNullOrEmpty(stackTrace)) return 0;
            int count = 0;
            int start = 0;
            while (start < stackTrace.Length && count < KEY_FRAMES)
            {
                int end = stackTrace.IndexOf('\n', start);
                if (end < 0) end = stackTrace.Length;
                var frame = normalizeFrame(stackTrace.Substring(start, end - start));
                if (frame.Length > 0)
                {
                    sb.Append('\n').Append(frame);
                    count++;
                }
                start = end + 1;
            }
            return count;
        }

        /// <summary>"Foo.Bar () (at Assets/X.cs:12)" / "at Foo.Bar () [0x0001c] in &lt;hash&gt;:0" → "Foo.Bar ()".</summary>
        private static string normalizeFrame(string frame)
        {
            var f = frame.Trim();
            if (f.StartsWith("at ", StringComparison.Ordinal)) f = f.Substring(3);
            f = cutAt(f, " (at ");
            f = cutAt(f, " [0x");
            f = cutAt(f, " in <");
            return f.Trim();
        }

        private static string cutAt(string value, string marker)
        {
            int at = value.IndexOf(marker, StringComparison.Ordinal);
            return at >= 0 ? value.Substring(0, at) : value;
        }

        /// <summary>Append the message with per-instance values collapsed: numbers and hex/GUID-like
        /// runs become '#', quoted literals become "&lt;s&gt;", so "Index 4817 of 'sword_3'" and
        /// "Index 12 of 'shield'" produce the same key.</summary>
        private static void appendNormalizedMessage(StringBuilder sb, string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            for (int i = 0; i < message.Length; i++)
            {
                char c = message[i];
                bool quoteOpens = c == '"' || (c == '\'' && (i == 0 || char.IsWhiteSpace(message[i - 1])));
                if (quoteOpens)
                {
                    int close = message.IndexOf(c, i + 1);
                    if (close > i)
                    {
                        sb.Append(LITERAL_MARK);
                        i = close;
                        continue;
                    }
                }
                if (!isHexChar(c))
                {
                    sb.Append(c);
                    continue;
                }
                // A run of hex characters (hyphens joined in, so a GUID is one run): a number, a hex
                // id, or an ordinary word ("face", "Bad").
                int end = i;
                bool hasDigit = false;
                bool allDigits = true;
                while (end < message.Length && (isHexChar(message[end]) || (message[end] == '-' && end > i)))
                {
                    char r = message[end];
                    hasDigit |= char.IsDigit(r);
                    allDigits &= char.IsDigit(r) || r == '-';
                    end++;
                }
                while (end - 1 > i && message[end - 1] == '-') end--; // a trailing hyphen is punctuation
                bool isValue = hasDigit && (allDigits || end - i >= MIN_HEX_ID_LENGTH);
                if (isValue) sb.Append(NUMBER_MARK);
                else sb.Append(message, i, end - i);
                i = end - 1;
            }
        }

        private static bool isHexChar(char c) =>
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

        /// <summary>Mutable dedupe slot: monotonic seconds when this key last reported + repeats since.</summary>
        private sealed class KeyWindow
        {
            public double LastSentSeconds;
            public int Suppressed;
        }
    }
}
