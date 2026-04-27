using System.Text;
using Hyperion.Core;
using Hyperion.Protocol;
using Xunit;

namespace Hyperion.Core.Tests;

public class SetCommandsTests
{
    private readonly CommandExecutor _executor;
    public SetCommandsTests()
    {
        _executor = new CommandExecutor();
    }

    private string ExecuteCommand(string cmd, params string[] args)
    {
        var responseBytes = _executor.Execute(new RespCommand { Cmd = cmd, Args = args });
        return Encoding.UTF8.GetString(responseBytes);
    }

    [Fact]
    public void Sadd_ShouldAddMembers()
    {
        Assert.Equal(":3\r\n", ExecuteCommand("SADD", "myset", "a", "b", "c"));
        Assert.Equal(":1\r\n", ExecuteCommand("SADD", "myset", "a", "d"));
    }

    [Fact]
    public void Smembers_ShouldReturnAllMembers()
    {
        ExecuteCommand("SADD", "myset", "a", "b");
        var res = ExecuteCommand("SMEMBERS", "myset");
        Assert.StartsWith("*2\r\n", res);
        Assert.Contains("$1\r\na\r\n", res);
        Assert.Contains("$1\r\nb\r\n", res);
    }

    [Fact]
    public void Sismember_ShouldReturnCorrectly()
    {
        ExecuteCommand("SADD", "myset", "a");
        Assert.Equal(":1\r\n", ExecuteCommand("SISMEMBER", "myset", "a"));
        Assert.Equal(":0\r\n", ExecuteCommand("SISMEMBER", "myset", "b"));
    }

    [Fact]
    public void Srem_ShouldRemoveMembers()
    {
        ExecuteCommand("SADD", "myset", "a", "b", "c");
        Assert.Equal(":1\r\n", ExecuteCommand("SREM", "myset", "b", "d"));
        Assert.Equal(":0\r\n", ExecuteCommand("SISMEMBER", "myset", "b"));
    }
}
