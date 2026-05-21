using System;
using Xunit;
using Hyperion.Cluster;

namespace Hyperion.Cluster.Tests;

public class FailureDetectorTests
{
    [Fact]
    public void CheckNodes_MarksPFail_WhenPingTimeoutExceeded()
    {
        var state = new ClusterState("node1");
        
        var node2 = new ClusterNode("node2") { PingSent = 1000, Flags = ClusterNodeFlags.Master };
        state.Nodes["node2"] = node2;

        var detector = new FailureDetector(state, nodeTimeoutMs: 15000);
        
        // At time 16001, timeout (15000) is exceeded
        detector.CheckNodes(16001);
        
        Assert.True(node2.Flags.HasFlag(ClusterNodeFlags.PFail));
    }

    [Fact]
    public void CheckNodes_MarksFail_WhenMajorityMastersReportPFail()
    {
        var state = new ClusterState("node1"); // Myself is master
        
        var node2 = new ClusterNode("node2") { Flags = ClusterNodeFlags.Master, PingSent = 1000 };
        state.Nodes["node2"] = node2;
        
        var node3 = new ClusterNode("node3") { Flags = ClusterNodeFlags.Master };
        state.Nodes["node3"] = node3;

        // node2 has been in PFAIL locally
        node2.Flags |= ClusterNodeFlags.PFail;
        
        // node3 reported node2 as PFAIL
        node2.FailReports["node3"] = 16000;

        var detector = new FailureDetector(state, nodeTimeoutMs: 15000);
        
        // 2 masters total (node1 and node3). Quorum is 2. Both agree node2 is PFAIL.
        detector.CheckNodes(16001);
        
        Assert.False(node2.Flags.HasFlag(ClusterNodeFlags.PFail));
        Assert.True(node2.Flags.HasFlag(ClusterNodeFlags.Fail));
    }
}
