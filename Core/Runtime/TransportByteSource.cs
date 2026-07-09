using System;
using System.Threading;
using System.Threading.Tasks;

namespace PFound.RemoteResourceCache.Core
{
    /// <summary>
    /// Adapts an <see cref="IResourceTransport"/> into an <see cref="IResourceByteSource"/>: a read-only,
    /// retryable remote layer reporting the <see cref="CacheTier.Remote"/> tier. A fetch either returns bytes,
    /// throws <see cref="ResourceNotFoundException"/> (terminal for this source), or throws another exception
    /// (transient — governed by the cache's <see cref="RetryPolicy"/>). Being read-only, it is never a
    /// write-back target.
    /// </summary>
    public sealed class TransportByteSource : IResourceByteSource
    {
        private readonly IResourceTransport _transport;

        public TransportByteSource(IResourceTransport transport) => _transport = transport;

        public CacheTier Tier => CacheTier.Remote;
        public bool CanWrite => false;
        public bool Retryable => true;

        public async Task<ByteReadResult> TryReadAsync(string key, CancellationToken cancellationToken) =>
            ByteReadResult.Hit(await _transport.FetchAsync(key, cancellationToken).ConfigureAwait(false));

        public void Write(string key, byte[] bytes) =>
            throw new NotSupportedException("A remote transport source is read-only and cannot be written back to.");
    }
}
