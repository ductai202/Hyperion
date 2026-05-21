using System;
using System.Collections;
using System.Collections.Generic;

namespace Hyperion.Cluster;

[Flags]
public enum ClusterNodeFlags : ushort
{
    None = 0,
    Master = 1 << 0,
    Replica = 1 << 1,
    PFail = 1 << 2,
    Fail = 1 << 3,
    Handshake = 1 << 4,
    NoAddr = 1 << 5,
    Myself = 1 << 6
}

public class ClusterNode
{
    public string NodeId { get; set; } = string.Empty;
    public string Ip { get; set; } = string.Empty;
    public int Port { get; set; }
    public int ClusterBusPort { get; set; }
    public ClusterNodeFlags Flags { get; set; }
    
    public string? MasterNodeId { get; set; }
    public ulong ConfigEpoch { get; set; }
    
    // 16384 bits to track slot ownership
    public BitArray Slots { get; } = new BitArray(16384);
    
    public long PingSent { get; set; }
    public long PongReceived { get; set; }
    
    // FailReports keeps track of node IDs that reported this node as PFAIL
    public Dictionary<string, long> FailReports { get; } = new();

    public bool IsMaster => (Flags & ClusterNodeFlags.Master) != 0;
    public bool IsReplica => (Flags & ClusterNodeFlags.Replica) != 0;
    public bool IsPFail => (Flags & ClusterNodeFlags.PFail) != 0;
    public bool IsFail => (Flags & ClusterNodeFlags.Fail) != 0;

    public ClusterNode(string nodeId)
    {
        NodeId = nodeId;
    }
}
