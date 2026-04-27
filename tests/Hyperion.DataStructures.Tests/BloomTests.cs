using Hyperion.DataStructures;
using Xunit;

namespace Hyperion.DataStructures.Tests;

public class BloomTests
{
    [Fact]
    public void Bloom_ShouldAddAndExist()
    {
        var bloom = new Bloom(1000, 0.01);
        bloom.Add("hello");
        bloom.Add("world");

        Assert.True(bloom.Exist("hello"));
        Assert.True(bloom.Exist("world"));
        
        // Probability of false positive is 1% here, so testing a few strings should be fine
        Assert.False(bloom.Exist("not_exist"));
        Assert.False(bloom.Exist("redis"));
    }

    [Fact]
    public void Bloom_AddHashAndExistHash()
    {
        var bloom = new Bloom(100, 0.05);
        var hash = bloom.CalcHash("test_hash");
        
        bloom.AddHash(hash);
        Assert.True(bloom.ExistHash(hash));
        
        var notExistHash = bloom.CalcHash("not_exist_hash");
        Assert.False(bloom.ExistHash(notExistHash));
    }
}
