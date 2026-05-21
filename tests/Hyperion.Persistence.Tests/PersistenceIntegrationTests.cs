using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Hyperion.Server;
using Hyperion.Persistence;

namespace Hyperion.Persistence.Tests;

public class PersistenceIntegrationTests : IDisposable
{

    private CancellationTokenSource? _cts;
    private Task? _serverTask;
    private int _port;
    private string _dbPath;

    public PersistenceIntegrationTests()
    {
        _dbPath = $"test_integration_{Guid.NewGuid():N}.rdb";
    }

    private void StartServer(bool useSingleThread)
    {
        var tcpListener = new TcpListener(System.Net.IPAddress.Any, 0);
        tcpListener.Start();
        _port = ((System.Net.IPEndPoint)tcpListener.LocalEndpoint).Port;
        tcpListener.Stop();

        var config = new PersistenceConfig
        {
            RdbFilePath = _dbPath,
            SaveOnShutdown = true
        };

        _cts = new CancellationTokenSource();

        if (useSingleThread)
        {
            var server = new SingleThreadServer(NullLogger<SingleThreadServer>.Instance, _port, config);
            _serverTask = server.RunAsync(_cts.Token);
        }
        else
        {
            var server = new HyperionServer(NullLoggerFactory.Instance, _port, numWorkers: 4, numIOHandlers: 2, persistenceConfig: config);
            _serverTask = server.RunAsync(_cts.Token);
        }
    }

    private void StopServer()
    {
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SaveAndRestore_PreservesData(bool useSingleThread)
    {
        StartServer(useSingleThread);

        // Add some keys
        Assert.Equal("+OK\r\n", SendCommand(new[] { "SET", "key1", "val1" }));
        Assert.Equal(":1\r\n", SendCommand(new[] { "SADD", "myset", "setval" }));
        
        // Wait for save or send SAVE command
        Assert.Equal("+OK\r\n", SendCommand(new[] { "SAVE" }));

        // Stop server (this would also save if saveOnShutdown is true)
        StopServer();

        Assert.True(File.Exists(_dbPath));

        // Start new server instance (it will load the RDB file)
        StartServer(useSingleThread);

        Assert.Equal("$4\r\nval1\r\n", SendCommand(new[] { "GET", "key1" }));
        Assert.Equal("*1\r\n$6\r\nsetval\r\n", SendCommand(new[] { "SMEMBERS", "myset" }));
    }

    public void Dispose()
    {
        StopServer();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }
}
