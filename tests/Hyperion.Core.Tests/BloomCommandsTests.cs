using System.Text;
using Hyperion.Core;
using Hyperion.Protocol;
using Xunit;

namespace Hyperion.Core.Tests;

public class BloomCommandsTests
{
    private readonly CommandExecutor _executor;
    
    public BloomCommandsTests()
    {
        _executor = new CommandExecutor();
    }

    private string ExecuteCommand(string cmd, params string[] args)
    {
        var responseBytes = _executor.Execute(new RespCommand { Cmd = cmd, Args = args });
        return Encoding.UTF8.GetString(responseBytes);
    }

    [Fact]
    public void BfReserve_ShouldCreateBloomFilter()
    {
        var res1 = ExecuteCommand("BF.RESERVE", "mybloom", "0.01", "100");
        Assert.Equal("+OK\r\n", res1);

        var res2 = ExecuteCommand("BF.RESERVE", "mybloom", "0.01", "100");
        Assert.Equal("-ERR item exists\r\n", res2);
    }

    [Fact]
    public void BfMadd_ShouldAddElements()
    {
        // Add multiple elements, should return array of 1s (newly added)
        var res = ExecuteCommand("BF.MADD", "mybloom2", "item1", "item2");
        Assert.StartsWith("*2\r\n", res);
        Assert.Contains(":1\r\n", res);

        // Add same elements, should return array of 0s (already exist)
        var res2 = ExecuteCommand("BF.MADD", "mybloom2", "item1");
        Assert.Equal("*1\r\n:0\r\n", res2);
    }

    [Fact]
    public void BfExists_ShouldCheckExistence()
    {
        ExecuteCommand("BF.MADD", "mybloom3", "item1");
        
        Assert.Equal(":1\r\n", ExecuteCommand("BF.EXISTS", "mybloom3", "item1"));
        Assert.Equal(":0\r\n", ExecuteCommand("BF.EXISTS", "mybloom3", "item2")); // doesn't exist
        Assert.Equal(":0\r\n", ExecuteCommand("BF.EXISTS", "not_exist_key", "item1")); // key doesn't exist
    }
}
