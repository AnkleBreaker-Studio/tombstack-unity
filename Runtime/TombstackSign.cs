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
    /// <c>t</c> is the SERVER's estimated clock (<see cref="TombstackHttp.ServerNowUnixSeconds"/>), not
    /// the raw device clock: the server rejects a timestamp more than 300s from its own, and a device
    /// clock that far off used to make every signed request fail.
    ///
    /// Fail-silent (§15): any failure returns null so the caller sends the request unsigned — the
    /// server accepts unsigned ingest during the signing rollout. The HMAC is streamed over the
    /// <c>"&lt;t&gt;."</c> prefix and the already-encoded body bytes the request uploads, so the body
    /// is encoded to UTF-8 exactly once per send (it used to be concatenated into a second full-size
    /// string and encoded a second time). The <see cref="HMACSHA256"/> instance and the hex
    /// <see cref="StringBuilder"/> are reused across calls (re-keyed only if the token changes);
    /// access is single-threaded in practice (the upload coroutine) but guarded by a lock for safety.
    /// </summary>
    internal static class TombstackSign
    {
        private static readonly object _lock = new object();
        private static HMACSHA256 _hmac;
        private static string _hmacKey;
        private static readonly StringBuilder _hex = new StringBuilder(64);

        /// <summary>
        /// Compute the signature header for the UTF-8 <paramref name="bodyBytes"/> keyed by
        /// <paramref name="ingestKey"/>. Returns null (never throws) when signing is impossible — the
        /// caller then sends unsigned.
        /// </summary>
        internal static string BuildHeader(string ingestKey, byte[] bodyBytes)
        {
            try
            {
                if (string.IsNullOrEmpty(ingestKey) || bodyBytes == null) return null;
                long t = TombstackHttp.ServerNowUnixSeconds();
                string tStr = t.ToString(CultureInfo.InvariantCulture);
                // Signed input: "<t>.<rawBody>" — binds the body to the timestamp (replay window).
                byte[] prefix = Encoding.UTF8.GetBytes(tStr + ".");
                lock (_lock)
                {
                    if (_hmac == null || !string.Equals(_hmacKey, ingestKey, StringComparison.Ordinal))
                    {
                        _hmac?.Dispose();
                        _hmac = new HMACSHA256(Encoding.UTF8.GetBytes(ingestKey));
                        _hmacKey = ingestKey;
                    }
                    _hmac.Initialize();
                    _hmac.TransformBlock(prefix, 0, prefix.Length, null, 0);
                    _hmac.TransformFinalBlock(bodyBytes, 0, bodyBytes.Length);
                    byte[] hash = _hmac.Hash;
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
