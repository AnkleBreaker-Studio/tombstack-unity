using System;
using System.Globalization;

namespace AnkleBreaker.Tombstack
{
    /// <summary>
    /// The one place the SDK formats a wire timestamp. Every ingest schema requires an ISO-8601 UTC
    /// instant such as <c>2026-10-09T13:45:02.123Z</c>; anything else is a 400, and a 400 is poison,
    /// so the payload is deleted.
    ///
    /// <c>DateTime.ToString(format)</c> without a culture is NOT that: <c>:</c> is the culture's time
    /// separator and <c>yyyy</c> counts years in the culture's calendar, so a player whose system
    /// locale is Thai (Buddhist calendar, year 2569) or one with a non-colon time separator sent a
    /// timestamp the server refused, and every crash, bug report, event, metric and heartbeat of
    /// that player was dropped. The invariant culture plus quoted separators make the output
    /// identical on every machine. Thread-safe; allocates only the returned string.
    /// </summary>
    internal static class TombstackTime
    {
        private const string ISO_FORMAT = "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fff'Z'";

        /// <summary>The current UTC instant as a wire timestamp.</summary>
        internal static string NowIso() => ToIso(DateTime.UtcNow);

        /// <summary>A UTC <paramref name="utc"/> as a wire timestamp.</summary>
        internal static string ToIso(DateTime utc) => utc.ToString(ISO_FORMAT, CultureInfo.InvariantCulture);
    }
}
