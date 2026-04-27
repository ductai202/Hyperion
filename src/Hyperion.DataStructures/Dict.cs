using System.Collections.Concurrent;

namespace Hyperion.DataStructures;

/// <summary>
/// Represents an object stored in the dictionary.
/// Includes the value and the last access time for LRU eviction.
/// </summary>
public class DictObject
{
    public string Key { get; }
    public string Value { get; set; }
    public long LastAccessTime { get; set; }

    public DictObject(string key, string value)
    {
        Key = key;
        Value = value;
        LastAccessTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}

/// <summary>
/// Main dictionary storage for Hyperion.
/// Manages key-value pairs and their expiration.
/// Go source: dict.go
/// </summary>
public class Dict
{
    private readonly ConcurrentDictionary<string, DictObject> _store = new();
    private readonly ConcurrentDictionary<string, long> _expiryStore = new();

    public DictObject NewObj(string key, string value, long ttlMs)
    {
        var obj = new DictObject(key, value);
        if (ttlMs > 0)
        {
            _expiryStore[key] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ttlMs;
        }
        return obj;
    }

    public void Set(string key, DictObject obj)
    {
        if (!_store.ContainsKey(key))
        {
            Stats.HashKeySpaceStat.Key++;
        }
        _store[key] = obj;
    }

    public DictObject? Get(string key)
    {
        if (_store.TryGetValue(key, out var obj))
        {
            obj.LastAccessTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return obj;
        }
        return null;
    }

    public bool Del(string key)
    {
        bool removed = _store.TryRemove(key, out _);
        if (removed)
        {
            Stats.HashKeySpaceStat.Key--;
            _expiryStore.TryRemove(key, out _);
        }
        return removed;
    }

    public bool HasExpired(string key)
    {
        if (_expiryStore.TryGetValue(key, out long expiry))
        {
            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > expiry)
            {
                Del(key);
                return true;
            }
        }
        return false;
    }

    public (long expiry, bool isExpirySet) GetExpiry(string key)
    {
        bool exists = _expiryStore.TryGetValue(key, out long expiry);
        return (expiry, exists);
    }

    public IDictionary<string, long> GetExpireDictStore()
    {
        return _expiryStore;
    }
}
