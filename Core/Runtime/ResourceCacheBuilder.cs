using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PFound.RemoteResourceCache.Core
{
    /// <summary>
    /// Composes a <see cref="ResourceCache{T}"/> from an ordered list of arbitrary <see cref="IResourceByteSource"/>
    /// layers (nearest first), instead of the fixed memory→disk→remote trio. A hit from any layer is decoded once,
    /// written back to every earlier writable layer, and cached in the always-present decoded memory tier. This
    /// enables memory-only, memory+remote (no disk), multi-disk-level, or fully custom topologies.
    ///
    /// The memory tier's bounds/eviction/TTL come from the supplied <see cref="CachePolicy"/> (as in the tiered
    /// constructor). For the common memory→disk→remote shape, prefer <see cref="ResourceCache{T}"/>'s tiered
    /// constructor — this builder is the escape hatch for everything else.
    /// </summary>
    public sealed class ResourceCacheBuilder<T>
    {
        private readonly ResourceDecoder<T> _decode;
        private readonly List<IResourceByteSource> _sources = new List<IResourceByteSource>();
        private CachePolicy _policy;
        private RetryPolicy _retry;
        private ResourceSizer<T> _sizeOf;
        private Action<T> _disposer;
        private Func<TimeSpan, CancellationToken, Task> _delay;
        private Func<DateTime> _clock;
        private IResourceLoadObserver _observer;

        public ResourceCacheBuilder(ResourceDecoder<T> decode) => _decode = decode;

        public static ResourceCacheBuilder<T> Create(ResourceDecoder<T> decode) => new ResourceCacheBuilder<T>(decode);

        public ResourceCacheBuilder<T> Policy(CachePolicy policy) { _policy = policy; return this; }
        public ResourceCacheBuilder<T> Retry(RetryPolicy retry) { _retry = retry; return this; }
        public ResourceCacheBuilder<T> Sizer(ResourceSizer<T> sizeOf) { _sizeOf = sizeOf; return this; }
        public ResourceCacheBuilder<T> Disposer(Action<T> disposer) { _disposer = disposer; return this; }
        public ResourceCacheBuilder<T> Delay(Func<TimeSpan, CancellationToken, Task> delay) { _delay = delay; return this; }
        public ResourceCacheBuilder<T> Clock(Func<DateTime> clock) { _clock = clock; return this; }
        public ResourceCacheBuilder<T> Observer(IResourceLoadObserver observer) { _observer = observer; return this; }

        /// <summary>Appends a layer. Order matters: earlier = nearer/faster, and only earlier layers receive write-backs.</summary>
        public ResourceCacheBuilder<T> AddSource(IResourceByteSource source) { _sources.Add(source); return this; }

        /// <summary>Appends a writable disk layer.</summary>
        public ResourceCacheBuilder<T> AddDisk(DiskCache disk) => AddSource(new DiskByteSource(disk));

        /// <summary>Appends a read-only remote layer over a transport.</summary>
        public ResourceCacheBuilder<T> AddRemote(IResourceTransport transport) => AddSource(new TransportByteSource(transport));

        public ResourceCache<T> Build() =>
            new ResourceCache<T>(_sources.ToArray(), _decode, _policy, _retry, _sizeOf, _disposer, _delay, _clock, _observer);
    }
}
