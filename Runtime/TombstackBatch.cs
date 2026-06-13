using System;
using System.Text;

namespace AnkleBreaker.Tombstack
{
    /// <summary>
    /// A bounded, preallocated buffer of pre-serialized JSON item strings (events OR metrics), with
    /// the §16/§15 batching policy: accumulate, flush on count/age/near-full, drop-oldest beyond cap.
    /// Steady-state allocation-free: <see cref="Add"/> overwrites a slot in place; only a flush (rare)
    /// builds the envelope string. Thread-safe via a single lock (capture can run off the main thread).
    ///
    /// Perf budget (spec §15): the backing string[] is allocated once at construction and never grows;
    /// adding an item reuses a ring slot (no per-frame/per-item allocation); the age check is a cheap
    /// locked read; an envelope StringBuilder is allocated only on the rare drain. Never throws.
    /// </summary>
    internal sealed class TombstackBatch
    {
        private readonly string[] _items;
        private int _head;
        private int _count;
        private readonly object _lock = new object();

        /// <summary>Flush when this many items have accumulated.</summary>
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

        /// <summary>Add a pre-serialized item. Returns true when a flush trigger (count/near-full)
        /// is now met. Drops the OLDEST item when at capacity (bounded; never grows).</summary>
        public bool Add(string itemJson, double nowSeconds)
        {
            if (string.IsNullOrEmpty(itemJson)) return false;
            lock (_lock)
            {
                if (_count == 0) _oldestAtSeconds = nowSeconds;
                if (_count == _items.Length)
                {
                    // Drop-oldest: advance head, keep count at cap (overwrite below).
                    _head = (_head + 1) % _items.Length;
                    _count--;
                }
                int tail = (_head + _count) % _items.Length;
                _items[tail] = itemJson;
                _count++;
                return _count >= FlushCount;
            }
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

        /// <summary>
        /// Drain the buffer into a <c>{ "sentAtIso":…, "items":[…] }</c> envelope string and reset.
        /// Returns null when empty. The items keep their own occurredAtIso (already serialized in).
        /// </summary>
        public string DrainEnvelope(string sentAtIso)
        {
            lock (_lock)
            {
                if (_count == 0) return null;
                var sb = new StringBuilder(64 + _count * 128);
                sb.Append("{\"sentAtIso\":\"").Append(sentAtIso).Append("\",\"items\":[");
                for (int i = 0; i < _count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(_items[(_head + i) % _items.Length]);
                }
                sb.Append("]}");
                _head = 0;
                _count = 0;
                return sb.ToString();
            }
        }
    }
}
