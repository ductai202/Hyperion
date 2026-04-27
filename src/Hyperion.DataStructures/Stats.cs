namespace Hyperion.DataStructures;

/// <summary>
/// Tracks global statistics for the Redis server.
/// </summary>
public static class Stats
{
    public static DateTimeOffset ServerStartTime { get; } = DateTimeOffset.UtcNow;

    public static class HashKeySpaceStat
    {
        // Use long for Interlocked operations
        private static long _key = 0;
        private static long _expires = 0;

        public static long Key => Interlocked.Read(ref _key);
        public static long Expires => Interlocked.Read(ref _expires);

        public static void IncrementKey() => Interlocked.Increment(ref _key);
        public static void DecrementKey() => Interlocked.Decrement(ref _key);
        public static void IncrementExpires() => Interlocked.Increment(ref _expires);
        public static void DecrementExpires() => Interlocked.Decrement(ref _expires);
    }
}
