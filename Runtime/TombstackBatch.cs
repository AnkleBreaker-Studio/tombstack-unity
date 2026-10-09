using System;
using System.Collections.Generic;
using System.Text;

namespace AnkleBreaker.Tombstack
{
    /// <summary>
    /// A bounded, preallocated buffer of pre-serialized JSON item strings (events OR metrics), with
    /// the §16/§15 batching policy: accumulate, flush on count or age, drop-oldest beyond cap.
    /// (Those are the ONLY two triggers. This docblock, three others and the README each listed a
    /// third one that fires as the ring approaches capacity; <see cref="Add"/> has never implemented
    /// it, and the plan that specified it was never built. The claim is removed rather than honoured,
    /// because the missing trigger is also what keeps overflow unreachable — see <see cref="FlushCount"/>.)
    /// Steady-state allocation-free: <see cref="Add"/> overwrites a slot in place; only a flush (rare)
    /// builds the envelope string. Thread-safe via a single lock (capture can run off the main thread).
    ///
    /// Perf budget (spec §15): the backing string[] is allocated once at construction and never grows;
    /// adding an item reuses a ring slot (no per-frame/per-item allocation); the age check is a cheap
    /// locked read; an envelope StringBuilder is allocated only on the rare drain. Never throws.
    /// </summary>
    internal sealed class TombstackBatch
    {
        private const int MAX_BATCH_BYTES = 512 * 1024;
        private readonly string[] _items;
        private int _head;
        private int _count;
        private readonly object _lock = new object();

        /// <summary>Flush when this many items have accumulated.
        ///
        /// Clamped to <c>capacity</c> by the constructor, and that clamp is load-bearing rather than
        /// defensive: <see cref="Add"/> flushes at <c>_count &gt;= FlushCount</c>, so while a flush
        /// actually drains the buffer can never REACH capacity and the drop-oldest branch below is
        /// unreachable. Lengthening <see cref="FlushAgeSeconds"/> therefore cannot cause a drop — the
        /// count trigger, not the age trigger, is what bounds occupancy.</summary>
        public readonly int FlushCount;
        /// <summary>Flush when the oldest item is this many seconds old.</summary>
        public readonly float FlushAgeSeconds;
        private double _oldestAtSeconds;

        public TombstackBatch(int capacity, int flushCount, float flushAgeSeconds)
        {
            _items = new string[capacity];
            FlushCount = Math.Min(flushCount, capacity);
            FlushAgeSeconds = flushAgeSeconds;
        }

        /// <summary>Add a pre-serialized item. Returns true when the COUNT flush trigger is now met.
        /// Drops the OLDEST item when at capacity (bounded; never grows) — reachable only when the
        /// caller's flush is a no-op, i.e. before <c>Tombstack.CollectingStarted</c>.</summary>
        public bool Add(string itemJson, double nowSeconds)
        {
            if (string.IsNullOrEmpty(itemJson)) return false;
            bool overflowed;
            bool shouldFlush;
            lock (_lock)
            {
                if (_count == 0) _oldestAtSeconds = nowSeconds;
                overflowed = _count == _items.Length;
                if (overflowed)
                {
                    // Drop-oldest: advance head, keep count at cap (overwrite below).
                    _head = (_head + 1) % _items.Length;
                    _count--;
                }
                int tail = (_head + _count) % _items.Length;
                _items[tail] = itemJson;
                _count++;
                shouldFlush = _count >= FlushCount;
            }
            // Recorded OUTSIDE the lock: Record may touch the session log, and this buffer's lock must
            // not be held across that. An overwritten event/metric used to vanish with no trace at all.
            if (overflowed) TombstackDrops.Record(DropReason.BatchOverflow);
            return shouldFlush;
        }

        /// <summary>True when the buffer holds something older than the age trigger.</summary>
        public bool ShouldFlushByAge(double nowSeconds)
        {
            lock (_lock)
            {
                return _count > 0 && (nowSeconds - _oldestAtSeconds) >= FlushAgeSeconds;
            }
        }

        public bool HasItems
        {
            get { lock (_lock) { return _count > 0; } }
        }

        /// <summary>Discard every buffered item (consent revoked). Not counted as a drop: the data was
        /// withdrawn by the player, not lost by the SDK. Allocation-free.</summary>
        public void Clear()
        {
            lock (_lock)
            {
                Array.Clear(_items, 0, _items.Length);
                _head = 0;
                _count = 0;
            }
        }

        /// <summary>Drain the next byte-bounded envelope, preserving the remaining items.</summary>
        public string DrainEnvelope(string sentAtIso)
        {
            lock (_lock)
            {
                if (_count == 0) return null;
                var sb = new StringBuilder(64 + _count * 128);
                sb.Append("{\"sentAtIso\":\"").Append(sentAtIso).Append("\",\"items\":[");
                int bytes = Encoding.UTF8.GetByteCount(sb.ToString()) + 2;
                int drained = 0;
                while (_count > 0)
                {
                    string item = _items[_head];
                    int itemBytes = Encoding.UTF8.GetByteCount(item) + (drained > 0 ? 1 : 0);
                    // An oversized individual item is isolated so its 413 cannot discard valid neighbours.
                    if (drained > 0 && bytes + itemBytes > MAX_BATCH_BYTES) break;
                    if (drained > 0) sb.Append(',');
                    sb.Append(item);
                    bytes += itemBytes;
                    _items[_head] = null;
                    _head = (_head + 1) % _items.Length;
                    _count--;
                    drained++;
                }
                sb.Append("]}");
                return sb.ToString();
            }
        }

        /// <summary>Drain a snapshot into byte-bounded envelopes; concurrent producers wait for the next flush.</summary>
        public List<string> DrainEnvelopes(string sentAtIso)
        {
            lock (_lock)
            {
                var envelopes = new List<string>();
                while (_count > 0) envelopes.Add(DrainEnvelope(sentAtIso));
                return envelopes;
            }
        }
    }
}
