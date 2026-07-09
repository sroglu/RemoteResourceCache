using System;

namespace PFound.RemoteResourceCache.Core
{
    /// <summary>
    /// Why a fetch could not be satisfied. The kind lets a caller branch without string-matching: show an
    /// offline banner on <see cref="Network"/>/<see cref="RetryExhausted"/>, a "missing" state on
    /// <see cref="NotFound"/>, and a hard error on <see cref="Decode"/>.
    /// </summary>
    public enum ResourceFailureKind
    {
        /// <summary>A transient transport fault with no retries left to spend (a single-attempt policy that failed).</summary>
        Network = 1,

        /// <summary>The remote has no resource for this key — terminal; retrying will not help.</summary>
        NotFound = 2,

        /// <summary>Bytes arrived but the decoder rejected them (corrupt/wrong format) — terminal.</summary>
        Decode = 3,

        /// <summary>Every retry attempt was spent on transient faults and all failed.</summary>
        RetryExhausted = 4,
    }

    /// <summary>The failure payload carried by a non-successful <see cref="ResourceResult{T}"/>.</summary>
    public readonly struct ResourceFailure
    {
        public ResourceFailureKind Kind { get; }
        public string Message { get; }

        /// <summary>The underlying transport/decoder exception, when there was one.</summary>
        public Exception Cause { get; }

        public ResourceFailure(ResourceFailureKind kind, string message, Exception cause = null)
        {
            Kind = kind;
            Message = message;
            Cause = cause;
        }

        public override string ToString() => Kind + ": " + Message;
    }
}
