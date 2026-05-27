using System;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Hyperion.Core;

/// <summary>
/// A dedicated execution thread that owns a private shard of data (Storage).
/// </summary>
public class Worker
{
    public int Id { get; }

    // Exposed for startup load (RdbReader fills Storage before commands arrive)
    // and for snapshot (SnapshotCoordinator can pass the array of storages to RdbWriter).
    public Storage Storage => _storage;

    private readonly Storage _storage;
    private readonly CommandExecutor _executor;

    // Channel is a thread-safe queue for async consumer-producer patterns
    private readonly Channel<WorkerTask> _taskChannel;

    /// <summary>
    /// How many commands to process between active-expiry sweeps.
    /// Redis runs expiry checks once per event-loop iteration (~100 commands).
    /// We use a similar cadence to keep expired keys from accumulating.
    /// </summary>
    private const int ActiveExpiryInterval = 100;
    private int _commandsSinceExpiry = 0;

    public Worker(int id, int bufferSize = 1024, int delayUs = 0)
    {
        Id = id;

        // Each worker gets its OWN private storage instance. This guarantees that
        // as long as a specific key is always routed to the same worker, it will
        // never experience race conditions, eliminating the need for locks.
        _storage  = new Storage();
        _executor = new CommandExecutor(_storage) { DelayUs = delayUs };

        // Bounded channel to prevent infinite memory usage under heavy load
        _taskChannel = Channel.CreateBounded<WorkerTask>(new BoundedChannelOptions(bufferSize)
        {
            SingleReader = true,  // We have only one reader (the Run loop)
            SingleWriter = false  // Multiple IO handlers can write to this channel
        });

        // Start the background execution loop
        _ = Task.Run(RunLoopAsync);
    }

    /// <summary>
    /// Enqueues a task to be processed by this worker.
    /// </summary>
    public async ValueTask EnqueueTaskAsync(WorkerTask task)
    {
        await _taskChannel.Writer.WriteAsync(task);
    }

    /// <summary>
    /// Exposes the underlying CommandExecutor so the server can wire
    /// persistence callbacks (OnSave, OnBgSave, etc.) after construction.
    /// </summary>
    public CommandExecutor GetExecutor() => _executor;

    private async Task RunLoopAsync()
    {
        // Continuously read tasks from the channel as they arrive
        await foreach (var task in _taskChannel.Reader.ReadAllAsync())
        {
            try
            {
                if (task.Kind == TaskKind.Snapshot)
                {
                    // Serialize this worker's own shard. Safe with zero locks because
                    // this runs on the worker thread — the sole owner of _storage.
                    HandleSnapshot(task);
                }
                else
                {
                    // Execute the command synchronously inside the worker thread
                    byte[] response = _executor.Execute(task.Command);
                    _storage.IncrementDirty();

                    // Notify the waiting IO handler that the result is ready
                    // Periodic active expiry sweep — delete stale expired keys
                    // in the background so they don't accumulate between client accesses.
                    if (++_commandsSinceExpiry >= ActiveExpiryInterval)
                    {
                        _commandsSinceExpiry = 0;
                        _executor.RunActiveExpiry();
                    }
                    task.ReplyCompletion.TrySetResult(response);
                }
            }
            catch (Exception ex)
            {
                // If anything crashes during execution, send the error back
                // instead of crashing the worker loop
                task.ReplyCompletion.TrySetException(ex);
            }
        }
    }

    private void HandleSnapshot(WorkerTask task)
    {
        try
        {
            // Signal success with an empty byte[] (the coordinator reads Storage directly)
            task.ReplyCompletion.TrySetResult(Array.Empty<byte>());
        }
        catch (Exception ex)
        {
            task.ReplyCompletion.TrySetException(ex);
        }
    }
}
