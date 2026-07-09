using System;

namespace PFound.RemoteResourceCache.Core
{
    /// <summary>
    /// The disk tier's storage boundary: a persistent, key-addressed byte blob store. The default
    /// <see cref="FileBlobStore"/> writes to a directory with atomic temp-file+rename publishes, but the
    /// interface keeps the core testable against a memory-backed fake and lets a project supply its own
    /// backing (encrypted volume, virtual filesystem, platform key/value store).
    /// </summary>
    public interface IBlobStore
    {
        /// <summary>Reads the blob for <paramref name="key"/>; returns false on a miss (no partial reads).</summary>
        bool TryRead(string key, out byte[] bytes);

        /// <summary>Publishes <paramref name="bytes"/> for <paramref name="key"/> atomically — a reader sees either the old blob or the whole new one, never a torn write.</summary>
        void Write(string key, byte[] bytes);

        bool Exists(string key);

        void Delete(string key);

        /// <summary>
        /// Removes every blob this store owns (its whole scope/partition), including any that are not tracked by
        /// a caller's index. Used to purge a prior content generation wholesale; the store's own metadata (e.g. a
        /// sidecar owned by a higher tier) is left untouched.
        /// </summary>
        void Clear();

        /// <summary>UTC write time of the stored blob, used by the cache to enforce a TTL.</summary>
        DateTime GetTimestampUtc(string key);
    }
}
