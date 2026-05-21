using System;
using Xunit;
using Hyperion.Cluster;

namespace Hyperion.Cluster.Tests;

public class Crc16Tests
{
    [Theory]
    [InlineData("123456789", 0x31C3)]
    [InlineData("hello", 50018)] 
    [InlineData("foo", 44950)]   
    public void Compute_ReturnsCorrectHash(string key, ushort expected)
    {
        ushort hash = Crc16.Compute(key);
        Assert.Equal(expected, hash);
    }

    [Theory]
    [InlineData("foo", 12182)] 
    [InlineData("hello", 866)] 
    public void GetSlotForKey_ReturnsCorrectSlot(string key, int expectedSlot)
    {
        int slot = ClusterState.GetSlotForKey(key);
        Assert.Equal(expectedSlot, slot);
    }

    [Fact]
    public void GetSlotForKey_RespectsHashTags()
    {
        int slot1 = ClusterState.GetSlotForKey("{user:1000}:name");
        int slot2 = ClusterState.GetSlotForKey("{user:1000}:age");
        int slot3 = ClusterState.GetSlotForKey("user:1000"); // Not same, because hash tag extracts the whole string if no tags? No, it extracts "user:1000"

        Assert.Equal(slot1, slot2);
        
        int slotRaw = ClusterState.GetSlotForKey("user:1000");
        Assert.Equal(slotRaw, slot1);
    }
}
