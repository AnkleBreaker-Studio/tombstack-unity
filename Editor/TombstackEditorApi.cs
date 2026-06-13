using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace AnkleBreaker.Tombstack.Editor
{
    /// <summary>
    /// Typed async client for the Tombstack editor API (<c>/api/editor/*</c>). All calls are
    /// fail-soft: they never throw into the editor loop, returning <see cref="ApiResult{T}"/>
    /// instead. Requests run on UnityWebRequest with completion observed via the
    /// <c>AsyncOperation.completed</c> callback (pumped by the editor player loop), so nothing
    /// ever blocks the main thread. Request bodies are built with the runtime's AOT-safe
    /// <see cref="TombstackJson"/> writer; responses parse with JsonUtility envelopes.
    /// </summary>
    public static class TombstackEditorApi
    {
        private const int REQUEST_TIMEOUT_SECONDS = 15;
        private const int LOGIN_BODY_CAPACITY = 256;
        private const long HTTP_UNAUTHORIZED = 401;
        private const long HTTP_TOO_MANY_REQUESTS = 429;

        private const string LOGIN_PATH = "/api/editor/auth/login";
        private const string LOGOUT_PATH = "/api/editor/auth/logout";
        private const string ME_PATH = "/api/editor/me";

        /// <summary>
        /// Sign in with Tombstack account credentials.
        /// <c>POST /api/editor/auth/login</c> → editor token + expiry.
        /// </summary>
        /// <param name="email">Account email.</param>
        /// <param name="password">Account password (sent over HTTPS, never stored).</param>
        public static async Task<ApiResult<EditorLoginData>> LoginAsync(string email, string password)
        {
            var body = buildLoginBody(email, password);
            var raw = await sendAsync(UnityWebRequest.kHttpVerbPOST, LOGIN_PATH, body, null);
            if (!raw.IsHttpSuccess)
                return ApiResult<EditorLoginData>.Failure(raw.Status, describeAuthFailure(raw));
            var envelope = parse<LoginEnvelope>(raw.Body);
            if (envelope?.data == null || string.IsNullOrEmpty(envelope.data.editorToken))
                return ApiResult<EditorLoginData>.Failure(raw.Status, "Malformed server response.");
            return ApiResult<EditorLoginData>.Success(envelope.data, raw.Status);
        }

        /// <summary>List studios + games for the signed-in account. <c>GET /api/editor/me</c>.</summary>
        /// <param name="editorToken">Bearer token from <see cref="LoginAsync"/>.</param>
        public static async Task<ApiResult<EditorMeData>> GetMeAsync(string editorToken)
        {
            var raw = await sendAsync(UnityWebRequest.kHttpVerbGET, ME_PATH, null, editorToken);
            if (!raw.IsHttpSuccess)
                return ApiResult<EditorMeData>.Failure(raw.Status, describeFailure(raw));
            var envelope = parse<MeEnvelope>(raw.Body);
            if (envelope?.data == null)
                return ApiResult<EditorMeData>.Failure(raw.Status, "Malformed server response.");
            return ApiResult<EditorMeData>.Success(envelope.data, raw.Status);
        }

        /// <summary>
        /// Mint a per-game SDK ingest token (tmb_...).
        /// <c>POST /api/editor/games/{gameId}/sdk-token</c>.
        /// </summary>
        /// <param name="editorToken">Bearer token from <see cref="LoginAsync"/>.</param>
        /// <param name="gameId">Server game id to scope the token to.</param>
        public static async Task<ApiResult<EditorSdkTokenData>> MintSdkTokenAsync(string editorToken, string gameId)
        {
            var path = "/api/editor/games/" + UnityWebRequest.EscapeURL(gameId) + "/sdk-token";
            var raw = await sendAsync(UnityWebRequest.kHttpVerbPOST, path, "{}", editorToken);
            if (!raw.IsHttpSuccess)
                return ApiResult<EditorSdkTokenData>.Failure(raw.Status, describeFailure(raw));
            var envelope = parse<SdkTokenEnvelope>(raw.Body);
            if (envelope?.data == null || string.IsNullOrEmpty(envelope.data.token))
                return ApiResult<EditorSdkTokenData>.Failure(raw.Status, "Malformed server response.");
            return ApiResult<EditorSdkTokenData>.Success(envelope.data, raw.Status);
        }

        /// <summary>
        /// Fetch the live dashboard summary for a game.
        /// <c>GET /api/editor/games/{gameId}/summary?days=N</c>.
        /// </summary>
        /// <param name="editorToken">Bearer token from <see cref="LoginAsync"/>.</param>
        /// <param name="gameId">Server game id.</param>
        /// <param name="days">Summary window in days (e.g. 7).</param>
        public static async Task<ApiResult<EditorGameSummaryData>> GetSummaryAsync(
            string editorToken, string gameId, int days)
        {
            var path = "/api/editor/games/" + UnityWebRequest.EscapeURL(gameId) + "/summary?days=" + days;
            var raw = await sendAsync(UnityWebRequest.kHttpVerbGET, path, null, editorToken);
            if (!raw.IsHttpSuccess)
                return ApiResult<EditorGameSummaryData>.Failure(raw.Status, describeFailure(raw));
            var envelope = parse<SummaryEnvelope>(raw.Body);
            if (envelope?.data == null)
                return ApiResult<EditorGameSummaryData>.Failure(raw.Status, "Malformed server response.");
            return ApiResult<EditorGameSummaryData>.Success(envelope.data, raw.Status);
        }

        /// <summary>Invalidate the editor token server-side. <c>POST /api/editor/auth/logout</c>.</summary>
        /// <param name="editorToken">Bearer token to invalidate.</param>
        public static async Task<ApiResult<bool>> LogoutAsync(string editorToken)
        {
            var raw = await sendAsync(UnityWebRequest.kHttpVerbPOST, LOGOUT_PATH, "{}", editorToken);
            if (!raw.IsHttpSuccess)
                return ApiResult<bool>.Failure(raw.Status, describeFailure(raw));
            return ApiResult<bool>.Success(true, raw.Status);
        }

        /// <summary>Login body via the runtime JSON writer (escaping handled, no reflection).</summary>
        private static string buildLoginBody(string email, string password)
        {
            var sb = new StringBuilder(LOGIN_BODY_CAPACITY);
            sb.Append('{');
            bool first = true;
            TombstackJson.AppendField(sb, "email", email ?? string.Empty, ref first);
            TombstackJson.AppendField(sb, "password", password ?? string.Empty, ref first);
            sb.Append(",\"editorInfo\":{");
            bool firstInfo = true;
            TombstackJson.AppendField(sb, "unityVersion", Application.unityVersion, ref firstInfo);
            TombstackJson.AppendField(sb, "machineName", SystemInfo.deviceName ?? "unknown", ref firstInfo);
            sb.Append("}}");
            return sb.ToString();
        }

        /// <summary>Run one request without blocking; resolves on the editor main thread.</summary>
        private static async Task<RawResponse> sendAsync(string method, string path, string body, string bearerToken)
        {
            UnityWebRequest req = null;
            try
            {
                req = new UnityWebRequest(TombstackProjectSettingsSO.instance.ResolveEndpoint() + path, method);
                req.downloadHandler = new DownloadHandlerBuffer();
                if (body != null)
                {
                    req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                    req.SetRequestHeader("Content-Type", "application/json");
                }
                if (!string.IsNullOrEmpty(bearerToken))
                    req.SetRequestHeader("Authorization", "Bearer " + bearerToken);
                req.timeout = REQUEST_TIMEOUT_SECONDS;

                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var op = req.SendWebRequest();
                op.completed += _ => tcs.TrySetResult(true);
                if (op.isDone) tcs.TrySetResult(true); // already finished before the handler attached
                await tcs.Task;

                bool transportFailed = req.result == UnityWebRequest.Result.ConnectionError
                    || req.result == UnityWebRequest.Result.DataProcessingError;
                return new RawResponse(
                    req.responseCode,
                    req.downloadHandler != null ? req.downloadHandler.text : null,
                    transportFailed);
            }
            catch (Exception e)
            {
                return new RawResponse(0, null, true, e.Message);
            }
            finally
            {
                req?.Dispose();
            }
        }

        /// <summary>Parse a JSON envelope; returns null (never throws) on malformed bodies.</summary>
        private static TEnvelope parse<TEnvelope>(string body) where TEnvelope : class
        {
            if (string.IsNullOrEmpty(body)) return null;
            try { return JsonUtility.FromJson<TEnvelope>(body); }
            catch { return null; }
        }

        /// <summary>User-facing message for a failed login response.</summary>
        private static string describeAuthFailure(RawResponse raw)
        {
            if (raw.TransportFailed) return "Could not reach Tombstack — check your connection.";
            if (raw.Status == HTTP_UNAUTHORIZED) return "Wrong email or password.";
            if (raw.Status == HTTP_TOO_MANY_REQUESTS) return "Too many attempts — wait a minute and retry.";
            return serverErrorOrDefault(raw, $"Sign-in failed (HTTP {raw.Status}).");
        }

        /// <summary>User-facing message for a failed authorized call.</summary>
        private static string describeFailure(RawResponse raw)
        {
            if (raw.TransportFailed) return "Could not reach Tombstack — check your connection.";
            if (raw.Status == HTTP_UNAUTHORIZED) return "Session expired — sign in again.";
            if (raw.Status == HTTP_TOO_MANY_REQUESTS) return "Rate limited — slow down and retry.";
            return serverErrorOrDefault(raw, $"Request failed (HTTP {raw.Status}).");
        }

        /// <summary>Prefer the server's envelope error message when one is present.</summary>
        private static string serverErrorOrDefault(RawResponse raw, string fallback)
        {
            var envelope = parse<BareEnvelope>(raw.Body);
            return string.IsNullOrEmpty(envelope?.error) ? fallback : envelope.error;
        }

        /// <summary>Immutable transport-level response snapshot.</summary>
        private readonly struct RawResponse
        {
            public readonly long Status;
            public readonly string Body;
            public readonly bool TransportFailed;
            public readonly string TransportError;

            public bool IsHttpSuccess => !TransportFailed && Status >= 200 && Status < 300;

            public RawResponse(long status, string body, bool transportFailed, string transportError = null)
            {
                Status = status;
                Body = body;
                TransportFailed = transportFailed;
                TransportError = transportError;
            }
        }
    }
}
