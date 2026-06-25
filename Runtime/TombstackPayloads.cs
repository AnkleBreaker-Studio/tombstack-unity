using System;

namespace AnkleBreaker.Tombstack
{
    // Wire DTOs serialized with JsonUtility. CONTRACT WARNING: JsonUtility serializes EVERY
    // public field — null strings become "" and null arrays become []. The server normalizes
    // empty optional ids via cleanOptionalId(); tests/unity-contract.test.ts pins these exact
    // shapes against the Zod ingest schemas. Never rename/add/remove a field here without
    // checking the server schema and the contract test.

    /// <summary>Crash report wire shape for <c>POST /api/v1/ingest/crashes</c>.</summary>
    [Serializable]
    public class CrashPayload
    {
        /// <summary>UTC ISO-8601 timestamp of the exception.</summary>
        public string occurredAtIso;
        /// <summary>Game build version (<c>Application.version</c>).</summary>
        public string buildVersion;
        /// <summary>Whitelisted OS name: windows | macos | linux | other.</summary>
        public string os;
        /// <summary>Whitelisted CPU architecture: x64 | arm64 | x86 | other.</summary>
        public string arch;
        /// <summary>Client-side SHA-256 fingerprint (message + normalized top frames).</summary>
        public string signature;
        /// <summary>Short human-readable hint (the exception message), max 512 chars.</summary>
        public string stackHint;
        /// <summary>Full managed stack trace, max 8192 chars (server derives the canonical signature).</summary>
        public string stackTrace;
        /// <summary>Player id set via <see cref="Tombstack.SetUser"/> ("" when anonymous).</summary>
        public string userId;
        /// <summary>Steam64 id set via <see cref="Tombstack.SetUser"/> ("" when absent).</summary>
        public string steamId;
        /// <summary>Recent log trail leading up to the crash (oldest → newest).</summary>
        public Breadcrumb[] breadcrumbs;
        /// <summary>When true the server returns <c>data.logUpload</c> (presigned PUT for the
        /// session log). JsonUtility serializes <c>false</c> when unset — the server treats
        /// <c>"log":false</c> as no-log, so the plain bool field is contract-safe.</summary>
        public bool log;
        /// <summary>Auto-captured exception screenshot metadata. JsonUtility always serializes this
        /// nested object, so an empty/zero <see cref="ScreenshotMeta.size"/> means "no screenshot";
        /// a positive size makes the server return <c>data.screenshotUpload</c> (presigned PUT).</summary>
        public ScreenshotMeta screenshot;
        /// <summary>Correlation: emitter role — "client" (default) or "server" (after StartMatch).</summary>
        public string role;
        /// <summary>Correlation: server id set via <see cref="Tombstack.SetMatchContext"/> ("" when unset).</summary>
        public string serverId;
        /// <summary>Correlation: match id (SetMatchContext / StartMatch); "" when unset (server cleans to undefined).</summary>
        public string matchId;
        /// <summary>Correlation: this launch's session id (GUID minted at Init).</summary>
        public string sessionId;
        /// <summary>Static device/runtime context (model, OS, CPU/GPU, screen, locale, engine).
        /// Null when capture failed; the server stores only the non-empty fields.</summary>
        public DevicePayload device;
    }

    /// <summary>Screenshot metadata attached to a crash or bug report. The bytes themselves are
    /// uploaded separately to the presigned <c>screenshotUpload</c> PUT; this only signals presence
    /// (<c>size &gt; 0</c>) + integrity. JsonUtility always emits it, so <c>size:0</c> means none.</summary>
    [Serializable]
    public class ScreenshotMeta
    {
        /// <summary>PNG byte length; <c>0</c> ⇒ no screenshot (JsonUtility always serializes the object).</summary>
        public int size;
        /// <summary>Lowercase hex SHA-256 of the PNG bytes ("" when no screenshot).</summary>
        public string sha256;
    }

    /// <summary>One entry of the recent-log trail attached to crashes and bug reports.</summary>
    [Serializable]
    public class Breadcrumb
    {
        /// <summary>UTC ISO-8601 timestamp of the log line.</summary>
        public string tsIso;
        /// <summary>Severity label (Unity LogType name or Info/Warning/Error for manual crumbs).</summary>
        public string level;
        /// <summary>Log message, max 512 chars. Manual category is folded in as a "[category] " prefix.</summary>
        public string message;
    }

    /// <summary>Player bug report wire shape for <c>POST /api/v1/ingest/bug-reports</c>.</summary>
    [Serializable]
    public class BugPayload
    {
        /// <summary>UTC ISO-8601 timestamp of the report.</summary>
        public string occurredAtIso;
        /// <summary>Game build version (<c>Application.version</c>).</summary>
        public string buildVersion;
        /// <summary>Whitelisted OS name: windows | macos | linux | other.</summary>
        public string os;
        /// <summary>Whitelisted CPU architecture: x64 | arm64 | x86 | other.</summary>
        public string arch;
        /// <summary>Optional free-form category (e.g. "ui"), max 32 chars ("" when absent).</summary>
        public string category;
        /// <summary>Player-written report body, max 4000 chars.</summary>
        public string message;
        /// <summary>Player id set via <see cref="Tombstack.SetUser"/> ("" when anonymous).</summary>
        public string userId;
        /// <summary>Steam64 id set via <see cref="Tombstack.SetUser"/> ("" when absent).</summary>
        public string steamId;
        /// <summary>Recent log trail leading up to the report (oldest → newest).</summary>
        public Breadcrumb[] breadcrumbs;
        /// <summary>When true the server returns <c>data.logUpload</c> (presigned PUT for the
        /// session log). Same JsonUtility quirk/contract note as <see cref="CrashPayload.log"/>.</summary>
        public bool log;
        /// <summary>Auto-captured screenshot metadata (same convention as <see cref="CrashPayload.screenshot"/>):
        /// JsonUtility always emits it, <c>size:0</c> ⇒ none, <c>size &gt; 0</c> ⇒ server returns
        /// <c>data.screenshotUpload</c>.</summary>
        public ScreenshotMeta screenshot;
        /// <summary>Correlation: emitter role — "client" (default) or "server" (after StartMatch).</summary>
        public string role;
        /// <summary>Correlation: server id set via <see cref="Tombstack.SetMatchContext"/> ("" when unset).</summary>
        public string serverId;
        /// <summary>Correlation: match id (SetMatchContext / StartMatch); "" when unset (server cleans to undefined).</summary>
        public string matchId;
        /// <summary>Correlation: this launch's session id (GUID minted at Init).</summary>
        public string sessionId;
        /// <summary>Static device/runtime context (model, OS, CPU/GPU, screen, locale, engine).
        /// Null when capture failed; the server stores only the non-empty fields.</summary>
        public DevicePayload device;
    }

    /// <summary>Static device + runtime context, snapshotted once at Init on the main thread and attached
    /// to crash + bug reports. JsonUtility emits every field (empty string / 0 when unknown); the server
    /// keeps only the non-empty values. Mirrors the server <c>deviceSchema</c>.</summary>
    [Serializable]
    public class DevicePayload
    {
        public string model;             // SystemInfo.deviceModel
        public string type;              // SystemInfo.deviceType (Handheld | Desktop | Console | Unknown)
        public string os;                // SystemInfo.operatingSystem (rich string)
        public string osFamily;          // SystemInfo.operatingSystemFamily
        public string cpu;               // SystemInfo.processorType
        public int cpuCount;             // SystemInfo.processorCount
        public int ramMB;                // SystemInfo.systemMemorySize
        public string gpu;               // SystemInfo.graphicsDeviceName
        public string gpuVendor;         // SystemInfo.graphicsDeviceVendor
        public string gpuVersion;        // SystemInfo.graphicsDeviceVersion
        public string gpuApi;            // SystemInfo.graphicsDeviceType
        public int vramMB;               // SystemInfo.graphicsMemorySize
        public string screen;            // "<width>x<height>"
        public float screenDpi;          // Screen.dpi
        public float refreshRate;        // Screen.currentResolution.refreshRateRatio
        public string orientation;       // Screen.orientation
        public bool fullscreen;          // Screen.fullScreen
        public string language;          // Application.systemLanguage
        public string engine;            // Application.unityVersion
        public string scriptingBackend;  // IL2CPP | Mono | Unknown (compile-time)
        public string platform;          // Application.platform
    }

    /// <summary>Session heartbeat wire shape for <c>POST /api/v1/ingest/heartbeats</c> (CCU / Sessions / Fleet).</summary>
    [Serializable]
    public class HeartbeatPayload
    {
        /// <summary>Stable per-launch session id (GUID minted at Init).</summary>
        public string sessionId;
        /// <summary>UTC ISO-8601 timestamp of the heartbeat.</summary>
        public string occurredAtIso;
        /// <summary>Game build version (<c>Application.version</c>) — feeds the Releases/Fleet screens.</summary>
        public string buildVersion;
        /// <summary>Whitelisted OS name: windows | macos | linux | other.</summary>
        public string os;
        /// <summary>Whitelisted CPU architecture: x64 | arm64 | x86 | other.</summary>
        public string arch;
        /// <summary>Player id set via <see cref="Tombstack.SetUser"/> ("" when anonymous) — feeds the Sessions screen.</summary>
        public string userId;
        /// <summary>Correlation: emitter role — "client" (default) or "server" (after StartMatch) — feeds the Fleet screen.</summary>
        public string role;
        /// <summary>Correlation: server id set via <see cref="Tombstack.SetMatchContext"/> ("" when unset).</summary>
        public string serverId;
        /// <summary>Correlation: match id (SetMatchContext / StartMatch); "" when unset (server cleans to undefined).</summary>
        public string matchId;
    }

    // ── Ingest response DTOs (parse-only) ───────────────────────────────────────────────────
    // JsonUtility.FromJson is lenient: unknown response fields are ignored, and absent nested
    // objects come back default-constructed — so the only reliable presence check is
    // string.IsNullOrEmpty(logUpload.url). Internal: never part of the public SDK surface.

    /// <summary>Envelope of a 2xx ingest response (<c>{"success":true,"data":{...}}</c>).</summary>
    [Serializable]
    internal sealed class IngestResponse
    {
        /// <summary>Server success flag (informational — HTTP status is authoritative).</summary>
        public bool success;
        /// <summary>Response payload; <c>logUpload</c> is present when the request set <c>"log":true</c>.</summary>
        public IngestResponseData data;
    }

    /// <summary>Data member of a crash / bug-report ingest response.</summary>
    [Serializable]
    internal sealed class IngestResponseData
    {
        /// <summary>Presigned session-log upload target (empty url when not granted).</summary>
        public LogUploadTarget logUpload;
        /// <summary>Presigned screenshot upload target (empty url when no screenshot was offered).
        /// Same shape as <see cref="LogUploadTarget"/>; POST the PNG via multipart/form-data (image/png,
        /// NO Authorization header — the game token must never reach the storage host).</summary>
        public LogUploadTarget screenshotUpload;
    }

    /// <summary>Presigned S3 POST target for the session log / screenshot (multipart/form-data).</summary>
    [Serializable]
    internal sealed class LogUploadTarget
    {
        /// <summary>Presigned POST URL (the bucket endpoint; time-limited — a 403 means expired/mismatch, drop).</summary>
        public string url;
        /// <summary>S3 object key the server stored on the crash/bug row.</summary>
        public string key;
        /// <summary>Presigned-POST policy fields ({k,v} pairs) appended to the multipart form, in order,
        /// before the `file` part (S3 requires `file` last). JsonUtility-parseable — the server also
        /// sends the raw <c>fields</c> object for browser clients, which JsonUtility cannot read.</summary>
        public FormField[] formFields;
    }

    /// <summary>One presigned-POST policy field (key/value), in a JsonUtility-parseable shape.</summary>
    [Serializable]
    internal sealed class FormField
    {
        public string k;
        public string v;
    }

    // ── Log-pull control-plane DTOs ─────────────────────────────────────────────────────────
    // The heartbeat 202 ack is parse-only (JsonUtility-lenient: absent fields → default, absent
    // arrays → null). The fulfil POST body is serialized like the ingest payloads (null → "",
    // server cleans empties via cleanOptionalId). tests/unity-contract.test.ts pins these keys
    // against the server's pull-request schemas.

    /// <summary>Envelope of the heartbeat 202 ack (<c>{"success":true,"data":{...}}</c>).</summary>
    [Serializable]
    internal sealed class HeartbeatAck
    {
        /// <summary>Server success flag (informational — HTTP status is authoritative).</summary>
        public bool success;
        /// <summary>Ack payload — carries the pull requests targeting this client.</summary>
        public HeartbeatAckData data;
    }

    /// <summary>Data member of the heartbeat ack — the command channel for log-pull requests.</summary>
    [Serializable]
    internal sealed class HeartbeatAckData
    {
        /// <summary>Pull requests this client should honour (empty/absent when none).</summary>
        public PullRequestDto[] pendingRequests;
    }

    /// <summary>One pending log-pull request handed to a client via the heartbeat ack.</summary>
    [Serializable]
    internal sealed class PullRequestDto
    {
        /// <summary>Public ULID of the request; stamped onto the fulfil URL + uploaded log.</summary>
        public string requestId;
        /// <summary>What <see cref="targetValue"/> identifies: userId | sessionId | matchId | server.</summary>
        public string targetType;
        /// <summary>The userId / sessionId / matchId / serverId this request targets.</summary>
        public string targetValue;
        /// <summary>Single-use fulfilment nonce minted server-side for this request; echoed back in the
        /// fulfil body so the server can authenticate the honouring client. Absent on older servers
        /// (JsonUtility leaves it "" → echoed as ""). </summary>
        public string fulfillNonce;
        /// <summary>Unix-epoch expiry of <see cref="fulfillNonce"/>; echoed back so the server can
        /// reject a stale nonce. Absent on older servers (JsonUtility leaves it 0).</summary>
        public long nonceExpiry;
    }

    /// <summary>Body the client POSTs to fulfil a pull (<c>/pull-requests/{id}/fulfill</c>) — its
    /// asserted correlation identity plus the fulfilment nonce, so the server can confirm the client
    /// is genuinely targeted and the fulfilment is authentic + fresh.</summary>
    [Serializable]
    internal sealed class PullFulfillPayload
    {
        /// <summary>Player id set via <see cref="Tombstack.SetUser"/> ("" when anonymous).</summary>
        public string userId;
        /// <summary>This launch's session id (the one the client heartbeated with).</summary>
        public string sessionId;
        /// <summary>Correlation: current match id (null when unset → omitted-equivalent server-side).</summary>
        public string matchId;
        /// <summary>Correlation: current server id (null when unset).</summary>
        public string serverId;
        /// <summary>The <see cref="PullRequestDto.fulfillNonce"/> from the ack, presented back verbatim.</summary>
        public string nonce;
        /// <summary>The <see cref="PullRequestDto.nonceExpiry"/> from the ack, presented back verbatim.</summary>
        public long nonceExpiry;
    }
}
