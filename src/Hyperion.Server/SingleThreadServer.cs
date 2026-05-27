using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Hyperion.Core;
using Hyperion.Persistence;
using Hyperion.Protocol;
using Microsoft.Extensions.Logging;

namespace Hyperion.Server;

/// <summary>
/// True single-threaded RESP server.
/// Key insight for high performance:
/// - ONE dedicated OS thread (LongRunning) owns all command execution — no locks.
/// - Responses are batched: all commands parsed from a single read are executed
///   and written into a PipeWriter buffer, then flushed ONCE per batch.
///   This eliminates the per-response syscall that was the 15k RPS ceiling.
///
/// RDB PERSISTENCE (single-thread mode):
/// - On startup: loads dump.rdb into Storage before the event loop starts.
/// - SAVE: enqueued as a special WorkItem; the event loop serializes Storage
///   synchronously (safe — only one thread touches Storage).
/// - Periodic saves: SnapshotCoordinator checks write-count thresholds every second.
/// - Shutdown: a final synchronous SAVE is performed before the process exits.
/// </summary>
public sealed class SingleThreadServer
{
    private readonly ICommandExecutor _executor;
    private readonly CommandExecutor _executorImpl;
    private readonly ILogger<SingleThreadServer> _logger;
    private readonly int _port;
    private readonly SnapshotCoordinator _snapshot;
    private readonly Storage _storage;
    private TcpListener? _listener;
    private const int ActiveExpiryInterval = 100;
    private int _commandsSinceExpiry;

    private readonly Channel<WorkItem> _workChannel = Channel.CreateUnbounded<WorkItem>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

    public SingleThreadServer(
        ILogger<SingleThreadServer> logger,
        int port,
        PersistenceConfig? persistenceConfig = null,
        int delayUs = 0)
    {
        _logger  = logger;
        _port    = port;
        _storage = new Storage();

        // Load RDB before creating the executor so the Storage is pre-populated
        var config  = persistenceConfig ?? new PersistenceConfig();
        var reader  = new RdbReader(config.RdbFilePath);
        var loaded  = reader.LoadSingle();
        if (loaded != null)
        {
            // Replace the empty storage with the loaded one
            _storage = loaded;
            _logger.LogInformation("[RDB] Loaded '{Path}'.", config.RdbFilePath);
        }

        _executorImpl = new CommandExecutor(_storage) { DelayUs = delayUs };
        _executor     = _executorImpl;

        // Wire persistence callbacks into the executor
        _snapshot = new SnapshotCoordinator(
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance
                .CreateLogger<SnapshotCoordinator>(),
            config);

        _executorImpl.OnWriteCommand = _snapshot.NotifyWrite;
        _executorImpl.GetLastSaveTime = () => _snapshot.LastSaveTime;

        // SAVE runs synchronously on the event-loop thread via the work channel
        _executorImpl.OnSave = () => _snapshot.SaveSingle(_storage);

        // BGSAVE: in single-thread mode we enqueue a SaveWorkItem that runs
        // on the event-loop thread (same as SAVE, but returns immediately)
        _executorImpl.OnBgSave = async () =>
        {
            bool ok = false;
            var tcs = new TaskCompletionSource<bool>();
            // Enqueue a special save item
            await _workChannel.Writer.WriteAsync(new WorkItem(null, tcs));
            ok = await tcs.Task;
            return ok;
        };

        Task.Factory.StartNew(RunEventLoopAsync, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _snapshot.StartPeriodicSave(async () =>
        {
            // Route through the event loop to stay single-threaded
            var tcs = new TaskCompletionSource<bool>();
            await _workChannel.Writer.WriteAsync(new WorkItem(null, tcs));
            await tcs.Task;
        });

        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        _logger.LogInformation("Hyperion (single-thread) listening on :{Port}", _port);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(cancellationToken); }
                catch (OperationCanceledException) { break; }
                _ = Task.Run(() => HandleConnectionAsync(client, cancellationToken), cancellationToken);
            }
        }
        finally
        {
            _snapshot.StopPeriodicSave();
            _workChannel.Writer.Complete();

            // Flush a final save before shutdown
            _snapshot.SaveSingle(_storage);
            _listener.Stop();
        }
    }

    private async Task RunEventLoopAsync()
    {
        await foreach (var item in _workChannel.Reader.ReadAllAsync())
        {
            try
            {
                if (item.Command == null)
                {
                    // Snapshot work item — serialize on this thread (safe: sole owner)
                    bool ok = _snapshot.SaveSingle(_storage);
                    item.SaveCompletion?.TrySetResult(ok);
                }
                else
                {
                    byte[] response = _executor.Execute(item.Command);
                    if (++_commandsSinceExpiry >= ActiveExpiryInterval)
                    {
                        _commandsSinceExpiry = 0;
                        _executorImpl.RunActiveExpiry();
                    }

                    item.Completion!.TrySetResult(response);
                }
            }
            catch (Exception ex)
            {
                item.Completion?.TrySetException(ex);
                item.SaveCompletion?.TrySetResult(false);
            }
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            var stream = client.GetStream();
            var reader = PipeReader.Create(stream);
            // PipeWriter buffers writes and flushes in batches — eliminates per-response syscall
            var writer = PipeWriter.Create(stream);

            try { await ProcessClientAsync(reader, writer, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug("Client disconnected: {Reason}", ex.Message);
            }
            finally
            {
                await reader.CompleteAsync();
                await writer.CompleteAsync();
            }
        }
    }

    private async Task ProcessClientAsync(PipeReader reader, PipeWriter writer, CancellationToken cancellationToken)
    {
        bool askingNext = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await reader.ReadAsync(cancellationToken);
            var buffer = result.Buffer;
            bool wroteAny = false;

            try
            {
                // Process ALL complete commands from this read in one batch
                while (TryParseCommand(ref buffer, out var command))
                {
                    if (command is not null)
                    {
                        if (command.Cmd == "ASKING")
                        {
                            askingNext = true;
                            writer.Write(RespEncoder.Encode("OK", isSimpleString: true));
                            wroteAny = true;
                            continue;
                        }

                        command.IsAsking = askingNext;
                        askingNext = false;

                        var item = new WorkItem(command);
                        await _workChannel.Writer.WriteAsync(item, cancellationToken);
                        byte[] response = await item.Completion!.Task;

                        // Write into PipeWriter buffer — no syscall yet
                        writer.Write(response);
                        wroteAny = true;
                    }
                }

                // Single flush for the entire batch — ONE syscall for all responses
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

    private sealed class WorkItem
    {
        public RespCommand? Command { get; }
        public TaskCompletionSource<byte[]>? Completion { get; }
        public TaskCompletionSource<bool>? SaveCompletion { get; }

        /// <summary>Command work item.</summary>
        public WorkItem(RespCommand command)
        {
            Command    = command;
            Completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>Snapshot work item (null command = save request).</summary>
        public WorkItem(RespCommand? command, TaskCompletionSource<bool> saveCompletion)
        {
            Command        = command;
            SaveCompletion = saveCompletion;
        }
    }
}
