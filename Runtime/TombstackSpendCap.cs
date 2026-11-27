using System;
using System.Globalization;
using UnityEngine;

namespace AnkleBreaker.Tombstack
{
    /// <summary>
    /// The studio's spending cap, as the SDK sees it. When the cap is reached the server refuses every
    /// ingest route, crash and bug reports included, with <c>402 {"error":"spend_cap_reached",
    /// "resumesAt":"..."}</c> until the billing period resumes or the studio raises its cap
    /// (src/lib/spend-cap-gate.ts). Refused data is not stored. Crash and bug reports exist only on the
    /// player's device, so the uploader holds them on disk until the resume time and sends them then, or
    /// sooner when the server accepts a request again; everything else the cap refuses is dropped.
    /// This class holds the pure part: recognising the answer and turning it into a wait. Never throws.
    /// </summary>
    internal static class TombstackSpendCap
    {
        internal const long HTTP_PAYMENT_REQUIRED = 402;
        /// <summary>Hold used when the answer names no usable resume time: a long backoff. An accepted
        /// request (a heartbeat, say) ends the hold earlier.</summary>
        internal const double FALLBACK_HOLD_SECONDS = 3600;
        /// <summary>Shortest hold. A resume time already past (a clock that disagrees, an answer that
        /// lags) must not turn into a resend loop against a server that still refuses.</summary>
        internal const double MIN_HOLD_SECONDS = 60;
        /// <summary>Longest hold honoured. A billing period is at most a month; a later resume time is
        /// read as this, and a launch after it tries again anyway.</summary>
        internal const double MAX_HOLD_SECONDS = 32 * 24 * 3600;
        /// <summary>The release is spread over this fraction of the hold, at most
        /// <see cref="RESUME_SPREAD_MAX_SECONDS"/>, so the players a cap stopped together do not all send
        /// their reports in the same second when it lifts. Never earlier than the resume time.</summary>
        internal const double RESUME_SPREAD_FRACTION = 0.1;
        internal const double RESUME_SPREAD_MAX_SECONDS = 300;
        private const int MIN_PLAUSIBLE_YEAR = 2024;
        private const int MAX_PLAUSIBLE_YEAR = 2100;

        /// <summary>The field of the server's refusal that the SDK reads (JsonUtility needs a class). Any
        /// 402 from the SDK's own endpoint is the cap; its code is not needed to recognise it.</summary>
        [Serializable]
        private sealed class CapRefusal
        {
            public string resumesAt;
        }

        /// <summary>True for the answer the spending cap gives on every ingest route.</summary>
        internal static bool IsCapRefusal(long status) => status == HTTP_PAYMENT_REQUIRED;

        /// <summary>
        /// Seconds to hold crash and bug reports after a refusal whose body is <paramref name="body"/>,
        /// measured against <paramref name="serverNow"/> (the device clock corrected by the offset learned
        /// from the server's Date headers), clamped to [<see cref="MIN_HOLD_SECONDS"/>,
        /// <see cref="MAX_HOLD_SECONDS"/>]. <see cref="FALLBACK_HOLD_SECONDS"/> without a resume time.
        /// </summary>
        internal static double HoldSeconds(string body, DateTimeOffset serverNow)
        {
            if (!TryParseResumesAt(body, out var resumesAt)) return FALLBACK_HOLD_SECONDS;
            var wait = (resumesAt - serverNow).TotalSeconds;
            return Math.Min(MAX_HOLD_SECONDS, Math.Max(MIN_HOLD_SECONDS, wait));
        }

        /// <summary>The hold plus this client's share of the spread; <paramref name="unit"/> is a random
        /// draw in [0, 1). Never less than the hold.</summary>
        internal static double ReleaseDelaySeconds(double holdSeconds, double unit)
        {
            var spread = Math.Min(RESUME_SPREAD_MAX_SECONDS, holdSeconds * RESUME_SPREAD_FRACTION);
            var share = unit >= 0 && unit < 1 ? unit : 0;
            return holdSeconds + spread * share;
        }

        /// <summary>The <c>resumesAt</c> of a refusal body, when it is present and a plausible instant.
        /// Parsed with the invariant culture (a Thai or Persian device calendar must not move it).</summary>
        internal static bool TryParseResumesAt(string body, out DateTimeOffset resumesAt)
        {
            resumesAt = default;
            if (string.IsNullOrEmpty(body)) return false;
            CapRefusal refusal;
            // Whatever the parser throws means "no usable resume time": the caller then holds for the
            // fallback instead of losing the hold (and the report) to an escaped exception.
            try { refusal = JsonUtility.FromJson<CapRefusal>(body); }
            catch (Exception) { return false; }
            if (refusal == null || string.IsNullOrEmpty(refusal.resumesAt)) return false;
            if (!DateTimeOffset.TryParse(refusal.resumesAt, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)) return false;
            if (parsed.Year < MIN_PLAUSIBLE_YEAR || parsed.Year > MAX_PLAUSIBLE_YEAR) return false;
            resumesAt = parsed;
            return true;
        }

        /// <summary>The resume time as the console line shows it ("2026-12-01 00:00 UTC"), or a phrase
        /// when the answer named none.</summary>
        internal static string DescribeResume(string body)
        {
            return TryParseResumesAt(body, out var resumesAt)
                ? resumesAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC"
                : "the server accepts data again";
        }
    }
}
