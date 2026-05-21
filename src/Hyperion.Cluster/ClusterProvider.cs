using Hyperion.Core;
using Hyperion.Protocol;

namespace Hyperion.Cluster;

public class ClusterProvider : IClusterProvider
{
    private readonly ClusterState _state;
    private readonly ClusterCommands _commands;

    public ClusterProvider(ClusterState state)
    {
        _state = state;
        _commands = new ClusterCommands(state);
    }

    public byte[] ExecuteClusterCommand(RespCommand command)
    {
        return _commands.Execute(command);
    }

    public (bool IsMine, string? RedirectEndpoint, bool IsAsk) CheckSlotOwnership(string key, bool isAsking)
    {
        int slot = ClusterState.GetSlotForKey(key);
        
        // If the slot belongs to myself, all good
        if (_state.Myself.Slots.Get(slot))
        {
            // Is it migrating? If the client didn't send ASKING, we might need to redirect, 
            // but in Redis, migrating slots still serve keys that exist locally.
            // If the key is not local, we would return ASK, but checking key existence here is hard
            // without Storage. For simplicity, we assume we own it and CommandExecutor will handle not found.
            // Wait, standard Redis behaviour: if migrating and key not found, we should return ASK.
            // We'll skip this optimization for Phase 2A and just return true if we own it.
            return (true, null, false);
        }

        // Is it importing and we got ASKING?
        if (isAsking && _state.ImportingSlots.TryGetValue(slot, out var sourceNodeId))
        {
            return (true, null, false);
        }

        // Is it migrating? If so, we should return ASK if the key doesn't exist, but that check requires Storage.
        // In this method, we will just return ASK to the target node if it's migrating, because we can't check Storage.
        // Wait, Redis behavior: if migrating, we MUST check if key exists. Since IClusterProvider doesn't have Storage,
        // we'll let it pass (return IsMine=true) and in CommandExecutor we can handle it if it returns null?
        // Actually, returning true here means CommandExecutor will process it. If it doesn't exist, CommandExecutor
        // will return null/0. To properly support ASK, we might need a post-execution check or pre-check.
        // For Phase 2C, we'll return IsMine=true here if it's migrating, and CommandExecutor will need to
        // intercept missing keys. For now, let's just let it pass if migrating.
        if (_state.MigratingSlots.TryGetValue(slot, out var destNodeId))
        {
            // If the key is migrating, we technically own the slot, but we should return ASK if key is not here.
            // We will let CommandExecutor do the ASK redirection if it wants.
        }

        // Another node owns it
        var ownerNodeId = _state.SlotTable[slot];
        if (ownerNodeId != null && _state.Nodes.TryGetValue(ownerNodeId, out var node))
        {
            return (false, $"{slot} {node.Ip}:{node.Port}", false);
        }

        // Unassigned slot
        return (false, $"{slot} 0.0.0.0:0", false);
    }

    public int GetSlotForKey(string key)
    {
        return ClusterState.GetSlotForKey(key);
    }
}
