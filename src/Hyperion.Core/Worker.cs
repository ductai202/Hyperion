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
    
    private readonly Storage _storage;
    private readonly CommandExecutor _executor;
    
    // Channel is a thread-safe queue for async consumer-producer patterns
    private readonly Channel<WorkerTask> _taskChannel;

    public Worker(int id, int bufferSize = 1024, int delayUs = 0)
    {
        Id = id;
        
        // Each worker gets its OWN private storage instance. This guarantees that
        // as long as a specific key is always routed to the same worker, it will
        // never experience race conditions, eliminating the need for locks.
        _storage = new Storage();
        _executor = new CommandExecutor(_storage) { DelayUs = delayUs };
        
        // Bounded channel to prevent infinite memory usage under heavy load
        _taskChannel = Channel.CreateBounded<WorkerTask>(new BoundedChannelOptions(bufferSize)
        {
            SingleReader = true, // We have only one reader (the Run loop)
            SingleWriter = false // Multiple IO handlers can write to this channel
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

    private async Task RunLoopAsync()
    {
        // Continuously read tasks from the channel as they arrive
        await foreach (var task in _taskChannel.Reader.ReadAllAsync())
        {
            try
            {
                // Execute the command synchronously inside the worker thread
                byte[] response = _executor.Execute(task.Command);
                
                // Notify the waiting IO handler that the result is ready
                task.ReplyCompletion.TrySetResult(response);
            }
            catch (Exception ex)
            {
                // If anything crashes during execution, send the error back
                // instead of crashing the worker loop
                task.ReplyCompletion.TrySetException(ex);
            }
        }
    }
}
