using System;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine;
#endif

namespace AnkleBreaker.Tombstack
{
    /// <summary>
    /// Resolved OS exit reason for a previous (dead) session. Null-field convention: a null
    /// <see cref="signature"/>/<see cref="stackHint"/> means "no override — keep the generic
    /// unclean-shutdown grouping", while <see cref="osExitReason"/> may still carry the raw OS
    /// constant as occurrence detail. <see cref="suppressReport"/> means the OS says this was NOT
    /// a crash at all (user force-stop, self-exit, permission change) — report nothing.
    /// </summary>
    internal sealed class ExitReasonInfo
    {
        /// <summary>Coarse cause label: oom | anr | signal | native_crash | app_crash | init_failure | excessive_resource.</summary>
        public string crashType;
        /// <summary>Raw OS constant name (e.g. "REASON_LOW_MEMORY") — occurrence detail.</summary>
        public string osExitReason;
        /// <summary>POSIX signal number when the process was signaled (0 otherwise).</summary>
        public int osSignal;
        /// <summary>Resident set size at death, in BYTES (0 = unknown). Occurrence detail — deliberately
        /// never part of the grouping signature (a unique RSS per crash would shatter grouping).</summary>
        public long rssAtDeathBytes;
        /// <summary>Grouping signature override (e.g. "oom-kill"), or null to keep the generic one.</summary>
        public string signature;
        /// <summary>Human hint override, or null to keep the generic unclean-shutdown hint.</summary>
        public string stackHint;
        /// <summary>True when the OS reports a non-crash termination — the caller must NOT report.</summary>
        public bool suppressReport;
    }

    /// <summary>
    /// Post-mortem OS exit-reason lookup (v0.17). No in-process handler can ever classify an OOM
    /// kill / ANR / force-stop — those are a SIGKILL from the kernel, no handler runs. The only
    /// source of truth is the OS itself, asked at the NEXT launch: Android 11+ exposes
    /// <c>ActivityManager.getHistoricalProcessExitReasons</c> (ApplicationExitInfo). Everything is
    /// fail-soft: any JNI/platform failure degrades to null → the caller keeps the existing
    /// heuristic exactly as before. iOS MetricKit is the planned counterpart (not yet wired).
    /// </summary>
    internal static class TombstackExitInfo
    {
        // ApplicationExitInfo reason constants — stable public Android API values (API 30+).
        private const int REASON_EXIT_SELF = 1;
        private const int REASON_SIGNALED = 2;
        private const int REASON_LOW_MEMORY = 3;
        private const int REASON_CRASH = 4;
        private const int REASON_CRASH_NATIVE = 5;
        private const int REASON_ANR = 6;
        private const int REASON_INITIALIZATION_FAILURE = 7;
        private const int REASON_PERMISSION_CHANGE = 8;
        private const int REASON_EXCESSIVE_RESOURCE_USAGE = 9;
        private const int REASON_USER_REQUESTED = 10;
        private const int REASON_USER_STOPPED = 11;
        private const int REASON_DEPENDENCY_DIED = 12;
        private const int REASON_OTHER = 13;
        private const int REASON_FREEZER = 14;

        /// <summary>This process's OS pid, persisted into the session marker so the NEXT launch can
        /// ask the OS specifically about this process's death. 0 when unavailable (lookup skipped).</summary>
        internal static int CurrentPid()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var process = new AndroidJavaClass("android.os.Process"))
                {
                    return process.CallStatic<int>("myPid");
                }
            }
            catch (Exception)
            {
                // fall through to the managed fallback below
            }
#endif
            try
            {
                return System.Diagnostics.Process.GetCurrentProcess().Id;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>
        /// Ask the OS why the previous process (by pid, from the surviving marker) died. Returns
        /// null when unknown/unsupported (pre-Android-11, non-Android, pid unavailable, any JNI
        /// failure) — the caller then keeps the generic unclean-shutdown heuristic unchanged.
        /// </summary>
        internal static ExitReasonInfo QueryPrevious(int pid)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (pid <= 0) return null;
            try
            {
                using (var version = new AndroidJavaClass("android.os.Build$VERSION"))
                {
                    if (version.GetStatic<int>("SDK_INT") < 30) return null; // ApplicationExitInfo is API 30+
                }
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                {
                    if (activity == null) return null;
                    var packageName = activity.Call<string>("getPackageName");
                    using (var am = activity.Call<AndroidJavaObject>("getSystemService", "activity"))
                    {
                        if (am == null) return null;
                        // pid filter = the strong match key: the OS returns only entries for OUR
                        // package, and pid narrows it to the exact process the marker belongs to.
                        using (var list = am.Call<AndroidJavaObject>("getHistoricalProcessExitReasons", packageName, pid, 1))
                        {
                            if (list == null || list.Call<int>("size") == 0) return null;
                            using (var info = list.Call<AndroidJavaObject>("get", 0))
                            {
                                int reason = info.Call<int>("getReason");
                                int status = info.Call<int>("getStatus");
                                long rssKb = 0;
                                try { rssKb = info.Call<long>("getRss"); } catch (Exception) { /* detail only */ }
                                // getRss() reports KILOBYTES — normalize to bytes for the wire.
                                return map(reason, status, rssKb * 1024L);
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"exit-reason lookup failed (keeping heuristic): {e.Message}");
                return null;
            }
#else
            return null;
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        /// <summary>Pure mapping: ApplicationExitInfo reason → crash classification. Non-crash
        /// terminations suppress; unknown/ambiguous reasons attach the raw constant only (generic
        /// grouping kept). Signature stays REASON-scoped — RSS/signal detail rides in fields.</summary>
        private static ExitReasonInfo map(int reason, int status, long rssBytes)
        {
            switch (reason)
            {
                case REASON_LOW_MEMORY:
                    return make("oom", "REASON_LOW_MEMORY", 0, rssBytes, "oom-kill",
                        "Out of memory — killed by the OS low-memory killer");
                case REASON_ANR:
                    return make("anr", "REASON_ANR", 0, rssBytes, "anr-kill",
                        "App Not Responding — killed by the OS watchdog");
                case REASON_SIGNALED:
                    return make("signal", "REASON_SIGNALED", status, rssBytes, $"native-signal-{status}",
                        $"Killed by signal {status}{signalName(status)}");
                case REASON_CRASH_NATIVE:
                    return make("native_crash", "REASON_CRASH_NATIVE", 0, rssBytes, "native-crash",
                        "Native crash in the previous session (OS-reported)");
                case REASON_CRASH:
                    return make("app_crash", "REASON_CRASH", 0, rssBytes, "app-crash",
                        "Unhandled application exception in the previous session (OS-reported)");
                case REASON_INITIALIZATION_FAILURE:
                    return make("init_failure", "REASON_INITIALIZATION_FAILURE", 0, rssBytes, "init-failure",
                        "Process failed to initialize (OS-reported)");
                case REASON_EXCESSIVE_RESOURCE_USAGE:
                    return make("excessive_resource", "REASON_EXCESSIVE_RESOURCE_USAGE", 0, rssBytes, "excessive-resource-kill",
                        "Killed by the OS for excessive resource usage");
                // NOT crashes: the user/system stopped the app on purpose. Reporting these as
                // crashes is exactly the false positive this feature removes.
                case REASON_EXIT_SELF:
                case REASON_PERMISSION_CHANGE:
                case REASON_USER_REQUESTED:
                case REASON_USER_STOPPED:
                    return new ExitReasonInfo { osExitReason = reasonName(reason), suppressReport = true };
                // Ambiguous — keep the generic unclean-shutdown grouping, attach the raw constant.
                case REASON_DEPENDENCY_DIED:
                case REASON_FREEZER:
                case REASON_OTHER:
                    return new ExitReasonInfo { osExitReason = reasonName(reason), rssAtDeathBytes = rssBytes };
                default:
                    return null; // unknown future reason → behave exactly as pre-0.17
            }
        }

        private static ExitReasonInfo make(string crashType, string osExitReason, int osSignal, long rssBytes, string signature, string stackHint)
        {
            return new ExitReasonInfo
            {
                crashType = crashType,
                osExitReason = osExitReason,
                osSignal = osSignal,
                rssAtDeathBytes = rssBytes,
                signature = signature,
                stackHint = stackHint,
            };
        }

        private static string reasonName(int reason)
        {
            switch (reason)
            {
                case REASON_EXIT_SELF: return "REASON_EXIT_SELF";
                case REASON_PERMISSION_CHANGE: return "REASON_PERMISSION_CHANGE";
                case REASON_USER_REQUESTED: return "REASON_USER_REQUESTED";
                case REASON_USER_STOPPED: return "REASON_USER_STOPPED";
                case REASON_DEPENDENCY_DIED: return "REASON_DEPENDENCY_DIED";
                case REASON_FREEZER: return "REASON_FREEZER";
                case REASON_OTHER: return "REASON_OTHER";
                default: return $"REASON_{reason}";
            }
        }

        /// <summary>Human name for the common kill signals (" (SIGSEGV)"); empty when unknown.</summary>
        private static string signalName(int signal)
        {
            switch (signal)
            {
                case 4: return " (SIGILL)";
                case 5: return " (SIGTRAP)";
                case 6: return " (SIGABRT)";
                case 7: return " (SIGBUS)";
                case 8: return " (SIGFPE)";
                case 9: return " (SIGKILL — possibly the low-memory killer)";
                case 11: return " (SIGSEGV)";
                case 13: return " (SIGPIPE)";
                case 15: return " (SIGTERM)";
                default: return string.Empty;
            }
        }
#endif
    }
}
