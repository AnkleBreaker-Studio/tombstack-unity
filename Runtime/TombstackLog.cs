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
    }
}
