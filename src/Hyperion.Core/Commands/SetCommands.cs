using System;
using System.Linq;
using Hyperion.Config;
using Hyperion.DataStructures;
using Hyperion.Protocol;

namespace Hyperion.Core.Commands;

public class SetCommands
{
    private readonly Storage _storage;

    public SetCommands(Storage storage)
    {
        _storage = storage;
    }

    public byte[] Sadd(string[] args)
    {
        if (args.Length < 2)
            return RespEncoder.Encode(new Exception("ERR wrong number of arguments for 'SADD' command"));
        string key = args[0];
        string[] members = args.Skip(1).ToArray();

        if (!_storage.SetStore.TryGetValue(key, out var set))
        {
            set = new SimpleSet(key);
            _storage.SetStore[key] = set;
        }

        int added = set.Add(members);
        return RespEncoder.Encode(added, isSimpleString: false);
    }

    public byte[] Smembers(string[] args)
    {
        if (args.Length != 1)
            return RespEncoder.Encode(new Exception("ERR wrong number of arguments for 'SMEMBERS' command"));
        string key = args[0];

        if (!_storage.SetStore.TryGetValue(key, out var set))
            return RespEncoder.Encode(Array.Empty<object>());

        return RespEncoder.Encode(set.Members());
    }

    public byte[] Sismember(string[] args)
    {
        if (args.Length != 2)
            return RespEncoder.Encode(new Exception("ERR wrong number of arguments for 'SISMEMBER' command"));
        string key = args[0];
        string member = args[1];

        if (!_storage.SetStore.TryGetValue(key, out var set))
            return Constants.RespZero;

        return set.IsMember(member) == 1 ? Constants.RespOne : Constants.RespZero;
    }

    public byte[] Srem(string[] args)
    {
        if (args.Length < 2)
            return RespEncoder.Encode(new Exception("ERR wrong number of arguments for 'SREM' command"));
        string key = args[0];
        string[] members = args.Skip(1).ToArray();

        if (!_storage.SetStore.TryGetValue(key, out var set))
            return Constants.RespZero;

        int removed = set.Rem(members);
        if (set.Members().Length == 0)
            _storage.SetStore.Remove(key);

        return RespEncoder.Encode(removed, isSimpleString: false);
    }
}
