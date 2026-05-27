using System.Text;
using Hyperion.Core;
using Hyperion.Protocol;
using Xunit;

namespace Hyperion.Core.Tests;

public class StringCommandsTests
{
    private readonly CommandExecutor _executor;

    public StringCommandsTests()
    {
        _executor = new CommandExecutor();
    }

    private string ExecuteStringCommand(string cmd, params string[] args)
    {
        var command = new RespCommand { Cmd = cmd, Args = args };
        var responseBytes = _executor.Execute(command);
        return Encoding.UTF8.GetString(responseBytes);
    }

    [Fact]
    public void SetAndGet_ShouldWork()
    {
        var setRes = ExecuteStringCommand("SET", "key1", "value1");
        Assert.Equal("+OK\r\n", setRes);

        var getRes = ExecuteStringCommand("GET", "key1");
        Assert.Equal("$6\r\nvalue1\r\n", getRes);
    }

    [Fact]
    public void Get_NonExistentKey_ShouldReturnNil()
    {
        var getRes = ExecuteStringCommand("GET", "nonexistent");
        Assert.Equal("$-1\r\n", getRes);
    }

    [Fact]
    public void Set_WithEx_ShouldExpire()
    {
        var setRes = ExecuteStringCommand("SET", "key2", "value2", "EX", "1");
        Assert.Equal("+OK\r\n", setRes);

        Thread.Sleep(1100);

        var getRes = ExecuteStringCommand("GET", "key2");
        Assert.Equal("$-1\r\n", getRes);
    }

    [Fact]
    public void Del_ShouldWork()
    {
        ExecuteStringCommand("SET", "key3", "value3");
        var delRes = ExecuteStringCommand("DEL", "key3");
        Assert.Equal(":1\r\n", delRes);

        var getRes = ExecuteStringCommand("GET", "key3");
        Assert.Equal("$-1\r\n", getRes);
    }

    [Fact]
    public void Ttl_ShouldReturnRemainingTime()
    {
        ExecuteStringCommand("SET", "key4", "value4", "EX", "10");

        var ttlRes = ExecuteStringCommand("TTL", "key4");
        Assert.StartsWith(":", ttlRes);
        Assert.EndsWith("\r\n", ttlRes);
        Assert.NotEqual(":-1\r\n", ttlRes);
        Assert.NotEqual(":-2\r\n", ttlRes);
    }

    [Fact]
    public void Incr_And_Decr_ShouldWork()
    {
        ExecuteStringCommand("SET", "counter", "10");

        var incrRes = ExecuteStringCommand("INCR", "counter");
        Assert.Equal(":11\r\n", incrRes);

        var decrRes = ExecuteStringCommand("DECR", "counter");
        Assert.Equal(":10\r\n", decrRes);
    }

    [Fact]
    public void Info_ShouldReturnServerInfo()
    {
        var infoRes = ExecuteStringCommand("INFO");
        Assert.Contains("# Server", infoRes);
        Assert.Contains("redis_version:7.0.0-hyperion", infoRes);
        Assert.Contains("# Memory", infoRes);
        Assert.Contains("# Keyspace", infoRes);
    }

    [Fact]
    public void LruEviction_ShouldEvictOldestKey()
    {
        int originalMaxKeys = Config.ServerConfig.MaxKeyNumber;
        int originalListeners = Config.ServerConfig.ListenerNumber;
        string originalPolicy = Config.ServerConfig.EvictionPolicy;
        int originalSampleSize = Config.ServerConfig.EpoolLruSampleSize;

        try
        {
            Config.ServerConfig.MaxKeyNumber = 5;
            Config.ServerConfig.ListenerNumber = 1;
            Config.ServerConfig.EvictionPolicy = "allkeys-lru";
            Config.ServerConfig.EpoolLruSampleSize = 20;

            var localExecutor = new CommandExecutor();

            string Exec(string cmd, params string[] args)
            {
                var command = new RespCommand { Cmd = cmd, Args = args };
                var responseBytes = localExecutor.Execute(command);
                return Encoding.UTF8.GetString(responseBytes);
            }

            for (int i = 1; i <= 5; i++)
            {
                Exec("SET", $"key_{i}", $"value_{i}");
                Thread.Sleep(15);
            }

            Exec("GET", "key_1");

            Exec("SET", "key_6", "value_6");

            var get2 = Exec("GET", "key_2");
            var get1 = Exec("GET", "key_1");
            var get6 = Exec("GET", "key_6");

            Assert.Equal("$-1\r\n", get2);
            Assert.Equal("$7\r\nvalue_1\r\n", get1);
            Assert.Equal("$7\r\nvalue_6\r\n", get6);
        }
        finally
        {
            Config.ServerConfig.MaxKeyNumber = originalMaxKeys;
            Config.ServerConfig.ListenerNumber = originalListeners;
            Config.ServerConfig.EvictionPolicy = originalPolicy;
            Config.ServerConfig.EpoolLruSampleSize = originalSampleSize;
        }
    }
}
