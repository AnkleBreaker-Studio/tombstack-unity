using UnityEngine;

namespace AnkleBreaker.Tombstack
{
    /// <summary>
    /// Tombstack SDK configuration asset. Create one via
    /// <c>Assets ▸ Create ▸ Tombstack ▸ Config</c>, name it <c>TombstackConfig</c>, and place it
    /// under any <c>Resources/</c> folder for zero-code auto-init. Alternatively call
    /// <see cref="Tombstack.Init(string,string,float)"/> manually and ignore this asset.
    /// </summary>
    [CreateAssetMenu(fileName = "TombstackConfig", menuName = "Tombstack/Config", order = 0)]
    public sealed class TombstackConfigSO : ScriptableObject
    {
        [Tooltip("Tombstack base URL, e.g. https://your-tenant.example.com")]
        [SerializeField] private string _endpoint = "https://your-tenant.example.com";

        [Tooltip("Per-game SDK token (tmb_...). Treat as a secret in shipped builds.")]
        [SerializeField] private string _gameToken = "tmb_REPLACE_ME";

        [Tooltip("Initialize automatically on game load (before the first scene).")]
        [SerializeField] private bool _autoInitOnLoad = true;

        [Tooltip("Require explicit consent (Tombstack.SetConsent(true)) before any capture/upload.")]
        [SerializeField] private bool _requireConsent = false;

        [Tooltip("Seconds between session heartbeats (used for CCU billing + crash rate).")]
        [SerializeField] private float _heartbeatIntervalSeconds = 60f;

        [Tooltip("Automatically capture unhandled exceptions (Unity log, unobserved Tasks, AppDomain) as crash reports.")]
        [SerializeField] private bool _autoCaptureExceptions = true;

        [Tooltip("Keep a rolling session log (~512 KB) and upload it with crash and bug reports.")]
        [SerializeField] private bool _uploadLogs = true;

        [Tooltip("On launch, detect a previous session that ended without a clean shutdown (hard crash, OOM kill, force quit) and report it with the preserved log.")]
        [SerializeField] private bool _detectUncleanShutdown = true;

        [Tooltip("Automatically emit a 'tombstack.rtt_ms' metric measuring the round-trip time of each successful ingest upload.")]
        [SerializeField] private bool _autoRttMetric = true;

        [Tooltip("Automatically add a breadcrumb when a scene loads or the active scene changes.")]
        [SerializeField] private bool _autoSceneBreadcrumbs = true;

        public string Endpoint => _endpoint;
        public string GameToken => _gameToken;
        public bool AutoInitOnLoad => _autoInitOnLoad;
        public bool RequireConsent => _requireConsent;
        public float HeartbeatIntervalSeconds => _heartbeatIntervalSeconds;
        public bool AutoCaptureExceptions => _autoCaptureExceptions;
        public bool UploadLogs => _uploadLogs;
        public bool DetectUncleanShutdown => _detectUncleanShutdown;
        public bool AutoRttMetric => _autoRttMetric;
        public bool AutoSceneBreadcrumbs => _autoSceneBreadcrumbs;
    }
}
