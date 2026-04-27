using Hyperion.DataStructures;
using Xunit;

namespace Hyperion.DataStructures.Tests;

public class DictTests
{
    [Fact]
    public void SetAndGet_ShouldWork()
    {
        var dict = new Dict();
        var obj = dict.NewObj("key1", "value1", -1);
        dict.Set("key1", obj);

        var retrieved = dict.Get("key1");
        
        Assert.NotNull(retrieved);
        Assert.Equal("value1", retrieved.Value);
    }

    [Fact]
    public void Del_ShouldRemoveKey()
    {
        var dict = new Dict();
        dict.Set("key1", dict.NewObj("key1", "value1", -1));
        
        bool deleted = dict.Del("key1");
        var retrieved = dict.Get("key1");

        Assert.True(deleted);
        Assert.Null(retrieved);
    }

    [Fact]
    public void Expiry_ShouldExpireKey()
    {
        var dict = new Dict();
        dict.Set("key1", dict.NewObj("key1", "value1", 100)); // 100ms TTL
        
        Assert.False(dict.HasExpired("key1"));
        
        Thread.Sleep(150); // Wait for expiry
        
        Assert.True(dict.HasExpired("key1"));
        Assert.Null(dict.Get("key1"));
    }
}
