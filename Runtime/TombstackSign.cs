using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AnkleBreaker.Tombstack
{
    /// <summary>
    /// Builds the <c>X-Tombstack-Signature</c> header for ingest POSTs (spec §S3): a timestamped
    /// HMAC-SHA256 of <c>"&lt;t&gt;.&lt;rawBody&gt;"</c> keyed with the per-game SDK token (the same
    /// secret already sent as the Bearer ingest key). Header value shape:
    /// <c>t=&lt;unixSec&gt;,v1=&lt;hex&gt;</c>.
    ///
    /// Fail-silent (§15): any failure returns null so the caller sends the request unsigned — the
    /// server accepts unsigned ingest during the signing rollout. Runs at send time, off the main
    /// game-frame path. The <see cref="HMACSHA256"/> instance and the hex <see cref="StringBuilder"/>
    /// are reused across calls (re-keyed only if the token changes) to avoid per-request allocation
    /// of the crypto primitive; access is single-threaded in practice (the upload coroutine) but
    /// guarded by a lock for safety.
    /// </summary>
    internal static class TombstackSign
    {
        private static readonly object _lock = new object();
        private static HMACSHA256 _hmac;
        private static string _hmacKey;
        private static readonly StringBuilder _hex = new StringBuilder(64);

        /// <summary>
        /// Compute the signature header for <paramref name="body"/> keyed by <paramref name="ingestKey"/>.
        /// Returns null (never throws) when signing is impossible — the caller then sends unsigned.
        /// </summary>
        internal static string BuildHeader(string ingestKey, string body)
        {
            try
            {
                if (string.IsNullOrEmpty(ingestKey) || body == null) return null;
                long t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                string tStr = t.ToString(CultureInfo.InvariantCulture);
                // Signed input: "<t>.<rawBody>" — binds the body to the timestamp (replay window).
                byte[] input = Encoding.UTF8.GetBytes(tStr + "." + body);
                lock (_lock)
                {
                    if (_hmac == null || !string.Equals(_hmacKey, ingestKey, StringComparison.Ordinal))
                    {
                        _hmac?.Dispose();
                        _hmac = new HMACSHA256(Encoding.UTF8.GetBytes(ingestKey));
                        _hmacKey = ingestKey;
                    }
                    byte[] hash = _hmac.ComputeHash(input);
                    _hex.Length = 0;
                    for (int i = 0; i < hash.Length; i++) _hex.Append(hash[i].ToString("x2"));
                    return "t=" + tStr + ",v1=" + _hex.ToString();
                }
            }
            catch (Exception e)
            {
                TombstackLog.Warn("request signing failed; sending unsigned: " + e.Message);
                return null;
            }
        }
    }
}
