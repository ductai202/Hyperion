using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Hyperion.Cluster;

public enum ClusterStatus
{
    Ok,
    Fail
}

public class ClusterState
{
    public ulong CurrentEpoch { get; set; }
    
    // NodeId -> ClusterNode
    public ConcurrentDictionary<string, ClusterNode> Nodes { get; } = new();
    
    public ClusterNode Myself { get; }
    
    // Slot -> NodeId
    public string?[] SlotTable { get; } = new string?[16384];
    
    // Slot -> NodeId (where the slot is migrating to)
    public ConcurrentDictionary<int, string> MigratingSlots { get; } = new();
    
    // Slot -> NodeId (where the slot is importing from)
    public ConcurrentDictionary<int, string> ImportingSlots { get; } = new();

    public ClusterStatus Status { get; set; } = ClusterStatus.Fail;
    
    public Action? SaveConfigCallback { get; set; }

    public ClusterState(string myNodeId)
    {
        Myself = new ClusterNode(myNodeId)
        {
            Flags = ClusterNodeFlags.Myself | ClusterNodeFlags.Master
        };
        Nodes[myNodeId] = Myself;
    }

    /// <summary>
    /// Gets the slot for a given key, honoring Redis Cluster hash tags.
    /// If a substring is enclosed in {} and not empty, only that substring is hashed.
    /// </summary>
    public static int GetSlotForKey(string key)
    {
        int start = key.IndexOf('{');
        if (start != -1)
        {
            int end = key.IndexOf('}', start + 1);
            if (end != -1 && end > start + 1)
            {
                key = key.Substring(start + 1, end - start - 1);
            }
        }
        return Crc16.Compute(key) % 16384;
    }

    public ClusterNode? LookupNode(int slot)
    {
        if (slot < 0 || slot >= 16384) return null;
        var nodeId = SlotTable[slot];
        if (nodeId == null) return null;
        Nodes.TryGetValue(nodeId, out var node);
        return node;
    }

    public void UpdateClusterStatus()
    {
        // Cluster is OK if all 16384 slots are assigned and the nodes owning them are not FAIL
        for (int i = 0; i < 16384; i++)
        {
            var node = LookupNode(i);
            if (node == null || node.IsFail)
            {
                Status = ClusterStatus.Fail;
                return;
            }
        }
        Status = ClusterStatus.Ok;
    }

    public void SaveConfig(string filePath)
    {
        var sb = new System.Text.StringBuilder();
        // Redis nodes.conf format:
        // <id> <ip:port@cport> <flags> <master> <ping-sent> <pong-recv> <config-epoch> <link-state> <slots>...
        foreach (var node in Nodes.Values)
        {
            var flagsList = new List<string>();
            if (node.Flags.HasFlag(ClusterNodeFlags.Myself)) flagsList.Add("myself");
            if (node.Flags.HasFlag(ClusterNodeFlags.Master)) flagsList.Add("master");
            if (node.Flags.HasFlag(ClusterNodeFlags.Replica)) flagsList.Add("slave");
            if (node.Flags.HasFlag(ClusterNodeFlags.Fail)) flagsList.Add("fail");
            if (node.Flags.HasFlag(ClusterNodeFlags.PFail)) flagsList.Add("fail?");
            if (node.Flags.HasFlag(ClusterNodeFlags.Handshake)) flagsList.Add("handshake");
            if (node.Flags.HasFlag(ClusterNodeFlags.NoAddr)) flagsList.Add("noaddr");
            if (flagsList.Count == 0) flagsList.Add("noflags");
            
            string flags = string.Join(",", flagsList);
            string master = string.IsNullOrEmpty(node.MasterNodeId) ? "-" : node.MasterNodeId;
            
            sb.Append($"{node.NodeId} {node.Ip}:{node.Port}@{node.ClusterBusPort} {flags} {master} {node.PingSent} {node.PongReceived} {node.ConfigEpoch} connected");

            // Slots
            int start = -1;
            for (int i = 0; i < 16384; i++)
            {
                if (node.Slots.Get(i))
                {
                    if (start == -1) start = i;
                }
                else
                {
                    if (start != -1)
                    {
                        if (start == i - 1) sb.Append($" {start}");
                        else sb.Append($" {start}-{i - 1}");
                        start = -1;
                    }
                }
            }
            if (start != -1)
            {
                if (start == 16383) sb.Append($" {start}");
                else sb.Append($" {start}-16383");
            }
            sb.AppendLine();
        }
        
        sb.AppendLine($"vars currentEpoch {CurrentEpoch} lastVoteEpoch 0");
        
        // Write atomically
        string tmp = filePath + ".tmp";
        System.IO.File.WriteAllText(tmp, sb.ToString());
        System.IO.File.Move(tmp, filePath, overwrite: true);
    }

    public static ClusterState? LoadConfig(string filePath)
    {
        if (!System.IO.File.Exists(filePath)) return null;

        string[] lines = System.IO.File.ReadAllLines(filePath);
        ClusterState? state = null;
        ulong currentEpoch = 0;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            if (parts[0] == "vars")
            {
                for (int i = 1; i < parts.Length - 1; i += 2)
                {
                    if (parts[i] == "currentEpoch" && ulong.TryParse(parts[i+1], out ulong e))
                    {
                        currentEpoch = e;
                    }
                }
                continue;
            }

            // Parse node line
            if (parts.Length >= 8)
            {
                string nodeId = parts[0];
                string address = parts[1];
                string flagsStr = parts[2];
                string master = parts[3];
                ulong.TryParse(parts[6], out ulong configEpoch);

                if (flagsStr.Contains("myself"))
                {
                    state = new ClusterState(nodeId);
                    state.Myself.ConfigEpoch = configEpoch;
                }
            }
        }

        if (state == null) return null; // No myself node found
        state.CurrentEpoch = currentEpoch;

        // Second pass to populate all nodes and slots
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("vars")) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 8) continue;

            string nodeId = parts[0];
            string address = parts[1]; // ip:port@cport
            string flagsStr = parts[2];
            string master = parts[3];
            ulong.TryParse(parts[6], out ulong configEpoch);

            var node = (nodeId == state.Myself.NodeId) ? state.Myself : new ClusterNode(nodeId);
            
            // Parse address
            var addrParts = address.Split('@');
            if (addrParts.Length > 0)
            {
                var ipPort = addrParts[0].Split(':');
                if (ipPort.Length == 2)
                {
                    node.Ip = ipPort[0];
                    if (int.TryParse(ipPort[1], out int p)) node.Port = p;
                }
            }
            if (addrParts.Length > 1 && int.TryParse(addrParts[1], out int cp))
                node.ClusterBusPort = cp;

            if (master != "-") node.MasterNodeId = master;
            node.ConfigEpoch = configEpoch;
            
            ClusterNodeFlags flags = ClusterNodeFlags.None;
            if (flagsStr.Contains("myself")) flags |= ClusterNodeFlags.Myself;
            if (flagsStr.Contains("master")) flags |= ClusterNodeFlags.Master;
            if (flagsStr.Contains("slave")) flags |= ClusterNodeFlags.Replica;
            if (flagsStr.Contains("fail?")) flags |= ClusterNodeFlags.PFail;
            else if (flagsStr.Contains("fail")) flags |= ClusterNodeFlags.Fail;
            if (flagsStr.Contains("handshake")) flags |= ClusterNodeFlags.Handshake;
            if (flagsStr.Contains("noaddr")) flags |= ClusterNodeFlags.NoAddr;
            node.Flags = flags;

            // Parse slots (parts[8..])
            for (int i = 8; i < parts.Length; i++)
            {
                string slotStr = parts[i];
                if (slotStr.StartsWith("[")) continue; // skip migrating/importing states for now

                var range = slotStr.Split('-');
                if (range.Length == 1)
                {
                    if (int.TryParse(range[0], out int s) && s >= 0 && s < 16384)
                    {
                        node.Slots.Set(s, true);
                        state.SlotTable[s] = nodeId;
                    }
                }
                else if (range.Length == 2)
                {
                    if (int.TryParse(range[0], out int start) && int.TryParse(range[1], out int end))
                    {
                        for (int s = start; s <= end && s < 16384; s++)
                        {
                            node.Slots.Set(s, true);
                            state.SlotTable[s] = nodeId;
                        }
                    }
                }
            }

            if (nodeId != state.Myself.NodeId)
                state.Nodes[nodeId] = node;
        }

        state.UpdateClusterStatus();
        return state;
    }
}
