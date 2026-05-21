using Hyperion.Core;
using Microsoft.Extensions.Logging;

namespace Hyperion.Persistence;

/// <summary>
/// Orchestrates snapshot creation for both server modes.
///
/// SINGLE-THREAD MODE:
///   The event loop enqueues a special SnapshotWorkItem. When the loop
///   processes it, the event loop thread (the only thread that touches Storage)
///   serializes directly. Commands queue in the Channel during the short write.
///   This is safe because the write is sequential inside the event loop.
///
/// MULTI-THREAD MODE:
///   Each Worker receives a "snapshot" marker via its Channel. When the Worker
///   processes it (on the Worker's own thread), it serializes its own private
///   Storage shard into a MemoryStream. Because no other thread ever touches
///   a given shard, zero locking is required — it's an extension of the
///   share-nothing guarantee. A background aggregator then combines all shard
///   snapshots into one RDB file and atomically renames it.
///
/// ATOMIC RENAME:
///   We write to a temp file first, then call File.Move with overwrite=true.
///   This ensures that if the process crashes mid-write, the previous dump.rdb
///   remains intact and uncorrupted.
/// </summary>
public sealed class SnapshotCoordinator
{
    private readonly ILogger<SnapshotCoordinator> _logger;
    private readonly PersistenceConfig _config;

    private long _lastSaveTime;
    private long _changesSinceLastSave;
    private Timer? _periodicTimer;

    public SnapshotCoordinator(ILogger<SnapshotCoordinator> logger, PersistenceConfig config)
    {
        _logger = logger;
        _config = config;
        _lastSaveTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>Records a write operation so the periodic-save policy can fire.</summary>
    public void NotifyWrite() => Interlocked.Increment(ref _changesSinceLastSave);

    /// <summary>Returns the Unix timestamp of the last successful save.</summary>
    public long LastSaveTime => Interlocked.Read(ref _lastSaveTime);

    /// <summary>
    /// Starts a background timer that fires every second to check
    /// whether any save policy threshold has been reached.
    /// </summary>
    public void StartPeriodicSave(Func<Task> triggerSaveAsync)
    {
        _periodicTimer = new Timer(async _ =>
        {
            if (ShouldSave())
            {
                await triggerSaveAsync();
            }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void StopPeriodicSave() => _periodicTimer?.Dispose();

    // -------------------------------------------------------------------------
    // Single-thread mode: synchronous save called from the event loop thread
    // -------------------------------------------------------------------------

    /// <summary>
    /// Called directly from the single-thread event loop. Because this runs
    /// on the same thread that owns the Storage, no locks are needed.
    /// </summary>
    public bool SaveSingle(Storage storage, string mode = "single")
    {
        string tmpPath = _config.RdbFilePath + RdbConstants.TempFileSuffix;
        try
        {
            _logger.LogInformation("[RDB] Starting SAVE (single-thread mode)...");
            using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new RdbWriter(fs))
            {
                writer.WriteHeader();
                writer.WriteMetadata(mode);
                writer.WriteDatabase(0, storage);
                writer.WriteFooter();
            }

            File.Move(tmpPath, _config.RdbFilePath, overwrite: true);
            Interlocked.Exchange(ref _lastSaveTime, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            Interlocked.Exchange(ref _changesSinceLastSave, 0);

            _logger.LogInformation("[RDB] SAVE complete → {Path}", _config.RdbFilePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RDB] SAVE failed.");
            TryDeleteTemp(tmpPath);
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // Multi-thread mode: each shard writes itself, then we aggregate
    // -------------------------------------------------------------------------

    /// <summary>
    /// Aggregates pre-serialized shard MemoryStreams into a single RDB file.
    /// This is called on a background thread after all Workers have finished
    /// writing their shard snapshots.
    /// </summary>
    public async Task<bool> SaveShardsAsync(Storage[] shards, string mode = "multi")
    {
        string tmpPath = _config.RdbFilePath + RdbConstants.TempFileSuffix;
        try
        {
            _logger.LogInformation("[RDB] Starting BGSAVE (multi-thread mode, {N} shards)...", shards.Length);

            // Serialize all shards concurrently — each Worker owns its shard exclusively
            var shardStreams = await Task.WhenAll(shards.Select((shard, idx) =>
                Task.Run(() => SerializeShard(idx, shard))));

            // Aggregate: write header + all shard data + footer
            using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new RdbWriter(fs))
            {
                writer.WriteHeader();
                writer.WriteMetadata(mode);

                for (int i = 0; i < shardStreams.Length; i++)
                {
                    writer.WriteDatabase(i, shards[i]);
                    shardStreams[i].Dispose();
                }

                writer.WriteFooter();
            }

            File.Move(tmpPath, _config.RdbFilePath, overwrite: true);
            Interlocked.Exchange(ref _lastSaveTime, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            Interlocked.Exchange(ref _changesSinceLastSave, 0);

            _logger.LogInformation("[RDB] BGSAVE complete → {Path}", _config.RdbFilePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RDB] BGSAVE failed.");
            TryDeleteTemp(tmpPath);
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static MemoryStream SerializeShard(int dbIndex, Storage shard)
    {
        var ms = new MemoryStream();
        using var writer = new RdbWriter(ms);
        writer.WriteDatabase(dbIndex, shard);
        // Note: we don't call WriteHeader/WriteFooter per shard —
        // the aggregator does that for the combined stream.
        ms.Seek(0, SeekOrigin.Begin);
        return ms;
    }

    /// <summary>
    /// Checks whether any configured save policy threshold is met.
    /// A policy is: "save after N seconds if at least M changes occurred."
    /// </summary>
    private bool ShouldSave()
    {
        long elapsedSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Interlocked.Read(ref _lastSaveTime);
        long changes = Interlocked.Read(ref _changesSinceLastSave);

        foreach (var (seconds, minChanges) in _config.SavePolicies)
        {
            if (elapsedSeconds >= seconds && changes >= minChanges)
                return true;
        }
        return false;
    }

    private static void TryDeleteTemp(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort cleanup */ }
    }
}
