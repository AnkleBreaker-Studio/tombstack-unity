using System;

namespace AnkleBreaker.Tombstack.Editor
{
    // Response DTOs for the editor API (`/api/editor/*`). Parsed with JsonUtility, which
    // cannot handle generics or dictionaries — hence one concrete [Serializable] envelope
    // per endpoint, all mirroring the server envelope { success, data?, error? }.

    /// <summary>Typed outcome of an editor API call. Never throws — check <see cref="Ok"/>.</summary>
    /// <typeparam name="T">Parsed <c>data</c> payload type.</typeparam>
    public sealed class ApiResult<T>
    {
        /// <summary>True when the HTTP call succeeded and the envelope carried data.</summary>
        public bool Ok { get; }
        /// <summary>HTTP status code (0 on transport failure / offline).</summary>
        public long Status { get; }
        /// <summary>Human-readable error when <see cref="Ok"/> is false; null otherwise.</summary>
        public string Error { get; }
        /// <summary>Parsed payload when <see cref="Ok"/> is true; default otherwise.</summary>
        public T Data { get; }

        private ApiResult(bool ok, long status, string error, T data)
        {
            Ok = ok;
            Status = status;
            Error = error;
            Data = data;
        }

        /// <summary>Create a successful result.</summary>
        public static ApiResult<T> Success(T data, long status)
            => new ApiResult<T>(true, status, null, data);

        /// <summary>Create a failed result with a user-facing message.</summary>
        public static ApiResult<T> Failure(long status, string error)
            => new ApiResult<T>(false, status, string.IsNullOrEmpty(error) ? "Unknown error." : error, default);
    }

    /// <summary>Payload of <c>POST /api/editor/auth/login</c>.</summary>
    [Serializable]
    public class EditorLoginData
    {
        /// <summary>Bearer token for subsequent editor API calls. Stored in EditorPrefs only.</summary>
        public string editorToken;
        /// <summary>UTC ISO-8601 expiry of <see cref="editorToken"/>.</summary>
        public string expiresAtIso;
        /// <summary>Account email the token was minted for.</summary>
        public string email;
    }

    /// <summary>A game visible to the signed-in account (from <c>GET /api/editor/me</c>).</summary>
    [Serializable]
    public class EditorGame
    {
        /// <summary>Server game id.</summary>
        public string id;
        /// <summary>Display name.</summary>
        public string name;
        /// <summary>Engine label (e.g. "unity").</summary>
        public string engine;
    }

    /// <summary>A studio membership of the signed-in account (from <c>GET /api/editor/me</c>).</summary>
    [Serializable]
    public class EditorStudio
    {
        /// <summary>Server studio id.</summary>
        public string id;
        /// <summary>Display name.</summary>
        public string name;
        /// <summary>The account's role in the studio (e.g. "owner").</summary>
        public string role;
        /// <summary>Games owned by the studio.</summary>
        public EditorGame[] games;
    }

    /// <summary>Payload of <c>GET /api/editor/me</c>.</summary>
    [Serializable]
    public class EditorMeData
    {
        /// <summary>Account email.</summary>
        public string email;
        /// <summary>Studios the account belongs to.</summary>
        public EditorStudio[] studios;
    }

    /// <summary>Payload of <c>POST /api/editor/games/{gameId}/sdk-token</c>.</summary>
    [Serializable]
    public class EditorSdkTokenData
    {
        /// <summary>Freshly minted per-game SDK ingest token (tmb_...). Game-facing by design.</summary>
        public string token;
    }

    /// <summary>One aggregated crash signature row (from the game summary).</summary>
    [Serializable]
    public class EditorSignatureSummary
    {
        /// <summary>Canonical signature hash — also the web detail-page key.</summary>
        public string signature;
        /// <summary>Short human-readable hint (top exception message).</summary>
        public string stackHint;
        /// <summary>Occurrences in the summary window.</summary>
        public long count;
        /// <summary>Distinct affected users in the summary window.</summary>
        public long affectedUsers;
        /// <summary>Triage status (e.g. "new" | "triaged" | "resolved").</summary>
        public string status;
    }

    /// <summary>One day of the crash trend (from the game summary).</summary>
    [Serializable]
    public class EditorTrendPoint
    {
        /// <summary>UTC ISO-8601 date of the bucket.</summary>
        public string dateIso;
        /// <summary>Crash count for the day.</summary>
        public long count;
    }

    /// <summary>Payload of <c>GET /api/editor/games/{gameId}/summary</c>.</summary>
    [Serializable]
    public class EditorGameSummaryData
    {
        /// <summary>Crash-free session percentage (0–100).</summary>
        public float crashFreePct;
        /// <summary>Total crashes in the last 24 hours.</summary>
        public long totalCrashes24h;
        /// <summary>Total crashes in the last 7 days.</summary>
        public long totalCrashes7d;
        /// <summary>True when the server detected an abnormal crash spike.</summary>
        public bool crashSpike;
        /// <summary>Top signatures by volume (max 10).</summary>
        public EditorSignatureSummary[] topSignatures;
        /// <summary>Daily crash counts, oldest first.</summary>
        public EditorTrendPoint[] dailyTrend;
    }

    /// <summary>Envelope for <c>POST /api/editor/auth/login</c>.</summary>
    [Serializable]
    internal class LoginEnvelope
    {
        public bool success;
        public EditorLoginData data;
        public string error;
    }

    /// <summary>Envelope for <c>GET /api/editor/me</c>.</summary>
    [Serializable]
    internal class MeEnvelope
    {
        public bool success;
        public EditorMeData data;
        public string error;
    }

    /// <summary>Envelope for <c>POST /api/editor/games/{gameId}/sdk-token</c>.</summary>
    [Serializable]
    internal class SdkTokenEnvelope
    {
        public bool success;
        public EditorSdkTokenData data;
        public string error;
    }

    /// <summary>Envelope for <c>GET /api/editor/games/{gameId}/summary</c>.</summary>
    [Serializable]
    internal class SummaryEnvelope
    {
        public bool success;
        public EditorGameSummaryData data;
        public string error;
    }

    /// <summary>Bare envelope for responses with no payload (logout) and for error bodies.</summary>
    [Serializable]
    internal class BareEnvelope
    {
        public bool success;
        public string error;
    }
}
