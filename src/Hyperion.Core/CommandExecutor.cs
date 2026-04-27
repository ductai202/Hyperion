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

    public CommandExecutor()
    {
        _storage = new Storage();
        _stringCommands = new StringCommands(_storage);
        _setCommands = new SetCommands(_storage);
        _zsetCommands = new ZSetCommands(_storage);
        _bloomCommands = new BloomCommands(_storage);
    }

    public CommandExecutor(Storage storage)
    {
        _storage = storage;
        _stringCommands = new StringCommands(_storage);
        _setCommands = new SetCommands(_storage);
        _zsetCommands = new ZSetCommands(_storage);
        _bloomCommands = new BloomCommands(_storage);
    }

    public byte[] Execute(RespCommand command)
    {
        return command.Cmd switch
        {
            "PING" => _stringCommands.Ping(command.Args),
            "SET"  => _stringCommands.Set(command.Args),
            "GET"  => _stringCommands.Get(command.Args),
            "TTL"  => _stringCommands.Ttl(command.Args),
            "DEL"  => _stringCommands.Del(command.Args),
            "INFO" => _stringCommands.Info(command.Args),
            "SADD" => _setCommands.Sadd(command.Args),
            "SMEMBERS" => _setCommands.Smembers(command.Args),
            "SISMEMBER" => _setCommands.Sismember(command.Args),
            "SREM" => _setCommands.Srem(command.Args),
            "ZADD" => _zsetCommands.Zadd(command.Args),
            "ZREM" => _zsetCommands.Zrem(command.Args),
            "ZSCORE" => _zsetCommands.Zscore(command.Args),
            "ZRANK" => _zsetCommands.Zrank(command.Args),
            "ZRANGE" => _zsetCommands.Zrange(command.Args),
            "BF.RESERVE" => _bloomCommands.BfReserve(command.Args),
            "BF.MADD" => _bloomCommands.BfMadd(command.Args),
            "BF.EXISTS" => _bloomCommands.BfExists(command.Args),
            _ => RespEncoder.Encode(new Exception($"ERR unknown command '{command.Cmd}'"))
        };
    }



    public void RunActiveExpiry()
    {
        var activeExpiry = new ActiveExpiry(_storage);
        activeExpiry.DeleteExpiredKeys();
    }
}
