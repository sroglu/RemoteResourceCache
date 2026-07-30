# PFound.RemoteResourceCache

A tiered cache for remote binary resources — by default **memory → disk → remote** — with
single-flight loads, ref-count pinning, retry-with-backoff, TTL on both the memory and disk tiers,
disk content-versioning + partitions, optional loading-feedback hooks, and pluggable eviction. The
fixed trio is the convenience default; `ResourceCacheBuilder<T>` composes arbitrary N-source
topologies (memory-only, extra disk levels, custom stores) with write-back to earlier layers. The
cache core is engine-free pure C# (`ResourceCache<T>`); a thin Unity adapter wires it to `Texture2D`.

## Quick reference

```csharp
// Pure library: construct one cache per resource type and call it. No scene object, MonoBehaviour, or DI.
string root = Path.Combine(Application.persistentDataPath, "cache");
var disk    = DiskCache.FromPolicy(new FileBlobStore(root), Path.Combine(root, "index.bin"), CachePolicy.Default);
var cache   = new ResourceCache<Texture2D>(
    myTransport,                            // IResourceTransport — the remote-tier seam
    disk,
    TextureResourceDecoders.DecodeTexture); // byte[] -> Texture2D

ResourceResult<Texture2D> result = await cache.GetAsync("https://cdn.example.com/hero.png");
if (result.Success) icon.texture = result.Value;   // result.Tier says which tier served it

// Textures with the settled BestHTTP transport auto-wired (PFOUND_BESTHTTP):
var textures = RemoteTextureCache.Create();

// Arbitrary topology (memory-only, extra disk levels, custom stores) with write-back to earlier layers:
var custom = ResourceCacheBuilder<byte[]>.Create(decode)
    .Policy(policy).Observer(spinnerObserver)      // OnLoadStarted/Completed/Failed
    .AddDisk(fastDisk).AddDisk(bulkDisk).AddRemote(myTransport)
    .Build();
```

## Dependencies

Core is dependency-free engine-free C#; the optional BestHTTP transport is gated behind the
`PFOUND_BESTHTTP` scripting define.

## Docs

Deep reference: [MODULE.md](MODULE.md).
</content>
