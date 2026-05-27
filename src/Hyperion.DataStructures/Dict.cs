using System.Collections.Generic;
using System.Threading;

namespace Hyperion.DataStructures;

/// <summary>
/// Coarse-grained clock refreshed by a timer every 10ms.
/// Eliminates DateTimeOffset.UtcNow syscall from the hot path (GET, SET, INCR, etc.).
/// Redis uses the same approach: server.unixtime is refreshed once per event-loop tick.
/// </summary>
public static class CoarseClock
{
    private static long _nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static readonly Timer _timer = new(_ =>
        Interlocked.Exchange(ref _nowMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
        null, 10, 10);

    public static long NowMs => Interlocked.Read(ref _nowMs);
}

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
        LastAccessTime = CoarseClock.NowMs;
    }
}

/// <summary>
/// Core dictionary structure for storing key-value pairs with TTL and eviction support.
/// Uses plain Dictionary because each Worker owns a private instance (share-nothing model).
/// No cross-thread access ever occurs — thread safety is guaranteed by the routing layer.
/// </summary>
public class Dict
{
    private readonly Dictionary<string, DictObject> _store = new();
    private readonly Dictionary<string, long> _expiryStore = new();
    private readonly List<string> _keyList = new();
    private readonly Dictionary<string, int> _keyIndexMap = new();

    public int Count => _store.Count;

    public string? GetRandomKey()
    {
        if (_keyList.Count == 0) return null;
        int idx = Random.Shared.Next(_keyList.Count);
        return _keyList[idx];
    }

    public DictObject NewObj(string key, string value, long ttlMs)
    {
        var obj = new DictObject(key, value);
        if (ttlMs > 0)
        {
            if (!_expiryStore.ContainsKey(key))
            {
                Stats.HashKeySpaceStat.IncrementExpires();
            }
            _expiryStore[key] = CoarseClock.NowMs + ttlMs;
        }
        return obj;
    }

    public void Set(string key, DictObject obj)
    {
        if (!_store.ContainsKey(key))
        {
            Stats.HashKeySpaceStat.IncrementKey();
            _keyList.Add(key);
            _keyIndexMap[key] = _keyList.Count - 1;
        }
        _store[key] = obj;
    }

    public DictObject? Get(string key)
    {
        if (_store.TryGetValue(key, out var obj))
        {
            obj.LastAccessTime = CoarseClock.NowMs;
            return obj;
        }
        return null;
    }

    public DictObject? Peek(string key)
    {
        if (_store.TryGetValue(key, out var obj))
        {
            return obj;
        }
        return null;
    }

    public bool Del(string key)
    {
        bool removed = _store.Remove(key);
        if (removed)
        {
            Stats.HashKeySpaceStat.DecrementKey();
            if (_expiryStore.Remove(key))
            {
                Stats.HashKeySpaceStat.DecrementExpires();
            }

            if (_keyIndexMap.TryGetValue(key, out int index))
            {
                int lastIndex = _keyList.Count - 1;
                if (index != lastIndex)
                {
                    string lastKey = _keyList[lastIndex];
                    _keyList[index] = lastKey;
                    _keyIndexMap[lastKey] = index;
                }
                _keyList.RemoveAt(lastIndex);
                _keyIndexMap.Remove(key);
            }
        }
        return removed;
    }

    public bool HasExpired(string key)
    {
        if (_expiryStore.TryGetValue(key, out long expiry))
        {
            if (CoarseClock.NowMs > expiry)
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

    /// <summary>
    /// Returns a snapshot of the expiry store keys for safe iteration.
    /// Plain Dictionary is not safe to enumerate while modifying, so we snapshot.
    /// </summary>
    public IReadOnlyDictionary<string, long> GetExpireDictStore()
    {
        return _expiryStore;
    }

    /// <summary>
    /// Returns all key-object pairs in the store for RDB serialization.
    /// Callers must filter expired keys themselves using GetExpireDictStore().
    /// </summary>
    public IEnumerable<KeyValuePair<string, DictObject>> GetAllEntries()
    {
        return _store;
    }

    /// <summary>
    /// Returns all currently live (non-expired) keys matching the given glob pattern.
    /// Expired keys are lazily deleted on-the-fly during this scan — same as Redis KEYS behavior.
    /// Pattern supports: '*' (any chars), '?' (one char), '[abc]' (char class).
    /// </summary>
    public List<string> GetLiveKeys(string pattern)
    {
        var result = new List<string>();
        // snapshot to avoid mutation-during-iteration when Del() is called below
        var snapshot = new List<string>(_keyList);
        long nowMs = CoarseClock.NowMs;

        foreach (var key in snapshot)
        {
            // Lazy-delete expired keys found during scan
            if (_expiryStore.TryGetValue(key, out long expiry) && nowMs > expiry)
            {
                Del(key);
                continue;
            }

            if (GlobMatch(pattern, key))
                result.Add(key);
        }
        return result;
    }

    /// <summary>
    /// Simple glob-style pattern matching. Supports '*', '?', and '[...]' character classes.
    /// Ported from Redis's stringmatchlen() in util.c.
    /// </summary>
    private static bool GlobMatch(string pattern, string str)
    {
        int p = 0, s = 0;
        while (p < pattern.Length && s < str.Length)
        {
            char pc = pattern[p];
            if (pc == '*')
            {
                // Skip consecutive stars
                while (p < pattern.Length && pattern[p] == '*') p++;
                if (p == pattern.Length) return true;
                while (s < str.Length)
                {
                    if (GlobMatch(pattern[p..], str[s..])) return true;
                    s++;
                }
                return false;
            }
            else if (pc == '?')
            {
                p++; s++;
            }
            else if (pc == '[')
            {
                p++; // skip '['
                bool not = p < pattern.Length && pattern[p] == '^';
                if (not) p++;
                bool matched = false;
                while (p < pattern.Length && pattern[p] != ']')
                {
                    if (p + 2 < pattern.Length && pattern[p + 1] == '-')
                    {
                        if (str[s] >= pattern[p] && str[s] <= pattern[p + 2])
                            matched = true;
                        p += 3;
                    }
                    else
                    {
                        if (str[s] == pattern[p]) matched = true;
                        p++;
                    }
                }
                if (p < pattern.Length) p++; // skip ']'
                if (matched == not) return false;
                s++;
            }
            else
            {
                if (pc != str[s]) return false;
                p++; s++;
            }
        }
        // Skip trailing stars
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length && s == str.Length;
    }
}
