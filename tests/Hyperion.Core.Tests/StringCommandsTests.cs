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
        var setRes = ExecuteStringCommand("SET", "key2", "value2", "EX", "1"); // expire in 1 sec
        Assert.Equal("+OK\r\n", setRes);

        // Sleep to let it expire
        Thread.Sleep(1100);

        var getRes = ExecuteStringCommand("GET", "key2");
        Assert.Equal("$-1\r\n", getRes); // Should be nil now
    }

    [Fact]
    public void Del_ShouldWork()
    {
        ExecuteStringCommand("SET", "key3", "value3");
        var delRes = ExecuteStringCommand("DEL", "key3");
        
        Assert.Equal(":1\r\n", delRes); // 1 key deleted

        var getRes = ExecuteStringCommand("GET", "key3");
        Assert.Equal("$-1\r\n", getRes); // nil
    }

    [Fact]
    public void Ttl_ShouldReturnRemainingTime()
    {
        ExecuteStringCommand("SET", "key4", "value4", "EX", "10"); // 10 seconds

        var ttlRes = ExecuteStringCommand("TTL", "key4");
        // Output should be roughly :10\r\n (could be 9 due to slight delays, we just check it's a number)
        Assert.StartsWith(":", ttlRes);
        Assert.EndsWith("\r\n", ttlRes);
        Assert.NotEqual(":-1\r\n", ttlRes); // Not "no expire"
        Assert.NotEqual(":-2\r\n", ttlRes); // Not "not exists"
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
}
