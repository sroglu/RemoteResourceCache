namespace PFound.RemoteResourceCache.Core
{
    /// <summary>
    /// How a bounded tier chooses which entry to drop when it is over its cap. Applies to both the memory and
    /// disk tiers (each picks its own). Pinned memory entries are exempt from all strategies.
    /// </summary>
    public enum EvictionStrategy
    {
        /// <summary>Least-recently-used: drop the entry whose last access is oldest. The default.</summary>
        Lru = 0,

        /// <summary>Least-frequently-used: drop the entry with the fewest accesses (ties broken by least-recently-used).</summary>
        Lfu = 1,

        /// <summary>Drop the entry created longest ago (oldest-created-first), regardless of access pattern.</summary>
        TimeBased = 2,
    }
}
