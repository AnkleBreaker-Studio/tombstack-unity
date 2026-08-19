using UnityEngine;

namespace AnkleBreaker.Tombstack
{
    /// <summary>
    /// Single internal logger for the SDK. Every internal failure funnels through here so the
    /// SDK never throws into game code, and so SDK-emitted lines carry a stable prefix that the
    /// breadcrumb recorder filters out (prevents a Tombstack-warning → breadcrumb feedback loop).
    /// </summary>
    internal static class TombstackLog
    {
        /// <summary>Prefix on every SDK log line; breadcrumb capture skips lines that start with it.</summary>
        internal const string PREFIX = "[Tombstack] ";

        /// <summary>Log a non-fatal SDK warning. Never throws.</summary>
        internal static void Warn(string message)
        {
            try { Debug.LogWarning(PREFIX + message); }
            catch { /* logging must never take the game down */ }
        }

        /// <summary>Log an informational SDK line (e.g. a benign decision like skipping a report). Never throws.</summary>
        internal static void Info(string message)
        {
            try { Debug.Log(PREFIX + message); }
            catch { /* logging must never take the game down */ }
        }

        /// <summary>
        /// Log a MISCONFIGURATION the SDK cannot work around — it captured nothing and never will
        /// until a human changes something. Never throws.
        ///
        /// Distinct from <see cref="Warn"/> deliberately. Every diagnostic the SDK emitted was a
        /// warning, including "Init skipped: missing token or endpoint" — and Unity's console
        /// collapses warnings behind a toggle, so the one line explaining why zero telemetry ever
        /// arrived sat where most developers never look. A studio following the zero-code path with an
        /// unfilled config saw no error, no data, and an onboarding wizard stuck on "Waiting for first
        /// crash…" forever, with nothing anywhere connecting the three.
        /// </summary>
        internal static void Error(string message)
        {
            try { Debug.LogError(PREFIX + message); }
            catch { /* logging must never take the game down */ }
        }
    }
}
