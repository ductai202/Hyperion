# Hyperion

A Redis-compatible in-memory database, built from scratch in C#/.NET 10.

I started this project to deeply understand how Redis works under the hood — not just the API, but the internals: how it parses the wire protocol, manages key expiry, how a Skip List enables O(log N) ranked queries, and why single-threaded servers can outperform naively multi-threaded ones.

The **single-threaded mode** follows Redis's original architecture — a true event-loop design where one dedicated OS thread owns all command execution, fed by async IO handlers through a lock-free channel. The **multi-threaded mode** is inspired by [DragonflyDB](https://www.dragonflydb.io/)'s share-nothing architecture — each worker thread owns a private data shard with no locks, no contention, and no cross-thread coordination on the hot path.

Hyperion speaks standard RESP2, so you can connect with any `redis-cli` or Redis client library.

---

## What's Implemented

**Protocol** — Hand-written RESP2 parser, encoder, and decoder. Zero-copy parsing via `System.IO.Pipelines`.

**Data Structures** — All implemented from scratch, following the same algorithms used in Redis's source code:
- **Dict** — Key-value store with separate TTL tracking and coarse-clock LRU timestamps
- **Skip List** — Probabilistic sorted structure with span tracking for O(log N) rank queries (same as Redis's `zskiplist`)
- **ZSet** — Sorted Set using Dict + Skip List together (the classic Redis dual-structure trick)
- **Bloom Filter** — Probabilistic membership test with optimal sizing via Kirsch-Mitzenmacker double hashing
- **Count-Min Sketch** — Frequency estimation with configurable error/probability bounds
- **Eviction Pool** — Sampling-based approximate LRU (Redis's approach of sampling keys instead of maintaining a full LRU list)

For the algorithm breakdown, Redis equivalence, and complexity analysis of each structure, see [doc/DataStructures.md](doc/DataStructures.md).

**Commands:**

| Category | Commands |
|---|---|
| Connection | `PING`, `INFO` |
| String | `SET`, `GET`, `DEL`, `TTL`, `KEYS`, `INCR`, `DECR` |
| Hash | `HSET`, `HGET`, `HDEL`, `HGETALL` |
| List | `LPUSH`, `RPUSH`, `LPOP`, `RPOP`, `LRANGE` |
| Set | `SADD`, `SREM`, `SISMEMBER`, `SMEMBERS` |
| Sorted Set | `ZADD`, `ZREM`, `ZSCORE`, `ZRANK`, `ZRANGE` |
| Bloom Filter | `BF.RESERVE`, `BF.MADD`, `BF.EXISTS` |
| Count-Min Sketch | `CMS.INITBYDIM`, `CMS.INITBYPROB`, `CMS.INCRBY`, `CMS.QUERY` |
| Persistence | `SAVE`, `BGSAVE`, `LASTSAVE`, `DBSIZE` |
| Cluster | `CLUSTER INFO`, `CLUSTER NODES`, `CLUSTER MYID`, `CLUSTER MEET`, `CLUSTER ADDSLOTS`, `CLUSTER DELSLOTS`, `CLUSTER KEYSLOT`, `CLUSTER SETSLOT`, `CLUSTER GETKEYSINSLOT`, `CLUSTER COUNTKEYSINSLOT`, `MIGRATE`, `ASKING` |

**Server modes:**
- **Single-threaded** — Follows Redis's original event-loop architecture. One dedicated OS thread owns all command execution. Async IO handlers feed commands through a lock-free `Channel`. Responses are batched with `PipeWriter` — one flush per read batch.
- **Multi-threaded (share-nothing)** — Inspired by [DragonflyDB](https://www.dragonflydb.io/). N worker threads each own a private storage shard. IO Handlers route commands via FNV-1a hash. No mutexes, no spinlocks, no contention on the hot path.

**Key expiry** — Both lazy (check on access/scan) and active (periodic sweep inside the single-thread event loop and each Worker event loop).

**RDB Persistence** — Point-in-time binary snapshots compatible with both server modes:
- Automatic saves via configurable policies (e.g. `save 300 100` — save after 5 min if ≥100 changes occurred)
- Manual `SAVE` (synchronous, blocks until done) and `BGSAVE` (background, returns immediately)
- Final save on graceful shutdown — data is never lost across restarts
- Custom binary format with **CRC64** integrity check; all types (including Bloom Filter and Count-Min Sketch) are persisted
- **Atomic write**: written to a `.tmp` file first, then renamed — an OS crash never corrupts the previous snapshot

**Redis Cluster Protocol** — Master-only clustering with dynamic slot routing:
- Full cluster bus binary protocol on port+10000 with gossip-based node discovery
- `CRC16(key) % 16384` hashing with `{hash tag}` support for deterministic routing
- Basic live slot migration primitives (`MIGRATE`, `CLUSTER SETSLOT`, `ASKING` support)
- Persistent topology state natively written and recovered via `nodes.conf`
- Returns `-MOVED` redirects; `ASKING` is supported for importing slots, while source-side `-ASK` redirects for migrated-away missing keys are still a gap
- Built-in two-phase distributed failure detection (`PFAIL` → `FAIL` consensus)
- Compatible with `redis-cli -c` for the implemented cluster subset; `CLUSTER SLOTS`, replicas, and failover are not implemented yet

---

## Architecture

### Single-Thread Mode

Follows **Redis's original event-loop architecture**. The goal is simple: **one thread, zero locks, maximum cache locality.**

Redis has always used a single-threaded event loop for command execution — `ae.c` in the Redis source. The reasons are well understood: no lock overhead, no cache line bouncing, deterministic execution order. Hyperion applies the same principle but uses C#'s `Channel<T>` and `LongRunning` task instead of `epoll`/`kqueue`.

#### 1. Connection Layer
The `TcpListener` accepts connections. For each client, a lightweight `async Task` (managed by the .NET thread pool) handles IO — reading bytes from the socket using `PipeReader`.

```mermaid
flowchart LR
    C1[Client 1] & C2[Client 2] & CN[Client N]
    -->|TCP connect| TL[TcpListener]
    TL -->|"AcceptTcpClientAsync\n(one Task per client)"| IH["Async IO Tasks\n(thread pool)"]
```

#### 2. Execution Layer
Each IO task parses complete RESP commands from its `PipeReader` and enqueues a `WorkItem` to a shared lock-free `Channel<WorkItem>`. A single dedicated OS thread (`LongRunning`) consumes the channel one item at a time and executes every command against the `CommandExecutor` — no synchronization needed because only this thread ever touches the data.

The event loop also runs active expiry every 100 processed commands by calling `RunActiveExpiry()` before completing that command's response. This keeps expired keys from accumulating even when clients stop reading them.

```mermaid
flowchart LR
    IH["Async IO Tasks"]
    -->|"RespParser.TryParseCommand\nChannel.WriteAsync WorkItem"| CH["Channel WorkItem\nUnbounded lock-free"]
    CH -->|SingleReader| EL["Event Loop Thread\nLongRunning pthread"]
    EL --> CE[CommandExecutor]
    CE --> ST[("Storage\nplain Dictionary")]
    ST -->|result bytes| EL
    EL -->|"TCS.TrySetResult"| IH
```

#### 3. Response Layer
Once the IO task receives the result from the `TaskCompletionSource`, it calls `PipeWriter.Write()` — which buffers the bytes locally. After all commands in the current read batch are processed, a **single** `PipeWriter.FlushAsync()` sends them all in one syscall.

```mermaid
flowchart LR
    IH["IO Task\n(per connection)"]
    -->|"PipeWriter.Write response\n(buffered, no syscall)"| PW["PipeWriter\nwrite buffer"]
    PW -->|"FlushAsync\nONE syscall per batch"| NET["NetworkStream"]
    NET --> C["Client"]
```

---

### Multi-Thread Mode (Share-Nothing)

Inspired by [DragonflyDB](https://www.dragonflydb.io/)'s share-nothing architecture. The goal: **scale across CPU cores with zero cross-thread data sharing.**

DragonflyDB (C++) proved that the right answer to multi-core scaling is not adding locks to a single store — it is eliminating shared state entirely. Each worker owns its own private shard, and keys are deterministically routed to shards via hash. Hyperion applies the same design in C#/.NET.

On an 8-core machine Hyperion spins up 8 Workers and 4 IO Handlers. Since a key always maps to the same Worker via FNV-1a hash, storage shards are completely isolated — no locking is ever needed.

#### 1. Connection Layer
`HyperionServer` accepts connections and distributes them to IO Handlers in **round-robin** using `Interlocked.Increment`. Each `IOHandler` is an async loop that reads from its assigned clients using `PipeReader`.

```mermaid
flowchart LR
    C1[Client 1] & C2[Client 2] & CN[Client N]
    -->|TCP connect| TL[TcpListener]
    TL -->|"round-robin\nInterlocked.Increment"| IH1[IOHandler 0]
    TL --> IH2[IOHandler 1]
    TL --> IHN[IOHandler N]
```

#### 2. Routing & Execution Layer
Each `IOHandler` parses commands and calls `HyperionServer.DispatchAsync()`. The dispatcher resolves any **Hash Tag** (`{tag}` syntax, same as Redis Cluster), runs FNV-1a hash mod N, and enqueues the `WorkerTask` to the matching Worker's `Channel`. Each Worker is a `LongRunning` thread with its own **private** `Storage` — no locking needed.

**FNV-1a** (Fowler-Noll-Vo) is used because it is extremely fast for short strings (the most common key type), provides excellent distribution, and uses `stackalloc Span<byte>` for zero heap allocation per routing call.

```mermaid
flowchart TD
    IH["IOHandler\n(parses RESP)"]
    -->|"DispatchAsync WorkerTask"| RT

    subgraph RT["Router — HyperionServer.DispatchAsync"]
        HT["1. Resolve Hash Tag\n{tag} extraction"]
        FNV["2. FNV-1a hash mod N\nstackalloc — zero heap alloc"]
        HT --> FNV
    end

    FNV -->|"key % N = 0"| W0
    FNV -->|"key % N = 1"| W1
    FNV -->|"key % N = N"| WN

    subgraph W0[Worker 0]
        CH0["Channel WorkItem"] --> EL0["LongRunning Thread"]
        EL0 --> S0[("Shard 0\nDictionary")]
    end
    subgraph W1[Worker 1]
        CH1["Channel WorkItem"] --> EL1["LongRunning Thread"]
        EL1 --> S1[("Shard 1\nDictionary")]
    end
    subgraph WN[Worker N]
        CHN["Channel WorkItem"] --> ELN["LongRunning Thread"]
        ELN --> SN[("Shard N\nDictionary")]
    end
```

**Multi-Key Commands — Scatter-Gather:** For commands like `DEL key1 key2` where keys map to different shards, `DispatchAsync` splits the command into per-shard sub-tasks, dispatches them all concurrently, then aggregates results — inspired by [Dragonfly's transaction model](https://www.dragonflydb.io/blog/transactions-in-dragonfly). **Hash Tags** (e.g. `{user:1}:name` and `{user:1}:age`) force related keys to the same shard to avoid cross-shard overhead entirely.

**Cross-Shard Scan Commands:** `KEYS <pattern>` is dispatched to every Worker and the per-shard arrays are merged into one RESP array. Each shard filters out expired keys during the scan, so `KEYS` does not return keys whose TTL has already elapsed.

Each Worker also runs active expiry every 100 processed commands inside its own event loop. The sweep is local to that Worker's private shard, so it preserves the share-nothing model and requires no locks.

#### 3. Response Layer
Same as single-thread: the `IOHandler` awaits all `TCS` results for the current read batch, writes them into a `PipeWriter` buffer, and flushes once.

```mermaid
flowchart LR
    W["Worker N\nTCS.TrySetResult"]
    -->|"await task.ReplyCompletion"| IH["IOHandler"]
    IH -->|"PipeWriter.Write\n(buffered, no syscall)"| PW[PipeWriter]
    PW -->|"FlushAsync\nONE syscall per batch"| NET[NetworkStream]
    NET --> C[Client]
```

**References:**
- [Redis internals: ae.c event loop](https://github.com/redis/redis/blob/unstable/src/ae.c)
- [DragonflyDB: Share-Nothing Architecture](https://www.dragonflydb.io/docs/about/faq)
- [DragonflyDB: Transactions & Scatter-Gather](https://www.dragonflydb.io/blog/transactions-in-dragonfly)
- [VLL: A Lock Manager Designed for Main Memory Database Systems](https://www.cs.umd.edu/~abadi/papers/vldbj-vll.pdf) — the research behind Dragonfly's multi-key coordination

---

## Getting Started

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)

```bash
git clone https://github.com/ductai202/Hyperion.git
cd Hyperion

# Multi-threaded mode (default) — data saved to ./dump.rdb
dotnet run --project src/Hyperion.Server -c Release -- --port 3000

# Single-threaded mode
dotnet run --project src/Hyperion.Server -c Release -- --port 3000 --mode single

# Custom RDB path
dotnet run --project src/Hyperion.Server -c Release -- --port 3000 --dir /var/lib/hyperion --dbfilename hyperion.rdb

# Disable persistence entirely
dotnet run --project src/Hyperion.Server -c Release -- --port 3000 --no-save
```

For maximum performance, publish as a self-contained binary:

```bash
dotnet publish src/Hyperion.Server/Hyperion.Server.csproj \\
  -c Release -r linux-x64 --self-contained true -o ./publish

./publish/Hyperion.Server --port 3000 --mode multi
```

Then connect:

```bash
redis-cli -p 3000

127.0.0.1:3000> SET hello world
OK
127.0.0.1:3000> GET hello
"world"
127.0.0.1:3000> ZADD leaderboard 100 "alice" 200 "bob"
(integer) 2
127.0.0.1:3000> ZRANK leaderboard "alice"
(integer) 0
```

### Running in Cluster Mode

To enable Redis Cluster mode and test gossip/redirection:

```bash
# Start three instances
./Hyperion.Server --port 7000 --cluster-enabled yes
./Hyperion.Server --port 7001 --cluster-enabled yes
./Hyperion.Server --port 7002 --cluster-enabled yes

# Form the cluster
redis-cli -p 7000 CLUSTER MEET 127.0.0.1 7001
redis-cli -p 7000 CLUSTER MEET 127.0.0.1 7002

# Assign hash slots
redis-cli -p 7000 CLUSTER ADDSLOTS {0..5460}
redis-cli -p 7001 CLUSTER ADDSLOTS {5461..10922}
redis-cli -p 7002 CLUSTER ADDSLOTS {10923..16383}

# Connect with cluster-aware client
redis-cli -c -p 7000 SET foo bar
```

## Run Tests

```bash
dotnet test
```

---

## Benchmark

Tested on WSL2 (Ubuntu) using `redis-benchmark` with 500 clients, 1M requests, 1M random keys.

```bash
redis-benchmark -n 1000000 -t set,get -c 500 -h 127.0.0.1 -p 3000 -r 1000000 --threads 3 --csv
```

| Server | SET (req/s) | GET (req/s) |
|---|---|---|
| Redis 7.4.1 | ~142,000 | ~136,000 |
| **Hyperion Single-Thread** | **~117,000** | **~110,000** |
| **Hyperion Multi-Thread** | **~107,000** | **~113,000** |

Both modes exceed **100,000 req/s** and reach **~80% of native Redis** throughput on the same hardware.

Full methodology, latency breakdown, pain points, and delay-workload analysis in [doc/Benchmark.md](doc/Benchmark.md).

---

## What's Next

- [x] Cluster: Live Slot Migration (`SETSLOT`, `MIGRATE`)
- [ ] Cluster: Replica nodes and automatic failover
- [ ] `ArrayPool<byte>` for response buffers to reduce GC pressure
- [ ] Pre-cached static RESP responses (`+OK`, `:1`, `$-1`) to eliminate hot-path encoding
