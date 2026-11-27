using UnityEditor;
using UnityEngine;

namespace AnkleBreaker.Tombstack.Editor
{
    /// <summary>
    /// Per-project editor settings for the Tombstack plugin, persisted to
    /// <c>ProjectSettings/TombstackSettings.asset</c> (versionable, never shipped in builds).
    /// Holds the project ↔ game binding and the optional endpoint override. The editor auth
    /// token deliberately does NOT live here — it is per-user and stays in EditorPrefs
    /// (see <see cref="TombstackSession"/>).
    /// </summary>
    [FilePath("ProjectSettings/TombstackSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class TombstackProjectSettingsSO : ScriptableSingleton<TombstackProjectSettingsSO>
    {
        /// <summary>Default Tombstack base URL (dashboard + editor API + ingestion). The canonical
        /// custom domain — the raw <c>*.cloudfront.net</c> origin is not directly reachable
        /// (Host-based routing returns 403), so the SDK must target this host.</summary>
        public const string DEFAULT_ENDPOINT = "https://tombstack.com";

        [SerializeField] private string _studioId = "";
        [SerializeField] private string _studioName = "";
        [SerializeField] private string _gameId = "";
        [SerializeField] private string _gameName = "";
        [SerializeField] private string _endpointOverride = "";

        /// <summary>Linked studio id ("" when unlinked).</summary>
        public string StudioId => _studioId;
        /// <summary>Linked studio display name ("" when unlinked).</summary>
        public string StudioName => _studioName;
        /// <summary>Linked game id ("" when unlinked).</summary>
        public string GameId => _gameId;
        /// <summary>Linked game display name ("" when unlinked).</summary>
        public string GameName => _gameName;
        /// <summary>Endpoint override ("" → use <see cref="DEFAULT_ENDPOINT"/>).</summary>
        public string EndpointOverride => _endpointOverride;
        /// <summary>True when this project is bound to a Tombstack game.</summary>
        public bool IsLinked => !string.IsNullOrEmpty(_gameId);

        /// <summary>
        /// Effective base URL, no trailing slash: the override when it is a valid https URL (http only to a
        /// loopback host), otherwise the default. An invalid override is ignored rather than followed: this
        /// file is committed with the project, and the plugin talks to this URL. Credentials still go to a
        /// non-default server only once the developer confirmed it (<see cref="TombstackEndpointTrust"/>).
        /// </summary>
        public string ResolveEndpoint()
        {
            if (!string.IsNullOrEmpty(_endpointOverride)
                && TombstackEndpointTrust.TryValidateBaseUrl(_endpointOverride, out var baseUrl, out _))
                return baseUrl;
            return DEFAULT_ENDPOINT.TrimEnd('/');
        }

        /// <summary>Scheme, host and non-default port of <see cref="ResolveEndpoint"/>, lower case.</summary>
        public string ResolveOrigin()
        {
            TombstackEndpointTrust.TryValidateBaseUrl(ResolveEndpoint(), out _, out var origin);
            return origin;
        }

        /// <summary>True when an override is set but cannot be used (not https, or http to a remote host).</summary>
        public bool HasInvalidEndpointOverride =>
            !string.IsNullOrEmpty(_endpointOverride)
            && !TombstackEndpointTrust.TryValidateBaseUrl(_endpointOverride, out _, out _);

        /// <summary>Bind this project to a studio + game and persist.</summary>
        /// <param name="studioId">Server studio id.</param>
        /// <param name="studioName">Studio display name.</param>
        /// <param name="gameId">Server game id.</param>
        /// <param name="gameName">Game display name.</param>
        public void SetLink(string studioId, string studioName, string gameId, string gameName)
        {
            _studioId = studioId ?? "";
            _studioName = studioName ?? "";
            _gameId = gameId ?? "";
            _gameName = gameName ?? "";
            Save(true);
        }

        /// <summary>Remove the project ↔ game binding and persist.</summary>
        public void ClearLink()
        {
            _studioId = "";
            _studioName = "";
            _gameId = "";
            _gameName = "";
            Save(true);
        }

        /// <summary>Set (or clear with "") the endpoint override and persist.</summary>
        /// <param name="endpoint">Base URL like <c>https://tombstack.example.com</c>, or "".</param>
        public void SetEndpointOverride(string endpoint)
        {
            _endpointOverride = endpoint == null ? "" : endpoint.Trim();
            Save(true);
        }
    }
}
