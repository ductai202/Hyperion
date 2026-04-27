using Hyperion.Config;

namespace Hyperion.DataStructures;

/// <summary>
/// Represents a candidate for eviction in the LRU pool.
/// Go source: eviction_pool.go
/// </summary>
public class EvictionCandidate
{
    public string Key { get; }
    public long LastAccessTime { get; }

    public EvictionCandidate(string key, long lastAccessTime)
    {
        Key = key;
        LastAccessTime = lastAccessTime;
    }
}

/// <summary>
/// Maintains a pool of keys sampled for eviction, sorted by last access time (oldest first).
/// This is an approximate LRU implementation similar to Redis.
/// Go source: eviction_pool.go
/// </summary>
public class EvictionPool
{
    private List<EvictionCandidate> _pool = new();

    public void Push(string key, long lastAccessTime)
    {
        var newItem = new EvictionCandidate(key, lastAccessTime);
        
        // Remove existing entry for the same key if it exists
        int existingIndex = _pool.FindIndex(c => c.Key == key);
        if (existingIndex != -1)
        {
            _pool.RemoveAt(existingIndex);
        }

        // Insert and keep sorted by LastAccessTime (oldest first)
        int insertIndex = _pool.FindIndex(c => c.LastAccessTime > lastAccessTime);
        if (insertIndex == -1)
        {
            _pool.Add(newItem);
        }
        else
        {
            _pool.Insert(insertIndex, newItem);
        }

        // Maintain pool size
        if (_pool.Count > ServerConfig.EpoolMaxSize)
        {
            // Remove the "newest" item (at the end of the pool)
            _pool.RemoveAt(_pool.Count - 1);
        }
    }

    public EvictionCandidate? Pop()
    {
        if (_pool.Count == 0) return null;

        // Return the oldest item (at the beginning)
        var oldest = _pool[0];
        _pool.RemoveAt(0);
        return oldest;
    }

    public int Count => _pool.Count;
}
