using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Hyperion.Protocol;

namespace Hyperion.Cluster;

public class ClusterCommands
{
    private readonly ClusterState _state;

    public ClusterCommands(ClusterState state)
    {
        _state = state;
    }

    public byte[] Execute(RespCommand command)
    {
        if (command.Args.Length == 0)
        {
            return RespEncoder.Encode(new Exception("ERR wrong number of arguments for 'cluster' command"));
        }

        string subCommand = command.Args[0].ToUpperInvariant();
        string[] args = command.Args.Skip(1).ToArray();

        return subCommand switch
        {
            "INFO" => Info(),
            "NODES" => Nodes(),
            "MYID" => MyId(),
            "SLOTS" => Slots(),
            "MEET" => Meet(args),
            "ADDSLOTS" => AddSlots(args),
            "DELSLOTS" => DelSlots(args),
            "SETSLOT" => SetSlot(args),
            "COUNTKEYSINSLOT" => CountKeysInSlot(args),
            "KEYSLOT" => KeySlot(args),
            _ => RespEncoder.Encode(new Exception($"ERR unknown CLUSTER subcommand '{subCommand}'"))
        };
    }

    private byte[] Info()
    {
        _state.UpdateClusterStatus();
        var sb = new StringBuilder();
        sb.AppendLine($"cluster_state:{(_state.Status == ClusterStatus.Ok ? "ok" : "fail")}");
        
        int slotsAssigned = 0, slotsOk = 0, slotsPfail = 0, slotsFail = 0;
        int size = 0;
        var masterNodes = _state.Nodes.Values.Where(n => n.IsMaster).ToList();

        for (int i = 0; i < 16384; i++)
        {
            var node = _state.LookupNode(i);
            if (node != null)
            {
                slotsAssigned++;
                if (node.IsFail) slotsFail++;
                else if (node.IsPFail) slotsPfail++;
                else slotsOk++;
            }
        }

        foreach (var node in masterNodes)
        {
            for (int i = 0; i < 16384; i++)
            {
                if (node.Slots.Get(i))
                {
                    size++;
                    break;
                }
            }
        }

        sb.AppendLine($"cluster_slots_assigned:{slotsAssigned}");
        sb.AppendLine($"cluster_slots_ok:{slotsOk}");
        sb.AppendLine($"cluster_slots_pfail:{slotsPfail}");
        sb.AppendLine($"cluster_slots_fail:{slotsFail}");
        sb.AppendLine($"cluster_known_nodes:{_state.Nodes.Count}");
        sb.AppendLine($"cluster_size:{size}");
        sb.AppendLine($"cluster_current_epoch:{_state.CurrentEpoch}");
        sb.AppendLine($"cluster_my_epoch:{_state.Myself.ConfigEpoch}");
        
        return RespEncoder.Encode(sb.ToString().TrimEnd());
    }

    private byte[] Nodes()
    {
        var sb = new StringBuilder();
        foreach (var node in _state.Nodes.Values)
        {
            string flags = string.Join(",", GetFlags(node));
            string master = node.MasterNodeId ?? "-";
            long pingSent = node.PingSent;
            long pongRecv = node.PongReceived;
            long epoch = (long)node.ConfigEpoch;
            string connected = "connected";

            sb.Append($"{node.NodeId} {node.Ip}:{node.Port}@{node.ClusterBusPort} {flags} {master} {pingSent} {pongRecv} {epoch} {connected}");

            // Append slots
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
        return RespEncoder.Encode(sb.ToString().TrimEnd());
    }

    private IEnumerable<string> GetFlags(ClusterNode node)
    {
        if (node.Flags.HasFlag(ClusterNodeFlags.Myself)) yield return "myself";
        if (node.Flags.HasFlag(ClusterNodeFlags.Master)) yield return "master";
        if (node.Flags.HasFlag(ClusterNodeFlags.Replica)) yield return "slave";
        if (node.Flags.HasFlag(ClusterNodeFlags.PFail)) yield return "fail?";
        if (node.Flags.HasFlag(ClusterNodeFlags.Fail)) yield return "fail";
        if (node.Flags.HasFlag(ClusterNodeFlags.Handshake)) yield return "handshake";
        if (node.Flags.HasFlag(ClusterNodeFlags.NoAddr)) yield return "noaddr";
        
        if (node.Flags == ClusterNodeFlags.None) yield return "noflags";
    }

    private byte[] MyId()
    {
        return RespEncoder.Encode(_state.Myself.NodeId);
    }

    private byte[] Slots()
    {
        // For CLUSTER SLOTS, we need to return an array of arrays:
        // [startSlot, endSlot, [ip, port, nodeid], ...]
        // We'll skip this full implementation for now or return simple string if too complex to encode manually
        return RespEncoder.Encode(new Exception("ERR CLUSTER SLOTS not implemented yet"));
    }

    private byte[] Meet(string[] args)
    {
        if (args.Length < 2) return RespEncoder.Encode(new Exception("ERR wrong number of arguments for 'CLUSTER MEET' command"));
        string ip = args[0];
        if (!int.TryParse(args[1], out int port)) return RespEncoder.Encode(new Exception("ERR Invalid port"));
        
        // Gossip engine will handle handshake
        return RespEncoder.Encode("OK", isSimpleString: true);
    }

    private byte[] AddSlots(string[] args)
    {
        if (args.Length == 0) return RespEncoder.Encode(new Exception("ERR wrong number of arguments for 'CLUSTER ADDSLOTS' command"));
        
        List<int> slots = new();
        foreach (var arg in args)
        {
            if (!int.TryParse(arg, out int slot) || slot < 0 || slot >= 16384)
            {
                return RespEncoder.Encode(new Exception("ERR Invalid or out of range slot"));
            }
            if (_state.SlotTable[slot] != null)
            {
                return RespEncoder.Encode(new Exception("ERR Slot already assigned"));
            }
            slots.Add(slot);
        }

        foreach (var slot in slots)
        {
            _state.SlotTable[slot] = _state.Myself.NodeId;
            _state.Myself.Slots.Set(slot, true);
        }

        _state.Myself.ConfigEpoch++;
        if (_state.Myself.ConfigEpoch > _state.CurrentEpoch) _state.CurrentEpoch = _state.Myself.ConfigEpoch;

        _state.UpdateClusterStatus();
        _state.SaveConfigCallback?.Invoke();
        return RespEncoder.Encode("OK", isSimpleString: true);
    }

    private byte[] DelSlots(string[] args)
    {
        if (args.Length == 0) return RespEncoder.Encode(new Exception("ERR wrong number of arguments for 'CLUSTER DELSLOTS' command"));
        
        List<int> slots = new();
        foreach (var arg in args)
        {
            if (!int.TryParse(arg, out int slot) || slot < 0 || slot >= 16384)
            {
                return RespEncoder.Encode(new Exception("ERR Invalid or out of range slot"));
            }
            if (_state.SlotTable[slot] != _state.Myself.NodeId)
            {
                return RespEncoder.Encode(new Exception("ERR Slot not assigned to this node"));
            }
            slots.Add(slot);
        }

        foreach (var slot in slots)
        {
            _state.SlotTable[slot] = null;
            _state.Myself.Slots.Set(slot, false);
        }

        _state.Myself.ConfigEpoch++;
        if (_state.Myself.ConfigEpoch > _state.CurrentEpoch) _state.CurrentEpoch = _state.Myself.ConfigEpoch;

        _state.UpdateClusterStatus();
        _state.SaveConfigCallback?.Invoke();
        return RespEncoder.Encode("OK", isSimpleString: true);
    }

    private byte[] SetSlot(string[] args)
    {
        // SETSLOT slot IMPORTING|MIGRATING|STABLE|NODE node-id
        if (args.Length < 2) return RespEncoder.Encode(new Exception("ERR wrong number of arguments for 'CLUSTER SETSLOT' command"));
        if (!int.TryParse(args[0], out int slot) || slot < 0 || slot >= 16384)
            return RespEncoder.Encode(new Exception("ERR Invalid slot"));

        string subcommand = args[1].ToUpperInvariant();
        switch (subcommand)
        {
            case "IMPORTING":
                if (args.Length != 3) return RespEncoder.Encode(new Exception("ERR wrong number of arguments for IMPORTING"));
                _state.ImportingSlots[slot] = args[2];
                break;
            case "MIGRATING":
                if (args.Length != 3) return RespEncoder.Encode(new Exception("ERR wrong number of arguments for MIGRATING"));
                _state.MigratingSlots[slot] = args[2];
                break;
            case "STABLE":
                _state.ImportingSlots.TryRemove(slot, out _);
                _state.MigratingSlots.TryRemove(slot, out _);
                break;
            case "NODE":
                if (args.Length != 3) return RespEncoder.Encode(new Exception("ERR wrong number of arguments for NODE"));
                string nodeId = args[2];
                if (!_state.Nodes.ContainsKey(nodeId)) return RespEncoder.Encode(new Exception("ERR Unknown node"));

                _state.SlotTable[slot] = nodeId;
                if (nodeId == _state.Myself.NodeId)
                    _state.Myself.Slots.Set(slot, true);
                else
                    _state.Myself.Slots.Set(slot, false);

                _state.ImportingSlots.TryRemove(slot, out _);
                _state.MigratingSlots.TryRemove(slot, out _);

                _state.Myself.ConfigEpoch++;
                if (_state.Myself.ConfigEpoch > _state.CurrentEpoch) _state.CurrentEpoch = _state.Myself.ConfigEpoch;
                _state.UpdateClusterStatus();
                _state.SaveConfigCallback?.Invoke();
                break;
            default:
                return RespEncoder.Encode(new Exception($"ERR Invalid CLUSTER SETSLOT subcommand '{subcommand}'"));
        }
        return RespEncoder.Encode("OK", isSimpleString: true);
    }

    private byte[] CountKeysInSlot(string[] args)
    {
        if (args.Length != 1 || !int.TryParse(args[0], out int slot) || slot < 0 || slot >= 16384)
        {
            return RespEncoder.Encode(new Exception("ERR Invalid slot"));
        }
        
        // This would require access to Storage, skipping for now
        return RespEncoder.Encode(0, isSimpleString: false);
    }

    private byte[] KeySlot(string[] args)
    {
        if (args.Length != 1) return RespEncoder.Encode(new Exception("ERR wrong number of arguments for 'CLUSTER KEYSLOT' command"));
        int slot = ClusterState.GetSlotForKey(args[0]);
        return RespEncoder.Encode(slot, isSimpleString: false);
    }
}
