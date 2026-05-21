using Hyperion.Core.Commands;
using Hyperion.Protocol;

namespace Hyperion.Core;

public class CommandExecutor : ICommandExecutor
{
    private readonly Storage _storage;
    private readonly StringCommands _stringCommands;
    private readonly SetCommands _setCommands;
    private readonly ZSetCommands _zsetCommands;
    private readonly BloomCommands _bloomCommands;
    private readonly CmsCommands _cmsCommands;
    private readonly HashCommands _hashCommands;
    private readonly ListCommands _listCommands;

    /// <summary>
    /// Optional callback invoked after every write command.
    /// The server wires this to SnapshotCoordinator.NotifyWrite() so the
    /// periodic-save policy can track changes without coupling Core to Persistence.
    /// </summary>
    public Action? OnWriteCommand { get; set; }

    /// <summary>
    /// Optional provider for cluster commands and hash slot routing.
    /// </summary>
    public IClusterProvider? ClusterProvider { get; set; }

    /// <summary>
    /// Optional callback invoked when SAVE is requested.
    /// Returns true on success.
    /// </summary>
    public Func<bool>? OnSave { get; set; }

    /// <summary>
    /// Optional callback invoked when BGSAVE is requested.
    /// Returns a Task that completes when the background save finishes.
    /// </summary>
    public Func<Task<bool>>? OnBgSave { get; set; }

    /// <summary>Returns the Unix timestamp of the last successful save.</summary>
    public Func<long>? GetLastSaveTime { get; set; }

    public CommandExecutor()
    {
        _storage = new Storage();
        _stringCommands = new StringCommands(_storage);
        _setCommands = new SetCommands(_storage);
        _zsetCommands = new ZSetCommands(_storage);
        _bloomCommands = new BloomCommands(_storage);
        _cmsCommands = new CmsCommands(_storage);
        _hashCommands = new HashCommands(_storage);
        _listCommands = new ListCommands(_storage);
    }

    public CommandExecutor(Storage storage)
    {
        _storage = storage;
        _stringCommands = new StringCommands(_storage);
        _setCommands = new SetCommands(_storage);
        _zsetCommands = new ZSetCommands(_storage);
        _bloomCommands = new BloomCommands(_storage);
        _cmsCommands = new CmsCommands(_storage);
        _hashCommands = new HashCommands(_storage);
        _listCommands = new ListCommands(_storage);
    }

    public int DelayUs { get; set; } = 0;

    // Commands that mutate storage — used to fire OnWriteCommand
    private static readonly HashSet<string> WriteCmds =
    [
        "SET", "DEL", "INCR", "DECR",
        "HSET", "HDEL",
        "LPUSH", "RPUSH", "LPOP", "RPOP",
        "SADD", "SREM",
        "ZADD", "ZREM",
        "BF.RESERVE", "BF.MADD",
        "CMS.INITBYDIM", "CMS.INITBYPROB", "CMS.INCRBY"
    ];

    public byte[] Execute(RespCommand command)
    {
        if (DelayUs > 0)
        {
            System.Threading.Thread.Sleep(TimeSpan.FromMicroseconds(DelayUs));
        }

        // Intercept CLUSTER command
        if (command.Cmd == "CLUSTER")
        {
            if (ClusterProvider == null)
            {
                return RespEncoder.Encode(new Exception("ERR This instance has cluster support disabled"));
            }

            if (command.Args.Length >= 2)
            {
                string sub = command.Args[0].ToUpperInvariant();
                if (sub == "GETKEYSINSLOT")
                {
                    if (command.Args.Length != 3 || !int.TryParse(command.Args[1], out int slot) || !int.TryParse(command.Args[2], out int count))
                        return RespEncoder.Encode(new Exception("ERR Invalid arguments for GETKEYSINSLOT"));

                    var keys = new List<string>();
                    foreach (var kv in _storage.DictStore.GetAllEntries())
                    {
                        if (ClusterProvider.GetSlotForKey(kv.Key) == slot)
                        {
                            keys.Add(kv.Key);
                            if (keys.Count >= count) break;
                        }
                    }
                    return RespEncoder.Encode(keys.ToArray());
                }
                else if (sub == "COUNTKEYSINSLOT")
                {
                    if (command.Args.Length != 2 || !int.TryParse(command.Args[1], out int slot))
                        return RespEncoder.Encode(new Exception("ERR Invalid arguments for COUNTKEYSINSLOT"));

                    int count = 0;
                    foreach (var kv in _storage.DictStore.GetAllEntries())
                    {
                        if (ClusterProvider.GetSlotForKey(kv.Key) == slot) count++;
                    }
                    return RespEncoder.Encode(count, isSimpleString: false);
                }
            }

            return ClusterProvider.ExecuteClusterCommand(command);
        }

        // Perform slot routing check if cluster is enabled and the command has keys
        if (ClusterProvider != null && command.Args.Length > 0 && IsDataCommand(command.Cmd))
        {
            var key = command.Cmd == "MIGRATE" && command.Args.Length > 2 ? command.Args[2] : command.Args[0];
            var (isMine, redirectNode, isAsk) = ClusterProvider.CheckSlotOwnership(key, command.IsAsking);
            if (!isMine)
            {
                string prefix = isAsk ? "ASK" : "MOVED";
                return RespEncoder.Encode(new Exception($"{prefix} {redirectNode}"));
            }

            // Cross-slot check for multi-key commands
            if (command.Args.Length > 1)
            {
                int firstSlot = ClusterProvider.GetSlotForKey(key);
                for (int i = 1; i < command.Args.Length; i++)
                {
                    if (IsKeyArg(command.Cmd, i))
                    {
                        if (ClusterProvider.GetSlotForKey(command.Args[i]) != firstSlot)
                        {
                            return RespEncoder.Encode(new Exception("CROSSSLOT Keys in request don't hash to the same slot"));
                        }
                    }
                }
            }
        }

        var result = command.Cmd switch
        {
            "PING"     => _stringCommands.Ping(command.Args),
            "SET"      => _stringCommands.Set(command.Args),
            "GET"      => _stringCommands.Get(command.Args),
            "TTL"      => _stringCommands.Ttl(command.Args),
            "DEL"      => _stringCommands.Del(command.Args),
            "INFO"     => _stringCommands.Info(command.Args),
            "INCR"     => _stringCommands.Incr(command.Args),
            "DECR"     => _stringCommands.Decr(command.Args),
            "SADD"     => _setCommands.Sadd(command.Args),
            "SMEMBERS" => _setCommands.Smembers(command.Args),
            "SISMEMBER"=> _setCommands.Sismember(command.Args),
            "SREM"     => _setCommands.Srem(command.Args),
            "ZADD"     => _zsetCommands.Zadd(command.Args),
            "ZREM"     => _zsetCommands.Zrem(command.Args),
            "ZSCORE"   => _zsetCommands.Zscore(command.Args),
            "ZRANK"    => _zsetCommands.Zrank(command.Args),
            "ZRANGE"   => _zsetCommands.Zrange(command.Args),
            "HSET"     => _hashCommands.HSet(command.Args),
            "HGET"     => _hashCommands.HGet(command.Args),
            "HDEL"     => _hashCommands.HDel(command.Args),
            "HGETALL"  => _hashCommands.HGetAll(command.Args),
            "LPUSH"    => _listCommands.LPush(command.Args),
            "RPUSH"    => _listCommands.RPush(command.Args),
            "LPOP"     => _listCommands.LPop(command.Args),
            "RPOP"     => _listCommands.RPop(command.Args),
            "LRANGE"   => _listCommands.LRange(command.Args),
            "BF.RESERVE"    => _bloomCommands.BfReserve(command.Args),
            "BF.MADD"       => _bloomCommands.BfMadd(command.Args),
            "BF.EXISTS"     => _bloomCommands.BfExists(command.Args),
            "CMS.INITBYDIM" => _cmsCommands.CmsInitByDim(command.Args),
            "CMS.INITBYPROB"=> _cmsCommands.CmsInitByProb(command.Args),
            "CMS.INCRBY"    => _cmsCommands.CmsIncrBy(command.Args),
            "CMS.QUERY"     => _cmsCommands.CmsQuery(command.Args),

            // --- Persistence commands ---
            "SAVE"      => ExecuteSave(),
            "BGSAVE"    => ExecuteBgSave(),
            "LASTSAVE"  => ExecuteLastSave(),
            "DBSIZE"    => ExecuteDbSize(),
            "MIGRATE"   => ExecuteMigrate(command.Args),

            _ => RespEncoder.Encode(new Exception($"ERR unknown command '{command.Cmd}'"))
        };

        // Notify coordinator of write so the save policy can track changes
        if (WriteCmds.Contains(command.Cmd))
            OnWriteCommand?.Invoke();

        return result;
    }

    private byte[] ExecuteSave()
    {
        if (OnSave == null)
            return RespEncoder.Encode(new Exception("ERR persistence is not configured"));

        bool ok = OnSave();
        return ok ? Config.Constants.RespOk : RespEncoder.Encode(new Exception("ERR RDB save failed"));
    }

    private byte[] ExecuteBgSave()
    {
        if (OnBgSave == null)
            return RespEncoder.Encode(new Exception("ERR persistence is not configured"));

        // Fire-and-forget; the background task runs independently
        _ = OnBgSave();
        return RespEncoder.Encode("Background saving started", isSimpleString: true);
    }

    private byte[] ExecuteLastSave()
    {
        long ts = GetLastSaveTime?.Invoke() ?? 0;
        return RespEncoder.Encode(ts, isSimpleString: false);
    }

    private byte[] ExecuteDbSize()
    {
        long count = DataStructures.Stats.HashKeySpaceStat.Key;
        return RespEncoder.Encode(count, isSimpleString: false);
    }

    public void RunActiveExpiry()
    {
        var activeExpiry = new ActiveExpiry(_storage);
        activeExpiry.DeleteExpiredKeys();
    }

    private byte[] ExecuteMigrate(string[] args)
    {
        // MIGRATE host port key "" timeout
        // (For simplicity we ignore destination DB, copy/replace flags for now)
        if (args.Length < 4) return RespEncoder.Encode(new Exception("ERR wrong number of arguments for 'migrate' command"));
        
        string host = args[0];
        if (!int.TryParse(args[1], out int port)) return RespEncoder.Encode(new Exception("ERR invalid port"));
        string key = args[2];
        if (!int.TryParse(args[4], out int timeout)) timeout = 1000;

        var obj = _storage.DictStore.Get(key);
        if (obj == null)
            return RespEncoder.Encode(new Exception("ERR no such key"));

        // Currently we only serialize Strings in MIGRATE for simplicity.
        // For full support we'd serialize other data types.
        if (!(obj.Value is string val))
            return RespEncoder.Encode(new Exception("ERR only string values are supported for MIGRATE currently"));

        long ttlMs = 0; // standard MIGRATE uses 0 if no TTL
        var expiryDict = _storage.DictStore.GetExpireDictStore();
        if (expiryDict.TryGetValue(key, out long expireAt))
        {
            long remain = expireAt - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            ttlMs = remain > 0 ? remain : 0;
        }

        try
        {
            using var client = new System.Net.Sockets.TcpClient(host, port);
            using var stream = client.GetStream();
            stream.ReadTimeout = timeout;
            stream.WriteTimeout = timeout;

            // Send standard RESTORE command
            // Note: Redis RESTORE format is specific (RDB encoded). We'll just use SET with PX for simplicity,
            // as this is a Hyperion-to-Hyperion internal thing and we haven't built an RDB item serializer.
            // Wait, SET doesn't support other types. Since we restricted to strings, SET works.
            var cmdBytes = System.Text.Encoding.UTF8.GetBytes($"*4\r\n$3\r\nSET\r\n${key.Length}\r\n{key}\r\n${val.Length}\r\n{val}\r\n$2\r\nPX\r\n${ttlMs.ToString().Length}\r\n{ttlMs}\r\n");
            if (ttlMs == 0)
                cmdBytes = System.Text.Encoding.UTF8.GetBytes($"*3\r\n$3\r\nSET\r\n${key.Length}\r\n{key}\r\n${val.Length}\r\n{val}\r\n");
            
            stream.Write(cmdBytes, 0, cmdBytes.Length);

            var resBuf = new byte[1024];
            int read = stream.Read(resBuf, 0, resBuf.Length);
            string res = System.Text.Encoding.UTF8.GetString(resBuf, 0, read);

            if (!res.StartsWith("+OK"))
                return RespEncoder.Encode(new Exception("ERR migration failed: target rejected"));

            // Success, delete locally
            _storage.DictStore.Del(key);
            return RespEncoder.Encode("OK", isSimpleString: true);
        }
        catch (Exception ex)
        {
            return RespEncoder.Encode(new Exception($"ERR migration failed: {ex.Message}"));
        }
    }

    private bool IsDataCommand(string cmd)
    {
        return cmd != "PING" && cmd != "INFO" && cmd != "SAVE" && cmd != "BGSAVE" && cmd != "LASTSAVE" && cmd != "DBSIZE";
    }

    private bool IsKeyArg(string cmd, int argIndex)
    {
        // Currently, only DEL takes multiple keys. Other commands take a single key at index 0.
        // MGET, MSET etc. are not yet implemented.
        if (cmd == "DEL") return true;
        return argIndex == 0;
    }
}
