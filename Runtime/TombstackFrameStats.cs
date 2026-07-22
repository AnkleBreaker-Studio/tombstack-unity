using System.Globalization;

namespace AnkleBreaker.Tombstack
{
    /// <summary>
    /// Per-heartbeat-interval frame statistics: pumped once per frame from
    /// <see cref="TombstackBehaviour"/>'s Update with <c>Time.unscaledDeltaTime</c>, drained by
    /// the heartbeat builder. Plain static fields — zero allocations per frame; the only
    /// allocation is the JSON fragment built once per heartbeat. Main-thread only (Update and
    /// the heartbeat coroutine both run there), so no locking. Editor-safe: pure arithmetic,
    /// no engine APIs.
    /// </summary>
    internal static class TombstackFrameStats
    {
        // 33.4 ms ≈ a missed 30 FPS frame budget; 250 ms is a visible hitch on any target.
        private const float SLOW_FRAME_MS = 33.4f;
        private const float HITCH_FRAME_MS = 250f;
        // Server contract clamps (heartbeat-schema.ts): fpsAvg ≤ 1000, hitchCount ≤ 1e6, worstFrameMs ≤ 1e7.
        private const float MAX_FPS = 1000f;
        private const int MAX_HITCH_COUNT = 1000000;
        private const int MAX_WORST_FRAME_MS = 10000000;

        // Intra-beat sampling: snapshot the average FPS every ~20s within the (default 60s) heartbeat
        // window and ship the series so the dashboard sees sub-beat frame health without extra rows.
        private const float SUBSAMPLE_WINDOW_SECONDS = 20f;
        private const int MAX_SUBSAMPLES = 6; // 6 × 20s covers up to a 120s beat (interval cap is 600s but samples are bounded)

        private static int _frameCount;
        private static float _elapsedSeconds;
        private static int _slowFrames;
        private static int _hitchCount;
        private static float _worstFrameMs;

        // 20s sub-window accumulators + the collected per-window FPS snapshots for this beat.
        private static int _subFrames;
        private static float _subElapsedSeconds;
        private static readonly float[] _subSamples = new float[MAX_SUBSAMPLES];
        private static int _subCount;

        /// <summary>Accumulate one frame. Called once per frame with <c>Time.unscaledDeltaTime</c>
        /// (unscaled so a paused/slow-motion timeScale never fakes a slow frame). Allocation-free.</summary>
        internal static void Sample(float unscaledDeltaSeconds)
        {
            // The first frame after a load/pause can report 0 — skip it rather than divide by it.
            if (unscaledDeltaSeconds <= 0f) return;
            _frameCount++;
            _elapsedSeconds += unscaledDeltaSeconds;
            float ms = unscaledDeltaSeconds * 1000f;
            if (ms > SLOW_FRAME_MS) _slowFrames++;
            if (ms > HITCH_FRAME_MS && _hitchCount < MAX_HITCH_COUNT) _hitchCount++;
            if (ms > _worstFrameMs) _worstFrameMs = ms;

            // 20s intra-beat window: when it fills, snapshot its average FPS and start the next window.
            // Allocation-free (writes into the fixed buffer); the last (partial) window is captured at drain.
            _subFrames++;
            _subElapsedSeconds += unscaledDeltaSeconds;
            if (_subElapsedSeconds >= SUBSAMPLE_WINDOW_SECONDS)
            {
                captureSubSample();
                _subFrames = 0;
                _subElapsedSeconds = 0f;
            }
        }

        /// <summary>Record the current sub-window's average FPS (clamped) into the fixed buffer, dropping
        /// the oldest silently once full so a very long beat can't grow the series unbounded.</summary>
        private static void captureSubSample()
        {
            if (_subFrames <= 0 || _subElapsedSeconds <= 0f) return;
            float fps = _subFrames / _subElapsedSeconds;
            if (fps > MAX_FPS) fps = MAX_FPS;
            if (_subCount < MAX_SUBSAMPLES) _subSamples[_subCount++] = fps;
        }

        /// <summary>
        /// Drain the accumulator into the heartbeat's OPTIONAL wire fragment —
        /// <c>"fpsAvg":59.8,"slowFramePct":1.2,"hitchCount":0,"worstFrameMs":45</c> — and reset
        /// for the next interval. Returns null when no frame was sampled, so the heartbeat omits
        /// the fields entirely (they are spliced into the JsonUtility output exactly like the
        /// user-metadata object; JsonUtility itself never sees them). InvariantCulture, 1 decimal
        /// on the two rates, values clamped to the server contract.
        /// </summary>
        internal static string ConsumeJson()
        {
            if (_frameCount <= 0 || _elapsedSeconds <= 0f)
            {
                reset();
                return null;
            }
            float fpsAvg = _frameCount / _elapsedSeconds;
            if (fpsAvg > MAX_FPS) fpsAvg = MAX_FPS;
            float slowFramePct = _slowFrames * 100f / _frameCount;
            if (slowFramePct > 100f) slowFramePct = 100f;
            int hitchCount = _hitchCount;
            int worstFrameMs = _worstFrameMs > MAX_WORST_FRAME_MS ? MAX_WORST_FRAME_MS : (int)_worstFrameMs;
            // Capture the trailing partial 20s window so a 60s beat yields ~3 samples (not 2 + a lost tail).
            captureSubSample();
            var subCount = _subCount;
            var samples = new float[subCount];
            for (int i = 0; i < subCount; i++) samples[i] = _subSamples[i];
            reset();
            var core = string.Format(
                CultureInfo.InvariantCulture,
                "\"fpsAvg\":{0:0.0},\"slowFramePct\":{1:0.0},\"hitchCount\":{2},\"worstFrameMs\":{3}",
                fpsAvg, slowFramePct, hitchCount, worstFrameMs);
            if (subCount == 0) return core;
            // Per-20s FPS series folded into this beat (no extra rows): "fpsSamples":[59.9,58.1,60.0]
            var sb = new System.Text.StringBuilder(core.Length + subCount * 7 + 16);
            sb.Append(core).Append(",\"fpsSamples\":[");
            for (int i = 0; i < subCount; i++)
            {
                if (i > 0) sb.Append(',');
                sb.AppendFormat(CultureInfo.InvariantCulture, "{0:0.0}", samples[i]);
            }
            sb.Append(']');
            return sb.ToString();
        }

        /// <summary>Start a fresh accumulation interval (every drain resets, sampled or not).</summary>
        private static void reset()
        {
            _frameCount = 0;
            _elapsedSeconds = 0f;
            _slowFrames = 0;
            _hitchCount = 0;
            _worstFrameMs = 0f;
            _subFrames = 0;
            _subElapsedSeconds = 0f;
            _subCount = 0;
        }
    }
}
