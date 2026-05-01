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

        _storage.BloomStore[key] = new Bloom(capacity, errorRate);
        return Constants.RespOk;
    }

    public byte[] BfMadd(string[] args)
    {
        if (args.Length < 2)
            return RespEncoder.Encode(new Exception("ERR wrong number of arguments for 'BF.MADD' command"));

        string key = args[0];
        if (!_storage.BloomStore.TryGetValue(key, out var bloom))
        {
            bloom = new Bloom(100, 0.01);
            _storage.BloomStore[key] = bloom;
        }

        object[] results = new object[args.Length - 1];
        for (int i = 1; i < args.Length; i++)
        {
            string ele = args[i];
            if (bloom.Exist(ele))
            {
                results[i - 1] = 0;
            }
            else
            {
                bloom.Add(ele);
                results[i - 1] = 1;
            }
        }

        return RespEncoder.Encode(results);
    }

    public byte[] BfExists(string[] args)
    {
        if (args.Length != 2)
            return RespEncoder.Encode(new Exception("ERR wrong number of arguments for 'BF.EXISTS' command"));

        string key = args[0];
        string ele = args[1];

        if (!_storage.BloomStore.TryGetValue(key, out var bloom))
            return Constants.RespZero;

        return bloom.Exist(ele) ? Constants.RespOne : Constants.RespZero;
    }
}
