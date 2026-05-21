using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Hyperion.Cluster;

public class ClusterBus
{
    private readonly ClusterState _state;
    private readonly GossipEngine _gossipEngine;
    private readonly ILogger _logger;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public ClusterBus(ClusterState state, GossipEngine gossipEngine, ILogger logger)
    {
        _state = state;
        _gossipEngine = gossipEngine;
        _logger = logger;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        int port = _state.Myself.ClusterBusPort;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        
        _logger.LogInformation("Cluster bus listening on port {Port}", port);
        
        Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientAsync(client, ct));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cluster bus accept loop error");
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                // Simple framing: read length prefix first if needed, but Redis cluster bus 
                // messages contain their own total length at offset 4.
                byte[] header = new byte[8];
                await stream.ReadExactlyAsync(header, 0, 8, ct);

                if (header[0] != 'R' || header[1] != 'C' || header[2] != 'm' || header[3] != 'b')
                {
                    _logger.LogWarning("Invalid cluster bus signature");
                    return;
                }

                uint totalLength = BitConverter.ToUInt32(header, 4);
                if (totalLength < 2205 || totalLength > 64 * 1024) return;

                byte[] fullMessage = new byte[totalLength];
                Array.Copy(header, fullMessage, 8);
                
                await stream.ReadExactlyAsync(fullMessage, 8, (int)totalLength - 8, ct);

                var msg = ClusterBusSerializer.Deserialize(fullMessage);
                if (msg != null)
                {
                    await _gossipEngine.ProcessMessageAsync(msg, stream);
                }
            }
        }
        catch (Exception ex)
        {
            // Expected during disconnects
            _logger.LogDebug(ex, "Cluster bus client error");
        }
    }

    public async Task SendMessageAsync(ClusterNode node, ClusterMessage msg)
    {
        try
        {
            using var client = new TcpClient();
            var cts = new CancellationTokenSource(2000); // 2s timeout
            await client.ConnectAsync(node.Ip, node.ClusterBusPort, cts.Token);
            
            using var stream = client.GetStream();
            var data = ClusterBusSerializer.Serialize(msg);
            await stream.WriteAsync(data, cts.Token);
            
            // If it's a PING or MEET, we expect a PONG reply
            if (msg.Type == ClusterMessageType.Ping || msg.Type == ClusterMessageType.Meet)
            {
                byte[] header = new byte[8];
                await stream.ReadExactlyAsync(header, 0, 8, cts.Token);
                uint totalLength = BitConverter.ToUInt32(header, 4);
                byte[] fullMessage = new byte[totalLength];
                Array.Copy(header, fullMessage, 8);
                await stream.ReadExactlyAsync(fullMessage, 8, (int)totalLength - 8, cts.Token);
                
                var reply = ClusterBusSerializer.Deserialize(fullMessage);
                if (reply != null)
                {
                    await _gossipEngine.ProcessMessageAsync(reply, stream);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send cluster message to {NodeId} ({Ip}:{Port})", node.NodeId, node.Ip, node.ClusterBusPort);
        }
    }
}
