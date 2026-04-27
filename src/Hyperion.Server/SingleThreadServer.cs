using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using Hyperion.Config;
using Hyperion.Core;
using Hyperion.Protocol;
using Microsoft.Extensions.Logging;

namespace Hyperion.Server;

/// <summary>
/// A single-threaded RESP server implementation using Socket.Select (IO Multiplexing).
/// This implementation handles all client IO and command execution in a single thread to avoid locking overhead.
/// </summary>
public sealed class SingleThreadServer
{
    private readonly ICommandExecutor _executor;
    private readonly ILogger<SingleThreadServer> _logger;
    private readonly int _port;
    private TcpListener? _listener;
    private readonly SemaphoreSlim _executionLock = new(1, 1);

    public SingleThreadServer(ICommandExecutor executor, ILogger<SingleThreadServer> logger, int port)
    {
        _executor = executor;
        _logger = logger;
        _port = port;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        _logger.LogInformation("Hyperion (single-thread) listening on :{Port}", _port);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                // Handle each connection concurrently, but each connection's commands
                // are processed sequentially (single-threaded per connection).
                _ = Task.Run(() => HandleConnectionAsync(client, cancellationToken), cancellationToken);
            }
        }
        finally
        {
            _listener.Stop();
            _logger.LogInformation("Hyperion server stopped.");
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        _logger.LogDebug("Client connected: {Endpoint}", endpoint);

        using (client)
        {
            var stream = client.GetStream();
            // PipeReader wraps the NetworkStream for efficient, zero-copy buffered reading.
            // .NET handles the underlying IOCP (Windows) / epoll (Linux) automatically.
            var reader = PipeReader.Create(stream);

            try
            {
                await ProcessClientAsync(reader, stream, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug("Client {Endpoint} disconnected: {Reason}", endpoint, ex.Message);
            }
            finally
            {
                await reader.CompleteAsync();
            }
        }

        _logger.LogDebug("Client disconnected: {Endpoint}", endpoint);
    }

    private async Task ProcessClientAsync(PipeReader reader, NetworkStream stream, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await reader.ReadAsync(cancellationToken);
            var buffer = result.Buffer;

            try
            {
                // Try to parse as many complete commands as possible from the buffer.
                while (TryParseCommand(ref buffer, out var command))
                {
                    if (command is not null)
                    {
                        // Ensure only one command is executed at a time globally in single-thread mode
                        await _executionLock.WaitAsync(cancellationToken);
                        try
                        {
                            var response = _executor.Execute(command);
                            await stream.WriteAsync(response, cancellationToken);
                        }
                        finally
                        {
                            _executionLock.Release();
                        }
                    }
                }

                // If the client closed the connection, stop the loop.
                if (result.IsCompleted)
                    break;
            }
            finally
            {
                // Tell the PipeReader how much data we consumed.
                reader.AdvanceTo(buffer.Start, buffer.End);
            }
        }
    }

    /// <summary>
    /// Attempts to parse one complete RESP command from the buffer.
    /// Returns true if a complete command was found and advances the buffer past it.
    /// Returns false if more data is needed (partial message).
    /// </summary>
    private static bool TryParseCommand(ref ReadOnlySequence<byte> buffer, out RespCommand? command)
    {
        var reader = new SequenceReader<byte>(buffer);

        if (RespParser.TryParseCommand(ref reader, out command))
        {
            // Slice the buffer to past the consumed bytes.
            buffer = buffer.Slice(reader.Position);
            return true;
        }

        return false;
    }
}
