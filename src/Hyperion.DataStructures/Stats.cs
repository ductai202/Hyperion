namespace Hyperion.DataStructures;

/// <summary>
/// Tracks global statistics for the Redis server.
/// </summary>
public static class Stats
{
    public static DateTimeOffset ServerStartTime { get; } = DateTimeOffset.UtcNow;

    public static class HashKeySpaceStat
    {
        public static long Key { get; set; } = 0;
        public static long Expires { get; set; } = 0;
    }
}
