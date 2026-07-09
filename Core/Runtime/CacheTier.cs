namespace PFound.RemoteResourceCache.Core
{
    /// <summary>
    /// Which tier answered a request. Ordered fastest→slowest; a hit populates every faster tier above it, so a
    /// second request for the same key reports a nearer tier. <see cref="None"/> is the sentinel for a failure
    /// result (nothing served it).
    /// </summary>
    public enum CacheTier
    {
        None = 0,
        Memory = 1,
        Disk = 2,
        Remote = 3,
    }
}
