using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hyperion.Core;
using Hyperion.Persistence;
using Hyperion.Cluster;
using Hyperion.Protocol;
using Microsoft.Extensions.Logging;

namespace Hyperion.Server;

/// <summary>
/// The orchestrator for Hyperion's multi-threaded "share-nothing" mode.
/// Initializes Workers and IOHandlers, and routes incoming commands to the
/// appropriate Worker based on the command's primary key.
///
/// RDB PERSISTENCE (multi-thread mode):
/// - On startup: loads dump.rdb and re-partitions keys across Workers by FNV-1a hash.
/// - BGSAVE: each Worker receives a SnapshotTask via its Channel and serializes its
///   own shard on its own thread (zero locks — share-nothing guarantee applies).
///   A background Task then aggregates all shard data into dump.rdb.
/// - SAVE: same as BGSAVE but the caller awaits the result synchronously.
/// - Shutdown: a final synchronous save before the process exits.
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

    private readonly SnapshotCoordinator _snapshot;
    private readonly ClusterState? _clusterState;
    private readonly ClusterProvider? _clusterProvider;

    private int _nextIOHandlerIndex = 0;
    private TcpListener? _listener;

    public HyperionServer(
        ILoggerFactory loggerFactory,
        int port,
        int numWorkers = 0,
        int numIOHandlers = 0,
        int delayUs = 0,
        PersistenceConfig? persistenceConfig = null,
        ClusterState? clusterState = null)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<HyperionServer>();
        _port   = port;
        _clusterState = clusterState;
        if (_clusterState != null)
        {
            _clusterProvider = new ClusterProvider(_clusterState);
        }

        int processorCount = Environment.ProcessorCount;
        _numWorkers    = numWorkers    > 0 ? numWorkers    : Math.Max(1, processorCount / 2);
        _numIOHandlers = numIOHandlers > 0 ? numIOHandlers : Math.Max(1, processorCount / 2);

        _logger.LogInformation(
            "Initializing Multi-Threaded Server. Workers: {Workers}, IO Handlers: {IOHandlers}, Cluster: {ClusterEnabled}",
            _numWorkers, _numIOHandlers, _clusterState != null);

        var config = persistenceConfig ?? new PersistenceConfig();
        _snapshot = new SnapshotCoordinator(
            loggerFactory.CreateLogger<SnapshotCoordinator>(), config);

        // --- Create Workers ---
        _workers = new Worker[_numWorkers];
        for (int i = 0; i < _numWorkers; i++)
            _workers[i] = new Worker(i, bufferSize: 10000, delayUs: delayUs);

        // --- Load RDB and distribute keys across shards ---
        var reader = new RdbReader(config.RdbFilePath);
        var shards = reader.LoadSharded(_numWorkers);
        if (shards != null)
        {
            // Copy loaded keys into each worker's private storage
            for (int i = 0; i < _numWorkers; i++)
                MergeStorage(shards[i], _workers[i].Storage);

            _logger.LogInformation("[RDB] Loaded '{Path}' across {N} shards.", config.RdbFilePath, _numWorkers);
        }

        // Wire persistence callbacks
        WirePersistenceToWorkers();

        // --- Create IOHandlers ---
        _ioHandlers = new IOHandler[_numIOHandlers];
        for (int i = 0; i < _numIOHandlers; i++)
            _ioHandlers[i] = new IOHandler(i, this, _loggerFactory.CreateLogger<IOHandler>());
    }

    // -------------------------------------------------------------------------
    // Persistence wiring
    // -------------------------------------------------------------------------

    private void WirePersistenceToWorkers()
    {
        // Each Worker's executor needs callbacks for SAVE/BGSAVE/LASTSAVE.
        // Because Workers are internal, we wire the callbacks on the first Worker's
        // executor (which is what the IOHandler routes persistence commands to).
        // In practice, SAVE/BGSAVE are global operations, so any worker's executor
        // can handle them — the callback reaches the SnapshotCoordinator either way.
        //
        // For write tracking, each Worker's executor fires OnWriteCommand independently.
        foreach (var worker in _workers)
        {
            var executor = worker.GetExecutor();
            executor.OnWriteCommand   = _snapshot.NotifyWrite;
            executor.GetLastSaveTime  = () => _snapshot.LastSaveTime;
            executor.OnSave           = () => SaveAllSync();
            executor.OnBgSave         = () => SaveAllAsync();
            if (_clusterProvider != null)
            {
                executor.ClusterProvider = _clusterProvider;
            }
        }
    }

    private bool SaveAllSync()
    {
        var storages = _workers.Select(w => w.Storage).ToArray();
        return _snapshot.SaveShardsAsync(storages, "multi").GetAwaiter().GetResult();
    }

    private Task<bool> SaveAllAsync()
    {
        var storages = _workers.Select(w => w.Storage).ToArray();
        return _snapshot.SaveShardsAsync(storages, "multi");
    }

    // -------------------------------------------------------------------------
    // Startup merge: copy loaded shard data into the Worker's existing Storage
    // -------------------------------------------------------------------------

    private static void MergeStorage(Storage src, Storage dst)
    {
        // Merge Dict (strings + TTLs)
        var expiry = src.DictStore.GetExpireDictStore();
        foreach (var kv in src.DictStore.GetAllEntries())
        {
            long ttlMs = -1;
            if (expiry.TryGetValue(kv.Key, out long exp))
            {
                long remain = exp - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (remain <= 0) continue; // expired
                ttlMs = remain;
            }
            var obj = dst.DictStore.NewObj(kv.Key, kv.Value.Value, ttlMs);
            dst.DictStore.Set(kv.Key, obj);
        }

        foreach (var kv in src.HashStore)  dst.HashStore[kv.Key]  = kv.Value;
        foreach (var kv in src.ListStore)  dst.ListStore[kv.Key]  = kv.Value;
        foreach (var kv in src.SetStore)   dst.SetStore[kv.Key]   = kv.Value;
        foreach (var kv in src.ZSetStore)  dst.ZSetStore[kv.Key]  = kv.Value;
        foreach (var kv in src.BloomStore) dst.BloomStore[kv.Key] = kv.Value;
        foreach (var kv in src.CmsStore)   dst.CmsStore[kv.Key]   = kv.Value;
    }

    // -------------------------------------------------------------------------
    // Routing
    // -------------------------------------------------------------------------

    /// <summary>
    /// Routes the task to the correct worker. Handles cross-slot routing for multi-key commands like DEL.
    /// </summary>
    public async ValueTask DispatchAsync(WorkerTask task)
    {
        if (task.Command.Cmd == "KEYS")
        {
            await DispatchKeysAsync(task);
            return;
        }

        // In cluster mode, multi-key commands must map to the same slot, which means they map to the same partition.
        // We let CommandExecutor handle the CROSSSLOT error if they don't.
        if (_clusterProvider == null && task.Command.Cmd == "DEL" && task.Command.Args.Length > 1)
        {
            var groups = task.Command.Args.GroupBy(GetPartitionId).ToList();

            if (groups.Count == 1)
            {
                await _workers[groups[0].Key].EnqueueTaskAsync(task);
                return;
            }

            var subTasks = new System.Collections.Generic.List<WorkerTask>();
            foreach (var group in groups)
            {
                var subCommand = new Protocol.RespCommand { Cmd = "DEL", Args = System.Linq.Enumerable.ToArray(group) };
                var subTask = new WorkerTask(subCommand);
                subTasks.Add(subTask);
                await _workers[group.Key].EnqueueTaskAsync(subTask);
            }

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
                    task.ReplyCompletion.TrySetException(error);
                else
                    task.ReplyCompletion.TrySetResult(Protocol.RespEncoder.Encode(totalDeleted, isSimpleString: false));
            });
        }
        else
        {
            int workerId;
            string cmd = task.Command.Cmd.ToUpperInvariant();
            if (cmd == "CLUSTER" && task.Command.Args.Length >= 2 && 
                (task.Command.Args[0].ToUpperInvariant() == "GETKEYSINSLOT" || 
                 task.Command.Args[0].ToUpperInvariant() == "COUNTKEYSINSLOT"))
            {
                if (int.TryParse(task.Command.Args[1], out int slot))
                    workerId = slot % _numWorkers;
                else
                    workerId = Random.Shared.Next(_numWorkers);
            }
            else
            {
                string routingKey = task.Command.Args.Length > 0 ? task.Command.Args[0] : "";
                if (cmd == "MIGRATE" && task.Command.Args.Length > 2) routingKey = task.Command.Args[2];

                workerId = string.IsNullOrEmpty(routingKey)
                    ? Random.Shared.Next(_numWorkers)
                    : GetPartitionId(routingKey);
            }
            await _workers[workerId].EnqueueTaskAsync(task);
        }
    }

    private async ValueTask DispatchKeysAsync(WorkerTask task)
    {
        if (task.Command.Args.Length != 1)
        {
            await _workers[0].EnqueueTaskAsync(task);
            return;
        }

        var subTasks = new List<WorkerTask>(_numWorkers);
        for (int i = 0; i < _numWorkers; i++)
        {
            var subCommand = new RespCommand
            {
                Cmd = task.Command.Cmd,
                Args = task.Command.Args
            };
            var subTask = new WorkerTask(subCommand);
            subTasks.Add(subTask);
            await _workers[i].EnqueueTaskAsync(subTask);
        }

        _ = Task.Run(async () =>
        {
            var keys = new List<string>();
            Exception? error = null;

            foreach (var subTask in subTasks)
            {
                try
                {
                    var responseBytes = await subTask.ReplyCompletion.Task;
                    keys.AddRange(DecodeStringArray(responseBytes));
                }
                catch (Exception ex)
                {
                    error = ex;
                    break;
                }
            }

            if (error != null)
                task.ReplyCompletion.TrySetException(error);
            else
                task.ReplyCompletion.TrySetResult(RespEncoder.Encode(keys.ToArray()));
        });
    }

    private static IEnumerable<string> DecodeStringArray(byte[] responseBytes)
    {
        RespDecoder.Decode(responseBytes, out var result, out _);
        if (result is object[] items)
        {
            foreach (var item in items)
            {
                if (item is string key)
                    yield return key;
            }
        }
        else if (result is Exception ex)
        {
            throw ex;
        }
    }

    private int GetPartitionId(string key)
    {
        if (_clusterState != null)
        {
            int slot = ClusterState.GetSlotForKey(key);
            return slot % _numWorkers;
        }

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
    /// FNV-1a hash using stackalloc — zero heap allocation per routing call.
    /// Previous version called Encoding.UTF8.GetBytes(key) which allocated a new byte[]
    /// on every single command dispatch (100k+ allocations/sec at target RPS).
    /// </summary>
    private static uint Fnv1aHash(string key)
    {
        const uint fnvPrime = 16777619;
        const uint fnvOffsetBasis = 2166136261;

        uint hash = fnvOffsetBasis;

        // stackalloc: bytes live on the stack frame, zero GC pressure.
        // 256 bytes covers keys up to 256 chars in ASCII/UTF-8 single-byte range.
        // For longer keys (rare), fall back to heap array.
        int maxBytes = Encoding.UTF8.GetMaxByteCount(key.Length);
        if (maxBytes <= 256)
        {
            Span<byte> bytes = stackalloc byte[maxBytes];
            int written = Encoding.UTF8.GetBytes(key, bytes);
            for (int i = 0; i < written; i++)
            {
                hash ^= bytes[i];
                hash *= fnvPrime;
            }
        }
        else
        {
            // Fallback for very long keys (>~85 chars with multibyte chars)
            byte[] bytes = Encoding.UTF8.GetBytes(key);
            foreach (byte b in bytes)
            {
                hash ^= b;
                hash *= fnvPrime;
            }
        }

        return hash;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _snapshot.StartPeriodicSave(() => SaveAllAsync().ContinueWith(_ => { }));

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

                int ioHandlerIndex = Math.Abs(Interlocked.Increment(ref _nextIOHandlerIndex) % _numIOHandlers);
                _ioHandlers[ioHandlerIndex].AddConnection(client, cancellationToken);
            }
        }
        finally
        {
            _snapshot.StopPeriodicSave();
            // Final synchronous save before exit
            SaveAllSync();
            _listener.Stop();
            _logger.LogInformation("Hyperion server stopped.");
        }
    }
}
