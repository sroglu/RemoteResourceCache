using System;

namespace PFound.RemoteResourceCache.Core
{
    /// <summary>
    /// Tuning for what each tier retains, how it evicts, and how long disk entries stay valid. Both tiers are
    /// bounded by an entry count and/or a byte cap (0 = unbounded on that axis) and each picks its own
    /// <see cref="EvictionStrategy"/>. Every field has a permissive default, so an empty policy is a valid one.
    /// </summary>
    public sealed class CachePolicy
    {
        // ---- memory tier ----
        public int MaxMemoryCount { get; set; }
        public long MaxMemoryBytes { get; set; }
        public EvictionStrategy MemoryEviction { get; set; } = EvictionStrategy.Lru;

        // ---- disk tier ----
        /// <summary>Max entries retained on disk; 0 = unbounded by count. The disk tier ENFORCES this.</summary>
        public int MaxDiskEntries { get; set; }

        /// <summary>Max total bytes retained on disk; 0 = unbounded by bytes. The disk tier ENFORCES this.</summary>
        public long MaxDiskBytes { get; set; }

        public EvictionStrategy DiskEviction { get; set; } = EvictionStrategy.Lru;

        /// <summary>How often the disk metadata index is allowed to flush to its sidecar (debounced). Zero = flush on every mutation.</summary>
        public TimeSpan DiskFlushInterval { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Content generation stamp for the on-disk store. Bump it whenever the meaning of cached bytes changes
        /// (a new encoding, a schema change, a forced refresh) so a cache written by an older generation is
        /// purged wholesale the next time the disk tier opens — old blobs never leak into a new generation.
        /// Default 0.
        /// </summary>
        public int DiskContentVersion { get; set; }

        /// <summary>
        /// Optional namespace for the disk root: an isolated sub-scope so several logical caches can share one
        /// root directory without their blobs (or their purges/clears) colliding. Null/empty = the root itself.
        /// </summary>
        public string DiskPartition { get; set; }

        // ---- shared ----
        /// <summary>How long a cached entry stays valid before it is treated as a miss and re-fetched; enforced on
        /// read by BOTH the memory and disk tiers. <see cref="TimeSpan.Zero"/> = never expires.</summary>
        public TimeSpan Ttl { get; set; }

        /// <summary>Optional gate for what may be cached; null = cache everything. Non-cacheable keys are still served, just not retained.</summary>
        public Func<string, bool> Cacheable { get; set; }

        public bool CanCache(string key) => Cacheable == null || Cacheable(key);

        public static CachePolicy Default => new CachePolicy();
    }
}
