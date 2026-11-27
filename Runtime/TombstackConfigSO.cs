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
        /// <summary>
        /// The unfilled endpoint default. <c>example.com</c> is IANA-reserved and
        /// <c>your-tenant.example.com</c> does not resolve — verified: curl answers
        /// "Could not resolve host". Public so the runtime guard in <c>Tombstack.Init</c> and the
        /// editor's <c>HasSdkToken</c> compare against the SAME literal that initialises the field
        /// below. A second copy of a placeholder is how a guard silently stops matching the default.
        /// </summary>
        public const string ENDPOINT_PLACEHOLDER = "https://your-tenant.example.com";

        /// <summary>The unfilled token default. See <see cref="ENDPOINT_PLACEHOLDER"/>.</summary>
        public const string TOKEN_PLACEHOLDER = "tmb_REPLACE_ME";

        // Tombstack credentials that also start with "tmb_" but are NOT per-game SDK tokens (an SDK token
        // is "tmb_" + 48 hex, so these can never be one). This asset ships inside the build, readable by
        // anyone who has it; only the ingest-only SDK token is made for that. Same prefixes as the server
        // (src/lib/studio-key.ts, editor-auth.ts, mcp-key.ts; tests/unity-shipped-credential-guard.test.ts).
        internal const string STUDIO_KEY_PREFIX = "tmb_st_";
        internal const string EDITOR_TOKEN_PREFIX = "tmb_ed_";
        internal const string MCP_KEY_PREFIX = "tmb_mcp_";

        /// <summary>What kind of non-SDK Tombstack credential <paramref name="token"/> is (for a message),
        /// or null when it is not one: a studio key, an editor sign-in token or an MCP key must never be
        /// in a game build. Read by the runtime (refuses to initialise), the editor and the build guard.</summary>
        internal static string DescribeNonSdkCredential(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            var t = token.Trim();
            if (t.StartsWith(STUDIO_KEY_PREFIX, System.StringComparison.Ordinal)) return "a studio key (tmb_st_...)";
            if (t.StartsWith(EDITOR_TOKEN_PREFIX, System.StringComparison.Ordinal)) return "an editor sign-in token (tmb_ed_...)";
            if (t.StartsWith(MCP_KEY_PREFIX, System.StringComparison.Ordinal)) return "an MCP key (tmb_mcp_...)";
            return null;
        }

        [Tooltip("Tombstack base URL — copy it from your dashboard. MUST be changed: the default does not resolve, and the SDK refuses to initialise while it is unfilled.")]
        [SerializeField] private string _endpoint = ENDPOINT_PLACEHOLDER;

        [Tooltip("Per-game SDK token (tmb_...). Treat as a secret in shipped builds. MUST be changed: the SDK refuses to initialise while it is unfilled.")]
        [SerializeField] private string _gameToken = TOKEN_PLACEHOLDER;

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

        [Tooltip("Emit a 'tombstack.rtt_ms' metric: the mean round-trip time of the SDK's uploads, at most one value every 15 minutes. Each value is stored and billed as a metric row, but does not use the per-session allowance for your own events and metrics.")]
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
