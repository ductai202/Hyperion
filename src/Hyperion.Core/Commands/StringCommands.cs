using System.Text;
using Hyperion.Config;
using Hyperion.DataStructures;
using Hyperion.Protocol;

namespace Hyperion.Core.Commands;

public class StringCommands
{
    private readonly Storage _storage;
    public StringCommands(Storage storage) => _storage = storage;

    public byte[] Ping(string[] args)
    {
        if (args.Length > 1) return RespEncoder.Encode(new Exception("ERR wrong number of arguments for 'ping' command"));
        if (args.Length == 0) return RespEncoder.Encode("PONG", isSimpleString: true);
        return RespEncoder.Encode(args[0], isSimpleString: false);
    }

    public byte[] Set(string[] args)
    {
        if (args.Length < 2 || args.Length == 3 || args.Length > 4) return RespEncoder.Encode(new Exception("ERR wrong number of arguments for 'SET' command"));
        string key = args[0];
        string value = args[1];
        long ttlMs = -1;
        if (args.Length > 2)
        {
            if (args[2].ToUpperInvariant() != "EX") return RespEncoder.Encode(new Exception("ERR syntax error"));
            if (!long.TryParse(args[3], out long ttlSec)) return RespEncoder.Encode(new Exception("ERR value is not an integer or out of range"));
            ttlMs = ttlSec * 1000;
        }
        var obj = _storage.DictStore.NewObj(key, value, ttlMs);
        _storage.DictStore.Set(key, obj);
        return Constants.RespOk;
    }

    public byte[] Get(string[] args)
    {
        if (args.Length != 1) return RespEncoder.Encode(new Exception("ERR wrong number of arguments for 'GET' command"));
        string key = args[0];
        var obj = _storage.DictStore.Get(key);
        if (obj == null || _storage.DictStore.HasExpired(key)) return Constants.RespNil;
        return RespEncoder.Encode(obj.Value, isSimpleString: false);
    }

    public byte[] Ttl(string[] args)
    {
        if (args.Length != 1) return RespEncoder.Encode(new Exception("ERR wrong number of arguments for 'TTL' command"));
        string key = args[0];
        var obj = _storage.DictStore.Get(key);
        if (obj == null) return Constants.TtlKeyNotExist;
        var (expiry, isExpirySet) = _storage.DictStore.GetExpiry(key);
        if (!isExpirySet) return Constants.TtlKeyExistNoExpire;
        long remainMs = expiry - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (remainMs < 0)
        {
            _storage.DictStore.Del(key);
            return Constants.TtlKeyNotExist;
        }
        return RespEncoder.Encode(remainMs / 1000, isSimpleString: false);
    }

    public byte[] Del(string[] args)
    {
        if (args.Length == 0) return RespEncoder.Encode(new Exception("ERR wrong number of arguments for 'DEL' command"));
        long count = 0;
        foreach (var key in args)
        {
            if (_storage.DictStore.Del(key)) count++;
        }
        return RespEncoder.Encode(count, isSimpleString: false);
    }

    public byte[] Info(string[] args)
    {
        var sb = new StringBuilder();
        
        sb.Append("# Server\r\n");
        sb.Append("redis_version:7.0.0-hyperion\r\n");
        sb.Append($"os:{Environment.OSVersion}\r\n");
        sb.Append($"process_id:{Environment.ProcessId}\r\n");
        sb.Append($"uptime_in_seconds:{(long)(DateTimeOffset.UtcNow - Stats.ServerStartTime).TotalSeconds}\r\n");
        sb.Append($"arch_bits:{(Environment.Is64BitProcess ? 64 : 32)}\r\n");

        sb.Append("\r\n# Memory\r\n");
        long usedMemory = GC.GetTotalMemory(false);
        sb.Append($"used_memory:{usedMemory}\r\n");
        sb.Append($"used_memory_human:{usedMemory / 1024.0 / 1024.0:F2}M\r\n");
        sb.Append($"used_memory_peak:{usedMemory}\r\n"); // Simplified

        sb.Append("\r\n# Keyspace\r\n");
        sb.Append($"db0:keys={Stats.HashKeySpaceStat.Key},expires={Stats.HashKeySpaceStat.Expires},avg_ttl=0\r\n");

        return RespEncoder.Encode(sb.ToString(), isSimpleString: false);
    }

    public byte[] Incr(string[] args) => IncrBy(args, 1);
    public byte[] Decr(string[] args) => IncrBy(args, -1);

    private byte[] IncrBy(string[] args, long delta)
    {
        if (args.Length != 1) return RespEncoder.Encode(new Exception($"ERR wrong number of arguments for command"));
        string key = args[0];
        
        var obj = _storage.DictStore.Get(key);
        long value = 0;
        
        if (obj != null && !_storage.DictStore.HasExpired(key))
        {
            if (!long.TryParse(obj.Value, out value))
            {
                return RespEncoder.Encode(new Exception("ERR value is not an integer or out of range"));
            }
        }
        
        value += delta;
        var newObj = _storage.DictStore.NewObj(key, value.ToString(), -1);
        
        // Preserve TTL if it exists
        var (expiry, isExpirySet) = _storage.DictStore.GetExpiry(key);
        if (isExpirySet)
        {
            long remainMs = expiry - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (remainMs > 0)
            {
                newObj = _storage.DictStore.NewObj(key, value.ToString(), remainMs);
            }
        }

        _storage.DictStore.Set(key, newObj);
        return RespEncoder.Encode(value, isSimpleString: false);
    }
}
