namespace PFound.RemoteResourceCache.Core
{
    /// <summary>
    /// Optional loading-feedback hooks for UI (a spinner, a progress overlay, load analytics). Attach one to a
    /// <see cref="ResourceCache{T}"/> and it is notified around every <see cref="ResourceCache{T}.GetAsync"/> /
    /// <see cref="ResourceCache{T}.AcquireAsync"/>: exactly one <see cref="OnLoadStarted"/> is followed by exactly
    /// one terminal callback — <see cref="OnLoadCompleted"/> on success or <see cref="OnLoadFailed"/> on a typed
    /// failure. Callbacks are per-request (concurrent callers sharing one single-flight load each get their own
    /// pair), so a spinner can be reference-counted per request. A memory-tier hit still fires the pair (started
    /// then completed with <see cref="CacheTier.Memory"/>) — an observer that only cares about slow loads can
    /// ignore memory-tier completions.
    /// </summary>
    public interface IResourceLoadObserver
    {
        /// <summary>A request for <paramref name="key"/> has begun resolving.</summary>
        void OnLoadStarted(string key);

        /// <summary>The request for <paramref name="key"/> succeeded, served by <paramref name="servedBy"/>.</summary>
        void OnLoadCompleted(string key, CacheTier servedBy);

        /// <summary>The request for <paramref name="key"/> failed with the given typed <paramref name="failure"/>.</summary>
        void OnLoadFailed(string key, ResourceFailure failure);
    }
}
