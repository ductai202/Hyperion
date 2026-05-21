using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Hyperion.Cluster;

namespace Hyperion.Cluster.Tests;

public class GossipEngineTests
{
    [Fact]
    public void CreateMessage_IncludesCorrectStateAndGossipEntries()
    {
        var state = new ClusterState("node1");
        state.Myself.Ip = "127.0.0.1";
        state.Myself.Port = 7000;
        state.Myself.ClusterBusPort = 17000;
        state.CurrentEpoch = 5;
        state.Myself.ConfigEpoch = 2;

        var node2 = new ClusterNode("node2") { Ip = "127.0.0.2", Port = 7001, ClusterBusPort = 17001 };
        state.Nodes["node2"] = node2;

        var engine = new GossipEngine(state, NullLogger.Instance);
        var msg = engine.CreateMessage(ClusterMessageType.Ping);

        Assert.Equal(ClusterMessageType.Ping, msg.Type);
        Assert.Equal("node1", msg.SenderNodeId);
        Assert.Equal(5UL, msg.CurrentEpoch);
        Assert.Equal(2UL, msg.ConfigEpoch);
        
        Assert.Single(msg.GossipEntries);
        Assert.Equal("node2", msg.GossipEntries[0].NodeId);
    }
}
