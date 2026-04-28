using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hyperion.Core;
using Microsoft.Extensions.Logging;

namespace Hyperion.Server;

/// <summary>
/// The orchestrator for Hyperion's multi-threaded "share-nothing" mode.
/// Initializes Workers and IOHandlers, and routes incoming commands to the
/// appropriate Worker based on the command's primary key.
/// </summary>
public sealed class HyperionServer
{
    private readonly ILogger<HyperionServer> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly int _port;
    
    private readonly Worker[] _workers;
    private readonly IOHandler[] _ioHandlers;
    
    private readonly int _numWorkers;
    private readonly int _numIOHandlers;
    
    private int _nextIOHandlerIndex = 0;
    private TcpListener? _listener;

    public HyperionServer(ILoggerFactory loggerFactory, int port, int numWorkers = 0, int numIOHandlers = 0, int delayUs = 0)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<HyperionServer>();
        _port = port;

        // Default to half cores for workers, half for IO handlers (similar to Dragonfly/Nietzsche)
        int processorCount = Environment.ProcessorCount;
        _numWorkers = numWorkers > 0 ? numWorkers : Math.Max(1, processorCount / 2);
        _numIOHandlers = numIOHandlers > 0 ? numIOHandlers : Math.Max(1, processorCount / 2);

        _logger.LogInformation("Initializing Multi-Threaded Server. Workers: {Workers}, IO Handlers: {IOHandlers}", _numWorkers, _numIOHandlers);

        // Initialize Workers
        _workers = new Worker[_numWorkers];
        for (int i = 0; i < _numWorkers; i++)
        {
            _workers[i] = new Worker(i, bufferSize: 10000, delayUs: delayUs);
        }

        // Initialize IO Handlers
        _ioHandlers = new IOHandler[_numIOHandlers];
        for (int i = 0; i < _numIOHandlers; i++)
        {
            _ioHandlers[i] = new IOHandler(i, this, _loggerFactory.CreateLogger<IOHandler>());
        }
    }

    /// <summary>
    /// Routes the task to the correct worker. Handles cross-slot routing for multi-key commands like DEL.
    /// </summary>
    public async ValueTask DispatchAsync(WorkerTask task)
    {
        if (task.Command.Cmd == "DEL" && task.Command.Args.Length > 1)
        {
            // Scatter-Gather multi-key DEL
            var groups = task.Command.Args.GroupBy(GetPartitionId).ToList();

            if (groups.Count == 1)
            {
                // All keys map to the same worker, dispatch normally
                await _workers[groups[0].Key].EnqueueTaskAsync(task);
                return;
            }

            // Scatter phase
            var subTasks = new System.Collections.Generic.List<WorkerTask>();
            foreach (var group in groups)
            {
                var subCommand = new Protocol.RespCommand { Cmd = "DEL", Args = System.Linq.Enumerable.ToArray(group) };
                var subTask = new WorkerTask(subCommand);
                subTasks.Add(subTask);
                await _workers[group.Key].EnqueueTaskAsync(subTask);
            }

            // Gather phase
            _ = Task.Run(async () =>
            {
                long totalDeleted = 0;
                Exception? error = null;

                foreach (var subTask in subTasks)
                {
                    try
                    {
                        var responseBytes = await subTask.ReplyCompletion.Task;
                        string respStr = Encoding.UTF8.GetString(responseBytes);
                        if (respStr.StartsWith(":") && respStr.EndsWith("\r\n"))
                        {
                            if (long.TryParse(respStr.Substring(1, respStr.Length - 3), out long deleted))
                            {
                                totalDeleted += deleted;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        error = ex;
                    }
                }

                if (error != null)
                {
                    task.ReplyCompletion.TrySetException(error);
                }
                else
                {
                    task.ReplyCompletion.TrySetResult(Protocol.RespEncoder.Encode(totalDeleted, isSimpleString: false));
                }
            });
        }
        else
        {
            int workerId = task.Command.Args.Length > 0 ? GetPartitionId(task.Command.Args[0]) : Random.Shared.Next(_numWorkers);
            await _workers[workerId].EnqueueTaskAsync(task);
        }
    }

    /// <summary>
    /// Implements Hash Tags (Redis Cluster style) to map related keys to the same worker,
    /// then uses FNV-1a hash to consistently map the key (or tag) to a specific worker.
    /// </summary>
    private int GetPartitionId(string key)
    {
        int start = key.IndexOf('{');
        if (start != -1)
        {
            int end = key.IndexOf('}', start + 1);
            if (end != -1 && end > start + 1)
            {
                key = key.Substring(start + 1, end - start - 1);
            }
        }
        return (int)(Fnv1aHash(key) % (uint)_numWorkers);
    }

    /// <summary>
    /// Implements the FNV-1a (Fowler-Noll-Vo) hash algorithm.
    /// 
    /// Why FNV-1a?
    /// 1. Speed: It is a non-cryptographic hash that is extremely fast, making it ideal for 
    ///    the hot path of a high-performance database.
    /// 2. Distribution: It provides excellent distribution for short strings (common for Redis keys),
    ///    ensuring even load balancing across worker shards.
    /// 3. Simplicity: The algorithm is small and requires no complex look-up tables or external dependencies.
    /// 4. Share-Nothing: It provides the deterministic routing required to ensure a key 
    ///    always maps to the same worker thread without cross-thread coordination.
    /// </summary>
    private static uint Fnv1aHash(string key)
    {
        const uint fnvPrime = 16777619;
        const uint fnvOffsetBasis = 2166136261;

        uint hash = fnvOffsetBasis;
        byte[] bytes = Encoding.UTF8.GetBytes(key);
        
        foreach (byte b in bytes)
        {
            hash ^= b;
            hash *= fnvPrime;
        }

        return hash;
    }

    /// <summary>
    /// Starts accepting incoming connections and round-robins them to the IO Handlers.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        _logger.LogInformation("Hyperion (multi-thread) listening on :{Port}", _port);

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

                // Round-robin assignment to an IO Handler
                int ioHandlerIndex = Interlocked.Increment(ref _nextIOHandlerIndex) % _numIOHandlers;
                
                // Absolute value to handle potential integer overflow turning negative
                ioHandlerIndex = Math.Abs(ioHandlerIndex);
                
                _ioHandlers[ioHandlerIndex].AddConnection(client, cancellationToken);
            }
        }
        finally
        {
            _listener.Stop();
            _logger.LogInformation("Hyperion server stopped.");
        }
    }
}
