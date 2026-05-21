using System;
using Xunit;
using Hyperion.Cluster;

namespace Hyperion.Cluster.Tests;

public class ClusterStateTests
{
    [Fact]
    public void UpdateClusterStatus_SetsOk_WhenAllSlotsAssignedToActiveNodes()
    {
        var state = new ClusterState("node1");
        state.Nodes["node1"].Flags = ClusterNodeFlags.Master | ClusterNodeFlags.Myself;

        // Assign all slots to node1
        for (int i = 0; i < 16384; i++)
        {
            state.SlotTable[i] = "node1";
        }

        state.UpdateClusterStatus();
        Assert.Equal(ClusterStatus.Ok, state.Status);
    }

    [Fact]
    public void UpdateClusterStatus_SetsFail_WhenSlotUnassigned()
    {
        var state = new ClusterState("node1");
        state.Nodes["node1"].Flags = ClusterNodeFlags.Master | ClusterNodeFlags.Myself;

        // Assign all but one slot
        for (int i = 0; i < 16383; i++)
        {
            state.SlotTable[i] = "node1";
        }
        state.SlotTable[16383] = null;

        state.UpdateClusterStatus();
        Assert.Equal(ClusterStatus.Fail, state.Status);
    }

    [Fact]
    public void UpdateClusterStatus_SetsFail_WhenSlotOwnerIsFail()
    {
        var state = new ClusterState("node1");
        
        var node2 = new ClusterNode("node2") { Flags = ClusterNodeFlags.Master | ClusterNodeFlags.Fail };
        state.Nodes["node2"] = node2;

        for (int i = 0; i < 16384; i++)
        {
            state.SlotTable[i] = "node2";
        }

        state.UpdateClusterStatus();
        Assert.Equal(ClusterStatus.Fail, state.Status);
    }

    [Fact]
    public void SaveConfig_LoadConfig_PreservesState()
    {
        string tmpFile = System.IO.Path.GetTempFileName();
        try
        {
            var state = new ClusterState("node_myself");
            state.Myself.Ip = "192.168.1.10";
            state.Myself.Port = 6379;
            state.Myself.ClusterBusPort = 16379;
            state.Myself.ConfigEpoch = 5;
            state.CurrentEpoch = 10;
            
            // Assign slots 0-100 to myself
            for (int i = 0; i <= 100; i++)
            {
                state.SlotTable[i] = "node_myself";
                state.Myself.Slots.Set(i, true);
            }

            // Add another node
            var node2 = new ClusterNode("node_other");
            node2.Ip = "192.168.1.11";
            node2.Port = 6380;
            node2.ClusterBusPort = 16380;
            node2.ConfigEpoch = 4;
            node2.Flags = ClusterNodeFlags.Master;
            state.Nodes["node_other"] = node2;
            
            // Assign slots 101-200 to node2
            for (int i = 101; i <= 200; i++)
            {
                state.SlotTable[i] = "node_other";
                node2.Slots.Set(i, true);
            }

            state.SaveConfig(tmpFile);

            var loaded = ClusterState.LoadConfig(tmpFile);
            Assert.NotNull(loaded);
            Assert.Equal("node_myself", loaded.Myself.NodeId);
            Assert.Equal("192.168.1.10", loaded.Myself.Ip);
            Assert.Equal(6379, loaded.Myself.Port);
            Assert.Equal(16379, loaded.Myself.ClusterBusPort);
            Assert.Equal(5u, loaded.Myself.ConfigEpoch);
            Assert.Equal(10u, loaded.CurrentEpoch);
            
            Assert.True(loaded.Myself.Slots.Get(50));
            Assert.False(loaded.Myself.Slots.Get(150));

            Assert.True(loaded.Nodes.ContainsKey("node_other"));
            var loadedNode2 = loaded.Nodes["node_other"];
            Assert.Equal("192.168.1.11", loadedNode2.Ip);
            Assert.Equal(6380, loadedNode2.Port);
            Assert.Equal(16380, loadedNode2.ClusterBusPort);
            Assert.Equal(4u, loadedNode2.ConfigEpoch);
            Assert.True(loadedNode2.Slots.Get(150));
            Assert.False(loadedNode2.Slots.Get(50));
            Assert.True(loadedNode2.Flags.HasFlag(ClusterNodeFlags.Master));
        }
        finally
        {
            if (System.IO.File.Exists(tmpFile))
                System.IO.File.Delete(tmpFile);
        }
    }
}
