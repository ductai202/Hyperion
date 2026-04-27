using Hyperion.Config;
using Hyperion.DataStructures;
using Xunit;

namespace Hyperion.DataStructures.Tests;

public class EvictionPoolTests
{
    [Fact]
    public void Push_ShouldMaintainSortedOrderAndMaxSize()
    {
        var pool = new EvictionPool();
        
        // Push more items than EpoolMaxSize (which is 16)
        for (int i = 0; i < 20; i++)
        {
            // Reverse order insertion to test sorting
            pool.Push($"key{i}", 1000 - i); 
        }

        Assert.Equal(ServerConfig.EpoolMaxSize, pool.Count);

        var oldest = pool.Pop();
        Assert.NotNull(oldest);
        // The oldest items (smallest lastAccessTime) should be at the front.
        // The items pushed last have smallest access time (1000-19 = 981).
        Assert.Equal("key19", oldest.Key);
        Assert.Equal(981, oldest.LastAccessTime);
    }
}
