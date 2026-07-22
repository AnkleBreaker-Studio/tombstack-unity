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

        [Tooltip("Start sending telemetry automatically at Init (default). Turn OFF to defer the first heartbeat until the game calls Tombstack.StartSession() — call that AFTER SetUser/SetEnvironment/SetUserMetadata so the first beat carries the player's identity + environment instead of anonymous + production. Crash/bug reports still send while deferred.")]
        [SerializeField] private bool _autoStartSession = true;

        [Tooltip("Seconds between session heartbeats (used for CCU billing + crash rate).")]
        [SerializeField] private float _heartbeatIntervalSeconds = 60f;

        [Tooltip("Send periodic session heartbeats. WARNING: turning this OFF blanks live CCU, sessions, crash-free %, releases, fleet liveness, user-metadata delivery, and server-triggered log pulls — the SDK logs a warning at init when disabled.")]
        [SerializeField] private bool _sendHeartbeats = true;

        [Tooltip("Collect per-heartbeat frame statistics (fpsAvg / slowFramePct / hitchCount / worstFrameMs, sampled allocation-free each frame). OFF omits the frame fields from heartbeats entirely.")]
        [SerializeField] private bool _collectFrameStats = true;

        [Tooltip("Deployment environment stamped on all telemetry (e.g. \"production\", \"development\"). Feeds the dashboard environment filter. Leave as \"production\" unless this build targets a non-prod environment.")]
        [SerializeField] private string _environment = "production";

        [Tooltip("Automatically capture unhandled exceptions (Unity log, unobserved Tasks, AppDomain) as crash reports.")]
        [SerializeField] private bool _autoCaptureExceptions = true;

        [Tooltip("Keep a rolling session log (~512 KB) and upload it with crash and bug reports.")]
        [SerializeField] private bool _uploadLogs = true;

        [Tooltip("How many recent launch/session logs to retain on disk (keyed by session id), so a specific PAST session's log can be pulled on demand by a server-side log request. Clamped to 1..10; default 3.")]
        [SerializeField] private int _retainedLaunchLogs = 3;

        [Tooltip("On launch, detect a previous session that ended without a clean shutdown (hard crash, OOM kill, force quit) and report it with the preserved log.")]
        [SerializeField] private bool _detectUncleanShutdown = true;

        [Tooltip("Automatically emit a 'tombstack.rtt_ms' metric measuring the round-trip time of each successful ingest upload.")]
        [SerializeField] private bool _autoRttMetric = true;

        [Tooltip("Automatically add a breadcrumb when a scene loads or the active scene changes.")]
        [SerializeField] private bool _autoSceneBreadcrumbs = true;

        [Tooltip("Detect app hangs: when the main thread stalls longer than the threshold while foreground, report a 'tmb.app_hang' analytics event (with duration, scene, threshold) once it recovers. Max one report per minute.")]
        [SerializeField] private bool _detectAppHangs = true;

        [Tooltip("Seconds the main thread must be unresponsive before it counts as an app hang. Minimum 2; 0 disables detection.")]
        [SerializeField] private float _appHangThresholdSeconds = 5f;

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
        public bool AutoStartSession => _autoStartSession;
        public float HeartbeatIntervalSeconds => _heartbeatIntervalSeconds;
        public bool SendHeartbeats => _sendHeartbeats;
        public bool CollectFrameStats => _collectFrameStats;
        public string Environment => _environment;
        public bool AutoCaptureExceptions => _autoCaptureExceptions;
        public bool UploadLogs => _uploadLogs;
        public int RetainedLaunchLogs => _retainedLaunchLogs;
        public bool DetectUncleanShutdown => _detectUncleanShutdown;
        public bool AutoRttMetric => _autoRttMetric;
        public bool AutoSceneBreadcrumbs => _autoSceneBreadcrumbs;
        public bool DetectAppHangs => _detectAppHangs;
        public float AppHangThresholdSeconds => _appHangThresholdSeconds;
        public bool SendExceptionsInEditor => _sendExceptionsInEditor;
        public bool CaptureScreenshotOnBugReport => _captureScreenshotOnBugReport;
        public bool CaptureScreenshotOnException => _captureScreenshotOnException;
        public int ScreenshotMaxDimension => _screenshotMaxDimension;
        public float ExceptionScreenshotThrottleSeconds => _exceptionScreenshotThrottleSeconds;
    }
}
