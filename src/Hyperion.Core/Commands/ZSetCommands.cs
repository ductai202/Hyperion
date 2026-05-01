using System;
using System.Globalization;
using Hyperion.Config;
using Hyperion.DataStructures;
using Hyperion.Protocol;

namespace Hyperion.Core.Commands;

public class ZSetCommands
{
    private readonly Storage _storage;

    public ZSetCommands(Storage storage)
    {
        _storage = storage;
    }

    public byte[] Zadd(string[] args)
    {
        if (args.Length < 3 || args.Length % 2 != 1)
            return RespEncoder.Encode(new Exception("ERR wrong number of arguments for 'ZADD' command"));

        string key = args[0];
        if (!_storage.ZSetStore.TryGetValue(key, out var zset))
        {
            zset = new ZSet();
            _storage.ZSetStore[key] = zset;
        }

        int added = 0;
        for (int i = 1; i < args.Length; i += 2)
        {
            if (!double.TryParse(args[i], NumberStyles.Any, CultureInfo.InvariantCulture, out double score))
                return RespEncoder.Encode(new Exception("ERR value is not a valid float"));
            string ele = args[i + 1];
            added += zset.Add(score, ele);
        }

        return RespEncoder.Encode(added, isSimpleString: false);
    }

    public byte[] Zrem(string[] args)
    {
        if (args.Length < 2)
            return RespEncoder.Encode(new Exception("ERR wrong number of arguments for 'ZREM' command"));

        string key = args[0];
        if (!_storage.ZSetStore.TryGetValue(key, out var zset))
            return Constants.RespZero;

        int removed = 0;
        for (int i = 1; i < args.Length; i++)
            removed += zset.Rem(args[i]);

        if (zset.Len() == 0)
            _storage.ZSetStore.Remove(key);

        return RespEncoder.Encode(removed, isSimpleString: false);
    }

    public byte[] Zscore(string[] args)
    {
        if (args.Length != 2)
            return RespEncoder.Encode(new Exception("ERR wrong number of arguments for 'ZSCORE' command"));

        string key = args[0];
        string ele = args[1];

        if (!_storage.ZSetStore.TryGetValue(key, out var zset))
            return Constants.RespNil;

        var (exist, score) = zset.GetScore(ele);
        if (!exist) return Constants.RespNil;

        return RespEncoder.Encode(score.ToString(CultureInfo.InvariantCulture), isSimpleString: false);
    }

    public byte[] Zrank(string[] args)
    {
        if (args.Length != 2)
            return RespEncoder.Encode(new Exception("ERR wrong number of arguments for 'ZRANK' command"));

        string key = args[0];
        string ele = args[1];

        if (!_storage.ZSetStore.TryGetValue(key, out var zset))
            return Constants.RespNil;

        var (rank, _) = zset.GetRank(ele, reverse: false);
        if (rank == -1) return Constants.RespNil;

        return RespEncoder.Encode(rank, isSimpleString: false);
    }

    public byte[] Zrange(string[] args)
    {
        if (args.Length < 3 || args.Length > 4)
            return RespEncoder.Encode(new Exception("ERR wrong number of arguments for 'ZRANGE' command"));

        string key = args[0];
        if (!long.TryParse(args[1], out long start))
            return RespEncoder.Encode(new Exception("ERR value is not an integer or out of range"));
        if (!long.TryParse(args[2], out long stop))
            return RespEncoder.Encode(new Exception("ERR value is not an integer or out of range"));

        if (!_storage.ZSetStore.TryGetValue(key, out var zset))
            return RespEncoder.Encode(Array.Empty<object>());

        var list = zset.GetRange(start, stop, reverse: false);
        return RespEncoder.Encode(list.ToArray());
    }
}
