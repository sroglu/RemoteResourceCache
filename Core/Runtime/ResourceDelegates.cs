namespace PFound.RemoteResourceCache.Core
{
    /// <summary>
    /// Turns the raw bytes of a fetched/cached resource into the concrete runtime type <typeparamref name="T"/>
    /// (e.g. a texture, an audio clip, a parsed document). Keeping decode as a caller-supplied delegate is what
    /// lets the core stay engine-free and generic: the disk/remote tiers only ever move <c>byte[]</c>.
    /// </summary>
    /// <remarks>A decoder that throws is reported by the cache as <see cref="ResourceFailureKind.Decode"/>.</remarks>
    public delegate T ResourceDecoder<out T>(byte[] rawBytes);

    /// <summary>
    /// Measures the in-memory footprint of a decoded resource in bytes, so the memory tier can enforce a byte
    /// budget (not just an entry count). Omit it to bound the memory tier by entry count alone.
    /// </summary>
    public delegate long ResourceSizer<in T>(T value);
}
