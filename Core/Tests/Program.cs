using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PFound.RemoteResourceCache.Core;

namespace PFound.RemoteResourceCache.Core.Tests
{
    /// <summary>
    /// Standalone mono/csc runner for the engine-free resource cache. Covers the tier orchestration, memory +
    /// disk eviction (LRU/LFU/TimeBased), enforced disk bounds with debounced metadata + reload, the disposal
    /// hook, batch preload, sync peek, clear APIs, and the xxHash3 filename hash — driven by an in-memory
    /// FakeTransport, a real FileBlobStore over a temp directory, and injected clock/delay so nothing sleeps.
    /// </summary>
    internal static class Program
    {
        private static int s_passed;
        private static int s_failed;

        private static int Main()
        {
            string temp = Path.Combine(Path.GetTempPath(), "pf_rrc_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                // ---- original tier + failure suite ----
                Run("Remote miss serves + populates disk and memory (fallthrough + upward)", () => Fallthrough_PopulatesUpward(temp));
                Run("Second request is a memory hit (no disk read, no download)", () => Memory_HitAfterPopulate(temp));
                Run("Fresh cache over a warm disk serves from disk offline", () => Disk_OfflineHit(temp));
                Run("Disk write is atomic (no .tmp residue left behind)", () => Disk_AtomicNoResidue(temp));
                Run("Memory tier evicts least-recently-used past the count cap", () => Memory_LruEviction(temp));
                Run("Memory tier evicts by byte budget", () => Memory_ByteEviction(temp));
                Run("Pinned (acquired) entry is never evicted", () => Memory_PinPreventsEviction(temp));
                Run("Release makes a pinned entry evictable again", () => Refcount_ReleaseMakesEvictable(temp));
                Run("Concurrent acquires stack ref-counts; each needs its own release", () => Refcount_StacksAndUnwinds(temp));
                Run("Concurrent requests for one key share a single download (single-flight)", () => SingleFlight_Dedupe(temp));
                Run("Transient failures retry then succeed", () => Retry_ThenSucceed(temp));
                Run("Fails after max attempts with RetryExhausted", () => Retry_FailsAfterMax(temp));
                Run("Single-attempt transient failure reports Network", () => Failure_Network(temp));
                Run("Missing remote resource reports NotFound (no retry)", () => Failure_NotFound(temp));
                Run("Undecodable bytes report Decode and are not cached", () => Failure_Decode(temp));
                Run("Non-cacheable key is served but not retained", () => Policy_NonCacheableNotRetained(temp));
                Run("Expired disk blob (TTL) is re-fetched", () => Policy_TtlExpiryRefetches(temp));

                // ---- selectable memory eviction ----
                Run("Memory LFU keeps frequently-used, drops least-frequent", () => Memory_LfuEviction(temp));
                Run("Memory TimeBased drops oldest-created regardless of access", () => Memory_TimeBasedEviction(temp));

                // ---- enforced disk eviction (each strategy) ----
                Run("Disk enforces count cap, LRU victim", () => Disk_EnforceCount_Lru(temp));
                Run("Disk enforces count cap, LFU victim", () => Disk_EnforceCount_Lfu(temp));
                Run("Disk enforces count cap, TimeBased victim", () => Disk_EnforceCount_TimeBased(temp));
                Run("Disk enforces byte cap", () => Disk_EnforceBytes(temp));

                // ---- disposal hook ----
                Run("On-evict disposal hook fires for the memory victim", () => Hook_FiresOnEvict(temp));
                Run("Pinned entry is disposed when its last lease is released after eviction", () => Hook_FiresOnReleaseAfterEviction(temp));
                Run("ClearMemory disposes every resident entry", () => Hook_FiresOnClearMemory(temp));

                // ---- batch / peek / clear ----
                Run("Batch preload de-dups keys and warms memory + disk", () => Batch_PreloadDedupWarms(temp));
                Run("Sync TryGet peeks memory only (never disk/network)", () => Peek_MemoryOnly(temp));
                Run("ClearDisk deletes blobs and metadata", () => Clear_Disk(temp));

                // ---- disk metadata persistence ----
                Run("Debounced flush persists metadata; reload restores it after restart", () => Disk_FlushReloadRestoresMetadata(temp));
                Run("Debounce delays the flush until the interval elapses", () => Disk_DebounceDelaysFlush(temp));

                // ---- xxHash3 filename ----
                Run("xxHash3 filename is deterministic + canonical empty vector", () => Hash_FilenameDeterminism());

                // ---- memory-tier TTL (gap 1) ----
                Run("Memory-tier TTL: an expired in-memory entry misses and re-loads", () => Memory_TtlExpiryRefetches(temp));

                // ---- disk content-version + partition (gap 2) ----
                Run("Disk content-version bump purges the prior on-disk generation", () => Disk_ContentVersionPurgesPrior(temp));
                Run("Disk content-version match keeps the generation intact", () => Disk_ContentVersionMatchKeeps(temp));
                Run("Disk partition isolates blobs (and their clears) under one root", () => Disk_PartitionIsolates(temp));

                // ---- loading-feedback hooks (gap 3) ----
                Run("Load observer fires started→completed on success (with the serving tier)", () => Observer_StartedCompleted(temp));
                Run("Load observer fires started→failed on a typed failure", () => Observer_StartedFailed(temp));

                // ---- arbitrary N-source composition (gap 4) ----
                Run("Composition warms earlier writable sources on a far hit (write-back)", () => Composition_WriteBackToEarlier(temp));
                Run("Composition supports a memory-only topology (no disk, no remote)", () => Composition_MemoryOnly(temp));
                Run("Composition: tiered builder matches the fixed memory→disk→remote default", () => Composition_TieredEquivalence(temp));
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch { /* best-effort temp cleanup */ }
            }

            Console.WriteLine();
            Console.WriteLine(s_failed == 0
                ? "ALL PASSED (" + s_passed + ")"
                : (s_passed + " passed, " + s_failed + " FAILED"));
            return s_failed == 0 ? 0 : 1;
        }

        // ---- tier behavior -----------------------------------------------------------------------

        private static void Fallthrough_PopulatesUpward(string root)
        {
            string dir = Fresh(root);
            var transport = new FakeTransport();
            transport.Put("logo", Bytes("logo-pixels"));
            var cache = NewCache(transport, dir);

            ResourceResult<byte[]> result = Wait(cache.GetAsync("logo"));
            Assert(result.Success, "remote fetch succeeds");
            AssertEqual(CacheTier.Remote, result.Tier, "served from remote on a cold miss");
            AssertEqual("logo-pixels", Str(result.Value), "correct bytes returned");
            AssertEqual(1, transport.Calls, "downloaded exactly once");
            Assert(cache.InMemory("logo"), "populated the memory tier");
            Assert(new FileBlobStore(dir).Exists("logo"), "populated the disk tier");
        }

        private static void Memory_HitAfterPopulate(string root)
        {
            string dir = Fresh(root);
            var transport = new FakeTransport();
            transport.Put("k", Bytes("v"));
            var cache = NewCache(transport, dir);

            Wait(cache.GetAsync("k"));
            ResourceResult<byte[]> second = Wait(cache.GetAsync("k"));
            AssertEqual(CacheTier.Memory, second.Tier, "second request served from memory");
            AssertEqual(1, transport.Calls, "no second download");
        }

        private static void Disk_OfflineHit(string root)
        {
            string dir = Fresh(root);
            var online = new FakeTransport();
            online.Put("k", Bytes("cached-on-disk"));
            Wait(NewCache(online, dir).GetAsync("k")); // warm the disk via one cache instance

            var offline = new FakeTransport { OfflineThrow = true };
            var reopened = NewCache(offline, dir);
            ResourceResult<byte[]> result = Wait(reopened.GetAsync("k"));

            AssertEqual(CacheTier.Disk, result.Tier, "served from the warm disk tier");
            AssertEqual("cached-on-disk", Str(result.Value), "disk bytes are intact");
            AssertEqual(0, offline.Calls, "no network access when a valid disk copy exists");
        }

        private static void Disk_AtomicNoResidue(string root)
        {
            string dir = Fresh(root);
            var transport = new FakeTransport();
            transport.Put("k", Bytes("payload"));
            Wait(NewCache(transport, dir).GetAsync("k"));

            foreach (string file in Directory.GetFiles(dir))
                Assert(!file.Contains(".tmp-"), "no temp file left after an atomic write");
        }

        // ---- memory tier: LRU + pinning ----------------------------------------------------------

        private static void Memory_LruEviction(string root)
        {
            string dir = Fresh(root);
            var transport = new FakeTransport();
            foreach (string k in new[] { "a", "b", "c" }) transport.Put(k, Bytes(k));
            var cache = NewCache(transport, dir, new CachePolicy { MaxMemoryCount = 2 });

            Wait(cache.GetAsync("a"));
            Wait(cache.GetAsync("b"));
            Wait(cache.GetAsync("c")); // inserting c evicts the LRU entry, a

            AssertEqual(2, cache.MemoryCount, "memory bounded to the count cap");
            Assert(!cache.InMemory("a"), "least-recently-used 'a' evicted");
            Assert(cache.InMemory("b"), "'b' retained");
            Assert(cache.InMemory("c"), "'c' retained");
        }

        private static void Memory_ByteEviction(string root)
        {
            string dir = Fresh(root);
            var transport = new FakeTransport();
            transport.Put("small", Bytes("xx"));     // 2 bytes
            transport.Put("big", Bytes("xxxxxxxx"));  // 8 bytes
            var cache = NewCache(transport, dir, new CachePolicy { MaxMemoryBytes = 9 });

            Wait(cache.GetAsync("small"));
            Wait(cache.GetAsync("big")); // 2+8 = 10 > 9 → evict LRU 'small'

            Assert(!cache.InMemory("small"), "byte budget evicted the LRU entry");
            Assert(cache.InMemory("big"), "the newest entry is retained");
            Assert(cache.MemoryBytes <= 9, "resident bytes stay within budget");
        }

        private static void Memory_PinPreventsEviction(string root)
        {
            string dir = Fresh(root);
            var transport = new FakeTransport();
            transport.Put("pinned", Bytes("keep-me"));
            transport.Put("other", Bytes("evict-me"));
            var cache = NewCache(transport, dir, new CachePolicy { MaxMemoryCount = 1 });

            ResourceResult<byte[]> acquired = Wait(cache.AcquireAsync("pinned"));
            Assert(acquired.Success && cache.IsPinned("pinned"), "acquire pinned the entry");

            Wait(cache.GetAsync("other"));

            Assert(cache.InMemory("pinned"), "pinned entry survives despite the cap");
            Assert(!cache.InMemory("other"), "the unpinned newcomer is dropped instead");
        }

        private static void Refcount_ReleaseMakesEvictable(string root)
        {
            string dir = Fresh(root);
            var transport = new FakeTransport();
            foreach (string k in new[] { "p", "q" }) transport.Put(k, Bytes(k));
            var cache = NewCache(transport, dir, new CachePolicy { MaxMemoryCount = 1 });

            Wait(cache.AcquireAsync("p"));
            cache.Release("p");
            Assert(!cache.IsPinned("p"), "release cleared the pin");

            Wait(cache.GetAsync("q"));
            Assert(!cache.InMemory("p"), "unpinned 'p' evicted after release");
            Assert(cache.InMemory("q"), "'q' resident");
        }

        private static void Refcount_StacksAndUnwinds(string root)
        {
            string dir = Fresh(root);
            var transport = new FakeTransport();
            transport.Put("k", Bytes("v"));
            var cache = NewCache(transport, dir, new CachePolicy { MaxMemoryCount = 1 });

            Wait(cache.AcquireAsync("k"));
            Wait(cache.AcquireAsync("k")); // memory hit → ref-count 2

            cache.Release("k");
            Assert(cache.IsPinned("k"), "still pinned after one of two releases");
            cache.Release("k");
            Assert(!cache.IsPinned("k"), "unpinned after the matching release");

            AssertThrows<InvalidOperationException>(() => cache.Release("k"), "an extra release is a fail-fast error");
        }

        // ---- single-flight -----------------------------------------------------------------------

        private static void SingleFlight_Dedupe(string root)
        {
            string dir = Fresh(root);
            var transport = new GatedTransport(Bytes("shared"));
            var cache = NewCache(transport, dir);

            Task<ResourceResult<byte[]>> first = cache.GetAsync("k");
            Task<ResourceResult<byte[]>> second = cache.GetAsync("k");
            transport.Release();

            ResourceResult<byte[]> r1 = Wait(first);
            ResourceResult<byte[]> r2 = Wait(second);
            AssertEqual(1, transport.Calls, "two concurrent requests shared one download");
            Assert(r1.Success && r2.Success, "both callers received the result");
            AssertEqual("shared", Str(r1.Value), "first got the bytes");
            AssertEqual("shared", Str(r2.Value), "second got the same bytes");
        }

        // ---- retry + typed failures --------------------------------------------------------------

        private static void Retry_ThenSucceed(string root)
        {
            string dir = Fresh(root);
            var transport = new FakeTransport { FailFirst = 2 };
            transport.Put("k", Bytes("eventually"));
            var cache = NewCache(transport, dir, retry: new RetryPolicy { MaxAttempts = 3 });

            ResourceResult<byte[]> result = Wait(cache.GetAsync("k"));
            Assert(result.Success, "succeeds on the third attempt");
            AssertEqual(3, transport.Calls, "two transient failures then a success");
            AssertEqual("eventually", Str(result.Value), "correct bytes after retry");
        }

        private static void Retry_FailsAfterMax(string root)
        {
            string dir = Fresh(root);
            var transport = new FakeTransport { FailFirst = 99 };
            transport.Put("k", Bytes("never-reached"));
            var cache = NewCache(transport, dir, retry: new RetryPolicy { MaxAttempts = 3 });

            ResourceResult<byte[]> result = Wait(cache.GetAsync("k"));
            Assert(!result.Success, "gives up after the attempt budget");
            AssertEqual(ResourceFailureKind.RetryExhausted, result.Failure.Kind, "classified as retry-exhausted");
            AssertEqual(3, transport.Calls, "tried exactly MaxAttempts times");
            Assert(!new FileBlobStore(dir).Exists("k"), "nothing cached on total failure");
        }

        private static void Failure_Network(string root)
        {
            string dir = Fresh(root);
            var transport = new FakeTransport { FailFirst = 1 };
            transport.Put("k", Bytes("unused"));
            var cache = NewCache(transport, dir, retry: RetryPolicy.None);

            ResourceResult<byte[]> result = Wait(cache.GetAsync("k"));
            Assert(!result.Success, "the lone attempt failed");
            AssertEqual(ResourceFailureKind.Network, result.Failure.Kind, "single-shot transient fault is Network");
            AssertEqual(1, transport.Calls, "exactly one attempt");
        }

        private static void Failure_NotFound(string root)
        {
            string dir = Fresh(root);
            var transport = new FakeTransport();
            var cache = NewCache(transport, dir, retry: new RetryPolicy { MaxAttempts = 3 });

            ResourceResult<byte[]> result = Wait(cache.GetAsync("ghost"));
            Assert(!result.Success, "a missing resource is a failure");
            AssertEqual(ResourceFailureKind.NotFound, result.Failure.Kind, "classified as not-found");
            AssertEqual(1, transport.Calls, "not-found is terminal — no retries");
        }

        private static void Failure_Decode(string root)
        {
            string dir = Fresh(root);
            var transport = new FakeTransport();
            transport.Put("k", Bytes("raw"));
            var cache = new ResourceCache<byte[]>(transport, NewDisk(dir, CachePolicy.Default),
                decode: _ => throw new FormatException("bad bytes"),
                sizeOf: b => b.Length, delay: NoDelay);

            ResourceResult<byte[]> result = Wait(cache.GetAsync("k"));
            Assert(!result.Success, "an undecodable payload fails");
            AssertEqual(ResourceFailureKind.Decode, result.Failure.Kind, "classified as decode failure");
            Assert(result.Failure.Cause is FormatException, "carries the decoder's exception");
            Assert(!new FileBlobStore(dir).Exists("k"), "bytes that can't decode are not cached");
        }

        private static void Policy_NonCacheableNotRetained(string root)
        {
            string dir = Fresh(root);
            var transport = new FakeTransport();
            transport.Put("temp", Bytes("do-not-keep"));
            var cache = NewCache(transport, dir, new CachePolicy { Cacheable = key => key != "temp" });

            ResourceResult<byte[]> result = Wait(cache.GetAsync("temp"));
            Assert(result.Success, "a non-cacheable resource is still served");
            AssertEqual("do-not-keep", Str(result.Value), "correct bytes returned");
            Assert(!cache.InMemory("temp"), "not retained in memory");
            Assert(!new FileBlobStore(dir).Exists("temp"), "not written to disk");
        }

        private static void Policy_TtlExpiryRefetches(string root)
        {
            string dir = Fresh(root);
            var transport = new FakeTransport();
            transport.Put("k", Bytes("v1"));
            var clock = new FakeClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            // Flush every write (interval 0) so the fake-clock CreatedUtc persists and reload doesn't fall back
            // to the file's real mtime — that keeps the TTL comparison fully deterministic.
            var policy = new CachePolicy { Ttl = TimeSpan.FromMinutes(10), DiskFlushInterval = TimeSpan.Zero };

            var first = NewCache(transport, dir, policy, clock: clock.Now);
            Wait(first.GetAsync("k"));
            AssertEqual(1, transport.Calls, "initial download");

            clock.Advance(TimeSpan.FromMinutes(20)); // past the TTL
            var second = NewCache(transport, dir, policy, clock: clock.Now);
            ResourceResult<byte[]> result = Wait(second.GetAsync("k"));

            AssertEqual(CacheTier.Remote, result.Tier, "expired disk blob is bypassed and re-fetched");
            AssertEqual(2, transport.Calls, "TTL expiry forced a fresh download");
        }

        // ---- selectable memory eviction ----------------------------------------------------------

        private static void Memory_LfuEviction(string root)
        {
            string dir = Fresh(root);
            var transport = new FakeTransport();
            foreach (string k in new[] { "a", "b", "c" }) transport.Put(k, Bytes(k));
            var cache = NewCache(transport, dir, new CachePolicy { MaxMemoryCount = 2, MemoryEviction = EvictionStrategy.Lfu });

            Wait(cache.GetAsync("a"));          // a: count 1
            Wait(cache.GetAsync("a"));          // a: count 2 (memory hit)
            Wait(cache.GetAsync("b"));          // b: count 1
            Wait(cache.GetAsync("c"));          // insert c → over cap → evict least-frequent (b, tie-broken LRU)

            Assert(cache.InMemory("a"), "frequently-used 'a' survives LFU");
            Assert(!cache.InMemory("b"), "least-frequently-used 'b' evicted");
            Assert(cache.InMemory("c"), "'c' retained");
        }

        private static void Memory_TimeBasedEviction(string root)
        {
            string dir = Fresh(root);
            var transport = new FakeTransport();
            foreach (string k in new[] { "a", "b", "c" }) transport.Put(k, Bytes(k));
            var cache = NewCache(transport, dir, new CachePolicy { MaxMemoryCount = 2, MemoryEviction = EvictionStrategy.TimeBased });

            Wait(cache.GetAsync("a"));          // created first
            Wait(cache.GetAsync("b"));          // created second
            Wait(cache.GetAsync("a"));          // recent access — must NOT save it under TimeBased
            Wait(cache.GetAsync("c"));          // insert c → evict oldest-created (a)

            Assert(!cache.InMemory("a"), "oldest-created 'a' evicted despite a recent access");
            Assert(cache.InMemory("b"), "'b' retained");
            Assert(cache.InMemory("c"), "'c' retained");
        }

        // ---- enforced disk eviction --------------------------------------------------------------

        private static void Disk_EnforceCount_Lru(string root)
        {
            string dir = Fresh(root);
            var clock = new FakeClock(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
            var disk = new DiskCache(new FileBlobStore(dir), Path.Combine(dir, "__idx.bin"),
                EvictionStrategy.Lru, maxEntries: 2, flushInterval: TimeSpan.Zero, clock: clock.Now);

            disk.Write("a", Bytes("a")); clock.Advance(Sec(1));
            disk.Write("b", Bytes("b")); clock.Advance(Sec(1));
            disk.TryRead("a", out _);   clock.Advance(Sec(1)); // touch a → b is now LRU
            disk.Write("c", Bytes("c"));                        // over cap → evict LRU (b)

            AssertEqual(2, disk.Count, "disk bounded to the count cap");
            Assert(disk.Exists("a"), "recently-touched 'a' kept");
            Assert(!disk.Exists("b"), "least-recently-used 'b' evicted");
            Assert(disk.Exists("c"), "'c' kept");
        }

        private static void Disk_EnforceCount_Lfu(string root)
        {
            string dir = Fresh(root);
            var clock = new FakeClock(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
            var disk = new DiskCache(new FileBlobStore(dir), Path.Combine(dir, "__idx.bin"),
                EvictionStrategy.Lfu, maxEntries: 2, flushInterval: TimeSpan.Zero, clock: clock.Now);

            disk.Write("a", Bytes("a")); clock.Advance(Sec(1));
            disk.TryRead("a", out _);    clock.Advance(Sec(1)); // a: 2 accesses
            disk.Write("b", Bytes("b")); clock.Advance(Sec(1)); // b: 1
            disk.Write("c", Bytes("c"));                        // over cap → evict least-frequent (b)

            Assert(disk.Exists("a"), "frequently-used 'a' kept");
            Assert(!disk.Exists("b"), "least-frequently-used 'b' evicted");
            Assert(disk.Exists("c"), "'c' kept");
        }

        private static void Disk_EnforceCount_TimeBased(string root)
        {
            string dir = Fresh(root);
            var clock = new FakeClock(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
            var disk = new DiskCache(new FileBlobStore(dir), Path.Combine(dir, "__idx.bin"),
                EvictionStrategy.TimeBased, maxEntries: 2, flushInterval: TimeSpan.Zero, clock: clock.Now);

            disk.Write("a", Bytes("a")); clock.Advance(Sec(1));
            disk.Write("b", Bytes("b")); clock.Advance(Sec(1));
            disk.TryRead("a", out _);    clock.Advance(Sec(1)); // touch a — must NOT save it under TimeBased
            disk.Write("c", Bytes("c"));                        // over cap → evict oldest-created (a)

            Assert(!disk.Exists("a"), "oldest-created 'a' evicted despite a recent access");
            Assert(disk.Exists("b"), "'b' kept");
            Assert(disk.Exists("c"), "'c' kept");
        }

        private static void Disk_EnforceBytes(string root)
        {
            string dir = Fresh(root);
            var disk = new DiskCache(new FileBlobStore(dir), Path.Combine(dir, "__idx.bin"),
                EvictionStrategy.Lru, maxBytes: 9, flushInterval: TimeSpan.Zero, clock: () => DateTime.UtcNow);

            disk.Write("small", Bytes("xx"));       // 2
            disk.Write("big", Bytes("xxxxxxxx"));    // 8 → 10 > 9 → evict LRU 'small'

            Assert(!disk.Exists("small"), "byte cap evicted the LRU blob");
            Assert(disk.Exists("big"), "the newest blob kept");
            Assert(disk.Bytes <= 9, "disk bytes within budget");
        }

        // ---- disposal hook -----------------------------------------------------------------------

        private static void Hook_FiresOnEvict(string root)
        {
            string dir = Fresh(root);
            var transport = new FakeTransport();
            transport.Put("a", Bytes("aaa"));
            transport.Put("b", Bytes("bbb"));
            var disposed = new List<string>();
            var cache = NewCache(transport, dir, new CachePolicy { MaxMemoryCount = 1 }, disposer: b => disposed.Add(Str(b)));

            Wait(cache.GetAsync("a"));
            Wait(cache.GetAsync("b")); // evicts a → hook fires

            AssertEqual(1, disposed.Count, "disposer fired once");
            AssertEqual("aaa", disposed[0], "disposed the evicted victim's value");
        }

        private static void Hook_FiresOnReleaseAfterEviction(string root)
        {
            string dir = Fresh(root);
            var transport = new FakeTransport();
            transport.Put("a", Bytes("aaa"));
            transport.Put("b", Bytes("bbb"));
            var disposed = new List<string>();
            var cache = NewCache(transport, dir, new CachePolicy { MaxMemoryCount = 1 }, disposer: b => disposed.Add(Str(b)));

            Wait(cache.AcquireAsync("a")); // pinned
            Wait(cache.AcquireAsync("b")); // pinned too → both exceed the cap of 1, neither evictable yet
            AssertEqual(0, disposed.Count, "nothing disposed while both are pinned");

            cache.Release("a"); // a now unpinned and still over cap → evicted + disposed
            AssertEqual(1, disposed.Count, "releasing the last lease disposed the over-cap entry");
            AssertEqual("aaa", disposed[0], "disposed 'a'");
            Assert(!cache.InMemory("a"), "'a' gone");
            Assert(cache.InMemory("b"), "still-pinned 'b' stays");
        }

        private static void Hook_FiresOnClearMemory(string root)
        {
            string dir = Fresh(root);
            var transport = new FakeTransport();
            transport.Put("a", Bytes("aaa"));
            transport.Put("b", Bytes("bbb"));
            var disposed = new List<string>();
            var cache = NewCache(transport, dir, disposer: b => disposed.Add(Str(b)));

            Wait(cache.GetAsync("a"));
            Wait(cache.GetAsync("b"));
            cache.ClearMemory();

            AssertEqual(0, cache.MemoryCount, "memory tier emptied");
            disposed.Sort();
            AssertEqual(2, disposed.Count, "both values disposed");
            AssertEqual("aaa", disposed[0], "disposed a");
            AssertEqual("bbb", disposed[1], "disposed b");
        }

        // ---- batch / peek / clear ----------------------------------------------------------------

        private static void Batch_PreloadDedupWarms(string root)
        {
            string dir = Fresh(root);
            var transport = new FakeTransport();
            foreach (string k in new[] { "a", "b", "c" }) transport.Put(k, Bytes(k));
            var cache = NewCache(transport, dir);

            Wait(cache.PreloadAsync(new[] { "a", "b", "a", "c", "b", "c" })); // 3 distinct

            AssertEqual(3, transport.Calls, "duplicate keys fetched once each");
            Assert(cache.InMemory("a") && cache.InMemory("b") && cache.InMemory("c"), "memory warmed for all");
            AssertEqual(3, cache.DiskCount, "disk warmed for all");
        }

        private static void Peek_MemoryOnly(string root)
        {
            string dir = Fresh(root);
            var transport = new FakeTransport();
            transport.Put("k", Bytes("v"));
            Wait(NewCache(transport, dir).GetAsync("k")); // warm disk only

            var fresh = NewCache(new FakeTransport { OfflineThrow = true }, dir);
            Assert(!fresh.TryGet("k", out _), "peek misses when only disk has it (memory-only)");

            Wait(fresh.GetAsync("k")); // pulls disk → memory
            Assert(fresh.TryGet("k", out byte[] v), "peek hits once resident in memory");
            AssertEqual("v", Str(v), "peek returns the value");
            Assert(!fresh.TryGet("absent", out _), "peek misses an unknown key");
        }

        private static void Clear_Disk(string root)
        {
            string dir = Fresh(root);
            var transport = new FakeTransport();
            transport.Put("a", Bytes("a"));
            transport.Put("b", Bytes("b"));
            var cache = NewCache(transport, dir);
            Wait(cache.GetAsync("a"));
            Wait(cache.GetAsync("b"));
            Assert(cache.DiskCount == 2, "disk warmed");

            Wait(cache.ClearDiskAsync());

            AssertEqual(0, cache.DiskCount, "disk index emptied");
            var store = new FileBlobStore(dir);
            Assert(!store.Exists("a") && !store.Exists("b"), "blob files deleted");
            Assert(!File.Exists(Path.Combine(dir, "__idx.bin")), "metadata sidecar deleted");
        }

        // ---- disk metadata persistence -----------------------------------------------------------

        private static void Disk_FlushReloadRestoresMetadata(string root)
        {
            string dir = Fresh(root);
            string idx = Path.Combine(dir, "__idx.bin");
            var clock = new FakeClock(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
            var store = new FileBlobStore(dir);

            var disk = new DiskCache(store, idx, EvictionStrategy.Lfu, flushInterval: TimeSpan.Zero, clock: clock.Now);
            disk.Write("k", Bytes("payload"));           // access count 1
            clock.Advance(Sec(1)); disk.TryRead("k", out _); // 2
            clock.Advance(Sec(1)); disk.TryRead("k", out _); // 3
            disk.Flush();

            // "Restart": a brand-new DiskCache over the same sidecar.
            var reloaded = new DiskCache(store, idx, EvictionStrategy.Lfu, flushInterval: TimeSpan.Zero, clock: clock.Now);
            Assert(reloaded.TryGetMetadata("k", out long size, out _, out _, out long restored), "metadata reloaded");
            AssertEqual(7L, size, "size restored");
            AssertEqual(3L, restored, "access count restored from the flushed sidecar (Write + 2 reads)");
            AssertEqual(1, reloaded.Count, "index restored");

            // The restored count is a live baseline: a read after restart continues from it, not from zero.
            clock.Advance(Sec(1));
            reloaded.TryRead("k", out _);
            reloaded.TryGetMetadata("k", out _, out _, out _, out long afterRead);
            AssertEqual(4L, afterRead, "access count continues from the restored baseline");
        }

        private static void Disk_DebounceDelaysFlush(string root)
        {
            string dir = Fresh(root);
            string idx = Path.Combine(dir, "__idx.bin");
            var clock = new FakeClock(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
            var disk = new DiskCache(new FileBlobStore(dir), idx, EvictionStrategy.Lru,
                flushInterval: TimeSpan.FromSeconds(10), clock: clock.Now);

            disk.Write("a", Bytes("a"));
            Assert(!File.Exists(idx), "no flush yet — inside the debounce window");

            clock.Advance(Sec(11));
            disk.Write("b", Bytes("b")); // interval elapsed → this mutation triggers the debounced flush
            Assert(File.Exists(idx), "flushed once the interval elapsed");
        }

        // ---- xxHash3 -----------------------------------------------------------------------------

        private static void Hash_FilenameDeterminism()
        {
            AssertEqual(0x2d06800538d394c2UL, XxHash3.Hash64(Array.Empty<byte>()), "canonical XXH3-64 empty vector");

            string key = "https://cdn.example.com/tex/hero.png";
            AssertEqual(XxHash3.HashToHex(key), XxHash3.HashToHex(key), "same key → same hash");
            AssertEqual(new FileBlobStore(Path.GetTempPath()).FileNameFor(key),
                        new FileBlobStore(Path.GetTempPath()).FileNameFor(key), "filename stable across store instances");
            AssertEqual(16, XxHash3.HashToHex(key).Length, "16-char hex digest");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string k in new[] { "a", "b", "c", "aa", key, key + "?v=2" })
                Assert(seen.Add(XxHash3.HashToHex(k)), "distinct keys yield distinct filenames for '" + k + "'");
        }

        // ---- memory-tier TTL (gap 1) -------------------------------------------------------------

        private static void Memory_TtlExpiryRefetches(string root)
        {
            string dir = Fresh(root);
            var transport = new FakeTransport();
            transport.Put("k", Bytes("v"));
            var clock = new FakeClock(new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc));

            // memory + remote only (no disk): isolates the memory tier's TTL so the re-load provably comes from
            // the remote source, not a disk fallback.
            var cache = ResourceCacheBuilder<byte[]>.Create(Identity)
                .Policy(new CachePolicy { Ttl = TimeSpan.FromMinutes(10) })
                .Sizer(b => b.Length).Delay(NoDelay).Clock(clock.Now)
                .AddRemote(transport)
                .Build();

            ResourceResult<byte[]> first = Wait(cache.GetAsync("k"));
            AssertEqual(CacheTier.Remote, first.Tier, "cold miss served from remote");
            AssertEqual(1, transport.Calls, "downloaded once");
            AssertEqual(CacheTier.Memory, Wait(cache.GetAsync("k")).Tier, "fresh entry served from memory");
            AssertEqual(1, transport.Calls, "no second download while fresh");

            clock.Advance(TimeSpan.FromMinutes(20)); // past the memory TTL

            ResourceResult<byte[]> afterExpiry = Wait(cache.GetAsync("k"));
            AssertEqual(CacheTier.Remote, afterExpiry.Tier, "expired memory entry misses and re-loads from remote");
            AssertEqual(2, transport.Calls, "TTL expiry forced a fresh download");
        }

        // ---- disk content-version + partition (gap 2) --------------------------------------------

        private static void Disk_ContentVersionPurgesPrior(string root)
        {
            string dir = Fresh(root);
            string idx = Path.Combine(dir, "__idx.bin");

            var v1 = new DiskCache(new FileBlobStore(dir), idx, flushInterval: TimeSpan.Zero, contentVersion: 1);
            v1.Write("a", Bytes("aa"));
            v1.Flush();
            Assert(new FileBlobStore(dir).Exists("a"), "generation 1 wrote the blob");

            // Reopen at a new content version → the whole prior generation is purged on open.
            var v2 = new DiskCache(new FileBlobStore(dir), idx, flushInterval: TimeSpan.Zero, contentVersion: 2);
            Assert(!new FileBlobStore(dir).Exists("a"), "bumping the content version purged the old blob");
            AssertEqual(0, v2.Count, "index emptied by the purge");

            // The new stamp persists: reopening at the SAME version does not purge again.
            v2.Write("b", Bytes("bb"));
            v2.Flush();
            var v2b = new DiskCache(new FileBlobStore(dir), idx, flushInterval: TimeSpan.Zero, contentVersion: 2);
            Assert(v2b.Exists("b"), "same-version reopen keeps the new generation");
            AssertEqual(1, v2b.Count, "index restored for the current generation");
        }

        private static void Disk_ContentVersionMatchKeeps(string root)
        {
            string dir = Fresh(root);
            string idx = Path.Combine(dir, "__idx.bin");

            var first = new DiskCache(new FileBlobStore(dir), idx, flushInterval: TimeSpan.Zero, contentVersion: 5);
            first.Write("a", Bytes("aa"));
            first.Flush();

            var reopened = new DiskCache(new FileBlobStore(dir), idx, flushInterval: TimeSpan.Zero, contentVersion: 5);
            Assert(reopened.Exists("a"), "matching content version keeps the blob");
            AssertEqual(1, reopened.Count, "index intact");
            AssertEqual(5, reopened.ContentVersion, "content version reported");
        }

        private static void Disk_PartitionIsolates(string root)
        {
            string dir = Fresh(root);
            var a = new FileBlobStore(dir, "alpha");
            var b = new FileBlobStore(dir, "beta");

            a.Write("k", Bytes("in-alpha"));
            b.Write("k", Bytes("in-beta"));

            Assert(a.TryRead("k", out byte[] va) && Str(va) == "in-alpha", "alpha holds its own bytes for the key");
            Assert(b.TryRead("k", out byte[] vb) && Str(vb) == "in-beta", "beta holds independent bytes for the same key");

            a.Clear();
            Assert(!a.Exists("k"), "clearing alpha wiped its partition");
            Assert(b.Exists("k"), "beta is untouched by alpha's clear");
        }

        // ---- loading-feedback hooks (gap 3) ------------------------------------------------------

        private static void Observer_StartedCompleted(string root)
        {
            string dir = Fresh(root);
            var transport = new FakeTransport();
            transport.Put("k", Bytes("v"));
            var rec = new RecordingObserver();
            var cache = new ResourceCache<byte[]>(transport, NewDisk(dir, CachePolicy.Default), Identity,
                policy: CachePolicy.Default, retry: null, sizeOf: b => b.Length, disposer: null,
                delay: NoDelay, clock: null, observer: rec);

            Wait(cache.GetAsync("k"));
            AssertEqual(1, rec.Started.Count, "started fired once");
            AssertEqual("k", rec.Started[0], "started carried the key");
            AssertEqual(1, rec.Completed.Count, "completed fired once");
            AssertEqual(CacheTier.Remote, rec.Completed[0].Item2, "completed reported the serving tier");
            AssertEqual(0, rec.Failed.Count, "no failure on success");

            Wait(cache.GetAsync("k")); // memory hit still fires the pair
            AssertEqual(CacheTier.Memory, rec.Completed[1].Item2, "memory hit completes with the Memory tier");
        }

        private static void Observer_StartedFailed(string root)
        {
            string dir = Fresh(root);
            var transport = new FakeTransport(); // nothing put → NotFound
            var rec = new RecordingObserver();
            var cache = new ResourceCache<byte[]>(transport, NewDisk(dir, CachePolicy.Default), Identity,
                policy: CachePolicy.Default, retry: RetryPolicy.None, sizeOf: b => b.Length, disposer: null,
                delay: NoDelay, clock: null, observer: rec);

            Wait(cache.GetAsync("ghost"));
            AssertEqual(1, rec.Started.Count, "started fired for the failing request");
            AssertEqual(0, rec.Completed.Count, "no completion on failure");
            AssertEqual(1, rec.Failed.Count, "failed fired once");
            AssertEqual(ResourceFailureKind.NotFound, rec.Failed[0].Item2.Kind, "failed carried the typed failure");
        }

        // ---- arbitrary N-source composition (gap 4) ----------------------------------------------

        private static void Composition_WriteBackToEarlier(string root)
        {
            var near = new MapByteSource(CacheTier.Disk, canWrite: true); // empty, writable near layer
            var transport = new FakeTransport();
            transport.Put("k", Bytes("far-bytes"));

            var cache = ResourceCacheBuilder<byte[]>.Create(Identity)
                .Policy(CachePolicy.Default).Sizer(b => b.Length).Delay(NoDelay)
                .AddSource(near)          // layer 0 — writable
                .AddRemote(transport)     // layer 1 — read-only remote
                .Build();

            ResourceResult<byte[]> hit = Wait(cache.GetAsync("k"));
            AssertEqual(CacheTier.Remote, hit.Tier, "served from the far remote layer on a cold miss");
            AssertEqual(1, transport.Calls, "downloaded once");
            Assert(near.Contains("k"), "the far hit was written back to the earlier writable source");

            cache.ClearMemory();
            ResourceResult<byte[]> second = Wait(cache.GetAsync("k"));
            AssertEqual(CacheTier.Disk, second.Tier, "now served by the warmed near layer, not the remote");
            AssertEqual(1, transport.Calls, "no second download — write-back satisfied it");
        }

        private static void Composition_MemoryOnly(string root)
        {
            // A memory-only topology: no disk, no remote. A miss is a graceful typed failure, never a throw.
            var cache = ResourceCacheBuilder<byte[]>.Create(Identity)
                .Policy(CachePolicy.Default).Sizer(b => b.Length).Delay(NoDelay)
                .Build();

            ResourceResult<byte[]> result = Wait(cache.GetAsync("x"));
            Assert(!result.Success, "a diskless/remoteless miss fails rather than throwing");
            AssertEqual(ResourceFailureKind.NotFound, result.Failure.Kind, "no source could provide it → NotFound");
            AssertEqual(0, cache.DiskCount, "diskless topology reports zero disk entries");
        }

        private static void Composition_TieredEquivalence(string root)
        {
            string dir = Fresh(root);
            var transport = new FakeTransport();
            transport.Put("k", Bytes("v"));
            var disk = NewDisk(dir, CachePolicy.Default);

            var cache = ResourceCacheBuilder<byte[]>.Create(Identity)
                .Policy(CachePolicy.Default).Sizer(b => b.Length).Delay(NoDelay)
                .AddDisk(disk)
                .AddRemote(transport)
                .Build();

            ResourceResult<byte[]> first = Wait(cache.GetAsync("k"));
            AssertEqual(CacheTier.Remote, first.Tier, "cold miss served from remote");
            Assert(cache.InMemory("k"), "memory warmed like the tiered default");
            AssertEqual(1, cache.DiskCount, "disk warmed like the tiered default");
            AssertEqual(CacheTier.Memory, Wait(cache.GetAsync("k")).Tier, "second request is a memory hit");
        }

        // ---- fixtures ----------------------------------------------------------------------------

        private static ResourceCache<byte[]> NewCache(
            IResourceTransport transport, string dir, CachePolicy policy = null,
            RetryPolicy retry = null, Action<byte[]> disposer = null, Func<DateTime> clock = null)
        {
            policy = policy ?? CachePolicy.Default;
            return new ResourceCache<byte[]>(transport, NewDisk(dir, policy, clock), Identity, policy, retry,
                sizeOf: b => b.Length, disposer: disposer, delay: NoDelay);
        }

        private static DiskCache NewDisk(string dir, CachePolicy policy, Func<DateTime> clock = null) =>
            DiskCache.FromPolicy(new FileBlobStore(dir), Path.Combine(dir, "__idx.bin"), policy, clock);

        private static byte[] Identity(byte[] raw) => (byte[])raw.Clone();
        private static Task NoDelay(TimeSpan span, CancellationToken ct) => Task.CompletedTask;
        private static TimeSpan Sec(double s) => TimeSpan.FromSeconds(s);
        private static byte[] Bytes(string s) => System.Text.Encoding.UTF8.GetBytes(s);
        private static string Str(byte[] b) => System.Text.Encoding.UTF8.GetString(b);

        private static string Fresh(string root)
        {
            string dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>In-memory transport: a key→bytes map, a call counter, N-failures-first, and an offline latch.</summary>
        private sealed class FakeTransport : IResourceTransport
        {
            private readonly Dictionary<string, byte[]> _content = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            public int Calls;
            public int FailFirst;
            public bool OfflineThrow;

            public void Put(string key, byte[] bytes) => _content[key] = bytes;

            public Task<byte[]> FetchAsync(string key, CancellationToken cancellationToken = default)
            {
                Calls++;
                if (OfflineThrow) throw new IOException("offline (transport must not be hit)");
                if (Calls <= FailFirst) throw new IOException("simulated transient failure");
                if (!_content.TryGetValue(key, out byte[] bytes)) throw new ResourceNotFoundException(key);
                return Task.FromResult(bytes);
            }
        }

        /// <summary>Transport whose fetch parks on a latch until <see cref="Release"/>, exercising single-flight.</summary>
        private sealed class GatedTransport : IResourceTransport
        {
            private readonly TaskCompletionSource<bool> _gate = new TaskCompletionSource<bool>();
            private readonly byte[] _payload;
            public int Calls;

            public GatedTransport(byte[] payload) => _payload = payload;

            public void Release() => _gate.TrySetResult(true);

            public async Task<byte[]> FetchAsync(string key, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref Calls);
                await _gate.Task.ConfigureAwait(false);
                return _payload;
            }
        }

        private sealed class FakeClock
        {
            private DateTime _now;
            public FakeClock(DateTime start) => _now = start;
            public DateTime Now() => _now;
            public void Advance(TimeSpan by) => _now += by;
        }

        /// <summary>Records the loading-feedback callbacks so a test can assert the started→terminal sequence.</summary>
        private sealed class RecordingObserver : IResourceLoadObserver
        {
            public readonly List<string> Started = new List<string>();
            public readonly List<Tuple<string, CacheTier>> Completed = new List<Tuple<string, CacheTier>>();
            public readonly List<Tuple<string, ResourceFailure>> Failed = new List<Tuple<string, ResourceFailure>>();

            public void OnLoadStarted(string key) => Started.Add(key);
            public void OnLoadCompleted(string key, CacheTier servedBy) => Completed.Add(Tuple.Create(key, servedBy));
            public void OnLoadFailed(string key, ResourceFailure failure) => Failed.Add(Tuple.Create(key, failure));
        }

        /// <summary>An in-memory byte source for composition tests: a dictionary-backed, non-retryable custom layer.</summary>
        private sealed class MapByteSource : IResourceByteSource
        {
            private readonly Dictionary<string, byte[]> _map = new Dictionary<string, byte[]>(StringComparer.Ordinal);

            public MapByteSource(CacheTier tier, bool canWrite)
            {
                Tier = tier;
                CanWrite = canWrite;
            }

            public CacheTier Tier { get; }
            public bool CanWrite { get; }
            public bool Retryable => false;

            public bool Contains(string key) => _map.ContainsKey(key);

            public Task<ByteReadResult> TryReadAsync(string key, CancellationToken cancellationToken) =>
                Task.FromResult(_map.TryGetValue(key, out byte[] bytes) ? ByteReadResult.Hit(bytes) : ByteReadResult.Miss);

            public void Write(string key, byte[] bytes) => _map[key] = bytes;
        }

        // ---- harness -----------------------------------------------------------------------------

        private static T Wait<T>(Task<T> task)
        {
            try { return task.GetAwaiter().GetResult(); }
            catch (AggregateException e) when (e.InnerException != null) { throw e.InnerException; }
        }

        private static void Wait(Task task)
        {
            try { task.GetAwaiter().GetResult(); }
            catch (AggregateException e) when (e.InnerException != null) { throw e.InnerException; }
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                s_passed++;
                Console.WriteLine("  PASS  " + name);
            }
            catch (Exception e)
            {
                s_failed++;
                Console.WriteLine("  FAIL  " + name + "  -> " + e.Message);
            }
        }

        private static void Assert(bool condition, string what)
        {
            if (!condition) throw new Exception("assertion failed: " + what);
        }

        private static void AssertEqual(object expected, object actual, string what)
        {
            if (!Equals(expected, actual))
                throw new Exception("expected <" + expected + "> but got <" + actual + "> for " + what);
        }

        private static void AssertThrows<TException>(Action action, string what) where TException : Exception
        {
            try { action(); }
            catch (TException) { return; }
            catch (Exception e) { throw new Exception("expected " + typeof(TException).Name + " but got " + e.GetType().Name + " for " + what); }
            throw new Exception("expected " + typeof(TException).Name + " but nothing was thrown for " + what);
        }
    }
}
