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
    /// </summary>
    public static class TombstackSession
    {
        private const string PREFS_TOKEN = "AnkleBreaker.Tombstack.EditorToken";
        private const string PREFS_EXPIRY = "AnkleBreaker.Tombstack.EditorTokenExpiresAtIso";
        private const string PREFS_EMAIL = "AnkleBreaker.Tombstack.EditorEmail";

        /// <summary>Raised after sign-in, sign-out, link, or unlink. UI windows refresh on it.</summary>
        public static event Action OnChanged;

        /// <summary>Email of the signed-in account ("" when signed out).</summary>
        public static string Email => EditorPrefs.GetString(PREFS_EMAIL, "");

        /// <summary>Bearer token for editor API calls ("" when signed out). Never log it.</summary>
        public static string EditorToken => EditorPrefs.GetString(PREFS_TOKEN, "");

        /// <summary>True when a non-expired editor token is present.</summary>
        public static bool IsSignedIn
        {
            get
            {
                if (string.IsNullOrEmpty(EditorToken)) return false;
                var expiry = readExpiryUtc();
                return expiry == null || expiry.Value > DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Sign in and persist the editor token to EditorPrefs.
        /// </summary>
        /// <param name="email">Account email.</param>
        /// <param name="password">Account password (forwarded once, never stored).</param>
        /// <returns>The API result — inspect <c>Error</c> for the UI on failure.</returns>
        public static async Task<ApiResult<EditorLoginData>> SignInAsync(string email, string password)
        {
            var result = await TombstackEditorApi.LoginAsync(email, password);
            if (result.Ok)
            {
                EditorPrefs.SetString(PREFS_TOKEN, result.Data.editorToken);
                EditorPrefs.SetString(PREFS_EXPIRY, result.Data.expiresAtIso ?? "");
                EditorPrefs.SetString(PREFS_EMAIL, result.Data.email ?? email ?? "");
                notifyChanged();
            }
            return result;
        }

        /// <summary>
        /// Sign out: best-effort server invalidation, then clear local state. Always succeeds
        /// locally even when offline.
        /// </summary>
        public static async Task SignOutAsync()
        {
            var token = EditorToken;
            clearLocal();
            notifyChanged();
            if (!string.IsNullOrEmpty(token))
                await TombstackEditorApi.LogoutAsync(token); // fail-soft; local state already gone
        }

        /// <summary>Drop the local token after a 401 so the UI returns to the sign-in state.</summary>
        public static void HandleUnauthorized()
        {
            if (string.IsNullOrEmpty(EditorToken)) return;
            clearLocal();
            notifyChanged();
        }

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
            var result = await TombstackEditorApi.MintSdkTokenAsync(EditorToken, gameId);
            if (!result.Ok)
            {
                if (result.Status == 401) HandleUnauthorized();
                return result;
            }

            var settings = TombstackProjectSettingsSO.instance;
            if (!TombstackConfigWriter.WriteLink(settings.ResolveEndpoint(), result.Data.token))
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

        private static void clearLocal()
        {
            EditorPrefs.DeleteKey(PREFS_TOKEN);
            EditorPrefs.DeleteKey(PREFS_EXPIRY);
            EditorPrefs.DeleteKey(PREFS_EMAIL);
        }

        private static DateTime? readExpiryUtc()
        {
            var iso = EditorPrefs.GetString(PREFS_EXPIRY, "");
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
