using System.Threading;
using System.Threading.Tasks;

namespace PFound.RemoteResourceCache.Core
{
    /// <summary>
    /// Adapts a <see cref="DiskCache"/> into an <see cref="IResourceByteSource"/>: a writable, non-retryable
    /// persistent layer that reports the <see cref="CacheTier.Disk"/> tier. Reads are synchronous under the hood
    /// (filesystem) and TTL-checked by the underlying tier. Used both by the fixed tiered cache and as a
    /// composable layer in <see cref="ResourceCacheBuilder{T}"/>.
    /// </summary>
    public sealed class DiskByteSource : IResourceByteSource
    {
        private readonly DiskCache _disk;

        public DiskByteSource(DiskCache disk) => _disk = disk;

        /// <summary>The wrapped disk tier (exposed for diagnostics / flush / clear wiring).</summary>
        public DiskCache Disk => _disk;

        public CacheTier Tier => CacheTier.Disk;
        public bool CanWrite => true;
        public bool Retryable => false;

        public Task<ByteReadResult> TryReadAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(_disk.TryRead(key, out byte[] bytes) ? ByteReadResult.Hit(bytes) : ByteReadResult.Miss);

        public void Write(string key, byte[] bytes) => _disk.Write(key, bytes);
    }
}
