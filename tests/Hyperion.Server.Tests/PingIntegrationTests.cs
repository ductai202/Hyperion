using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Hyperion.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hyperion.Server.Tests;

public class PingIntegrationTests : IDisposable
{
    private readonly SingleThreadServer _server;
    private readonly CancellationTokenSource _cts;
    private readonly Task _serverTask;
    private readonly int _port;

    public PingIntegrationTests()
    {
        _port = 3000 + Random.Shared.Next(1, 10000);
        var logger = NullLogger<SingleThreadServer>.Instance;

        _server = new SingleThreadServer(logger, _port);
        _cts = new CancellationTokenSource();
        _serverTask = _server.RunAsync(_cts.Token);

        Thread.Sleep(100);
    }

    [Fact]
    public async Task Ping_ShouldReturnPong()
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", _port);
        using var stream = client.GetStream();

        var request = Encoding.UTF8.GetBytes("*1\r\n$4\r\nPING\r\n");
        await stream.WriteAsync(request, 0, request.Length);

        var buffer = new byte[1024];
        int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
        var response = Encoding.UTF8.GetString(buffer, 0, bytesRead);

        Assert.Equal("+PONG\r\n", response);
    }

    [Fact]
    public async Task Ping_WithArgument_ShouldEchoArgument()
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", _port);
        using var stream = client.GetStream();

        var request = Encoding.UTF8.GetBytes("*2\r\n$4\r\nPING\r\n$5\r\nhello\r\n");
        await stream.WriteAsync(request, 0, request.Length);

        var buffer = new byte[1024];
        int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
        var response = Encoding.UTF8.GetString(buffer, 0, bytesRead);

        Assert.Equal("$5\r\nhello\r\n", response);
    }

    [Fact]
    public async Task EventLoop_ShouldRunActiveExpiryPeriodically()
    {
        await SendCommandAsync("*5\r\n$3\r\nSET\r\n$20\r\nsinglethread:expired\r\n$5\r\nvalue\r\n$2\r\nEX\r\n$1\r\n1\r\n");
        await Task.Delay(1100);

        for (int i = 0; i < 100; i++)
            await SendCommandAsync("*1\r\n$4\r\nPING\r\n");

        var storageField = typeof(SingleThreadServer).GetField("_storage", BindingFlags.Instance | BindingFlags.NonPublic);
        var storage = Assert.IsType<Storage>(storageField!.GetValue(_server));
        Assert.DoesNotContain(
            storage.DictStore.GetAllEntries(),
            entry => entry.Key == "singlethread:expired");
    }

    private async Task<string> SendCommandAsync(string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", _port);
        using var stream = client.GetStream();

        var bytes = Encoding.UTF8.GetBytes(request);
        await stream.WriteAsync(bytes, 0, bytes.Length);

        var buffer = new byte[1024];
        int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
        return Encoding.UTF8.GetString(buffer, 0, bytesRead);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _serverTask.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        _cts.Dispose();
    }
}
