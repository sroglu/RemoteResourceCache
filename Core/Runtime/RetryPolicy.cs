using System;

namespace PFound.RemoteResourceCache.Core
{
    /// <summary>
    /// How the remote tier retries transient faults: a total attempt count and the wait before each retry. Give
    /// <see cref="Delays"/> for an explicit per-attempt schedule, or leave it null to derive an exponential
    /// backoff from <see cref="BaseBackoff"/> × <see cref="BackoffMultiplier"/>^(attempt-1).
    /// </summary>
    public sealed class RetryPolicy
    {
        /// <summary>Total attempts including the first (must be ≥ 1). One means "try once, never retry".</summary>
        public int MaxAttempts { get; set; } = 1;

        public TimeSpan BaseBackoff { get; set; } = TimeSpan.Zero;

        public double BackoffMultiplier { get; set; } = 2.0;

        /// <summary>Explicit backoff schedule; when set, overrides the exponential derivation. The last entry is reused if there are more retries than entries.</summary>
        public TimeSpan[] Delays { get; set; }

        /// <summary>Wait before the retry that follows attempt number <paramref name="attempt"/> (1-based).</summary>
        public TimeSpan BackoffFor(int attempt)
        {
            if (Delays != null && Delays.Length > 0)
                return Delays[Math.Min(attempt - 1, Delays.Length - 1)];

            double factor = BackoffMultiplier <= 0 ? 1.0 : BackoffMultiplier;
            double ms = BaseBackoff.TotalMilliseconds * Math.Pow(factor, attempt - 1);
            return TimeSpan.FromMilliseconds(ms);
        }

        /// <summary>Three attempts with a 200 ms → 400 ms exponential backoff.</summary>
        public static RetryPolicy Default =>
            new RetryPolicy { MaxAttempts = 3, BaseBackoff = TimeSpan.FromMilliseconds(200), BackoffMultiplier = 2.0 };

        /// <summary>A single attempt with no retry.</summary>
        public static RetryPolicy None => new RetryPolicy { MaxAttempts = 1 };
    }
}
