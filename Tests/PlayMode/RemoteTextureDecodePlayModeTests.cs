using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PFound.RemoteResourceCache.Core;
using UnityEngine;
using UnityEngine.TestTools;

namespace PFound.RemoteResourceCache.Tests
{
    /// <summary>
    /// PlayMode coverage for the paths that need a live graphics device: decoding real PNG bytes into a
    /// <see cref="Texture2D"/> through the whole memory→disk→remote cache. The remote tier is a fake that
    /// serves locally-generated PNG bytes, so this runs offline; the real network round-trip through
    /// <see cref="UnityWebRequestTransport"/> against a live URL is left as a manual check.
    /// </summary>
    public sealed class RemoteTextureDecodePlayModeTests
    {
        [UnityTest]
        public IEnumerator DecodesPngThroughCache_ThenServesFromMemory()
        {
            string dir = Path.Combine(Application.temporaryCachePath, "pf_rrc_playmode_" + Guid.NewGuid().ToString("N"));
            byte[] png = MakePng(4, 4, new Color32(200, 100, 50, 255));

            var transport = new FakeTransport();
            transport.Put("swatch", png);
            var disk = DiskCache.FromPolicy(new FileBlobStore(dir), Path.Combine(dir, "index.bin"), CachePolicy.Default);
            var cache = new ResourceCache<Texture2D>(
                transport, disk, TextureResourceDecoders.DecodeTexture,
                sizeOf: TextureResourceDecoders.SizeOfTexture,
                disposer: t => UnityEngine.Object.Destroy(t));

            Task<ResourceResult<Texture2D>> remoteTask = cache.GetAsync("swatch");
            while (!remoteTask.IsCompleted) yield return null;
            ResourceResult<Texture2D> remote = remoteTask.GetAwaiter().GetResult();

            Assert.IsTrue(remote.Success, "PNG decoded through the cache");
            Assert.AreEqual(CacheTier.Remote, remote.Tier);
            Assert.AreEqual(4, remote.Value.width);
            Assert.AreEqual(4, remote.Value.height);

            Task<ResourceResult<Texture2D>> memoryTask = cache.GetAsync("swatch");
            while (!memoryTask.IsCompleted) yield return null;
            Assert.AreEqual(CacheTier.Memory, memoryTask.GetAwaiter().GetResult().Tier, "second get from memory");
            Assert.AreEqual(1, transport.Calls, "decoded texture reused, not re-fetched");

            try { Directory.Delete(dir, true); } catch { /* best-effort */ }
        }

        private static byte[] MakePng(int width, int height, Color32 fill)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false);
            var pixels = new Color32[width * height];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = fill;
            texture.SetPixels32(pixels);
            texture.Apply();
            byte[] png = texture.EncodeToPNG();
            UnityEngine.Object.Destroy(texture);
            return png;
        }

        private sealed class FakeTransport : IResourceTransport
        {
            private readonly Dictionary<string, byte[]> _content = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            public int Calls;

            public void Put(string key, byte[] bytes) => _content[key] = bytes;

            public Task<byte[]> FetchAsync(string key, CancellationToken cancellationToken = default)
            {
                Calls++;
                if (!_content.TryGetValue(key, out byte[] bytes)) throw new ResourceNotFoundException(key);
                return Task.FromResult(bytes);
            }
        }
    }
}
