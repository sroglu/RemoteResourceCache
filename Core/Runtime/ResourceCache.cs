using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PFound.RemoteResourceCache.Core
{
    /// <summary>
    /// A generic, tiered resource cache. By default: <b>memory → disk → remote</b> — a request is answered by the
    /// fastest tier that has the key; a miss falls through, and a value fetched from a slower tier populates every
    /// faster tier above it. The remote tier is reached through a pluggable <see cref="IResourceTransport"/>; raw
    /// bytes become a <typeparamref name="T"/> through a caller-supplied <see cref="ResourceDecoder{T}"/>, so the
    /// whole type stays engine-free and unit-testable.
    ///
    /// Internally the tiers are an ordered list of <see cref="IResourceByteSource"/> layers plus an always-present
    /// decoded memory tier on top; <see cref="ResourceCacheBuilder{T}"/> composes arbitrary N-source topologies
    /// (memory-only, extra disk levels, custom stores) while the tiered constructor keeps the common shape a
    /// one-liner.
    ///
    /// Layered on top of the tiers: <b>single-flight</b> (concurrent requests for one key share a load),
    /// <b>ref-counting</b> (<see cref="AcquireAsync"/>/<see cref="Release"/> pin a resource in memory),
    /// <b>retry</b> with typed <see cref="ResourceFailure"/> results, selectable eviction on both bounded tiers,
    /// a <b>disposal hook</b> for native resources, batch <see cref="PreloadAsync"/>, a synchronous memory-only
    /// <see cref="TryGet"/>, TTL expiry on both the memory and disk tiers, optional
    /// <see cref="IResourceLoadObserver"/> loading-feedback hooks, and <see cref="ClearMemory"/>/<see cref="ClearDiskAsync"/>.
    /// </summary>
    public sealed class ResourceCache<T>
    {
        private readonly IReadOnlyList<IResourceByteSource> _sources;
        private readonly DiskCache _disk; // first disk layer, for diagnostics/flush/clear; null in a diskless topology
        private readonly ResourceDecoder<T> _decode;
        private readonly ResourceSizer<T> _sizeOf;
        private readonly CachePolicy _policy;
        private readonly RetryPolicy _retry;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private readonly IResourceLoadObserver _observer;

        private readonly MemoryResourceCache<T> _memory;

        private readonly object _flightGate = new object();
        private readonly Dictionary<string, Task<ResourceResult<T>>> _inFlight =
            new Dictionary<string, Task<ResourceResult<T>>>(StringComparer.Ordinal);

        /// <summary>The common memory→disk→remote cache. For other topologies use <see cref="ResourceCacheBuilder{T}"/>.</summary>
        public ResourceCache(
            IResourceTransport transport,
            DiskCache disk,
            ResourceDecoder<T> decode,
            CachePolicy policy = null,
            RetryPolicy retry = null,
            ResourceSizer<T> sizeOf = null,
            Action<T> disposer = null,
            Func<TimeSpan, CancellationToken, Task> delay = null,
            Func<DateTime> clock = null,
            IResourceLoadObserver observer = null)
            : this(new IResourceByteSource[] { new DiskByteSource(disk), new TransportByteSource(transport) },
                   decode, policy, retry, sizeOf, disposer, delay, clock, observer)
        {
        }

        /// <summary>Composition constructor: an ordered source list (nearest first). Used by <see cref="ResourceCacheBuilder{T}"/>.</summary>
        internal ResourceCache(
            IReadOnlyList<IResourceByteSource> sources,
            ResourceDecoder<T> decode,
            CachePolicy policy,
            RetryPolicy retry,
            ResourceSizer<T> sizeOf,
            Action<T> disposer,
            Func<TimeSpan, CancellationToken, Task> delay,
            Func<DateTime> clock,
            IResourceLoadObserver observer)
        {
            _sources = sources;
            _decode = decode;
            _policy = policy ?? CachePolicy.Default;
            _retry = retry ?? RetryPolicy.Default;
            _sizeOf = sizeOf ?? (_ => 0L);
            _delay = delay ?? ((span, ct) => Task.Delay(span, ct));
            _observer = observer;

            Func<DateTime> theClock = clock ?? (() => DateTime.UtcNow);
            _memory = new MemoryResourceCache<T>(
                _policy.MaxMemoryCount, _policy.MaxMemoryBytes, _policy.MemoryEviction, disposer, _policy.Ttl, theClock);
            _disk = FirstDisk(sources);
        }

        private static DiskCache FirstDisk(IReadOnlyList<IResourceByteSource> sources)
        {
            for (int i = 0; i < sources.Count; i++)
                if (sources[i] is DiskByteSource diskSource) return diskSource.Disk;
            return null; // a diskless topology (memory-only / custom) genuinely has no disk tier
        }

        // ---- diagnostics -------------------------------------------------------------------------

        public int MemoryCount => _memory.Count;
        public long MemoryBytes => _memory.Bytes;
        public bool InMemory(string key) => _memory.Contains(key);
        public bool IsPinned(string key) => _memory.IsPinned(key);
        public int DiskCount => _disk == null ? 0 : _disk.Count;
        public long DiskBytes => _disk == null ? 0 : _disk.Bytes;

        // ---- public API --------------------------------------------------------------------------

        /// <summary>Synchronous, memory-tier-only peek — instant, never touches disk or network. Records the access on a hit; a TTL-expired entry misses.</summary>
        public bool TryGet(string key, out T value) => _memory.TryGet(key, out value);

        /// <summary>Gets the resource for <paramref name="key"/> from the nearest tier, without pinning it.</summary>
        public Task<ResourceResult<T>> GetAsync(string key, CancellationToken cancellationToken = default) =>
            ResolveAsync(key, pin: false, cancellationToken);

        /// <summary>Like <see cref="GetAsync"/>, but on success pins the resource in memory (ref-count +1). Balance every successful acquire with a <see cref="Release"/>.</summary>
        public Task<ResourceResult<T>> AcquireAsync(string key, CancellationToken cancellationToken = default) =>
            ResolveAsync(key, pin: true, cancellationToken);

        /// <summary>Releases one pin taken by <see cref="AcquireAsync"/>. Reaching zero makes the entry evictable (and disposes it if it was only held past the cap by the pin).</summary>
        public void Release(string key) => _memory.Release(key);

        /// <summary>
        /// Warms memory + disk for a set of keys concurrently, de-duplicated within the batch (and against any
        /// in-flight load). Fire-and-forget: the returned task completes when the batch settles; awaiting it is
        /// optional. Honors <paramref name="cancellationToken"/>.
        /// </summary>
        public Task PreloadAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var tasks = new List<Task>();
            foreach (string key in keys)
                if (seen.Add(key))
                    tasks.Add(GetAsync(key, cancellationToken));
            return Task.WhenAll(tasks);
        }

        /// <summary>Removes one entry from the memory tier (disposing its native value); returns false if it wasn't resident.</summary>
        public bool RemoveFromMemory(string key) => _memory.Remove(key);

        /// <summary>Drops the whole memory tier, releasing every native value through the disposal hook.</summary>
        public void ClearMemory() => _memory.Clear();

        /// <summary>Deletes every disk blob and its metadata. Async — it touches the filesystem. No-op in a diskless topology.</summary>
        public Task ClearDiskAsync() => _disk == null ? Task.CompletedTask : Task.Run(() => _disk.Clear());

        /// <summary>Persists the disk metadata index now. Call on shutdown so LFU/TimeBased ordering survives a restart. No-op in a diskless topology.</summary>
        public void FlushDisk()
        {
            if (_disk != null) _disk.Flush();
        }

        // ---- resolution --------------------------------------------------------------------------

        private async Task<ResourceResult<T>> ResolveAsync(string key, bool pin, CancellationToken cancellationToken)
        {
            _observer?.OnLoadStarted(key);
            ResourceResult<T> result = await ResolveCoreAsync(key, pin, cancellationToken).ConfigureAwait(false);
            if (result.Success) _observer?.OnLoadCompleted(key, result.Tier);
            else _observer?.OnLoadFailed(key, result.Failure);
            return result;
        }

        private async Task<ResourceResult<T>> ResolveCoreAsync(string key, bool pin, CancellationToken cancellationToken)
        {
            if (_memory.TryGet(key, out T cached))
            {
                if (pin) _memory.Acquire(key, cached, _sizeOf(cached));
                return ResourceResult<T>.Ok(cached, CacheTier.Memory);
            }

            Task<ResourceResult<T>> flight;
            lock (_flightGate)
            {
                if (!_inFlight.TryGetValue(key, out flight))
                {
                    flight = LoadFromSourcesAsync(key, cancellationToken);
                    // Only track a still-running load for de-duplication. A load that completed synchronously
                    // (e.g. a warm disk/near hit, or a fake transport) needs no dedup and must not linger in the
                    // map — otherwise a later same-key request would await the stale completed task. Untracking
                    // is done by a completion continuation (attached under the lock) rather than the load's own
                    // finally, so add-then-remove ordering can never invert.
                    if (!flight.IsCompleted)
                    {
                        _inFlight[key] = flight;
                        flight.ContinueWith(
                            _ => { lock (_flightGate) _inFlight.Remove(key); },
                            CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                    }
                }
            }

            ResourceResult<T> result = await flight.ConfigureAwait(false);

            if (pin && result.Success)
                _memory.Acquire(key, result.Value, _sizeOf(result.Value));

            return result;
        }

        private async Task<ResourceResult<T>> LoadFromSourcesAsync(string key, CancellationToken cancellationToken)
        {
            Exception lastTransient = null;
            ResourceNotFoundException notFound = null;

            for (int i = 0; i < _sources.Count; i++)
            {
                IResourceByteSource source = _sources[i];
                byte[] bytes = null;
                bool hit = false;

                if (source.Retryable)
                {
                    // A retryable source (e.g. a remote transport) returns a hit or throws: transient faults
                    // are retried with backoff; a not-found is terminal for THIS source, so move to the next.
                    for (int attempt = 1; attempt <= _retry.MaxAttempts; attempt++)
                    {
                        ByteReadResult read;
                        try
                        {
                            read = await source.TryReadAsync(key, cancellationToken).ConfigureAwait(false);
                        }
                        catch (ResourceNotFoundException nf)
                        {
                            notFound = nf;
                            break;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (NullReferenceException)
                        {
                            // A null transport (or other unconfigured dependency) is a programmer
                            // misconfiguration, not a transient network fault — fail fast, never retry.
                            throw;
                        }
                        catch (Exception transient)
                        {
                            lastTransient = transient;
                            if (attempt < _retry.MaxAttempts)
                                await _delay(_retry.BackoffFor(attempt), cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        if (read.Found) { bytes = read.Bytes; hit = true; }
                        break;
                    }
                }
                else
                {
                    ByteReadResult read = await source.TryReadAsync(key, cancellationToken).ConfigureAwait(false);
                    if (read.Found) { bytes = read.Bytes; hit = true; }
                }

                if (hit)
                {
                    if (!TryDecode(key, bytes, out T value, out ResourceResult<T> decodeFailure))
                        return decodeFailure;

                    if (_policy.CanCache(key))
                    {
                        WriteBackToEarlier(i, key, bytes);
                        PopulateMemory(key, value);
                    }
                    return ResourceResult<T>.Ok(value, source.Tier);
                }
            }

            // No source produced the resource. A transient fault outranks a not-found (it means "come back
            // when online"); a pure not-found (or plain misses everywhere) is terminal.
            if (lastTransient != null)
            {
                ResourceFailureKind kind = _retry.MaxAttempts > 1
                    ? ResourceFailureKind.RetryExhausted
                    : ResourceFailureKind.Network;
                return ResourceResult<T>.Fail(kind,
                    "Failed to fetch '" + key + "' after " + _retry.MaxAttempts + " attempt(s).", lastTransient);
            }
            if (notFound != null)
                return ResourceResult<T>.Fail(ResourceFailureKind.NotFound, notFound.Message, notFound);
            return ResourceResult<T>.Fail(ResourceFailureKind.NotFound, "No source could provide '" + key + "'.");
        }

        /// <summary>Warms every earlier writable source with the freshly-fetched bytes (nearest-first write-back).</summary>
        private void WriteBackToEarlier(int hitIndex, string key, byte[] bytes)
        {
            for (int j = 0; j < hitIndex; j++)
                if (_sources[j].CanWrite)
                    _sources[j].Write(key, bytes);
        }

        private bool TryDecode(string key, byte[] bytes, out T value, out ResourceResult<T> failure)
        {
            try
            {
                value = _decode(bytes);
                failure = default;
                return true;
            }
            catch (Exception e)
            {
                value = default;
                failure = ResourceResult<T>.Fail(ResourceFailureKind.Decode,
                    "Failed to decode resource for '" + key + "'.", e);
                return false;
            }
        }

        private void PopulateMemory(string key, T value)
        {
            if (_policy.CanCache(key))
                _memory.Insert(key, value, _sizeOf(value));
        }
    }
}
