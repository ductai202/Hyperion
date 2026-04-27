namespace Hyperion.DataStructures;

/// <summary>
/// Keeps track of the database statistics like total keys, expired keys, etc.
/// Go source: stats.go
/// </summary>
public static class Stats
{
    public static class HashKeySpaceStat
    {
        public static long Key { get; set; } = 0;
        public static long Expires { get; set; } = 0;
    }
}
