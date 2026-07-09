namespace PFound.RemoteResourceCache.Core
{
    /// <summary>
    /// The outcome of a cache request: either a resource plus the <see cref="CacheTier"/> that served it, or a
    /// typed <see cref="ResourceFailure"/>. Modelled as a value so the normal miss/offline paths don't throw —
    /// failure is data, not an exception. (Genuine programming errors — a null decoder, a released-but-unpinned
    /// key — still throw, per the fail-fast contract.)
    /// </summary>
    public readonly struct ResourceResult<T>
    {
        public bool Success { get; }

        /// <summary>The decoded resource on success; <c>default</c> on failure.</summary>
        public T Value { get; }

        /// <summary>Which tier served the value on success; <see cref="CacheTier.None"/> on failure.</summary>
        public CacheTier Tier { get; }

        public ResourceFailure Failure { get; }

        private ResourceResult(bool success, T value, CacheTier tier, ResourceFailure failure)
        {
            Success = success;
            Value = value;
            Tier = tier;
            Failure = failure;
        }

        public static ResourceResult<T> Ok(T value, CacheTier tier) =>
            new ResourceResult<T>(true, value, tier, default);

        public static ResourceResult<T> Fail(ResourceFailure failure) =>
            new ResourceResult<T>(false, default, CacheTier.None, failure);

        public static ResourceResult<T> Fail(ResourceFailureKind kind, string message, System.Exception cause = null) =>
            Fail(new ResourceFailure(kind, message, cause));
    }
}
