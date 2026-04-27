using System;
using System.Collections.Generic;
using System.Linq;
using Hyperion.Config;
using Hyperion.Protocol;

namespace Hyperion.Core.Commands;

public class ListCommands
{
    private readonly Storage _storage;
    public ListCommands(Storage storage) => _storage = storage;

    public byte[] LPush(string[] args) => Push(args, true);
    public byte[] RPush(string[] args) => Push(args, false);

    private byte[] Push(string[] args, bool left)
    {
        if (args.Length < 2) return RespEncoder.Encode(new Exception("ERR wrong number of arguments for command"));
        string key = args[0];
        
        var list = _storage.ListStore.GetOrAdd(key, _ => new LinkedList<string>());
        int count = 0;
        
        for (int i = 1; i < args.Length; i++)
        {
            if (left) list.AddFirst(args[i]);
            else list.AddLast(args[i]);
        }
        count = list.Count;
        
        return RespEncoder.Encode(count, isSimpleString: false);
    }

    public byte[] LPop(string[] args) => Pop(args, true);
    public byte[] RPop(string[] args) => Pop(args, false);

    private byte[] Pop(string[] args, bool left)
    {
        if (args.Length != 1) return RespEncoder.Encode(new Exception("ERR wrong number of arguments for command"));
        string key = args[0];

        if (!_storage.ListStore.TryGetValue(key, out var list))
        {
            return Constants.RespNil;
        }

        string? value = null;
        if (list.Count > 0)
        {
            if (left)
            {
                value = list.First?.Value;
                list.RemoveFirst();
            }
            else
            {
                value = list.Last?.Value;
                list.RemoveLast();
            }
            
            if (list.Count == 0)
            {
                _storage.ListStore.TryRemove(key, out _);
            }
        }
        
        if (value == null) return Constants.RespNil;
        return RespEncoder.Encode(value, isSimpleString: false);
    }

    public byte[] LRange(string[] args)
    {
        if (args.Length != 3) return RespEncoder.Encode(new Exception("ERR wrong number of arguments for 'lrange' command"));
        string key = args[0];
        
        if (!int.TryParse(args[1], out int start) || !int.TryParse(args[2], out int stop))
        {
            return RespEncoder.Encode(new Exception("ERR value is not an integer or out of range"));
        }

        if (!_storage.ListStore.TryGetValue(key, out var list))
        {
            return RespEncoder.Encode(Array.Empty<string>());
        }

        var results = new List<string>();
        int count = list.Count;
        if (start < 0) start = count + start;
        if (stop < 0) stop = count + stop;
        
        if (start < 0) start = 0;
        if (stop < 0) stop = 0;
        if (start >= count || start > stop) return RespEncoder.Encode(Array.Empty<string>());
        if (stop >= count) stop = count - 1;

        var current = list.First;
        int currentIndex = 0;
        
        while (current != null && currentIndex <= stop)
        {
            if (currentIndex >= start)
            {
                results.Add(current.Value);
            }
            current = current.Next;
            currentIndex++;
        }

        return RespEncoder.Encode(results.ToArray());
    }
}
