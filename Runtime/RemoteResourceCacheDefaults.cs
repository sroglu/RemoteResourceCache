using PFound.RemoteResourceCache.Core;

namespace PFound.RemoteResourceCache
{
    /// <summary>
    /// The process-wide default remote transport. The foundation keeps NO hard reference to any HTTP library —
    /// an optional adapter (e.g. the <c>PFOUND_BESTHTTP</c>-gated BestHTTP transport) reaches in via a
    /// <c>[RuntimeInitializeOnLoadMethod]</c> registrar and installs itself here before the first scene loads.
    /// Factories read this when no transport is injected; it is null until something registers one (fail-fast:
    /// building a cache with neither an injected nor a registered transport throws).
    /// </summary>
    public static class RemoteResourceCacheDefaults
    {
        public static IResourceTransport Transport { get; set; }
    }
}
