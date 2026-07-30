#if PFOUND_BESTHTTP
using System;
using System.Threading;
using System.Threading.Tasks;
using BestHTTP;
using PFound.RemoteResourceCache.Core;

namespace PFound.RemoteResourceCache.Transport
{
    /// <summary>
    /// The default remote tier: an <see cref="IResourceTransport"/> over the BestHTTP library (HTTP/2,
    /// connection reuse). Lives in its own asmdef gated behind the <c>PFOUND_BESTHTTP</c> define so the
    /// foundation compiles without BestHTTP present; enable the define in projects that ship the library. The
    /// key IS the URL (URL/domain building is out of scope here — a caller that needs a base URL composes keys
    /// itself or injects a mapping transport). Stays dumb per the core contract: retry/backoff, single-flight,
    /// decoding and caching all live above it in <see cref="ResourceCache{T}"/>.
    /// </summary>
    /// <remarks>
    /// An HTTP 404 is turned into the terminal <see cref="ResourceNotFoundException"/> (reported as
    /// <see cref="ResourceFailureKind.NotFound"/>, no retry); any other transport error propagates as a transient
    /// fault so the retry policy governs it. <c>GetRawDataAsync</c> already honors cancellation.
    /// </remarks>
    public sealed class BestHttpResourceTransport : IResourceTransport
    {
        public async Task<byte[]> FetchAsync(string key, CancellationToken cancellationToken = default)
        {
            var request = new HTTPRequest(new Uri(key));
            var response = await request.GetHTTPResponseAsync(cancellationToken);

            if (response.StatusCode == 404)
                throw new ResourceNotFoundException(key);

            if (!response.IsSuccess)
                throw new ResourceCacheException(
                    "Transport error fetching '" + key + "': HTTP " + response.StatusCode + " " + response.Message + ".");

            return response.Data;
        }
    }
}
#endif
