using System;
using System.Collections.Generic;
using System.IO;

namespace PFound.RemoteResourceCache.Core
{
    /// <summary>
    /// The persistent middle tier: byte blobs on disk (via an <see cref="IBlobStore"/>) plus an in-memory index
    /// of per-entry metadata (size, created, last-accessed, access-count) that lets the tier ENFORCE its own
    /// bound — a max entry count and/or byte cap, evicting by the chosen <see cref="EvictionStrategy"/>. The
    /// metadata index is persisted to a sidecar with a DEBOUNCED flush (in memory during the session, written
    /// periodically and on shutdown) so a hot read/write path never rewrites the whole store, yet LFU/TimeBased
    /// ordering survives a restart. Blob presence is sourced from the filesystem, so an offline read of a
    /// pre-warmed cache works even if the sidecar was never flushed (the entry is adopted from the file's mtime).
    /// TTL expiry is enforced on read. A <b>content version</b> stamps the generation of the cached bytes: when
    /// the configured version differs from the one persisted in the sidecar, the whole prior generation is purged
    /// on open, so stale bytes never leak across a format/schema change. Engine-free / mono-testable.
    /// </summary>
    public sealed class DiskCache
    {
        private sealed class DiskEntry
        {
            public string Key;
            public long Size;
            public DateTime CreatedUtc;
            public DateTime LastAccessedUtc;
            public long AccessCount;
            public long AccessSeq; // per-session monotonic tiebreak for equal timestamps
        }

        private const uint Magic = 0x58435252; // "RRCX"
        private const int FormatVersion = 2;    // v2 adds the content-version stamp to the sidecar header

        private readonly IBlobStore _store;
        private readonly string _metadataPath;
        private readonly EvictionStrategy _strategy;
        private readonly int _maxEntries;
        private readonly long _maxBytes;
        private readonly TimeSpan _ttl;
        private readonly TimeSpan _flushInterval;
        private readonly int _contentVersion;
        private readonly Func<DateTime> _clock;

        private readonly Dictionary<string, DiskEntry> _index = new Dictionary<string, DiskEntry>(StringComparer.Ordinal);
        private long _bytes;
        private long _seq;
        private bool _dirty;
        private DateTime _lastFlush;

        public DiskCache(
            IBlobStore store,
            string metadataPath,
            EvictionStrategy strategy = EvictionStrategy.Lru,
            int maxEntries = 0,
            long maxBytes = 0,
            TimeSpan ttl = default,
            TimeSpan flushInterval = default,
            Func<DateTime> clock = null,
            int contentVersion = 0)
        {
            _store = store;
            _metadataPath = metadataPath;
            _strategy = strategy;
            _maxEntries = maxEntries;
            _maxBytes = maxBytes;
            _ttl = ttl;
            _flushInterval = flushInterval;
            _contentVersion = contentVersion;
            _clock = clock ?? (() => DateTime.UtcNow);
            _lastFlush = _clock();
            int storedVersion = LoadIndex();
            if (storedVersion != _contentVersion)
                PurgeGeneration();
        }

        /// <summary>Builds a disk tier from the disk-related knobs of a <see cref="CachePolicy"/>.</summary>
        public static DiskCache FromPolicy(IBlobStore store, string metadataPath, CachePolicy policy, Func<DateTime> clock = null) =>
            new DiskCache(store, metadataPath, policy.DiskEviction, policy.MaxDiskEntries, policy.MaxDiskBytes,
                policy.Ttl, policy.DiskFlushInterval, clock, policy.DiskContentVersion);

        public int Count => _index.Count;
        public long Bytes => _bytes;

        /// <summary>The content generation this tier is stamped with (see <see cref="CachePolicy.DiskContentVersion"/>).</summary>
        public int ContentVersion => _contentVersion;

        public bool TryGetMetadata(string key, out long size, out DateTime createdUtc, out DateTime lastAccessedUtc, out long accessCount)
        {
            if (_index.TryGetValue(key, out var e))
            {
                size = e.Size; createdUtc = e.CreatedUtc; lastAccessedUtc = e.LastAccessedUtc; accessCount = e.AccessCount;
                return true;
            }
            size = 0; createdUtc = default; lastAccessedUtc = default; accessCount = 0;
            return false;
        }

        /// <summary>Reads a fresh blob and records the access; returns false on a miss OR when the entry has expired past its TTL.</summary>
        public bool TryRead(string key, out byte[] bytes)
        {
            if (!_store.TryRead(key, out bytes))
                return false;

            DiskEntry entry = GetOrAdopt(key, bytes.Length);

            if (_ttl > TimeSpan.Zero && (_clock() - entry.CreatedUtc) > _ttl)
            {
                Remove(key);
                bytes = null;
                return false;
            }

            entry.LastAccessedUtc = _clock();
            entry.AccessSeq = ++_seq;
            entry.AccessCount++;
            _dirty = true;
            MaybeFlush();
            return true;
        }

        /// <summary>Writes a blob atomically and enforces the tier's bound with the chosen strategy.</summary>
        public void Write(string key, byte[] bytes)
        {
            _store.Write(key, bytes);
            DateTime now = _clock();
            if (_index.TryGetValue(key, out var e))
            {
                _bytes -= e.Size;
                e.Size = bytes.Length;
                e.CreatedUtc = now;
                e.LastAccessedUtc = now;
                e.AccessSeq = ++_seq;
                e.AccessCount++;
                _bytes += e.Size;
            }
            else
            {
                e = new DiskEntry
                {
                    Key = key, Size = bytes.Length, CreatedUtc = now,
                    LastAccessedUtc = now, AccessSeq = ++_seq, AccessCount = 1,
                };
                _index[key] = e;
                _bytes += e.Size;
            }
            _dirty = true;
            EnforceBounds();
            MaybeFlush();
        }

        public bool Exists(string key) => _store.Exists(key);

        public void Remove(string key)
        {
            if (_index.TryGetValue(key, out var e))
            {
                _bytes -= e.Size;
                _index.Remove(key);
            }
            _store.Delete(key);
            _dirty = true;
        }

        /// <summary>Drops every blob (the store's whole scope, orphans included) and the metadata sidecar.</summary>
        public void Clear()
        {
            _store.Clear();
            _index.Clear();
            _bytes = 0;
            _dirty = false;
            if (File.Exists(_metadataPath)) File.Delete(_metadataPath);
            _lastFlush = _clock();
        }

        /// <summary>
        /// Wipes the store and re-stamps the sidecar with the current content version. Runs on open when the
        /// persisted version does not match the configured one, so a prior generation's bytes are never served.
        /// </summary>
        private void PurgeGeneration()
        {
            _store.Clear();
            _index.Clear();
            _bytes = 0;
            // Persist the new generation stamp immediately so a crash before the debounced flush doesn't leave
            // an ambiguous (or old) version on disk. Purging an empty index is cheap.
            _dirty = true;
            Flush();
        }

        /// <summary>Persists the metadata index now (called on shutdown, or when the debounce interval elapses).</summary>
        public void Flush()
        {
            using (var ms = new MemoryStream())
            {
                using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    w.Write(Magic);
                    w.Write(FormatVersion);
                    w.Write(_contentVersion);
                    w.Write(_index.Count);
                    foreach (var e in _index.Values)
                    {
                        w.Write(e.Key);
                        w.Write(e.Size);
                        w.Write(e.CreatedUtc.Ticks);
                        w.Write(e.LastAccessedUtc.Ticks);
                        w.Write(e.AccessCount);
                    }
                }
                string temp = _metadataPath + ".tmp-" + Guid.NewGuid().ToString("N");
                File.WriteAllBytes(temp, ms.ToArray());
                if (File.Exists(_metadataPath)) File.Delete(_metadataPath);
                File.Move(temp, _metadataPath);
            }
            _dirty = false;
            _lastFlush = _clock();
        }

        private void MaybeFlush()
        {
            if (!_dirty) return;
            if (_flushInterval <= TimeSpan.Zero || (_clock() - _lastFlush) >= _flushInterval)
                Flush();
        }

        private DiskEntry GetOrAdopt(string key, long size)
        {
            if (_index.TryGetValue(key, out var e))
                return e;

            // A file present with no index entry (a prior session that wrote but never flushed): adopt it from
            // the file's own mtime so reads still work and it counts toward the bound.
            DateTime created = _store.GetTimestampUtc(key);
            e = new DiskEntry
            {
                Key = key, Size = size, CreatedUtc = created,
                LastAccessedUtc = created, AccessSeq = ++_seq, AccessCount = 1,
            };
            _index[key] = e;
            _bytes += size;
            _dirty = true;
            return e;
        }

        private void EnforceBounds()
        {
            while (OverBounds())
            {
                DiskEntry victim = SelectVictim();
                if (victim == null) break;
                Remove(victim.Key);
            }
        }

        private bool OverBounds() =>
            (_maxEntries > 0 && _index.Count > _maxEntries) ||
            (_maxBytes > 0 && _bytes > _maxBytes);

        private DiskEntry SelectVictim()
        {
            DiskEntry best = null;
            foreach (var e in _index.Values)
                if (best == null || RanksLowerThan(e, best))
                    best = e;
            return best;
        }

        // "Ranks lower" == "should be evicted before". a beats b when a is the better victim.
        private bool RanksLowerThan(DiskEntry a, DiskEntry b)
        {
            switch (_strategy)
            {
                case EvictionStrategy.Lfu:
                    if (a.AccessCount != b.AccessCount) return a.AccessCount < b.AccessCount;
                    return a.LastAccessedUtc != b.LastAccessedUtc ? a.LastAccessedUtc < b.LastAccessedUtc : a.AccessSeq < b.AccessSeq;
                case EvictionStrategy.TimeBased:
                    return a.CreatedUtc < b.CreatedUtc;
                default: // Lru
                    return a.LastAccessedUtc != b.LastAccessedUtc ? a.LastAccessedUtc < b.LastAccessedUtc : a.AccessSeq < b.AccessSeq;
            }
        }

        /// <summary>Loads the sidecar and returns the content version it was stamped with (matching the configured
        /// version when there is nothing prior to reconcile, so a fresh or unreadable sidecar never triggers a purge).</summary>
        private int LoadIndex()
        {
            if (!File.Exists(_metadataPath)) return _contentVersion;
            byte[] raw = File.ReadAllBytes(_metadataPath);
            using (var ms = new MemoryStream(raw))
            using (var r = new BinaryReader(ms, System.Text.Encoding.UTF8))
            {
                if (ms.Length < 16 || r.ReadUInt32() != Magic) return _contentVersion;
                int format = r.ReadInt32();
                if (format != FormatVersion) return _contentVersion;
                int storedVersion = r.ReadInt32();
                int count = r.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    var e = new DiskEntry
                    {
                        Key = r.ReadString(),
                        Size = r.ReadInt64(),
                        CreatedUtc = new DateTime(r.ReadInt64(), DateTimeKind.Utc),
                        LastAccessedUtc = new DateTime(r.ReadInt64(), DateTimeKind.Utc),
                        AccessCount = r.ReadInt64(),
                        AccessSeq = ++_seq,
                    };
                    // Only keep entries whose blob is actually still present.
                    if (_store.Exists(e.Key))
                    {
                        _index[e.Key] = e;
                        _bytes += e.Size;
                    }
                }
                return storedVersion;
            }
        }
    }
}
