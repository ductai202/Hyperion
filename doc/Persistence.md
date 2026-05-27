# RDB Persistence

Hyperion implements a custom binary snapshot format inspired by Redis RDB, adapted for Hyperion's C#/.NET runtime and share-nothing architecture.

---

## Why RDB (Not AOF)

| Property | RDB (Hyperion) | AOF |
|---|---|---|
| **File size** | Compact binary | Large (all commands) |
| **Restart time** | Milliseconds | Seconds–minutes (replay) |
| **Data loss** | Up to last save interval | Near zero (if `fsync always`) |
| **Complexity** | Medium | Medium |

RDB loads the full state of the server in milliseconds because it is a direct memory dump, not a log of commands to re-execute. This is the same reason Redis defaults to RDB.

---

## File Format

```
[Header: "REDIS0011"]
[0xFA AuxField: "hyperion-ver" → "1.0.0"]
[0xFA AuxField: "ctime"        → unix timestamp]
[0xFA AuxField: "used-mem"    → heap bytes]
[0xFA AuxField: "mode"        → "single" | "multi"]
[0xFE SelectDB: db-index]
[0xFB ResizeDB: key-count, expiry-count]
[for each key:
    [optional: 0xFC + 8-byte little-endian expiry-ms]
    [type-byte]
    [key: length-encoded string]
    [value: type-specific encoding]
]
[0xFF EOF]
[8-byte CRC64 checksum, little-endian]
```

### Opcodes

| Byte | Name | Purpose |
|---|---|---|
| `0xFA` | `AUX` | Metadata field (key-value string pair) |
| `0xFE` | `SELECTDB` | Database index selector |
| `0xFB` | `RESIZEDB` | Hint: (dict size, expiry size) |
| `0xFC` | `EXPIREMS` | 8-byte absolute expiry timestamp in milliseconds |
| `0xFF` | `EOF` | End of file; CRC64 follows |

### Value Type Bytes

| Byte | Type | Encoding |
|---|---|---|
| `0` | String | Length-encoded UTF-8 string |
| `1` | List | Length prefix + N strings |
| `2` | Set | Length prefix + N strings |
| `3` | ZSet | Length prefix + N (string, double) pairs |
| `4` | Hash | Length prefix + N (field, value) string pairs |
| `10` | Bloom Filter | `entries` (u64) + `error` (f64) + raw bit-array bytes |
| `11` | Count-Min Sketch | `width` (u32) + `depth` (u32) + row-major counter matrix |

### Length Encoding

Variable-length encoding (same as Redis) to minimize file size for small lengths:

| Top 2 bits | Width | Max value |
|---|---|---|
| `00` | 6-bit | 63 |
| `01` | 14-bit | 16,383 |
| `10` | 32-bit (big-endian) | 4,294,967,295 |

### CRC64 Checksum

The last 8 bytes of the file are the CRC64/Jones checksum of all preceding bytes. On load, Hyperion recomputes the checksum and refuses to load a file with a mismatch, protecting against disk errors and incomplete writes.

**Why CRC not MD5/SHA?**  
CRC is a mathematical error-detection algorithm — not a security hash. It is ~30x faster than MD5 for the same purpose (detecting accidental bit-flips). Security is irrelevant here because the RDB file is always local.

---

## Atomic Write Strategy

Hyperion never writes directly to `dump.rdb`. Instead:

1. Write all data to `dump.rdb.tmp`
2. `File.Move("dump.rdb.tmp", "dump.rdb", overwrite: true)`

An OS-level `rename()` is atomic on both Linux and Windows. If the process crashes mid-write, `dump.rdb` remains the last known-good snapshot. `dump.rdb.tmp` is a partial file and is ignored on restart.

---

## Persistence in Single-Thread Mode

```
Clients → IO Handlers → Channel<WorkItem>
                              ↓
                   Event Loop Thread (sole owner of Storage)
                        ↓         ↓
                    Commands    SnapshotWorkItem
                                    ↓
                               RdbWriter.SaveSingle()
```

When a save is triggered (timer or `SAVE` command), a `SnapshotWorkItem` is enqueued into the same Channel the event loop reads commands from. The event loop processes it like any other item — it is the only thread that ever touches Storage, so no locks are needed. Normal commands queue during the save (typically < 100ms for 1M keys).

---

## Persistence in Multi-Thread Mode

```
Worker 0 (owns shard 0) ──→ snapshot shard 0
Worker 1 (owns shard 1) ──→ snapshot shard 1    → SnapshotCoordinator → dump.rdb
Worker N (owns shard N) ──→ snapshot shard N
```

The current `SnapshotCoordinator.SaveShardsAsync()` implementation:
1. Collects the `Storage` reference for each Worker.
2. Starts background `Task.Run` serialization work for each shard.
3. Writes the combined RDB file from the live shard references, then atomically renames the `.tmp` file.

This matches the code currently in `SnapshotCoordinator`, but it is weaker than the ideal share-nothing snapshot design: the save path does not yet enqueue a snapshot marker into each Worker's channel before reading that Worker's shard, and the temporary per-shard streams produced by `SerializeShard()` are not used by the final writer. That means concurrent writes can overlap with multi-thread `BGSAVE`/periodic saves. The intended next step is to serialize each shard on its owning Worker event loop, then aggregate immutable shard buffers in the background.

On load, `RdbReader.LoadSharded()` reads the file and distributes each key to the correct shard using FNV-1a hash — the same routing function used by the live server. This ensures every key after a restart lands on the same Worker it would have for a fresh `SET`.

---

## Save Policies

Configured in `PersistenceConfig.SavePolicies` as a list of `(afterSeconds, ifAtLeastChanges)` pairs. A snapshot is triggered when **any** policy is satisfied.

**Defaults (mirrors Redis):**

| Save after | If at least |
|---|---|
| 3600 seconds (1 hour) | 1 change |
| 300 seconds (5 min) | 100 changes |
| 60 seconds | 10,000 changes |

### CLI Configuration

```bash
# Default RDB path: ./dump.rdb
./Hyperion.Server --port 3000

# Custom directory and filename
./Hyperion.Server --port 3000 --dir /var/lib/hyperion --dbfilename hyperion.rdb

# Disable persistence
./Hyperion.Server --port 3000 --no-save
```

### Manual Commands

| Command | Behaviour |
|---|---|
| `SAVE` | Blocking save on the event-loop thread. Returns `+OK` when done. |
| `BGSAVE` | Non-blocking save. Returns immediately; save runs in background. |
| `LASTSAVE` | Returns Unix timestamp of the last successful save. |
| `DBSIZE` | Returns the total number of tracked keys. |

`DBSIZE` currently reads the global keyspace stats counter. Expired keys may remain counted until they are removed by lazy access/scan or by active expiry in the event loops.

---

## Key Design Decisions

### Why not fork() like Redis?
`fork()` + Copy-on-Write is Linux-only and incompatible with .NET's GC. The GC maintains internal write barriers and thread-safety structures that break under `fork()`. Hyperion instead relies on its **share-nothing** architecture and custom RDB writer. The current multi-thread save implementation still reads live shard references from background tasks; moving shard serialization onto each owning Worker event loop is the remaining step needed to fully preserve the zero-contention snapshot model.

### Why CRC64 over CRC32?
At 1 billion keys, the probability of an undetected CRC32 collision is ~1 in 4 billion (~0.000000023%). CRC64 reduces that to negligible. The cost is 8 bytes vs 4 bytes at the end of the file — negligible.

### Why not a Redis-compatible RDB format?
Redis RDB does not define types for Bloom Filters or Count-Min Sketch (they are Modules in Redis). A fully Redis-compatible format would require omitting or corrupting these keys, which is worse than using a custom extension. Hyperion's format is documented here — it is straightforward to write a converter if needed.
