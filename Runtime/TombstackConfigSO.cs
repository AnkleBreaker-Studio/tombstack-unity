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

        [Tooltip("When ON (default), automatically-caught exceptions are reported even while running in the Unity Editor. Turn OFF to silence automatic exception reports during in-Editor testing — manual Tombstack.ReportException still sends, and shipped builds are unaffected.")]
        [SerializeField] private bool _sendExceptionsInEditor = true;

        [Tooltip("Attach an automatic screenshot of the current frame to player bug reports (ON by default — players expect a report to capture what they see).")]
        [SerializeField] private bool _captureScreenshotOnBugReport = true;

        [Tooltip("Attach an automatic screenshot when an exception is captured, for visual context (OFF by default — opt-in; throttled and invisible to the player).")]
        [SerializeField] private bool _captureScreenshotOnException = false;

        [Tooltip("Longest-side pixel cap for captured screenshots (downscaled to keep uploads small). 0 = full resolution.")]
        [SerializeField] private int _screenshotMaxDimension = 1280;

        [Tooltip("Minimum seconds between exception screenshots, so an exception storm cannot spam GPU readbacks.")]
        [SerializeField] private float _exceptionScreenshotThrottleSeconds = 10f;

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
        public bool SendExceptionsInEditor => _sendExceptionsInEditor;
        public bool CaptureScreenshotOnBugReport => _captureScreenshotOnBugReport;
        public bool CaptureScreenshotOnException => _captureScreenshotOnException;
        public int ScreenshotMaxDimension => _screenshotMaxDimension;
        public float ExceptionScreenshotThrottleSeconds => _exceptionScreenshotThrottleSeconds;
    }
}
