using System;
using System.Collections.Generic;
using Hyperion.Config;
using Hyperion.Protocol;

namespace Hyperion.Core.Commands;

public class HashCommands
{
    private readonly Storage _storage;
    public HashCommands(Storage storage) => _storage = storage;

    public byte[] HSet(string[] args)
    {
        if (args.Length < 3 || args.Length % 2 != 1)
            return RespEncoder.Encode(new Exception("ERR wrong number of arguments for 'hset' command"));
        string key = args[0];

        if (!_storage.HashStore.TryGetValue(key, out var hash))
        {
            hash = new Dictionary<string, string>();
            _storage.HashStore[key] = hash;
        }

        int added = 0;
        for (int i = 1; i < args.Length; i += 2)
        {
            string field = args[i];
            string value = args[i + 1];
            if (!hash.ContainsKey(field)) added++;
            hash[field] = value;
        }

        return RespEncoder.Encode(added, isSimpleString: false);
    }

    public byte[] HGet(string[] args)
    {
        if (args.Length != 2)
            return RespEncoder.Encode(new Exception("ERR wrong number of arguments for 'hget' command"));
        string key = args[0];
        string field = args[1];

        if (_storage.HashStore.TryGetValue(key, out var hash) &&
            hash.TryGetValue(field, out var value))
            return RespEncoder.Encode(value, isSimpleString: false);

        return Constants.RespNil;
    }

    public byte[] HDel(string[] args)
    {
        if (args.Length < 2)
            return RespEncoder.Encode(new Exception("ERR wrong number of arguments for 'hdel' command"));
        string key = args[0];

        if (!_storage.HashStore.TryGetValue(key, out var hash))
            return RespEncoder.Encode(0, isSimpleString: false);

        int removed = 0;
        for (int i = 1; i < args.Length; i++)
        {
            if (hash.Remove(args[i])) removed++;
        }

        if (hash.Count == 0)
            _storage.HashStore.Remove(key);

        return RespEncoder.Encode(removed, isSimpleString: false);
    }

    public byte[] HGetAll(string[] args)
    {
        if (args.Length != 1)
            return RespEncoder.Encode(new Exception("ERR wrong number of arguments for 'hgetall' command"));
        string key = args[0];

        if (!_storage.HashStore.TryGetValue(key, out var hash))
            return RespEncoder.Encode(Array.Empty<string>());

        var results = new List<string>(hash.Count * 2);
        foreach (var kvp in hash)
        {
            results.Add(kvp.Key);
            results.Add(kvp.Value);
        }

        return RespEncoder.Encode(results.ToArray());
    }
}
