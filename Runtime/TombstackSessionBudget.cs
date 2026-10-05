using System;
using System.Threading;

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
        /// <summary>The budget's denominator in seconds. Pinned to the server's SESSION_BUDGET_WINDOW_SECONDS.</summary>
        internal const int WINDOW_SECONDS = 1800;

        /// <summary>Response header carrying the budget the SERVER applies to this studio.</summary>
        internal const string SERVER_BUDGET_HEADER = "X-Tombstack-Session-Budget";

        /// <summary>
        /// The limit the server announced: -1 = not heard yet (nothing is refused, rows are still counted),
        /// 0 = NO budget for this studio, &gt;0 = that many rows per window.
        ///
        /// WHY NOTHING IS REFUSED BEFORE THE SERVER ANSWERS. Until the first events/metrics reply, this class
        /// used to assume the free-plan ceiling. A game that fires many events at launch (funnel steps
        /// first of all) therefore lost them on EVERY launch, paying studio or not, and on studios the owner
        /// raised to a higher capacity (src/lib/ingest-capacity.ts) the client would refuse what the server
        /// accepts. The server is the enforcement either way; the client only saves uploads, so the safe
        /// direction while it does not know is to send.
        ///
        /// WHY THE SERVER DECIDES. Since 2026-09-25 the server lifts the budget for studios that pay for their
        /// rows (overage-priced), because on a real game it was discarding over half of all custom telemetry —
        /// most of it HERE, before sending — and funnels lost the people whose steps were dropped. A hardcoded
        /// client budget cannot follow a per-studio policy, so the SDK adopts what the server says it enforces.
        /// Kept across sessions of one launch (it is a studio property); reset only by the static reset.
        /// </summary>
        private static int _serverLimit = -1;

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
                int announced = Volatile.Read(ref _serverLimit);
                if (announced == 0) return true; // the server applies no budget to this studio
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
                    // Counted even before the server has answered, so the two halves agree on how deep
                    // into the window this session is once it does; refused only against a KNOWN limit.
                    return announced < 0 || _charged <= announced;
                }
            }
            catch
            {
                // FAIL OPEN. A budget that cannot be evaluated must not become silent data loss; the
                // server still enforces the real ceiling, and it is the one that binds.
                return true;
            }
        }

        /// <summary>
        /// Adopt the budget announced by an ingest response: <c>none</c>, or <c>&lt;rows&gt;/&lt;windowSeconds&gt;</c>.
        /// A missing or unreadable header changes nothing (an older server, a proxy, a failure page); a limit
        /// quoted over a different window than this SDK counts is ignored rather than misapplied.
        /// </summary>
        internal static void ObserveServerHeader(string value)
        {
            try
            {
                if (string.IsNullOrEmpty(value)) return;
                string v = value.Trim();
                if (string.Equals(v, "none", StringComparison.OrdinalIgnoreCase))
                {
                    Volatile.Write(ref _serverLimit, 0);
                    return;
                }
                int slash = v.IndexOf('/');
                if (slash <= 0) return;
                if (!int.TryParse(v.Substring(0, slash), out int rows) || rows <= 0) return;
                if (!int.TryParse(v.Substring(slash + 1), out int window) || window != WINDOW_SECONDS) return;
                Volatile.Write(ref _serverLimit, rows);
            }
            catch
            {
                // Never throws into the upload loop; the previous limit stays in force.
            }
        }

        /// <summary>The limit currently applied (-1 = default, 0 = none). For diagnostics and tests.</summary>
        internal static int ServerLimit => Volatile.Read(ref _serverLimit);

        /// <summary>Forget the server's announcement — static reset only (Enter Play Mode without domain reload).</summary>
        internal static void ResetServerLimit() => Volatile.Write(ref _serverLimit, -1);

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
