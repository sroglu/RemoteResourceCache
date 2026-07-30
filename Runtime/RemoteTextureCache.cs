using System.IO;
using PFound.RemoteResourceCache.Core;
using UnityEngine;

namespace PFound.RemoteResourceCache
{
    /// <summary>
    /// Convenience wiring for the common case: a memory→disk→remote cache of <see cref="Texture2D"/> whose keys
    /// are URLs. It combines the injected (or registrar-supplied default) <see cref="IResourceTransport"/>, a
    /// <see cref="FileBlobStore"/> + <see cref="DiskCache"/> under <see cref="Application.persistentDataPath"/>,
    /// the texture decoder/sizer from <see cref="TextureResourceDecoders"/>, and a disposer that destroys the
    /// evicted <see cref="Texture2D"/> so the memory tier doesn't leak native objects. It holds NO hard HTTP
    /// default — projects supply BestHTTP (via its registrar) or inject their own transport.
    /// </summary>
    public static class RemoteTextureCache
    {
        public static ResourceCache<Texture2D> Create(
            IResourceTransport transport = null,
            string diskSubdirectory = "RemoteResourceCache",
            CachePolicy policy = null,
            RetryPolicy retry = null)
        {
            // Fail-fast: no transport injected and none registered → a genuine misconfiguration.
            IResourceTransport t = transport ?? RemoteResourceCacheDefaults.Transport;

            CachePolicy effective = policy ?? CachePolicy.Default;
            string root = Path.Combine(Application.persistentDataPath, diskSubdirectory);
            // The optional partition scopes blobs to a sub-folder; the metadata sidecar stays at the root so a
            // content-version purge (which wipes the partition) never touches it.
            var store = new FileBlobStore(root, effective.DiskPartition);
            var disk = DiskCache.FromPolicy(store, Path.Combine(root, "index.bin"), effective);

            return new ResourceCache<Texture2D>(
                t,
                disk,
                TextureResourceDecoders.DecodeTexture,
                policy,
                retry,
                TextureResourceDecoders.SizeOfTexture,
                disposer: DestroyTexture);
        }

        private static void DestroyTexture(Texture2D texture)
        {
            if (Application.isPlaying) Object.Destroy(texture);
            else Object.DestroyImmediate(texture);
        }
    }
}
