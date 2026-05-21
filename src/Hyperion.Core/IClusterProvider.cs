using Hyperion.Protocol;

namespace Hyperion.Core;

public interface IClusterProvider
{
    /// <summary>
    /// Executes a CLUSTER subcommand.
    /// </summary>
    byte[] ExecuteClusterCommand(RespCommand command);

    /// <summary>
    /// Checks if this node is responsible for the slot of the given key.
    /// Returns (true, null) if we own it.
    /// Returns (false, "ip:port") if another node owns it, indicating a MOVED or ASK redirection.
    /// </summary>
    (bool IsMine, string? RedirectEndpoint, bool IsAsk) CheckSlotOwnership(string key, bool isAsking);
    int GetSlotForKey(string key);
}
