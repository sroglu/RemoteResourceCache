using System.Threading;
using System.Threading.Tasks;

namespace PFound.RemoteResourceCache.Core
{
    /// <summary>
    /// The remote tier's fetch boundary: turn a key into the raw bytes for that resource. Deliberately
    /// dumb — retry/backoff, single-flight dedupe, decoding and caching all live ABOVE this in
    /// <see cref="ResourceCache{T}"/>, so a transport stays trivial and swappable: UnityWebRequest,
    /// HttpClient, or an in-memory fake for tests.
    /// </summary>
    /// <remarks>
    /// A missing resource must surface as <see cref="ResourceNotFoundException"/> (a terminal condition the
    /// cache reports as <see cref="ResourceFailureKind.NotFound"/>). Any other thrown exception is treated as
    /// a transient network fault and is subject to the retry policy.
    /// </remarks>
    public interface IResourceTransport
    {
        Task<byte[]> FetchAsync(string key, CancellationToken cancellationToken = default);
    }
}
