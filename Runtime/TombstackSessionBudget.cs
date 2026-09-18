using System;

namespace AnkleBreaker.Tombstack
{
    /// <summary>
    /// The CLIENT half of the per-session custom-telemetry budget.
    ///
    /// The server enforces this ceiling (<c>src/lib/session-budget.ts</c>) because game clients are
    /// hostile input, and nothing here changes that: this class cannot make the server accept a row, it
    /// can only stop the SDK paying to send one the server would refuse. A refused row has already cost
    /// an HTTP request, a token verification and four rate-counter writes on our side, plus radio time
    /// and a serialized payload on the player's device. Refusing it here costs one integer compare.
    ///
    /// ── WHAT IT COUNTS ──
    ///
    /// CUSTOM rows only: events and metrics, the ones a game's own Track* calls produce. It never sees a
    /// crash, a bug report or a heartbeat — those upload on their own paths and are never budgeted.
    /// Heartbeats in particular ARE the billing meter, and dropping one would change an invoice.
    ///
    /// The SDK's own <c>tombstack.dropped_*</c> health counters are exempt (<c>TrackSdkMetric</c> passes
    /// <c>chargeBudget: false</c>). Without that exemption the feature would eat its own report: a
    /// session over budget could not tell the studio it was over budget.
    ///
    /// ── WHY THE WINDOW IS WALL-CLOCK ALIGNED ──
    ///
    /// The server buckets on <c>floor(unixSeconds / WINDOW_SECONDS)</c> and so does this class, using UTC
    /// epoch seconds. Aligning them means the two halves refuse the SAME rows: a client window that
    /// started at session launch would drift out of phase and start dropping rows the server would have
    /// stored, which is data loss caused by an optimisation.
    ///
    /// The device clock can be wrong, and that is tolerable here in a way it is not for a timestamp: a
    /// skewed clock only shifts WHICH window a row is charged to, never how many rows a window allows.
    /// The server's counter, keyed on the server's own clock, is what actually binds.
    ///
    /// Thread-safe (Track* may be called from any thread) and allocation-free on the hot path. Never throws.
    /// </summary>
    internal static class TombstackSessionBudget
    {
        /// <summary>
        /// Custom rows one session may produce per <see cref="WINDOW_SECONDS"/>.
        ///
        /// PINNED to the server's <c>CUSTOM_ROWS_PER_SESSION_WINDOW</c> by tests/session-budget-sdk.test.ts,
        /// which reads this file. Do not move it here alone — the server is the enforcement, and a client
        /// budget larger than the server's would send rows that get refused anyway while a smaller one
        /// would drop rows the server would have kept. The derivation lives in the server file's docblock.
        /// </summary>
        internal const int CUSTOM_ROWS_PER_SESSION_WINDOW = 60;

        /// <summary>The budget's denominator in seconds. Pinned to the server's SESSION_BUDGET_WINDOW_SECONDS.</summary>
        internal const int WINDOW_SECONDS = 1800;

        /// <summary>Unix second the currently-counted window opened. -1 until the first charge.</summary>
        private static long _windowStart = -1;

        /// <summary>Custom rows charged inside <see cref="_windowStart"/>.</summary>
        private static int _charged;

        private static readonly object _lock = new object();

        /// <summary>
        /// Charge one custom row against this session's budget.
        ///
        /// Returns TRUE when the row is within budget and may be buffered, FALSE when it must be dropped.
        /// A FALSE answer is recorded as <see cref="DropReason.SessionBudget"/> by the caller, so it
        /// reaches the studio's dashboard rather than vanishing — a silent client-side drop is exactly
        /// the failure <see cref="TombstackDrops"/> exists to end.
        ///
        /// Charges the ATTEMPT, not the acceptance: a row refused here still advances the counter, which
        /// is what the server does too, so the two halves stay in agreement about how deep into the
        /// window a session is.
        /// </summary>
        internal static bool TryCharge()
        {
            try
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                long window = (now / WINDOW_SECONDS) * WINDOW_SECONDS;
                lock (_lock)
                {
                    if (window != _windowStart)
                    {
                        _windowStart = window;
                        _charged = 0;
                    }
                    // Increment BEFORE the compare so the Nth row is the last one allowed and the
                    // (N+1)th is the first refused — the same boundary the server's `isOverLimit`
                    // draws. An off-by-one here would put the two halves one row apart forever.
                    _charged++;
                    return _charged <= CUSTOM_ROWS_PER_SESSION_WINDOW;
                }
            }
            catch
            {
                // FAIL OPEN. A budget that cannot be evaluated must not become silent data loss; the
                // server still enforces the real ceiling, and it is the one that binds.
                return true;
            }
        }

        /// <summary>Rows charged in the current window — for diagnostics and tests. Not a wire field.</summary>
        internal static int ChargedInWindow
        {
            get { lock (_lock) { return _charged; } }
        }

        /// <summary>Forget the current window. Called when a NEW session id is minted so a fresh session
        /// starts with a fresh budget instead of inheriting the previous one's remainder.</summary>
        internal static void Reset()
        {
            lock (_lock)
            {
                _windowStart = -1;
                _charged = 0;
            }
        }
    }
}
