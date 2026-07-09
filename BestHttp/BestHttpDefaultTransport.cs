#if PFOUND_BESTHTTP
using UnityEngine;

namespace PFound.RemoteResourceCache.Transport
{
    /// <summary>
    /// Installs <see cref="BestHttpResourceTransport"/> as the process-wide default transport whenever this
    /// adapter is compiled in (the <c>PFOUND_BESTHTTP</c> define). Runs before the first scene loads, so a
    /// <see cref="RemoteTextureCache.Create"/> call with no injected transport picks BestHTTP. The foundation
    /// keeps no hard reference to BestHTTP — the optional assembly reaches in, not the reverse.
    /// </summary>
    internal static class BestHttpDefaultTransport
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register() => RemoteResourceCacheDefaults.Transport = new BestHttpResourceTransport();
    }
}
#endif
