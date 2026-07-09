using System;
using System.Collections.Generic;

namespace PFound.RemoteResourceCache.Core
{
    /// <summary>
    /// The fastest tier: an in-memory map of decoded resources bounded by entry count and/or total bytes, with
    /// ref-counted pinning and a selectable <see cref="EvictionStrategy"/> (LRU / LFU / TimeBased). Each entry
    /// carries the metadata the strategies need — a monotonic last-access stamp, an access count, and a creation
    /// stamp — and eviction scans for the lowest-ranked UNPINNED entry: a pinned (in-use) resource is never
    /// evicted, even if that pushes the tier over its caps. When an entry actually leaves the tier (evicted,
    /// removed, replaced, or cleared) an optional disposer fires so the adapter can release the underlying native
    /// object (e.g. destroy a Texture2D). Reaching ref-count zero re-checks the caps, so a resource that was only
    /// held past its cap by a pin is disposed the moment its last lease is released.
    ///
    /// A <b>time-to-live</b> is enforced on READ (symmetrically with the disk tier): if a positive TTL is set,
    /// an entry whose wall-clock age exceeds it is a miss — it is evicted (and disposed) at read time and the
    /// caller falls through to a slower tier, so a stale-in-RAM resource is never handed back.
    /// </summary>
    internal sealed class MemoryResourceCache<T>
    {
        private sealed class Entry
        {
            public string Key;
            public T Value;
            public long Size;
            public int RefCount;
            public long CreationSeq;
            public long LastAccessSeq;
            public long AccessCount;
            public DateTime CreatedUtc; // wall-clock stamp for TTL expiry (distinct from the monotonic CreationSeq)
        }

        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly int _maxCount;   // 0 = unbounded
        private readonly long _maxBytes;  // 0 = unbounded
        private readonly EvictionStrategy _strategy;
        private readonly Action<T> _dispose;
        private readonly TimeSpan _ttl;   // Zero = entries never expire
        private readonly Func<DateTime> _clock;

        private long _bytes;
        private long _seq;
        private long _creationSeq;

        public MemoryResourceCache(int maxCount, long maxBytes, EvictionStrategy strategy, Action<T> dispose,
            TimeSpan ttl, Func<DateTime> clock)
        {
            _maxCount = maxCount;
            _maxBytes = maxBytes;
            _strategy = strategy;
            _dispose = dispose;
            _ttl = ttl;
            _clock = clock;
        }

        public int Count => _entries.Count;
        public long Bytes => _bytes;

        public bool Contains(string key) => _entries.ContainsKey(key);
        public bool IsPinned(string key) => _entries.TryGetValue(key, out var e) && e.RefCount > 0;

        public bool TryGet(string key, out T value)
        {
            if (_entries.TryGetValue(key, out var e))
            {
                if (IsExpired(e))
                {
                    // Stale in RAM: drop + dispose it and report a miss so the caller falls through to a
                    // slower tier (which re-validates its own TTL and re-fetches if needed).
                    Remove(key);
                    value = default;
                    return false;
                }
                Touch(e);
                value = e.Value;
                return true;
            }
            value = default;
            return false;
        }

        private bool IsExpired(Entry e) => _ttl > TimeSpan.Zero && (_clock() - e.CreatedUtc) > _ttl;

        public void Insert(string key, T value, long size) => Upsert(key, value, size, pin: false);

        public void Acquire(string key, T value, long size) => Upsert(key, value, size, pin: true);

        public void Release(string key)
        {
            // Fail-fast: releasing an unknown or unpinned key is a caller bug.
            Entry e = _entries[key];
            if (e.RefCount == 0)
                throw new InvalidOperationException("Release without a matching Acquire for key '" + key + "'.");
            e.RefCount--;
            if (e.RefCount == 0)
                EvictToCaps(null); // an entry kept past the cap only because it was pinned now becomes evictable
        }

        /// <summary>Removes one entry and disposes its value; returns false if absent. Fires the disposal hook.</summary>
        public bool Remove(string key)
        {
            if (!_entries.TryGetValue(key, out var e)) return false;
            _entries.Remove(key);
            _bytes -= e.Size;
            _dispose?.Invoke(e.Value);
            return true;
        }

        /// <summary>Removes and disposes every entry (outstanding leases become invalid).</summary>
        public void Clear()
        {
            if (_dispose != null)
                foreach (var e in _entries.Values) _dispose(e.Value);
            _entries.Clear();
            _bytes = 0;
        }

        private void Upsert(string key, T value, long size, bool pin)
        {
            if (_entries.TryGetValue(key, out var e))
            {
                if (_dispose != null && !EqualityComparer<T>.Default.Equals(e.Value, value))
                    _dispose(e.Value); // replacing the value: release the old native object
                _bytes += size - e.Size;
                e.Value = value;
                e.Size = size;
                e.CreatedUtc = _clock(); // fresh content restarts the TTL clock
                Touch(e);
            }
            else
            {
                e = new Entry
                {
                    Key = key, Value = value, Size = size, CreatedUtc = _clock(),
                    CreationSeq = ++_creationSeq, LastAccessSeq = ++_seq, AccessCount = 1,
                };
                _entries[key] = e;
                _bytes += size;
            }

            // Pin BEFORE eviction so a freshly acquired entry can't be evicted by its own insert.
            if (pin) e.RefCount++;

            // Exempt the just-inserted entry: an insert evicts OTHER idle entries, never the value it is caching
            // and handing back this very moment.
            EvictToCaps(e);

            // If the tier is STILL over its caps and this newcomer is unpinned, the only thing left to drop is the
            // newcomer itself (every other entry is pinned). Drop it — but do NOT dispose it: it is a live value
            // being returned to the caller right now, so destroying its native object here would be a use-after-free.
            // A pinned newcomer stays (pins are allowed to exceed the caps).
            if (e.RefCount == 0 && OverCaps() && _entries.ContainsKey(key))
            {
                _entries.Remove(key);
                _bytes -= e.Size;
            }
        }

        private void Touch(Entry e)
        {
            e.LastAccessSeq = ++_seq;
            e.AccessCount++;
        }

        private void EvictToCaps(Entry exempt)
        {
            while (OverCaps())
            {
                Entry victim = SelectVictim(exempt);
                if (victim == null) break; // everything left is pinned or exempt — allowed to exceed the cap
                _entries.Remove(victim.Key);
                _bytes -= victim.Size;
                _dispose?.Invoke(victim.Value);
            }
        }

        private bool OverCaps() =>
            (_maxCount > 0 && _entries.Count > _maxCount) ||
            (_maxBytes > 0 && _bytes > _maxBytes);

        private Entry SelectVictim(Entry exempt)
        {
            Entry best = null;
            foreach (var e in _entries.Values)
            {
                if (e.RefCount > 0 || ReferenceEquals(e, exempt)) continue; // pinned / just-inserted are exempt
                if (best == null || RanksLowerThan(e, best))
                    best = e;
            }
            return best;
        }

        // a "ranks lower" than b == a is the better eviction victim.
        private bool RanksLowerThan(Entry a, Entry b)
        {
            switch (_strategy)
            {
                case EvictionStrategy.Lfu:
                    return a.AccessCount != b.AccessCount ? a.AccessCount < b.AccessCount : a.LastAccessSeq < b.LastAccessSeq;
                case EvictionStrategy.TimeBased:
                    return a.CreationSeq < b.CreationSeq;
                default: // Lru
                    return a.LastAccessSeq < b.LastAccessSeq;
            }
        }
    }
}
