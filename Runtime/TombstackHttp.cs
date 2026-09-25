using System;
using System.Globalization;
using System.Threading;

namespace AnkleBreaker.Tombstack
{
    /// <summary>What a 401 from an ingest endpoint actually means for the payload that got it.</summary>
    internal enum UnauthorizedKind
    {
        /// <summary>Not a 401.</summary>
        None,
        /// <summary>The key verified, the HMAC or its timestamp did not (<c>invalid_signature</c>), or the
        /// 401 carried no recognisable code. Almost always a wrong device clock: retryable once the
        /// signing clock has been corrected from the server's <c>Date</c> header.</summary>
        Signature,
        /// <summary>The key itself was refused (<c>invalid_api_key</c>: revoked, rotated, unknown).
        /// Retrying in-session cannot help; only a new build (or a restored key) can.</summary>
        Key,
    }

    /// <summary>
    /// HTTP bookkeeping the uploader needs and <c>UnityWebRequest</c> does not give it: a server clock
    /// offset learned from response <c>Date</c> headers (so request signing survives a wrong device
    /// clock), 401 classification, and <c>Retry-After</c> parsing. Pure and thread-safe; never throws.
    ///
    /// Why the clock offset exists: the server rejects a signature whose timestamp is more than
    /// <c>MAX_SIGNATURE_SKEW_SECONDS</c> (300s, src/lib/ingest-signature.ts) away from its own clock
    /// with <c>401 {"code":"invalid_signature"}</c>. A player whose device clock is five minutes off
    /// (a dead CMOS battery, a manually set clock, a dual-boot timezone mix-up) signed every request
    /// stale, and the uploader treated that 401 as poison and DELETED the spooled crash report.
    /// Every response - success or failure - carries the server's <c>Date</c>, so the SDK learns the
    /// offset from the first reply and signs with corrected time from then on.
    /// </summary>
    internal static class TombstackHttp
    {
        internal const long HTTP_UNAUTHORIZED = 401;
        /// <summary>Machine-readable 401 codes, from src/lib/api-error-code.ts INGEST_AUTH_FAILURES.
        /// Matched quoted, so the substring can only hit the JSON string value.</summary>
        private const string INVALID_API_KEY_CODE = "\"invalid_api_key\"";
        /// <summary>A server Date moves the offset only when it disagrees by at least this much, so
        /// the one-second header resolution and request latency never make the offset jitter.</summary>
        private const long OFFSET_HYSTERESIS_SECONDS = 2;
        /// <summary>Plausibility bounds on the SERVER's clock (not the device's, which may be anywhere).
        /// A Date outside them came from something other than our server (a captive portal, a broken
        /// proxy) and must not steer signing.</summary>
        private const int MIN_PLAUSIBLE_SERVER_YEAR = 2024;
        private const int MAX_PLAUSIBLE_SERVER_YEAR = 2100;
        /// <summary>Longest Retry-After the uploader honours. The server's rate-limit windows are
        /// seconds to minutes; anything longer is treated as this, never as "park the crash forever".</summary>
        internal const float MAX_RETRY_AFTER_SECONDS = 600f;

        // Server time minus device time, in whole seconds. long + Interlocked: a torn read on a
        // 32-bit ARM device would sign with a garbage timestamp.
        private static long _offsetSeconds;

        /// <summary>Current server-minus-device clock offset in seconds (0 until a Date is seen).</summary>
        internal static long OffsetSeconds => Interlocked.Read(ref _offsetSeconds);

        /// <summary>Best estimate of the SERVER's current unix time: device UTC plus the learned offset.
        /// Used for the signature timestamp, which the server checks against its own clock.</summary>
        internal static long ServerNowUnixSeconds() =>
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() + OffsetSeconds;

        /// <summary>Forget the learned offset (static-state reset between editor play sessions).</summary>
        internal static void Reset() => Interlocked.Exchange(ref _offsetSeconds, 0);

        /// <summary>
        /// Learn the server clock offset from a response's <c>Date</c> header. Returns true when the
        /// header parsed and was plausible (whether or not the offset moved). Never throws.
        /// </summary>
        internal static bool ObserveDateHeader(string dateHeader) =>
            ObserveDateHeader(dateHeader, DateTimeOffset.UtcNow);

        /// <summary>Testable core of <see cref="ObserveDateHeader(string)"/> with an explicit device now.</summary>
        internal static bool ObserveDateHeader(string dateHeader, DateTimeOffset deviceNowUtc)
        {
            if (!TryParseHttpDate(dateHeader, out var serverNow)) return false;
            if (serverNow.Year < MIN_PLAUSIBLE_SERVER_YEAR || serverNow.Year > MAX_PLAUSIBLE_SERVER_YEAR) return false;
            long observed = serverNow.ToUnixTimeSeconds() - deviceNowUtc.ToUnixTimeSeconds();
            long current = OffsetSeconds;
            if (Math.Abs(observed - current) >= OFFSET_HYSTERESIS_SECONDS)
                Interlocked.Exchange(ref _offsetSeconds, observed);
            return true;
        }

        /// <summary>Parse an RFC 7231 IMF-fixdate (<c>Sun, 06 Nov 1994 08:49:37 GMT</c>). Never throws.</summary>
        internal static bool TryParseHttpDate(string value, out DateTimeOffset result)
        {
            result = default;
            if (string.IsNullOrEmpty(value)) return false;
            return DateTimeOffset.TryParseExact(
                value.Trim(), "r", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out result);
        }

        /// <summary>
        /// Classify a 401. An <c>invalid_api_key</c> body is a credential problem; anything else
        /// (<c>invalid_signature</c>, or a 401 with no recognisable body from an intermediary) is
        /// treated as a signing-clock problem, which is retryable. Erring toward retryable is
        /// deliberate: the cost is a bounded number of retries, the alternative was deleting crashes.
        /// </summary>
        internal static UnauthorizedKind ClassifyUnauthorized(long responseCode, string responseBody)
        {
            if (responseCode != HTTP_UNAUTHORIZED) return UnauthorizedKind.None;
            if (!string.IsNullOrEmpty(responseBody)
                && responseBody.IndexOf(INVALID_API_KEY_CODE, StringComparison.Ordinal) >= 0)
                return UnauthorizedKind.Key;
            return UnauthorizedKind.Signature;
        }

        /// <summary>
        /// Seconds a <c>Retry-After</c> header asks the client to wait (delta-seconds or an HTTP date),
        /// clamped to [0, <see cref="MAX_RETRY_AFTER_SECONDS"/>]; -1 when absent or unparseable.
        /// </summary>
        internal static float ParseRetryAfterSeconds(string header, DateTimeOffset nowUtc)
        {
            if (string.IsNullOrEmpty(header)) return -1f;
            var trimmed = header.Trim();
            if (long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                return Math.Min(seconds, (long)MAX_RETRY_AFTER_SECONDS);
            if (!TryParseHttpDate(trimmed, out var at)) return -1f;
            var wait = (float)(at - nowUtc).TotalSeconds;
            if (wait < 0f) return 0f;
            return Math.Min(wait, MAX_RETRY_AFTER_SECONDS);
        }
    }
}
