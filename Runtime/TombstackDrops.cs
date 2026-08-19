using System.Threading;

namespace AnkleBreaker.Tombstack
{
    /// <summary>
    /// Why the SDK let go of a payload it had already accepted from the game. One reason per
    /// silent-discard site in the uploader; each gets its own metric name so the studio can tell
    /// "my players are offline for a long time" apart from "the server is rejecting my payloads".
    /// </summary>
    internal enum DropReason
    {
        /// <summary>The on-disk write-ahead queue is at its file cap. Crash/bug reports still go out
        /// this session, but they are no longer durable — one that exhausts its in-session retries is
        /// gone for good instead of being retried on the next launch.</summary>
        OfflineQueueFull = 0,
        /// <summary>The in-memory outbound queue hit its soft cap and evicted its oldest non-crash
        /// payload (an event/metric batch or a heartbeat) to stay bounded.</summary>
        OutboundQueueFull = 1,
        /// <summary>The server answered 4xx (validation/auth/expired presign), so the payload is
        /// poison and was discarded rather than retried forever.</summary>
        Rejected = 2,
        /// <summary>An event/metric batch buffer was full and overwrote its oldest item — the game is
        /// producing telemetry faster than it can be flushed.</summary>
        BatchOverflow = 3,
    }

    /// <summary>
    /// Counts payloads the SDK discards, and ships those counts where the STUDIO can see them
    /// (Sentry calls the same idea a client report). Before this existed, the 64-file offline-queue
    /// cap and the three other discard sites returned in silence: at best a <c>[Tombstack]</c> console
    /// warning on the player's own machine, which the SDK deliberately keeps OUT of the uploaded
    /// session log to avoid a capture feedback loop. So a studio could lose crash reports for a month
    /// and never learn.
    ///
    /// Two surfaces, both on existing shipped contracts (no server change):
    /// <list type="bullet">
    /// <item>a <c>tombstack.dropped_*</c> metric per reason, emitted on the next SUCCESSFUL ingest
    /// upload — it becomes its own series in the dashboard's metric explorer;</item>
    /// <item>a line in the rolling session log, which rides along with every crash and bug report.</item>
    /// </list>
    /// Reporting is deliberately deferred to a success: a drop usually means the device is offline or
    /// backed up, and emitting the report right then would just queue behind (or cause) more drops.
    ///
    /// Counters are cumulative for the launch; the reported figure is the DELTA since the last report,
    /// and it is cleared only once the metric is confirmed buffered — a report that could not be
    /// emitted rolls into the next one instead of vanishing. Thread-safe (drops are recorded from the
    /// upload coroutine, the batch buffers, and any thread calling Track*). Never throws.
    /// </summary>
    internal static class TombstackDrops
    {
        /// <summary>Metric-name prefix; a full name is this plus the reason's suffix below.
        /// tests/sdk-dropped-visibility.test.ts pins these literals against the server metric schema
        /// and the dashboard's metric explorer.</summary>
        internal const string METRIC_PREFIX = "tombstack.dropped_";

        /// <summary>Unit label on every drop metric (they count payloads, they are not durations).</summary>
        internal const string METRIC_UNIT = "count";

        internal const int REASON_COUNT = 4;

        private static readonly string[] _suffix =
        {
            "offline_queue_full",
            "outbound_queue_full",
            "rejected",
            "batch_overflow",
        };

        private static readonly string[] _why =
        {
            "the offline queue is at its file cap, so crash/bug reports are no longer durable across a restart",
            "the outbound queue hit its cap and evicted its oldest non-crash payload",
            "the server rejected the payload with a 4xx, so it was discarded instead of retried",
            "an event/metric batch buffer overflowed before it could be flushed",
        };

        // Cumulative for this launch (never reset) — what GetDiagnostics reports.
        private static readonly int[] _total = new int[REASON_COUNT];
        // Not yet shipped to the studio. Cleared only once a metric is confirmed buffered.
        private static readonly int[] _unreported = new int[REASON_COUNT];
        // Log-once latch per reason, so a long offline stretch cannot spam the console or the log.
        private static readonly int[] _announced = new int[REASON_COUNT];

        /// <summary>The metric name carrying <paramref name="reason"/>, e.g.
        /// <c>tombstack.dropped_offline_queue_full</c>.</summary>
        internal static string MetricName(DropReason reason) => METRIC_PREFIX + _suffix[(int)reason];

        /// <summary>One sentence explaining <paramref name="reason"/>, written into the session log.</summary>
        internal static string Explain(DropReason reason) => _why[(int)reason];

        /// <summary>Payloads discarded so far this launch, all reasons. Surfaced by
        /// <see cref="Tombstack.GetDiagnostics"/> so a dev HUD can show it without waiting for a send.</summary>
        internal static int TotalDropped
        {
            get
            {
                int sum = 0;
                for (int i = 0; i < REASON_COUNT; i++) sum += Volatile.Read(ref _total[i]);
                return sum;
            }
        }

        /// <summary>Payloads discarded so far this launch for one reason.</summary>
        internal static int DroppedFor(DropReason reason) => Volatile.Read(ref _total[(int)reason]);

        /// <summary>
        /// Record one discarded payload. Lock-free and allocation-free on the repeat path — the first
        /// drop of each reason also writes one console warning and one session-log line, so the FACT of
        /// dropping reaches a crash report even if no upload ever succeeds afterwards.
        /// Safe to call from any thread and from inside another lock. Never throws.
        /// </summary>
        internal static void Record(DropReason reason)
        {
            try
            {
                int i = (int)reason;
                Interlocked.Increment(ref _total[i]);
                Interlocked.Increment(ref _unreported[i]);
                if (Interlocked.Exchange(ref _announced[i], 1) != 0) return;
                TombstackLog.Warn(
                    "dropping payloads (" + _suffix[i] + "): " + _why[i] +
                    ". Counted, and reported to your dashboard as the " + MetricName(reason) + " metric.");
                writeToSessionLog("started dropping payloads (" + _suffix[i] + "): " + _why[i]);
            }
            catch { /* visibility bookkeeping must never take the game down */ }
        }

        /// <summary>
        /// Ship every reason with a pending count as a <c>tombstack.dropped_*</c> metric. Called by the
        /// upload host right after a successful ingest POST (never after the metrics batch itself — see
        /// the recursion gate at the call site). A reason whose metric could not be buffered keeps its
        /// count for the next attempt rather than losing it. Never throws.
        /// </summary>
        internal static void ReportPending()
        {
            try
            {
                for (int i = 0; i < REASON_COUNT; i++)
                {
                    int pending = Volatile.Read(ref _unreported[i]);
                    if (pending <= 0) continue;
                    // Confirm-then-clear: TrackSdkMetric returns false when the metric was NOT buffered
                    // (not initialized, or consent denied). Clearing first would re-create the exact bug
                    // this class exists to fix — a count that disappears with nobody told.
                    if (!Tombstack.TrackSdkMetric(METRIC_PREFIX + _suffix[i], pending, METRIC_UNIT)) continue;
                    Interlocked.Add(ref _unreported[i], -pending);
                    writeToSessionLog(
                        pending + " payload(s) dropped (" + _suffix[i] + "), " +
                        Volatile.Read(ref _total[i]) + " this launch: " + _why[i]);
                }
            }
            catch { /* reporting is best-effort; a failure here must never break the upload path */ }
        }

        /// <summary>Write one marker into the rolling session log — the copy that is uploaded with
        /// crashes and bug reports. Goes in DIRECTLY rather than through <see cref="TombstackLog"/>,
        /// whose <c>[Tombstack]</c> lines are deliberately filtered out of the mirror by
        /// <c>Tombstack.handleLog</c>. No-ops when the studio turned session-log upload off. Never throws.</summary>
        private static void writeToSessionLog(string message)
        {
            try
            {
                if (!Tombstack.SessionLogEnabled) return;
                TombstackSessionLog.Append("Warning", TombstackLog.PREFIX + message, null);
            }
            catch { /* best-effort */ }
        }
    }
}
