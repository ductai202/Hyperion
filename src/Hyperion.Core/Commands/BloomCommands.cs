using System;
using System.Globalization;
using Hyperion.Config;
using Hyperion.DataStructures;
using Hyperion.Protocol;

namespace Hyperion.Core.Commands;

public class BloomCommands
{
    private readonly Storage _storage;

    public BloomCommands(Storage storage)
    {
        _storage = storage;
    }

    public byte[] BfReserve(string[] args)
    {
        if (args.Length != 3)
            return RespEncoder.Encode(new Exception("ERR wrong number of arguments for 'BF.RESERVE' command"));

        string key = args[0];
        if (!double.TryParse(args[1], NumberStyles.Any, CultureInfo.InvariantCulture, out double errorRate))
            return RespEncoder.Encode(new Exception("ERR bad error rate"));

        if (!ulong.TryParse(args[2], out ulong capacity))
            return RespEncoder.Encode(new Exception("ERR bad capacity"));

        if (_storage.BloomStore.ContainsKey(key))
            return RespEncoder.Encode(new Exception("ERR item exists"));

        var bloom = new Bloom(capacity, errorRate);
        _storage.BloomStore.TryAdd(key, bloom);
        return Constants.RespOk;
    }

    public byte[] BfMadd(string[] args)
    {
        if (args.Length < 2)
            return RespEncoder.Encode(new Exception("ERR wrong number of arguments for 'BF.MADD' command"));

        string key = args[0];
        // If not exists, RedisBloom usually auto-creates with default parameters,
        // but here we can just create with some defaults or require reserve first.
        // Let's mimic basic behavior: auto create with 100 capacity, 0.01 error rate if missing
        var bloom = _storage.BloomStore.GetOrAdd(key, k => new Bloom(100, 0.01));

        object[] results = new object[args.Length - 1];
        for (int i = 1; i < args.Length; i++)
        {
            string ele = args[i];
            if (bloom.Exist(ele))
            {
                results[i - 1] = 0; // already exists
            }
            else
            {
                bloom.Add(ele);
                results[i - 1] = 1; // newly added
            }
        }

        return RespEncoder.Encode(results); // array of integers
    }

    public byte[] BfExists(string[] args)
    {
        if (args.Length != 2)
            return RespEncoder.Encode(new Exception("ERR wrong number of arguments for 'BF.EXISTS' command"));

        string key = args[0];
        string ele = args[1];

        if (!_storage.BloomStore.TryGetValue(key, out var bloom))
        {
            return Constants.RespZero; // doesn't exist
        }

        bool exists = bloom.Exist(ele);
        return exists ? Constants.RespOne : Constants.RespZero;
    }
}
