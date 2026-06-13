using System;
using System.IO;
using UnityEngine;

namespace AnkleBreaker.Tombstack
{
    /// <summary>
    /// Contents of the session marker file (<c>Tombstack/session.lock</c>), JsonUtility
    /// round-tripped. Written at session start, deleted on clean quit — a marker found at the
    /// NEXT Init means the previous session died hard (crash, OOM kill, or force quit).
    /// </summary>
    [Serializable]
    internal sealed class SessionMarkerData
    {
        /// <summary>Session id (GUID "N") of the run that wrote the marker.</summary>
        public string sessionId;
        /// <summary>UTC ISO-8601 timestamp of that session's start.</summary>
        public string startedAtIso;
        /// <summary>Build version of that session (used on the synthetic unclean-shutdown report).</summary>
        public string buildVersion;
        /// <summary>OS of that session (windows | macos | linux | other).</summary>
        public string os;
        /// <summary>Architecture of that session (x64 | arm64 | x86 | other).</summary>
        public string arch;
    }

    /// <summary>
    /// Dirty-session ("crashed last run") marker. Pure local file bookkeeping — reading or
    /// deleting the marker is not capture; writing it (and reporting from it) only happens once
    /// capture is allowed. Every file op is wrapped: a locked or missing file degrades to
    /// "no marker", never to an exception in game code.
    /// </summary>
    internal static class TombstackSessionMarker
    {
        private const string DIR_NAME = "Tombstack";
        private const string MARKER_NAME = "session.lock";

        private static string _dirPath;
        private static string _markerPath;

        /// <summary>Cache paths once on the main thread at Init (persistentDataPath rule).</summary>
        internal static void Configure(string persistentDataPath)
        {
            try
            {
                if (string.IsNullOrEmpty(persistentDataPath)) return;
                _dirPath = Path.Combine(persistentDataPath, DIR_NAME);
                _markerPath = Path.Combine(_dirPath, MARKER_NAME);
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"session marker unavailable: {e.Message}");
            }
        }

        /// <summary>
        /// Read AND delete the previous run's marker. Returns null when absent or unreadable —
        /// a null here means "previous session ended cleanly (or nothing is known)".
        /// </summary>
        internal static SessionMarkerData TakePrevious()
        {
            try
            {
                if (_markerPath == null || !File.Exists(_markerPath)) return null;
                var text = File.ReadAllText(_markerPath);
                File.Delete(_markerPath);
                var data = JsonUtility.FromJson<SessionMarkerData>(text);
                return data != null && !string.IsNullOrEmpty(data.sessionId) ? data : null;
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"could not read previous session marker: {e.Message}");
                return null;
            }
        }

        /// <summary>Write this session's marker (start of the dirty-session detection window).</summary>
        internal static void Write(string sessionId, string startedAtIso, string buildVersion, string os, string arch)
        {
            try
            {
                if (_markerPath == null) return;
                var data = new SessionMarkerData
                {
                    sessionId = sessionId,
                    startedAtIso = startedAtIso,
                    buildVersion = buildVersion,
                    os = os,
                    arch = arch,
                };
                Directory.CreateDirectory(_dirPath);
                File.WriteAllText(_markerPath, JsonUtility.ToJson(data));
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"could not write session marker: {e.Message}");
            }
        }

        /// <summary>Delete the marker — on clean quit, or to clear a stale one when detection is off.</summary>
        internal static void Delete()
        {
            try
            {
                if (_markerPath != null && File.Exists(_markerPath)) File.Delete(_markerPath);
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"could not clear session marker: {e.Message}");
            }
        }
    }
}
