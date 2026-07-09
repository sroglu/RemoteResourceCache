using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PFound.RemoteResourceCache.Core;
using UnityEngine;

namespace PFound.RemoteResourceCache.Tests
{
    /// <summary>
    /// EditMode coverage for the engine adapter that does NOT need a live network or a GPU: the adapter
    /// assembly wires the engine-free <see cref="ResourceCache{T}"/> to a real <see cref="FileBlobStore"/> and
    /// runs a full memory→disk→remote flow, and the texture decoder rejects undecodable bytes per contract.
    /// The real <see cref="UnityWebRequestTransport"/> round-trip and valid-image decode live in the PlayMode
    /// suite (they touch the network / graphics device).
    /// </summary>
    public sealed class RemoteResourceCacheAdapterTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "pf_rrc_editmode_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { /* best-effort */ }
        }

        [Test]
        public void Cache_WiresTiersThroughAdapterAssembly()
        {
            var transport = new FakeTransport();
            transport.Put("k", System.Text.Encoding.UTF8.GetBytes("bytes"));
            var disk = DiskCache.FromPolicy(new FileBlobStore(_dir), System.IO.Path.Combine(_dir, "index.bin"), CachePolicy.Default);
            var cache = new ResourceCache<byte[]>(
                transport, disk, raw => (byte[])raw.Clone(),
                sizeOf: b => b.Length, delay: (span, ct) => Task.CompletedTask);

            ResourceResult<byte[]> first = cache.GetAsync("k").GetAwaiter().GetResult();
            Assert.IsTrue(first.Success);
            Assert.AreEqual(CacheTier.Remote, first.Tier, "cold miss served from remote");

            ResourceResult<byte[]> second = cache.GetAsync("k").GetAwaiter().GetResult();
            Assert.AreEqual(CacheTier.Memory, second.Tier, "second request served from memory");
            Assert.AreEqual(1, transport.Calls, "single download for two requests");
        }

        [Test]
        public void RemoteTextureCache_FailsFast_WhenNoTransportAvailable()
        {
            IResourceTransport saved = RemoteResourceCacheDefaults.Transport;
            RemoteResourceCacheDefaults.Transport = null;
            try
            {
                // No injected transport and none registered → building + fetching must surface a genuine null,
                // not silently fall back to some UnityWebRequest default.
                var cache = RemoteTextureCache.Create(diskSubdirectory: "RRCTest_" + System.Guid.NewGuid().ToString("N"));
                Assert.Throws<System.NullReferenceException>(
                    () => cache.GetAsync("http://example.invalid/x.png").GetAwaiter().GetResult());
            }
            finally
            {
                RemoteResourceCacheDefaults.Transport = saved;
            }
        }

        [Test]
        public void DecodeTexture_RejectsUndecodableBytes()
        {
            byte[] garbage = { 1, 2, 3, 4, 5, 6, 7, 8 };
            Assert.Throws<ResourceCacheException>(() => TextureResourceDecoders.DecodeTexture(garbage));
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
