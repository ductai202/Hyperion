using Hyperion.DataStructures;
using Xunit;

namespace Hyperion.DataStructures.Tests;

public class SimpleSetTests
{
    [Fact]
    public void Add_ShouldAddMembers()
    {
        var set = new SimpleSet("myset");
        int added = set.Add("a", "b", "c");
        Assert.Equal(3, added);
        Assert.Equal(1, set.Add("a", "d"));
        Assert.Equal(4, set.Members().Length);
    }

    [Fact]
    public void Rem_ShouldRemoveMembers()
    {
        var set = new SimpleSet("myset");
        set.Add("a", "b", "c");
        Assert.Equal(2, set.Rem("b", "c", "d"));
        Assert.Single(set.Members());
    }

    [Fact]
    public void IsMember_ShouldReturnCorrectly()
    {
        var set = new SimpleSet("myset");
        set.Add("a");
        Assert.Equal(1, set.IsMember("a"));
        Assert.Equal(0, set.IsMember("b"));
    }
}
