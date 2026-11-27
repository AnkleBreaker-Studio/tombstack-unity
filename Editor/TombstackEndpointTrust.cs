using System;
using System.Collections.Generic;
using UnityEditor;

namespace AnkleBreaker.Tombstack.Editor
{
    /// <summary>
    /// Decides where the editor plugin may send the account's credentials: the password and two-factor
    /// code at sign-in, and the editor session token on every call. Only to the official Tombstack
    /// server, or to a server the developer confirmed on this machine.
    ///
    /// The endpoint override lives in <c>ProjectSettings/TombstackSettings.asset</c>, a file committed
    /// with the project: it is the project's word, not the developer's, so opening a project must never
    /// be enough to send credentials somewhere else. Confirmations are kept per user and per machine
    /// (EditorPrefs), never in the project. Main thread (EditorPrefs, dialogs).
    /// </summary>
    public static class TombstackEndpointTrust
    {
        private const string PREFS_TRUSTED_ORIGINS = "AnkleBreaker.Tombstack.TrustedOrigins";
        private const char ORIGIN_SEPARATOR = '\n';
        private const int MAX_TRUSTED_ORIGINS = 32;

        /// <summary>Origin of the official server (<see cref="TombstackProjectSettingsSO.DEFAULT_ENDPOINT"/>).</summary>
        public static string OfficialOrigin => originOf(TombstackProjectSettingsSO.DEFAULT_ENDPOINT);

        /// <summary>
        /// Validate a base URL the plugin may talk to: an absolute <c>https</c> URL, or <c>http</c> to a
        /// loopback host (local development); no user name or password in it, no query or fragment. On
        /// success, <paramref name="baseUrl"/> is the URL without a trailing slash and
        /// <paramref name="origin"/> its scheme, host and non-default port, in lower case.
        /// </summary>
        public static bool TryValidateBaseUrl(string url, out string baseUrl, out string origin)
        {
            baseUrl = null;
            origin = null;
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
            if (uri.IsFile || uri.IsUnc) return false;
            bool https = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
            bool loopbackHttp = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                                && uri.IsLoopback;
            if (!https && !loopbackHttp) return false;
            if (string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)) return false;
            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;
            origin = uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
            baseUrl = (origin + uri.AbsolutePath).TrimEnd('/');
            return true;
        }

        /// <summary>True when credentials may go to <paramref name="origin"/> without asking: the official
        /// server, or a server confirmed on this machine.</summary>
        public static bool IsTrusted(string origin)
        {
            if (string.IsNullOrEmpty(origin)) return false;
            if (string.Equals(origin, OfficialOrigin, StringComparison.Ordinal)) return true;
            return readTrusted().Contains(origin);
        }

        /// <summary>
        /// Make sure credentials may go to the server the current project uses. True for the official
        /// server or a server already confirmed. Otherwise, and only when <paramref name="askUser"/> (a
        /// click, never an automatic call), a dialog names the server and the answer is remembered.
        /// </summary>
        public static bool ConfirmCurrent(bool askUser)
        {
            var origin = TombstackProjectSettingsSO.instance.ResolveOrigin();
            if (IsTrusted(origin)) return true;
            if (!askUser || string.IsNullOrEmpty(origin)) return false;
            return ConfirmWithDialog(origin);
        }

        /// <summary>Ask the developer whether to trust <paramref name="origin"/> with their Tombstack
        /// sign-in; a yes is remembered on this machine. Main thread.</summary>
        public static bool ConfirmWithDialog(string origin)
        {
            if (string.IsNullOrEmpty(origin)) return false;
            if (IsTrusted(origin)) return true;
            bool trust = EditorUtility.DisplayDialog(
                "Tombstack: confirm server",
                "This project sends your Tombstack sign-in (email, password, two-factor code) and your editor "
                + "session to:\n\n" + origin + "\n\nThis is not the Tombstack service (" + OfficialOrigin + "). "
                + "Continue only if you or your studio run this server.",
                "Trust this server",
                "Cancel");
            if (trust) remember(origin);
            return trust;
        }

        /// <summary>The message shown when a call was not made because the server is not confirmed.</summary>
        public static string NotConfirmedMessage(string origin) =>
            "Not connected: this project points the Tombstack plugin at " + (origin ?? "an invalid address")
            + ", which you have not confirmed. Use Refresh or Sign In to confirm it, or clear the endpoint in "
            + "Project Settings > Tombstack.";

        private static string originOf(string url)
        {
            TryValidateBaseUrl(url, out _, out var origin);
            return origin;
        }

        private static HashSet<string> readTrusted()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            var raw = EditorPrefs.GetString(PREFS_TRUSTED_ORIGINS, "");
            if (string.IsNullOrEmpty(raw)) return set;
            foreach (var entry in raw.Split(ORIGIN_SEPARATOR))
            {
                // Re-validated on every read: a hand-edited preference cannot add a non-https server.
                if (TryValidateBaseUrl(entry, out _, out var origin)
                    && string.Equals(origin, entry, StringComparison.Ordinal))
                    set.Add(origin);
            }
            return set;
        }

        private static void remember(string origin)
        {
            var set = readTrusted();
            if (set.Count >= MAX_TRUSTED_ORIGINS || !set.Add(origin)) return;
            EditorPrefs.SetString(PREFS_TRUSTED_ORIGINS, string.Join(ORIGIN_SEPARATOR.ToString(), set));
        }
    }
}
