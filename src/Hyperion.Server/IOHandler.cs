using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Hyperion.Core;
using Hyperion.Protocol;
using Microsoft.Extensions.Logging;

namespace Hyperion.Server;

/// <summary>
/// Handles network I/O for a subset of connected clients.
/// Uses PipeWriter for batched writes: accumulates all responses from
/// one read iteration into the write buffer, flushes once per batch.
/// This eliminates the per-response WriteAsync syscall bottleneck.
/// </summary>
public class IOHandler
{
    private readonly int _id;
    private readonly HyperionServer _server;
    private readonly ILogger<IOHandler> _logger;
    private bool _askingNext = false;

    public IOHandler(int id, HyperionServer server, ILogger<IOHandler> logger)
    {
        _id = id;
        _server = server;
        _logger = logger;
    }

    public void AddConnection(TcpClient client, CancellationToken cancellationToken)
    {
        _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        _logger.LogDebug("[IOHandler {Id}] Client connected: {Endpoint}", _id, endpoint);

        using (client)
        {
            var stream = client.GetStream();
            var reader = PipeReader.Create(stream);
            var writer = PipeWriter.Create(stream);

            try { await ProcessStreamAsync(reader, writer, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug("[IOHandler {Id}] Client {Endpoint} disconnected: {Reason}", _id, endpoint, ex.Message);
            }
            finally
            {
                await reader.CompleteAsync();
                await writer.CompleteAsync();
            }
        }
    }

    private async Task ProcessStreamAsync(PipeReader reader, PipeWriter writer, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await reader.ReadAsync(cancellationToken);
            var buffer = result.Buffer;
            bool wroteAny = false;

            try
            {
                // Dispatch ALL commands parsed from this read batch in parallel,
                // then collect responses in order and write them all at once.
                // This is the key: one flush per read, not one flush per command.
                while (TryParseCommand(ref buffer, out var command))
                {
                    if (command is not null)
                    {
                        if (command.Cmd == "ASKING")
                        {
                            _askingNext = true;
                            writer.Write(RespEncoder.Encode("OK", isSimpleString: true));
                            wroteAny = true;
                            continue;
                        }

                        command.IsAsking = _askingNext;
                        _askingNext = false;

                        var task = new WorkerTask(command);
                        await _server.DispatchAsync(task);
                        byte[] responseBytes = await task.ReplyCompletion.Task;

                        // Buffer the response — no syscall yet
                        writer.Write(responseBytes);
                        wroteAny = true;
                    }
                }

                // ONE flush for all responses in this batch
                if (wroteAny)
                {
                    var flushResult = await writer.FlushAsync(cancellationToken);
                    if (flushResult.IsCompleted) break;
                }

                if (result.IsCompleted) break;
            }
            finally
            {
                reader.AdvanceTo(buffer.Start, buffer.End);
            }
        }
    }

    private static bool TryParseCommand(ref ReadOnlySequence<byte> buffer, out RespCommand? command)
    {
        var reader = new SequenceReader<byte>(buffer);
        if (RespParser.TryParseCommand(ref reader, out command))
        {
            buffer = buffer.Slice(reader.Position);
            return true;
        }
        return false;
    }
}
