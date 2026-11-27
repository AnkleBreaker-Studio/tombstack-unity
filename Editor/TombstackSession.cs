using System;
using System.Globalization;
using System.Threading.Tasks;
using UnityEditor;

namespace AnkleBreaker.Tombstack.Editor
{
    /// <summary>
    /// Editor-side auth state for the signed-in Tombstack account. The editor token is
    /// per-user and machine-local: it lives ONLY in EditorPrefs (never inside the project
    /// folder, never in version control). The per-project game binding lives in
    /// <see cref="TombstackProjectSettingsSO"/>; the game-facing tmb_ SDK token is written
    /// into the project via <see cref="TombstackConfigWriter"/> — that one is meant to ship.
    /// All operations are fail-soft and never throw into the editor loop.
    ///
    /// Tokens are kept per server (origin): a token is only ever sent back to the server that issued it,
    /// and a 401 from one server signs out of that server only. The official server keeps the historical
    /// keys, so an existing sign-in survives the update. Which server a project uses comes from
    /// <see cref="TombstackProjectSettingsSO.ResolveOrigin"/>.
    /// </summary>
    public static class TombstackSession
    {
        private const string PREFS_TOKEN = "AnkleBreaker.Tombstack.EditorToken";
        private const string PREFS_EXPIRY = "AnkleBreaker.Tombstack.EditorTokenExpiresAtIso";
        private const string PREFS_EMAIL = "AnkleBreaker.Tombstack.EditorEmail";

        /// <summary>Raised after sign-in, sign-out, link, or unlink. UI windows refresh on it.</summary>
        public static event Action OnChanged;

        /// <summary>The server (scheme, host, port) this project's editor calls go to.</summary>
        public static string ServerOrigin => currentOrigin();

        /// <summary>Email of the account signed in to this project's server ("" when signed out).</summary>
        public static string Email => EditorPrefs.GetString(prefsKey(PREFS_EMAIL, currentOrigin()), "");

        /// <summary>Bearer token for this project's server ("" when signed out). Never log it.</summary>
        public static string EditorToken => EditorPrefs.GetString(prefsKey(PREFS_TOKEN, currentOrigin()), "");

        /// <summary>True when a non-expired editor token is present for this project's server.</summary>
        public static bool IsSignedIn
        {
            get
            {
                if (string.IsNullOrEmpty(EditorToken)) return false;
                var expiry = readExpiryUtc(currentOrigin());
                return expiry == null || expiry.Value > DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Sign in and persist the editor token to EditorPrefs, under the server that issued it.
        /// </summary>
        /// <param name="email">Account email.</param>
        /// <param name="password">Account password (forwarded once, never stored).</param>
        /// <param name="code">Two-factor code when the account has two-factor on; null otherwise.</param>
        /// <returns>The API result — inspect <c>Error</c> and <c>ErrorCode</c> for the UI on failure.</returns>
        public static async Task<ApiResult<EditorLoginData>> SignInAsync(string email, string password, string code = null)
        {
            var origin = currentOrigin();
            var result = await TombstackEditorApi.LoginAsync(email, password, code);
            if (result.Ok)
            {
                EditorPrefs.SetString(prefsKey(PREFS_TOKEN, origin), result.Data.editorToken);
                EditorPrefs.SetString(prefsKey(PREFS_EXPIRY, origin), result.Data.expiresAtIso ?? "");
                EditorPrefs.SetString(prefsKey(PREFS_EMAIL, origin), result.Data.email ?? email ?? "");
                notifyChanged();
            }
            return result;
        }

        /// <summary>
        /// Sign out of this project's server: best-effort server invalidation, then clear local state.
        /// Always succeeds locally even when offline.
        /// </summary>
        public static async Task SignOutAsync()
        {
            var token = EditorToken;
            clearLocal(currentOrigin());
            notifyChanged();
            if (!string.IsNullOrEmpty(token))
                await TombstackEditorApi.LogoutAsync(token); // fail-soft; local state already gone
        }

        /// <summary>Drop the token of the server that answered 401 so the UI returns to the sign-in state.
        /// Tokens for other servers are kept.</summary>
        /// <param name="origin">The server the failed request went to, captured before awaiting it (the
        /// project's server can change meanwhile); null = this project's server now.</param>
        /// <param name="refusedToken">The token that request carried. When the server's stored token is
        /// another one (a sign-in made while the request was out), it is kept. Null = whatever is stored.</param>
        public static void HandleUnauthorized(string origin = null, string refusedToken = null)
        {
            var target = origin ?? currentOrigin();
            var stored = EditorPrefs.GetString(prefsKey(PREFS_TOKEN, target), "");
            if (string.IsNullOrEmpty(stored)) return;
            if (refusedToken != null && !string.Equals(stored, refusedToken, StringComparison.Ordinal)) return;
            clearLocal(target);
            notifyChanged();
        }

        /// <summary>The project's server changed (endpoint override saved): windows refresh, and the Hub
        /// drops what it loaded from the previous server.</summary>
        internal static void NotifyServerChanged() => notifyChanged();

        /// <summary>True while <paramref name="origin"/> and <paramref name="token"/>, captured when a
        /// request was sent, are still this project's server and its sign-in. An answer that arrives
        /// after the server changed or another sign-in replaced the token belongs to that earlier session
        /// and must not be shown or used.</summary>
        internal static bool IsCurrent(string origin, string token) =>
            string.Equals(origin, ServerOrigin, StringComparison.Ordinal)
            && string.Equals(token, EditorToken, StringComparison.Ordinal);

        /// <summary>
        /// Link this project to a game: mint a tmb_ SDK token, write it (plus the endpoint)
        /// into the runtime config asset, and persist the binding in project settings.
        /// </summary>
        /// <param name="studioId">Server studio id.</param>
        /// <param name="studioName">Studio display name.</param>
        /// <param name="gameId">Server game id.</param>
        /// <param name="gameName">Game display name.</param>
        /// <returns>The mint result — inspect <c>Error</c> for the UI on failure.</returns>
        public static async Task<ApiResult<EditorSdkTokenData>> LinkProjectAsync(
            string studioId, string studioName, string gameId, string gameName)
        {
            // Before minting: a token minted for a config that cannot be written would be wasted.
            var problem = TombstackConfigWriter.DescribeConfigProblem();
            if (problem != null) return ApiResult<EditorSdkTokenData>.Failure(0, problem);

            // The server the token is minted by, and the endpoint written with it, are this moment's:
            // the project's endpoint can change while the request is in flight.
            var settings = TombstackProjectSettingsSO.instance;
            var origin = currentOrigin();
            var endpoint = settings.ResolveEndpoint();
            var token = EditorToken;
            var result = await TombstackEditorApi.MintSdkTokenAsync(token, gameId);
            if (!result.Ok)
            {
                if (result.Status == 401) HandleUnauthorized(origin, token);
                return result;
            }

            if (!TombstackConfigWriter.WriteLink(endpoint, result.Data.token))
                return ApiResult<EditorSdkTokenData>.Failure(0, "Token minted but the config asset could not be written.");

            settings.SetLink(studioId, studioName, gameId, gameName);
            notifyChanged();
            return result;
        }

        /// <summary>Unlink this project: clear the binding and blank the config's SDK token.</summary>
        public static void UnlinkProject()
        {
            TombstackProjectSettingsSO.instance.ClearLink();
            TombstackConfigWriter.ClearSdkToken();
            notifyChanged();
        }

        /// <summary>True when the plugin is fully configured (signed in + project linked).</summary>
        public static bool IsConfigured => IsSignedIn && TombstackProjectSettingsSO.instance.IsLinked;

        /// <summary>The EditorPrefs key of <paramref name="baseKey"/> for <paramref name="origin"/>: the
        /// historical key for the official server, the key suffixed with the origin for any other.</summary>
        private static string prefsKey(string baseKey, string origin)
        {
            if (string.IsNullOrEmpty(origin)
                || string.Equals(origin, TombstackEndpointTrust.OfficialOrigin, StringComparison.Ordinal))
                return baseKey;
            return baseKey + "@" + origin;
        }

        private static string currentOrigin() => TombstackProjectSettingsSO.instance.ResolveOrigin();

        private static void clearLocal(string origin)
        {
            EditorPrefs.DeleteKey(prefsKey(PREFS_TOKEN, origin));
            EditorPrefs.DeleteKey(prefsKey(PREFS_EXPIRY, origin));
            EditorPrefs.DeleteKey(prefsKey(PREFS_EMAIL, origin));
        }

        private static DateTime? readExpiryUtc(string origin)
        {
            var iso = EditorPrefs.GetString(prefsKey(PREFS_EXPIRY, origin), "");
            if (string.IsNullOrEmpty(iso)) return null;
            if (DateTime.TryParse(iso, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
                return parsed;
            return null;
        }

        private static void notifyChanged()
        {
            try { OnChanged?.Invoke(); }
            catch (Exception e) { UnityEngine.Debug.LogWarning($"[Tombstack] session listener failed: {e.Message}"); }
        }
    }
}
