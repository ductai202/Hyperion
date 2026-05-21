using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Hyperion.Server;
using Hyperion.Cluster;

namespace Hyperion.Cluster.Tests;

public class ClusterIntegrationTests : IDisposable
{
    private HyperionServer? _server;
    private CancellationTokenSource? _cts;
    private Task? _serverTask;
    private int _port;
    private ClusterBus? _bus;
    private GossipEngine? _gossip;

    public ClusterIntegrationTests()
    {
    }

    private void StartServer(bool setupSlots)
    {
        var tcpListener = new TcpListener(System.Net.IPAddress.Any, 0);
        tcpListener.Start();
        _port = ((System.Net.IPEndPoint)tcpListener.LocalEndpoint).Port;
        tcpListener.Stop();

        string myId = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N").Substring(0, 8);
        var state = new ClusterState(myId);
        state.Myself.Ip = "127.0.0.1";
        state.Myself.Port = _port;
        state.Myself.ClusterBusPort = 20000 + Random.Shared.Next(1, 10000);

        if (setupSlots)
        {
            // Give this node slots 0 to 5000
            for (int i = 0; i <= 5000; i++)
            {
                state.SlotTable[i] = myId;
                state.Myself.Slots.Set(i, true);
            }
            state.UpdateClusterStatus();
        }

        _gossip = new GossipEngine(state, NullLogger.Instance);
        _bus = new ClusterBus(state, _gossip, NullLogger.Instance);
        _gossip.SetBus(_bus);
        
        _bus.Start();
        _gossip.Start();

        _server = new HyperionServer(NullLoggerFactory.Instance, _port, numWorkers: 4, numIOHandlers: 2, clusterState: state);
        _cts = new CancellationTokenSource();
        _serverTask = _server.RunAsync(_cts.Token);
    }

    private void StopServer()
    {
        _gossip?.Stop();
        _bus?.Stop();

        if (_cts != null && _serverTask != null)
        {
            _cts.Cancel();
            try { _serverTask.Wait(2000); } catch { }
            _cts.Dispose();
            _cts = null;
            _serverTask = null;
        }
    }

    private string SendCommand(string[] args)
    {
        using var client = new TcpClient("127.0.0.1", _port);
        using var stream = client.GetStream();

        var sb = new StringBuilder();
        sb.Append($"*{args.Length}\r\n");
        foreach (var arg in args)
            sb.Append($"${Encoding.UTF8.GetByteCount(arg)}\r\n{arg}\r\n");

        var reqBytes = Encoding.UTF8.GetBytes(sb.ToString());
        stream.Write(reqBytes, 0, reqBytes.Length);

        var resBytes = new byte[1024];
        int bytesRead = stream.Read(resBytes, 0, resBytes.Length);
        return Encoding.UTF8.GetString(resBytes, 0, bytesRead);
    }

    [Fact]
    public void ClusterInfo_ReturnsCorrectData()
    {
        StartServer(setupSlots: true);
        var result = SendCommand(new[] { "CLUSTER", "INFO" });
        
        Assert.Contains("cluster_state:fail", result); // Fail because not all 16384 slots are covered (only 5001)
        Assert.Contains("cluster_slots_assigned:5001", result);
        Assert.Contains("cluster_known_nodes:1", result);
    }

    [Fact]
    public void SlotRouting_RedirectsIfSlotNotOwned()
    {
        StartServer(setupSlots: false); // Node owns 0 slots
        
        var result = SendCommand(new[] { "SET", "hello", "world" });
        // Key "hello" -> slot 866. Node doesn't own it.
        Assert.StartsWith("-MOVED 866 0.0.0.0:0\r\n", result);
    }

    [Fact]
    public void SlotRouting_ExecutesIfSlotOwned()
    {
        StartServer(setupSlots: true); // Node owns 0-5000
        
        // Key "hello" -> slot 866. Node owns it!
        var result = SendCommand(new[] { "SET", "hello", "world" });
        Assert.Equal("+OK\r\n", result);
        
        // Key "foo" -> slot 803 (Wait, let's test a key we know).
        // Let's use KEYSLOT to verify.
        var slotRes = SendCommand(new[] { "CLUSTER", "KEYSLOT", "hello" });
        Assert.Equal(":866\r\n", slotRes);
    }

    [Fact]
    public void CrossSlot_FailsForMultipleKeysInDifferentSlots()
    {
        StartServer(setupSlots: true); // Owns 0-5000
        
        // "hello" is slot 866. "world" is slot 10924 (out of range, but for cross-slot test, just matters they are different)
        var result = SendCommand(new[] { "DEL", "hello", "world" });
        Assert.StartsWith("-CROSSSLOT", result);
    }

    [Fact]
    public void Asking_AllowsCommandExecutionOnImportingSlot()
    {
        StartServer(setupSlots: false); // Node owns 0 slots

        // Slot 866 (hello) is not owned.
        // If we try SET hello world, we get MOVED.
        var movedRes = SendCommand(new[] { "SET", "hello", "world" });
        Assert.StartsWith("-MOVED 866", movedRes);

        // Now set slot 866 as IMPORTING from some node
        var setSlotRes = SendCommand(new[] { "CLUSTER", "SETSLOT", "866", "IMPORTING", "some-node-id" });
        Assert.Equal("+OK\r\n", setSlotRes);

        // If we try again without ASKING, we still get MOVED or ASK depending on logic, but currently we just get MOVED because it's not strictly assigned to us.
        // Wait, if it's importing and we don't send ASKING, it returns MOVED (since someone else owns it).
        // If we send ASKING, we can bypass the redirect.
        using var client = new TcpClient("127.0.0.1", _port);
        using var stream = client.GetStream();

        // Send ASKING
        var askingCmd = "*1\r\n$6\r\nASKING\r\n";
        stream.Write(Encoding.UTF8.GetBytes(askingCmd));
        
        var askingRes = new byte[1024];
        int read = stream.Read(askingRes, 0, askingRes.Length);
        Assert.Equal("+OK\r\n", Encoding.UTF8.GetString(askingRes, 0, read));

        // Immediately send SET hello world on the same connection
        var setCmd = "*3\r\n$3\r\nSET\r\n$5\r\nhello\r\n$5\r\nworld\r\n";
        stream.Write(Encoding.UTF8.GetBytes(setCmd));
        
        var setRes = new byte[1024];
        read = stream.Read(setRes, 0, setRes.Length);
        Assert.Equal("+OK\r\n", Encoding.UTF8.GetString(setRes, 0, read));
    }

    [Fact]
    public void ClusterGetKeysInSlot_ReturnsCorrectKeys()
    {
        StartServer(setupSlots: true); // Node owns 0-5000

        // "hello" -> slot 866
        SendCommand(new[] { "SET", "hello", "val1" });
        
        var getKeysRes = SendCommand(new[] { "CLUSTER", "GETKEYSINSLOT", "866", "10" });
        Assert.Contains("hello", getKeysRes);

        var countRes = SendCommand(new[] { "CLUSTER", "COUNTKEYSINSLOT", "866" });
        Assert.Equal(":1\r\n", countRes);
    }

    [Fact]
    public void Migrate_FailsIfKeyNotFound()
    {
        StartServer(setupSlots: true); // Owns 0-5000
        
        // "hello" maps to slot 866, which is owned by this node
        var result = SendCommand(new[] { "MIGRATE", "127.0.0.1", "6380", "hello", "0", "1000" });
        Assert.StartsWith("-ERR no such key", result);
    }

    public void Dispose()
    {
        StopServer();
    }
}
