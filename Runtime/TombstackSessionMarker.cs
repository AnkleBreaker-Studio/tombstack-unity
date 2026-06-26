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
        /// <summary>True when the session ran in the Unity Editor — a Play Mode stop is NOT a player crash.</summary>
        public bool isEditor;
        /// <summary>True when the app was BACKGROUNDED (OnApplicationPause(true)) at its last update — i.e.
        /// the OS suspended it before death. A backgrounded-then-killed app is a normal close, not a crash.</summary>
        public bool backgrounded;
        /// <summary>UTC ISO-8601 of the last lifecycle update (session start, or the last pause/resume).
        /// Used as the crash time for a foreground death so the report is dated near when it actually died.</summary>
        public string lastAliveIso;
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
        // The marker this session wrote, kept in memory so lifecycle updates (foreground/background)
        // can be flushed without re-reading the file. Null until Write() SUCCEEDS. volatile: Write may
        // run off the main thread (deferred SetConsent) while UpdateState runs on the main thread, so
        // the publish/consume of this reference needs acquire/release ordering.
        private static volatile SessionMarkerData _current;

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

        /// <summary>Write this session's marker (start of the dirty-session detection window). The app
        /// starts foreground-active (backgrounded=false); <see cref="UpdateState"/> refreshes that as the
        /// app is paused/resumed so a surviving marker records the state at the moment of death.</summary>
        internal static void Write(string sessionId, string startedAtIso, string buildVersion, string os, string arch, bool isEditor)
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
                    isEditor = isEditor,
                    backgrounded = false,        // a fresh session is foreground-active
                    lastAliveIso = startedAtIso, // refined on each pause/resume
                };
                Directory.CreateDirectory(_dirPath);
                File.WriteAllText(_markerPath, JsonUtility.ToJson(data));
                _current = data; // publish only after the write succeeds → UpdateState stays a no-op until then
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"could not write session marker: {e.Message}");
            }
        }

        /// <summary>
        /// Record a foreground/background transition into the live marker (no-op until Write() has run).
        /// This is what lets the next launch tell a normal background kill (backgrounded=true) from a real
        /// foreground crash (backgrounded=false). Cheap: a single small-file rewrite on each transition.
        /// </summary>
        internal static void UpdateState(bool backgrounded, string nowIso)
        {
            try
            {
                var prev = _current;
                if (_markerPath == null || prev == null) return;
                // Build a fresh object and write it BEFORE publishing — never mutate the shared/live
                // marker before the I/O. If the write throws, _current keeps its last good value (and
                // the disk its last good content), so a later update retries from a consistent baseline.
                var next = new SessionMarkerData
                {
                    sessionId = prev.sessionId,
                    startedAtIso = prev.startedAtIso,
                    buildVersion = prev.buildVersion,
                    os = prev.os,
                    arch = prev.arch,
                    isEditor = prev.isEditor,
                    backgrounded = backgrounded,
                    lastAliveIso = string.IsNullOrEmpty(nowIso) ? prev.lastAliveIso : nowIso,
                };
                File.WriteAllText(_markerPath, JsonUtility.ToJson(next));
                _current = next; // publish only on success
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"could not update session marker: {e.Message}");
            }
        }

        /// <summary>Delete the marker — on clean quit, or to clear a stale one when detection is off.</summary>
        internal static void Delete()
        {
            _current = null;
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
