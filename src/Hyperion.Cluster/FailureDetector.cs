using System;
using System.Linq;

namespace Hyperion.Cluster;

public class FailureDetector
{
    private readonly ClusterState _state;
    private readonly int _nodeTimeoutMs;

    public FailureDetector(ClusterState state, int nodeTimeoutMs = 15000)
    {
        _state = state;
        _nodeTimeoutMs = nodeTimeoutMs;
    }

    public void CheckNodes(long nowMs)
    {
        foreach (var kvp in _state.Nodes)
        {
            var node = kvp.Value;
            if (node.Flags.HasFlag(ClusterNodeFlags.Myself)) continue;

            // Phase 1: Local suspicion (PFAIL)
            bool isPFail = false;
            if (node.PingSent > 0 && (nowMs - node.PingSent) > _nodeTimeoutMs)
            {
                isPFail = true;
            }

            if (isPFail && !node.Flags.HasFlag(ClusterNodeFlags.PFail))
            {
                node.Flags |= ClusterNodeFlags.PFail;
            }
            else if (!isPFail && node.Flags.HasFlag(ClusterNodeFlags.PFail))
            {
                node.Flags &= ~ClusterNodeFlags.PFail;
            }

            // Phase 2: Cluster consensus (FAIL)
            if (node.Flags.HasFlag(ClusterNodeFlags.PFail))
            {
                // Count masters reporting this node as PFAIL
                int neededQuorum = (_state.Nodes.Values.Count(n => n.IsMaster) / 2) + 1;
                int pfailCount = 0;

                // Cleanup old fail reports
                var oldReports = node.FailReports.Where(r => (nowMs - r.Value) > _nodeTimeoutMs * 2).Select(r => r.Key).ToList();
                foreach (var old in oldReports) node.FailReports.Remove(old);

                foreach (var report in node.FailReports)
                {
                    if (_state.Nodes.TryGetValue(report.Key, out var reportingNode) && reportingNode.IsMaster)
                    {
                        pfailCount++;
                    }
                }

                // If I am a master, I count towards the quorum
                if (_state.Myself.IsMaster) pfailCount++;

                if (pfailCount >= neededQuorum && !node.Flags.HasFlag(ClusterNodeFlags.Fail))
                {
                    node.Flags &= ~ClusterNodeFlags.PFail;
                    node.Flags |= ClusterNodeFlags.Fail;
                    // Note: Would broadcast FAIL message here
                }
            }
        }
    }
}
