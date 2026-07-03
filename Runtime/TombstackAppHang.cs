using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using UnityEngine;

namespace AnkleBreaker.Tombstack
{
    /// <summary>
    /// App-hang watchdog: a background thread watches a pulse counter that the main thread
    /// increments every frame (<see cref="TombstackBehaviour"/>.Update → <see cref="Pump"/>).
    /// When the main thread has not pumped for longer than the configured threshold while the
    /// app is foreground (not paused), the stall start is recorded; when the main thread
    /// RESUMES, its next Pump reports ONE <c>tmb.app_hang</c> event through the normal
    /// TrackEvent pipeline (on the main thread, so the active scene name is safe to read) plus
    /// a Warning breadcrumb. No cross-thread stack capture is attempted — not feasible in
    /// managed Unity — so hang events group by scene instead. Config:
    /// <c>DetectAppHangs</c> / <c>AppHangThresholdSeconds</c> on <see cref="TombstackConfigSO"/>
    /// (default ON / 5s, min 2s; 0 = disabled). Fail-silent: the watchdog body never throws.
    /// </summary>
    internal static class TombstackAppHang
    {
        private const string HANG_EVENT_NAME = "tmb.app_hang";
        private const int POLL_INTERVAL_MS = 500;
        private const int STOP_JOIN_TIMEOUT_MS = POLL_INTERVAL_MS * 3;
        private const float MIN_THRESHOLD_SECONDS = 2f;
        private const float REPORT_COOLDOWN_SECONDS = 60f;

        private static Thread _watchdog;
        private static volatile bool _running;
        // App backgrounded (OnApplicationPause): frames legitimately stop — never a hang.
        private static volatile bool _paused;
        // Incremented by the main thread each frame; read by the watchdog to detect stalls.
        private static volatile int _pulse;
        // Handshake: the watchdog stamps a detected stall, the main thread consumes it on resume.
        // _stallStartSeconds is written BEFORE the volatile _stallPending=true (release) and read
        // AFTER the volatile read of _stallPending (acquire), so the stamp is always visible.
        private static volatile bool _stallPending;
        private static double _stallStartSeconds;
        private static float _thresholdSeconds;
        // Main-thread only: cooldown bookkeeping (max 1 hang report per 60s).
        private static double _lastReportAtSeconds = double.NegativeInfinity;

        /// <summary>Start the watchdog (called from <see cref="TombstackBehaviour.Bootstrap"/> after
        /// init). <paramref name="thresholdSeconds"/> ≤ 0 disables detection; positive values are
        /// clamped to a 2s minimum so an over-eager threshold can't spam events. Never throws.</summary>
        internal static void Start(float thresholdSeconds)
        {
            try
            {
                if (_watchdog != null || thresholdSeconds <= 0f) return;
                _thresholdSeconds = Mathf.Max(MIN_THRESHOLD_SECONDS, thresholdSeconds);
                _running = true;
                _watchdog = new Thread(watch) { IsBackground = true, Name = "Tombstack-AppHang" };
                _watchdog.Start();
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"app-hang watchdog start failed: {e.Message}");
            }
        }

        /// <summary>Stop and join the watchdog (quit path — bounded join, never throws).</summary>
        internal static void Stop()
        {
            try
            {
                _running = false;
                var thread = _watchdog;
                _watchdog = null;
                if (thread != null && thread.IsAlive) thread.Join(STOP_JOIN_TIMEOUT_MS);
            }
            catch { /* quit path must never throw */ }
        }

        /// <summary>Record an app foreground/background transition (from OnApplicationPause).
        /// While paused the watchdog keeps resetting its baseline — a suspended app is not hung.</summary>
        internal static void NotifyPause(bool paused) => _paused = paused;

        /// <summary>
        /// Main-thread pulse, called once per frame from <see cref="TombstackBehaviour"/>.Update.
        /// Steady state is one increment + one volatile read (zero allocation). When the watchdog
        /// flagged a stall while we were frozen, this is the RESUME moment: report the hang once,
        /// under the 60s cooldown, with the active scene name (main thread, so the API is safe).
        /// </summary>
        internal static void Pump()
        {
            _pulse++;
            if (!_stallPending) return;
            _stallPending = false;
            try
            {
                double now = monotonic();
                double durationMs = (now - _stallStartSeconds) * 1000.0;
                if (now - _lastReportAtSeconds < REPORT_COOLDOWN_SECONDS) return;
                _lastReportAtSeconds = now;
                string duration = ((long)durationMs).ToString(CultureInfo.InvariantCulture);
                string threshold = ((long)(_thresholdSeconds * 1000f)).ToString(CultureInfo.InvariantCulture);
                string scene = activeSceneName();
                Tombstack.AddBreadcrumb("app hang " + duration + "ms", BreadcrumbLevel.Warning);
                Tombstack.TrackEvent(HANG_EVENT_NAME, new Dictionary<string, string>
                {
                    { "duration_ms", duration },
                    { "scene", scene },
                    { "threshold_ms", threshold },
                });
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"app-hang report failed: {e.Message}");
            }
        }

        /// <summary>Watchdog body: sleep 500ms per check; when the pulse hasn't moved for longer
        /// than the threshold (and the app is not paused), stamp the stall ONCE and let the main
        /// thread report it on resume. Wrapped whole — a watchdog must never take the app down.</summary>
        private static void watch()
        {
            try
            {
                int lastPulse = _pulse;
                double lastPumpAtSeconds = monotonic();
                bool stallSignalled = false;
                while (_running)
                {
                    Thread.Sleep(POLL_INTERVAL_MS);
                    int pulse = _pulse;
                    double now = monotonic();
                    if (pulse != lastPulse)
                    {
                        // Main thread is alive — advance the baseline. (A previously signalled
                        // stall is being consumed by Pump on that same resumed main thread.)
                        lastPulse = pulse;
                        lastPumpAtSeconds = now;
                        stallSignalled = false;
                        continue;
                    }
                    if (_paused)
                    {
                        // Backgrounded: keep resetting so suspended time never counts as a hang.
                        lastPumpAtSeconds = now;
                        stallSignalled = false;
                        continue;
                    }
                    if (!stallSignalled && now - lastPumpAtSeconds > _thresholdSeconds)
                    {
                        _stallStartSeconds = lastPumpAtSeconds; // stamp BEFORE the volatile flag (release)
                        _stallPending = true;
                        stallSignalled = true; // one signal per stall — re-armed when the pulse moves
                    }
                }
            }
            catch { /* watchdog thread must never throw (domain teardown aborts land here) */ }
        }

        /// <summary>Active scene name for grouping (main-thread only — called from Pump).</summary>
        private static string activeSceneName()
        {
            try
            {
                var name = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                return string.IsNullOrEmpty(name) ? "unknown" : name;
            }
            catch
            {
                return "unknown";
            }
        }

        /// <summary>Monotonic seconds (never runs backward on a clock/NTP jump).</summary>
        private static double monotonic() =>
            System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
    }
}
