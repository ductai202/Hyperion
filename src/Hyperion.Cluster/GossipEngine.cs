using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Hyperion.Cluster;

public class GossipEngine
{
    private readonly ClusterState _state;
    private readonly ILogger _logger;
    private readonly FailureDetector _failureDetector;
    private ClusterBus? _bus;
    private Timer? _timer;

    public GossipEngine(ClusterState state, ILogger logger)
    {
        _state = state;
        _logger = logger;
        _failureDetector = new FailureDetector(state);
    }

    public void SetBus(ClusterBus bus)
    {
        _bus = bus;
    }

    public void Start()
    {
        _timer = new Timer(OnTick, null, 1000, 1000);
    }

    public void Stop()
    {
        _timer?.Dispose();
    }

    private void OnTick(object? state)
    {
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _failureDetector.CheckNodes(now);

            if (_bus == null) return;

            // Ping N/10 nodes, minimum 3
            var nodesToPing = _state.Nodes.Values
                .Where(n => !n.Flags.HasFlag(ClusterNodeFlags.Myself) && !n.Flags.HasFlag(ClusterNodeFlags.Handshake))
                .OrderBy(n => n.PongReceived) // Ping nodes we haven't heard from recently
                .Take(Math.Max(3, _state.Nodes.Count / 10))
                .ToList();

            foreach (var node in nodesToPing)
            {
                node.PingSent = now;
                var msg = CreateMessage(ClusterMessageType.Ping);
                _ = _bus.SendMessageAsync(node, msg);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in gossip tick");
        }
    }

    public ClusterMessage CreateMessage(ClusterMessageType type)
    {
        var msg = new ClusterMessage
        {
            Type = type,
            Port = (ushort)_state.Myself.Port,
            CurrentEpoch = _state.CurrentEpoch,
            ConfigEpoch = _state.Myself.ConfigEpoch,
            SenderNodeId = _state.Myself.NodeId,
            MySlots = new System.Collections.BitArray(_state.Myself.Slots),
            MyIp = _state.Myself.Ip,
            CPort = (ushort)_state.Myself.ClusterBusPort,
            Flags = _state.Myself.Flags,
            State = _state.Status
        };

        // Select gossip entries (N/10 nodes)
        var gossipNodes = _state.Nodes.Values
            .Where(n => !n.Flags.HasFlag(ClusterNodeFlags.Myself))
            .OrderBy(_ => Random.Shared.Next())
            .Take(Math.Max(3, _state.Nodes.Count / 10))
            .ToList();

        var entries = new List<GossipEntry>();
        foreach (var n in gossipNodes)
        {
            entries.Add(new GossipEntry
            {
                NodeId = n.NodeId,
                Ip = n.Ip,
                Port = (ushort)n.Port,
                CPort = (ushort)n.ClusterBusPort,
                PingSent = (uint)n.PingSent,
                PongReceived = (uint)n.PongReceived,
                Flags = n.Flags
            });
        }
        msg.GossipEntries = entries.ToArray();
        msg.Count = (ushort)entries.Count;

        return msg;
    }

    public async Task ProcessMessageAsync(ClusterMessage msg, NetworkStream stream)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // If we don't know the sender and it's not a MEET, ignore (or maybe process later)
        if (!_state.Nodes.TryGetValue(msg.SenderNodeId, out var senderNode))
        {
            if (msg.Type == ClusterMessageType.Meet)
            {
                senderNode = new ClusterNode(msg.SenderNodeId)
                {
                    Ip = msg.MyIp,
                    Port = msg.Port,
                    ClusterBusPort = msg.CPort,
                    Flags = msg.Flags & ~ClusterNodeFlags.Myself
                };
                _state.Nodes[msg.SenderNodeId] = senderNode;
                _logger.LogInformation("Added new node {NodeId} via MEET", msg.SenderNodeId);
            }
            else
            {
                return;
            }
        }

        // Update sender info
        senderNode.Ip = msg.MyIp;
        senderNode.Port = msg.Port;
        senderNode.ClusterBusPort = msg.CPort;
        senderNode.PongReceived = now;
        senderNode.PingSent = 0; // Clear pending ping

        // If config epoch is higher, update slots
        if (msg.ConfigEpoch > senderNode.ConfigEpoch)
        {
            senderNode.ConfigEpoch = msg.ConfigEpoch;
            for (int i = 0; i < 16384; i++)
            {
                bool owns = msg.MySlots.Get(i);
                if (owns)
                {
                    if (_state.SlotTable[i] != msg.SenderNodeId)
                    {
                        // Someone else owned it, now this node claims it with a higher epoch
                        var oldOwnerId = _state.SlotTable[i];
                        if (oldOwnerId != null && _state.Nodes.TryGetValue(oldOwnerId, out var oldOwner))
                        {
                            oldOwner.Slots.Set(i, false);
                        }
                        _state.SlotTable[i] = msg.SenderNodeId;
                        senderNode.Slots.Set(i, true);
                    }
                }
                else
                {
                    if (_state.SlotTable[i] == msg.SenderNodeId)
                    {
                        _state.SlotTable[i] = null;
                        senderNode.Slots.Set(i, false);
                    }
                }
            }
        }

        if (msg.CurrentEpoch > _state.CurrentEpoch)
        {
            _state.CurrentEpoch = msg.CurrentEpoch;
        }

        // Process gossip entries
        foreach (var entry in msg.GossipEntries)
        {
            if (_state.Nodes.TryGetValue(entry.NodeId, out var node))
            {
                // Update node flags based on gossip
                if (entry.Flags.HasFlag(ClusterNodeFlags.PFail) || entry.Flags.HasFlag(ClusterNodeFlags.Fail))
                {
                    node.FailReports[msg.SenderNodeId] = now;
                }
                else
                {
                    node.FailReports.Remove(msg.SenderNodeId);
                }
            }
            else
            {
                // We don't know this node. If it's not in FAIL state, maybe we should handshake it
                if (!entry.Flags.HasFlag(ClusterNodeFlags.Fail) && !entry.Flags.HasFlag(ClusterNodeFlags.NoAddr))
                {
                    var newNode = new ClusterNode(entry.NodeId)
                    {
                        Ip = entry.Ip,
                        Port = entry.Port,
                        ClusterBusPort = entry.CPort,
                        Flags = ClusterNodeFlags.Handshake
                    };
                    _state.Nodes[entry.NodeId] = newNode;
                    
                    // Immediately initiate handshake
                    if (_bus != null)
                    {
                        var meetMsg = CreateMessage(ClusterMessageType.Meet);
                        _ = _bus.SendMessageAsync(newNode, meetMsg);
                    }
                }
            }
        }

        // Reply to PING/MEET with PONG
        if (msg.Type == ClusterMessageType.Ping || msg.Type == ClusterMessageType.Meet)
        {
            var pong = CreateMessage(ClusterMessageType.Pong);
            var data = ClusterBusSerializer.Serialize(pong);
            await stream.WriteAsync(data);
        }
    }
}
