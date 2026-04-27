using System.Collections.Concurrent;

namespace Hyperion.DataStructures;

public class SimpleSet
{
    public string Key { get; }
    private readonly ConcurrentDictionary<string, byte> _dict = new();

    public SimpleSet(string key)
    {
        Key = key;
    }

    public int Add(params string[] members)
    {
        int added = 0;
        foreach (var m in members)
        {
            if (_dict.TryAdd(m, 0)) added++;
        }
        return added;
    }

    public int Rem(params string[] members)
    {
        int removed = 0;
        foreach (var m in members)
        {
            if (_dict.TryRemove(m, out _)) removed++;
        }
        return removed;
    }

    public int IsMember(string member)
    {
        return _dict.ContainsKey(member) ? 1 : 0;
    }

    public string[] Members()
    {
        return _dict.Keys.ToArray();
    }
}
