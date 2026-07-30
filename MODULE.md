# RemoteResourceCache

> **Module group — Content & Assets.** Sibling modules in this group: `ContentDelivery`, `AssetPipeline`, `Compression`. Grouped by purpose — see the catalog `Assets/PFound/README.md` and each module's **Dependencies** for exact edges.

## Purpose

Serve remote binary resources fast and offline-tolerantly through a tiered cache — by default
**memory → disk → remote**. It de-duplicates concurrent loads (single-flight), pins hot resources
by ref-count, retries transient transport faults with backoff, expires entries by TTL on **both**
the memory and disk tiers, and evicts bounded tiers by a selectable strategy. The disk tier carries
a **content version** (bump it to purge a prior on-disk generation) and an optional **partition**
namespace; optional **loading-feedback hooks** report started/completed/failed for spinner UIs; and
an **N-source composition builder** replaces the fixed trio with any ordered layer topology
(memory-only, extra disk levels, custom stores) with write-back to earlier writable layers. The
cache engine is pure engine-free C# (`ResourceCache<T>`, generic over the decoded runtime type); a
thin Unity adapter specialises it to `Texture2D`.

## Assemblies

| Assembly | Folder | Engine refs | Purpose |
|----------|--------|-------------|---------|
| `PFound.RemoteResourceCache.Core` | `Core/Runtime/` | `noEngineReferences: true` | The engine-free cache: `ResourceCache<T>`, tiers, policies, seams. |
| `PFound.RemoteResourceCache` | `Runtime/` | Unity | Unity adapter: `RemoteTextureCache`, texture decoders, the default-transport registrar. |
| `PFound.RemoteResourceCache.BestHttp` | `BestHttp/` | Unity | Optional BestHTTP transport, `defineConstraints: ["PFOUND_BESTHTTP"]`. |
| `PFound.RemoteResourceCache.Core.Tests` | `Core/Tests/` | `noEngineReferences: true` | Standalone csc/mono runner for the core. |
| `PFound.RemoteResourceCache.Tests` | `Tests/` | Editor | EditMode adapter tests. |
| `PFound.RemoteResourceCache.PlayModeTests` | `Tests/PlayMode/` | PlayMode | PlayMode texture-decode tests. |

Every assembly is `autoReferenced: false` — a consumer references it explicitly.

## Dependencies

- **Core** — none. No PFound modules, no third-party packages, engine-free.
- **Runtime** — references `PFound.RemoteResourceCache.Core` + UnityEngine.
- **BestHttp** — references Core, Runtime, and `BestHTTP`; compiled only when the `PFOUND_BESTHTTP`
  scripting define is present. See [Conditional Compilation](#conditional-compilation).

## Key Types

**Core cache (`PFound.RemoteResourceCache.Core`)**

- `ResourceCache<T>` — the generic tiered cache; the primary entry point. Construct one per resource type.
- `ResourceCacheBuilder<T>` — fluent builder for arbitrary N-source topologies (memory-only, extra disk levels, custom stores).
- `MemoryResourceCache<T>` — the RAM tier: bounded by count and/or bytes, ref-count pinning, eviction, read-time TTL.
- `DiskCache` — the persistent middle tier over an `IBlobStore` with a debounced metadata index, TTL, and a content-version generation stamp.
- `FileBlobStore` — file-backed `IBlobStore`; one file per key named by `XxHash3` of the key, atomic temp+rename writes, optional partition sub-scope.
- `DiskByteSource` / `TransportByteSource` — the built-in composable layers wrapping a `DiskCache` (writable) and an `IResourceTransport` (read-only remote).
- `CacheTier` — enum `None`/`Memory`/`Disk`/`Remote`: which tier served a result.
- `CachePolicy` — retention/eviction/TTL tuning for both tiers, plus disk content-version + partition (`CachePolicy.Default`).
- `EvictionStrategy` — enum `Lru`/`Lfu`/`TimeBased`.
- `RetryPolicy` — remote-tier retry: attempts + backoff (`RetryPolicy.Default`, `RetryPolicy.None`).
- `ResourceResult<T>` — the outcome value: `Success`, `Value`, `Tier`, `Failure`.
- `ResourceFailure` / `ResourceFailureKind` — typed failure (`Network`/`NotFound`/`Decode`/`RetryExhausted`).

**Seams (interfaces + delegates)**

- `IResourceTransport` — the remote-tier fetch boundary: `Task<byte[]> FetchAsync(key, ct)`.
- `IBlobStore` — the disk-tier storage boundary (`TryRead`/`Write`/`Exists`/`Delete`/`GetTimestampUtc`/`Clear`).
- `IResourceByteSource` — one ordered layer of a composed cache (`Tier`, `CanWrite`, `Retryable`, `TryReadAsync`, `Write`); `ByteReadResult` is its hit/miss return.
- `IResourceLoadObserver` — optional loading-feedback hooks: `OnLoadStarted`/`OnLoadCompleted`/`OnLoadFailed`.
- `ResourceDecoder<out T>(byte[] rawBytes)` — turns raw bytes into `T`.
- `ResourceSizer<in T>(T value)` — measures a decoded value's in-memory footprint (bytes).
- `ResourceCacheException` / `ResourceNotFoundException` — the fetch-contract exception types.

**Unity adapter (`PFound.RemoteResourceCache`)**

- `RemoteTextureCache` — `Create(...)` factory returning a wired `ResourceCache<Texture2D>`.
- `TextureResourceDecoders` — `DecodeTexture`/`SizeOfTexture`, `DecodeSprite`/`SizeOfSprite`.
- `RemoteResourceCacheDefaults` — the process-wide default `IResourceTransport` slot.

**BestHTTP transport (`PFound.RemoteResourceCache.Transport`, gated)**

- `BestHttpResourceTransport` — `IResourceTransport` over BestHTTP.
- `BestHttpDefaultTransport` — `[RuntimeInitializeOnLoadMethod]` registrar that installs it as the default.

## Public API

### `ResourceCache<T>` construction

```csharp
public ResourceCache(
    IResourceTransport transport,      // remote-tier seam; the key IS the URL
    DiskCache disk,                    // persistent middle tier
    ResourceDecoder<T> decode,         // byte[] -> T
    CachePolicy policy = null,         // null => CachePolicy.Default
    RetryPolicy retry = null,          // null => RetryPolicy.Default (3 attempts, 200ms exp)
    ResourceSizer<T> sizeOf = null,    // null => every entry sizes to 0 (count-bounded only)
    Action<T> disposer = null,         // called on eviction/removal for native values
    Func<TimeSpan, CancellationToken, Task> delay = null, // null => Task.Delay (injectable for tests)
    Func<DateTime> clock = null,       // null => DateTime.UtcNow (injectable; drives memory + disk TTL)
    IResourceLoadObserver observer = null) // null => no loading-feedback callbacks
```

`transport`, `disk`, and `decode` are required; the rest have permissive defaults. There is no null
guard on the required trio — passing a null transport surfaces as a thrown (non-retried) error on
the first fetch, per the fail-fast contract. This constructor is sugar for the two-source
composition `[disk, remote]`; for any other topology use `ResourceCacheBuilder<T>`.

### `ResourceCacheBuilder<T>` — arbitrary N-source composition

Compose an ordered list of `IResourceByteSource` layers (nearest first) instead of the fixed trio.
A hit from any layer is decoded once, **written back to every earlier writable layer**, and cached
in the always-present decoded memory tier — so a value pulled from a far layer warms the near ones.
Enables memory-only, memory+remote (no disk), multi-disk-level, or fully custom topologies. The
memory tier's bounds/eviction/TTL come from the supplied `CachePolicy`.

```csharp
var cache = ResourceCacheBuilder<byte[]>.Create(decode)
    .Policy(policy).Retry(retry).Sizer(sizeOf).Disposer(disposer)
    .Clock(clock).Delay(delay).Observer(observer)
    .AddDisk(diskA)                  // writable near layer         (sugar for AddSource(new DiskByteSource(diskA)))
    .AddDisk(diskB)                  // writable second disk level
    .AddRemote(transport)            // read-only remote            (sugar for AddSource(new TransportByteSource(transport)))
    .AddSource(myCustomLayer)        // any IResourceByteSource
    .Build();
```

`AddSource` order is the tier order: earlier = nearer/faster, and only earlier layers receive
write-backs. A source declares its `Tier` (reported in the result), whether it `CanWrite`
(write-back target), and whether it is `Retryable` (governed by the `RetryPolicy`; a read-only
remote is retryable, a store is not). With **no** sources you get a memory-only cache: a miss is a
graceful `NotFound` failure, not a throw.

### Loading-feedback hooks (`IResourceLoadObserver`)

Attach an observer (constructor `observer:` arg or `.Observer(...)`) and it is notified per request:
exactly one `OnLoadStarted(key)` followed by exactly one terminal `OnLoadCompleted(key, servedBy)`
or `OnLoadFailed(key, failure)`. Callbacks are per-request (concurrent callers sharing one
single-flight load each get their own pair), so a spinner can be reference-counted. A memory-tier
hit still fires the pair (completing with `CacheTier.Memory`) — ignore memory-tier completions if
you only care about slow loads.

### `ResourceCache<T>` methods

- `Task<ResourceResult<T>> GetAsync(string key, CancellationToken cancellationToken = default)` —
  fetch from the nearest tier; does not pin.
- `Task<ResourceResult<T>> AcquireAsync(string key, CancellationToken cancellationToken = default)` —
  like `GetAsync` but on success pins in memory (ref-count +1). Balance each with `Release`.
- `void Release(string key)` — drop one pin; at zero the entry becomes evictable.
- `bool TryGet(string key, out T value)` — synchronous memory-tier-only peek; never touches disk/network.
- `Task PreloadAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)` —
  concurrently warm many keys, de-duplicated within the batch and against in-flight loads.
- `bool RemoveFromMemory(string key)` — evict one memory entry (disposing it); false if not resident.
- `void ClearMemory()` — drop the whole memory tier through the disposer.
- `Task ClearDiskAsync()` — delete every disk blob + its metadata.
- `void FlushDisk()` — persist the disk index now; call on shutdown so LFU/TimeBased ordering survives.
- Diagnostics: `int MemoryCount`, `long MemoryBytes`, `int DiskCount`, `long DiskBytes`,
  `bool InMemory(string key)`, `bool IsPinned(string key)`.

### `ResourceResult<T>`

`bool Success`, `T Value`, `CacheTier Tier`, `ResourceFailure Failure`. Miss/offline paths are data,
not exceptions — inspect `Failure.Kind` (`Network`/`NotFound`/`Decode`/`RetryExhausted`),
`Failure.Message`, `Failure.Cause`. Factory helpers: `ResourceResult<T>.Ok(value, tier)`,
`ResourceResult<T>.Fail(...)`.

### `CachePolicy`

Memory: `int MaxMemoryCount`, `long MaxMemoryBytes`, `EvictionStrategy MemoryEviction` (`Lru`).
Disk: `int MaxDiskEntries`, `long MaxDiskBytes`, `EvictionStrategy DiskEviction` (`Lru`),
`TimeSpan DiskFlushInterval` (5s), `int DiskContentVersion` (0; bump to purge a prior on-disk
generation on open), `string DiskPartition` (null = the root itself; a sub-scope isolating blobs
under one root). Shared: `TimeSpan Ttl` (`Zero` = never expires; enforced on read by **both** the
memory and disk tiers), `Func<string, bool> Cacheable` (null = cache everything; `bool CanCache(key)`).
`0` on any bound axis means unbounded on that axis. `CachePolicy.Default` = an all-defaults instance.

### `RetryPolicy`

`int MaxAttempts` (default 1 = try once), `TimeSpan BaseBackoff`, `double BackoffMultiplier` (2.0),
`TimeSpan[] Delays` (explicit per-attempt schedule; overrides the exponential derivation).
`TimeSpan BackoffFor(int attempt)`. Presets: `RetryPolicy.Default` (3 attempts, 200ms→400ms
exponential), `RetryPolicy.None` (single attempt).

### `DiskCache` / `FileBlobStore`

- `new DiskCache(IBlobStore store, string metadataPath, EvictionStrategy strategy = Lru, int maxEntries = 0, long maxBytes = 0, TimeSpan ttl = default, TimeSpan flushInterval = default, Func<DateTime> clock = null, int contentVersion = 0)`. When the persisted content version differs from `contentVersion`, the whole prior generation is purged on open; `int ContentVersion` reports the current stamp.
- `static DiskCache DiskCache.FromPolicy(IBlobStore store, string metadataPath, CachePolicy policy, Func<DateTime> clock = null)` — builds the disk tier from a policy's disk knobs incl. `DiskContentVersion` (used by `RemoteTextureCache`).
- `new FileBlobStore(string rootDirectory, string partition = null)`; `string FileNameFor(string key)` — the xxHash3 filename a key maps to. `IBlobStore`: `TryRead`, `Write` (atomic), `Exists`, `Delete`, `GetTimestampUtc`, `Clear` (wipes the store's own blobs — leaves a foreign sidecar alone). `partition` scopes the store to a sub-directory so several caches share one root without colliding.

### `IResourceTransport`

```csharp
Task<byte[]> FetchAsync(string key, CancellationToken cancellationToken = default);
```

The key IS the URL. A missing resource must be thrown as `ResourceNotFoundException` (terminal →
`NotFound`, no retry). Any other exception is treated as a transient fault and governed by the
`RetryPolicy`.

### Unity adapter

```csharp
public static ResourceCache<Texture2D> RemoteTextureCache.Create(
    IResourceTransport transport = null,               // null => RemoteResourceCacheDefaults.Transport
    string diskSubdirectory = "RemoteResourceCache",   // under Application.persistentDataPath
    CachePolicy policy = null,
    RetryPolicy retry = null);
```

Wires a `FileBlobStore` + `DiskCache` under `Application.persistentDataPath/<diskSubdirectory>`, the
`TextureResourceDecoders.DecodeTexture` decoder, `SizeOfTexture` sizer, and a disposer that destroys
the evicted `Texture2D`. `RemoteResourceCacheDefaults.Transport { get; set; }` is the process-wide
default-transport slot. `TextureResourceDecoders` also exposes `DecodeSprite` / `SizeOfSprite`.

## Setup / wiring

Pure library — **no scene object, MonoBehaviour, or DI registration.** You construct a cache and
call it. Construct one per resource type (usually once, held by whatever owns the resource lifetime),
because each instance owns its own memory + disk tiers.

**Transport is the one thing you must supply.** The core takes an `IResourceTransport` in its
constructor. The Unity adapter instead reads a process-wide default from
`RemoteResourceCacheDefaults.Transport` when you don't pass one:

- **BestHTTP (settled default).** Add `PFOUND_BESTHTTP` to Scripting Define Symbols. The
  `PFound.RemoteResourceCache.BestHttp` assembly (define-constrained on `PFOUND_BESTHTTP`) then
  compiles `BestHttpDefaultTransport`, which installs `BestHttpResourceTransport` into
  `RemoteResourceCacheDefaults.Transport` via `[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]`.
  Nothing else to wire — `RemoteTextureCache.Create()` picks it up.
- **Custom transport.** Pass your own `IResourceTransport` to `Create(transport: ...)` or to the
  `ResourceCache<T>` constructor, or assign `RemoteResourceCacheDefaults.Transport` yourself before
  the first fetch. With neither a passed nor a registered transport, the first fetch throws
  (fail-fast — no defensive null guard).

Texture cache with the registered (BestHTTP) transport + defaults:

```csharp
var cache = RemoteTextureCache.Create();
var result = await cache.GetAsync("https://cdn.example.com/hero.png");
if (result.Success) icon.texture = result.Value;   // result.Tier says which tier served it
else Debug.LogError($"{result.Failure.Kind}: {result.Failure.Message}");

// Pin something that must stay resident, then release:
var pinned = await cache.AcquireAsync(url);
// ... use pinned.Value ...
cache.Release(url);

cache.FlushDisk();   // on shutdown, so eviction ordering persists to next session
```

Fully manual wiring (any resource type):

```csharp
string root = Path.Combine(Application.persistentDataPath, "cache");
var store = new FileBlobStore(root);
var disk  = DiskCache.FromPolicy(store, Path.Combine(root, "index.bin"),
    new CachePolicy { MaxMemoryBytes = 50_000_000, MaxDiskBytes = 500_000_000 });
var cache = new ResourceCache<Texture2D>(
    myTransport, disk, TextureResourceDecoders.DecodeTexture,
    new CachePolicy { MaxMemoryBytes = 50_000_000, MaxDiskBytes = 500_000_000 },
    RetryPolicy.Default, TextureResourceDecoders.SizeOfTexture,
    disposer: tex => Object.Destroy(tex));
```

## File Structure

```
RemoteResourceCache/
├── Core/
│   ├── Runtime/                         # PFound.RemoteResourceCache.Core (engine-free)
│   │   ├── ResourceCache.cs             # the generic tiered cache — primary entry
│   │   ├── ResourceCacheBuilder.cs      # fluent N-source composition builder
│   │   ├── MemoryResourceCache.cs       # RAM tier (bounds, pinning, eviction, read-time TTL)
│   │   ├── DiskCache.cs                 # persistent tier + metadata index + TTL + content version
│   │   ├── FileBlobStore.cs             # file-backed IBlobStore (xxHash3 names, atomic writes, partition)
│   │   ├── IBlobStore.cs                # disk-tier storage seam
│   │   ├── IResourceByteSource.cs       # composable-layer seam + ByteReadResult
│   │   ├── DiskByteSource.cs            # DiskCache-as-layer (writable)
│   │   ├── TransportByteSource.cs       # transport-as-layer (read-only remote)
│   │   ├── IResourceLoadObserver.cs     # loading-feedback hooks (started/completed/failed)
│   │   ├── IResourceTransport.cs        # remote-tier fetch seam
│   │   ├── CachePolicy.cs               # retention/eviction/TTL + content-version/partition tuning
│   │   ├── CacheTier.cs                 # None/Memory/Disk/Remote
│   │   ├── EvictionStrategy.cs          # Lru/Lfu/TimeBased
│   │   ├── RetryPolicy.cs               # attempts + backoff
│   │   ├── ResourceResult.cs            # outcome value
│   │   ├── ResourceFailure.cs           # typed failure + kind
│   │   ├── ResourceDelegates.cs         # ResourceDecoder<T> / ResourceSizer<T>
│   │   ├── ResourceCacheExceptions.cs   # ResourceCacheException / ResourceNotFoundException
│   │   └── XxHash3.cs                   # non-crypto hash for blob filenames
│   └── Tests/                           # standalone csc/mono runner (Program.cs)
├── Runtime/                             # PFound.RemoteResourceCache (Unity adapter)
│   ├── RemoteTextureCache.cs            # Create() factory for ResourceCache<Texture2D>
│   ├── TextureResourceDecoders.cs       # DecodeTexture/Sprite + sizers
│   └── RemoteResourceCacheDefaults.cs   # process-wide default-transport slot
├── BestHttp/                            # PFound.RemoteResourceCache.BestHttp (PFOUND_BESTHTTP)
│   ├── BestHttpResourceTransport.cs     # IResourceTransport over BestHTTP
│   └── BestHttpDefaultTransport.cs      # RuntimeInitializeOnLoadMethod registrar
├── Tests/                               # EditMode adapter tests (+ PlayMode/)
├── README.md
└── MODULE.md
```

## Downstream Dependents

None within PFound — this is a leaf capability module consumed directly by game projects that need
remote-resource caching.

## Conditional Compilation

`PFOUND_BESTHTTP` gates the entire `PFound.RemoteResourceCache.BestHttp` assembly (via
`defineConstraints`) and additionally wraps both of its source files in `#if PFOUND_BESTHTTP`. With
the define **absent**, the module compiles fully with no reference to BestHTTP, and
`RemoteResourceCacheDefaults.Transport` stays null until a project supplies its own transport. With
the define **present** and the BestHTTP library in the project, `BestHttpResourceTransport` is
auto-registered before the first scene loads. The Core and Runtime assemblies never reference
BestHTTP — the optional adapter reaches in, not the reverse.

## Limitations / Known Gaps

- **The key IS the URL.** No base-URL/domain composition — a caller that needs it composes keys or
  injects a mapping transport.
- **One tier set per instance.** Each `ResourceCache<T>` owns its own memory + disk tiers; there is
  no shared cross-type pool. Construct once and hold it.
- **Disk index is debounced, not transactional.** Flush cadence is `DiskFlushInterval`; call
  `FlushDisk()` on shutdown so LFU/TimeBased ordering survives. Blob presence is reconciled from the
  filesystem, so a pre-warmed cache still reads offline even if the sidecar was never flushed.
- **`XxHash3` is for filename derivation, not integrity** — fast, non-cryptographic.
- **Bytes-based memory bounding needs a `ResourceSizer<T>`.** Omit it and the memory tier is bounded
  by entry count only (every entry sizes to 0).
</content>
