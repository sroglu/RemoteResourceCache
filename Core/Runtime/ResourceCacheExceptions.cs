using System;

namespace PFound.RemoteResourceCache.Core
{
    /// <summary>Base for exceptions thrown by the resource cache. Transport faults that are part of the fetch
    /// contract (see <see cref="ResourceNotFoundException"/>) derive from this so the cache can classify them.</summary>
    public class ResourceCacheException : Exception
    {
        public ResourceCacheException(string message) : base(message) { }
        public ResourceCacheException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Thrown by an <see cref="IResourceTransport"/> when the remote genuinely has no resource for the key
    /// (e.g. an HTTP 404). This is a terminal signal — the cache turns it into a
    /// <see cref="ResourceFailureKind.NotFound"/> result and does NOT retry. Any other transport exception is
    /// treated as a transient network fault instead.
    /// </summary>
    public sealed class ResourceNotFoundException : ResourceCacheException
    {
        public string Key { get; }

        public ResourceNotFoundException(string key)
            : base("No resource found for key '" + key + "'.")
        {
            Key = key;
        }
    }
}
