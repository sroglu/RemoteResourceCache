using System.Threading;
using System.Threading.Tasks;

namespace PFound.RemoteResourceCache.Core
{
    /// <summary>
    /// The outcome of reading one byte source: a hit carrying the bytes, or a miss. A miss means "this source
    /// does not have the key" and the cache falls through to the next source; it is not a failure.
    /// </summary>
    public readonly struct ByteReadResult
    {
        public bool Found { get; }
        public byte[] Bytes { get; }

        private ByteReadResult(bool found, byte[] bytes)
        {
            Found = found;
            Bytes = bytes;
        }

        public static ByteReadResult Hit(byte[] bytes) => new ByteReadResult(true, bytes);
        public static ByteReadResult Miss => new ByteReadResult(false, null);
    }

    /// <summary>
    /// One ordered layer of a composed cache: a byte-addressed store or fetcher the cache consults in turn.
    /// <see cref="ResourceCache{T}"/> asks each source (nearest first) via <see cref="TryReadAsync"/>; the first
    /// hit is decoded once and <b>written back</b> to every earlier <see cref="CanWrite"/> source (and the memory
    /// tier), so a value pulled from a far source warms the near ones. This generalises the fixed
    /// memory→disk→remote tiering to an arbitrary N-source topology (memory-only, extra disk levels, custom
    /// stores) via <see cref="ResourceCacheBuilder{T}"/>.
    /// </summary>
    public interface IResourceByteSource
    {
        /// <summary>The tier label reported in a <see cref="ResourceResult{T}.Tier"/> when this source serves a hit.</summary>
        CacheTier Tier { get; }

        /// <summary>Whether this source accepts write-backs (a persistent/warmable store true; a read-only remote false).</summary>
        bool CanWrite { get; }

        /// <summary>
        /// Whether reads may fault transiently and should be governed by the <see cref="RetryPolicy"/>. A retryable
        /// source reports a hit or throws — a transient exception is retried, and a
        /// <see cref="ResourceNotFoundException"/> is terminal for this source (the cache moves on to the next).
        /// A non-retryable source instead returns <see cref="ByteReadResult.Miss"/> for an absent key.
        /// </summary>
        bool Retryable { get; }

        Task<ByteReadResult> TryReadAsync(string key, CancellationToken cancellationToken);

        /// <summary>Write-back hook; only ever called when <see cref="CanWrite"/> is true.</summary>
        void Write(string key, byte[] bytes);
    }
}
