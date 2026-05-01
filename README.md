# Hyperion

A Redis-compatible in-memory database, built from scratch in C#/.NET 10.

I started this project to deeply understand how Redis works under the hood — not just the API, but the internals: how it parses the wire protocol, manages key expiry, how a Skip List enables O(log N) ranked queries, and why single-threaded servers can outperform naively multi-threaded ones.

The multi-threaded mode uses a **share-nothing architecture** — each worker thread owns a private data shard with no locks on the hot path. The single-threaded mode uses a **true event-loop** design: one dedicated OS thread owns all command execution, fed by a lock-free channel from async IO handlers.

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

**Commands:**

| Category | Commands |
|---|---|
| Connection | `PING`, `INFO` |
| String | `SET`, `GET`, `DEL`, `TTL`, `INCR`, `DECR` |
| Hash | `HSET`, `HGET`, `HDEL`, `HGETALL` |
| List | `LPUSH`, `RPUSH`, `LPOP`, `RPOP`, `LRANGE` |
| Set | `SADD`, `SREM`, `SISMEMBER`, `SMEMBERS` |
| Sorted Set | `ZADD`, `ZREM`, `ZSCORE`, `ZRANK`, `ZRANGE` |
| Bloom Filter | `BF.RESERVE`, `BF.MADD`, `BF.EXISTS` |
| Count-Min Sketch | `CMS.INITBYDIM`, `CMS.INITBYPROB`, `CMS.INCRBY`, `CMS.QUERY` |

**Server modes:**
- **Single-threaded** — One dedicated OS thread owns all command execution. Async IO handlers feed commands via a lock-free `Channel`. Responses are batched with `PipeWriter` for maximum throughput.
- **Multi-threaded (share-nothing)** — N worker threads, each with its own private storage shard. Keys are routed via FNV-1a hash. IO handlers batch responses with `PipeWriter`. No locks anywhere on the hot path.

**Key expiry** — Both lazy (check on access) and active (background sweep sampling and deleting expired keys).

---

## Architecture

### Single-Thread Mode

One dedicated OS thread (`TaskCreationOptions.LongRunning` → a real `pthread` on Linux) owns all command execution. Multiple async IO handlers read from their TCP connections and enqueue commands to a single shared `Channel<WorkItem>`. The event-loop thread drains the channel sequentially — zero locks, zero contention.

Responses are accumulated into a `PipeWriter` buffer per connection and flushed **once per read batch** instead of once per command. This batched-write design is the key to high throughput: one `writev()` syscall serves all pipelined responses.

```mermaid
graph TD
    subgraph Clients
        C1[Client 1]
        C2[Client 2]
        CN[Client N]
    end

    C1 & C2 & CN -->|TCP| TL[TCP Listener]
    TL -->|Spawn async handler| IH[IO Handlers\n async, one per connection]

    subgraph "Single Event Loop Thread pthread"
        CH[Channel WorkItem\nlock-free queue]
        EL[Event Loop\nLongRunning Task]
        CE[Command Executor]
        ST[(Private Storage\nplain Dictionary)]
        CH --> EL --> CE --> ST
    end

    IH -->|WriteAsync WorkItem| CH
    EL -->|TrySetResult response| IH
    IH -->|PipeWriter.Write batch| IH
    IH -->|FlushAsync once per batch| C1 & C2 & CN
```

### Multi-Thread Mode (Share-Nothing)

Inspired by DragonflyDB. The workload is split into **IO Handlers** (async network IO) and **Workers** (synchronous command execution). Each worker owns its own private storage shard — because a key always routes to the same worker via FNV-1a hash, there is never any cross-thread data access.

IO Handlers batch all responses from a single read into a `PipeWriter` buffer and flush once, eliminating the per-response syscall overhead.

```mermaid
flowchart TD
    subgraph Clients
        C1[Client 1]
        C2[Client 2]
        CN[Client N]
    end

    C1 & C2 & CN -->|TCP| TL[TCP Listener]

    subgraph "IO Layer"
        TL -->|Round Robin| IH1[IO Handler 1]
        TL -->|Round Robin| IH2[IO Handler 2]
    end

    subgraph "Routing"
        IH1 & IH2 -->|FNV-1a Hash stackalloc| RT[Key Router]
    end

    subgraph "Execution Layer Share-Nothing"
        RT -->|Key maps to Worker 0| W0[Worker 0\nChannel + LongRunning]
        RT -->|Key maps to Worker 1| W1[Worker 1\nChannel + LongRunning]
        RT -->|Key maps to Worker N| WN[Worker N\nChannel + LongRunning]

        W0 --- S0[(Shard 0\nplain Dictionary)]
        W1 --- S1[(Shard 1\nplain Dictionary)]
        WN --- SN[(Shard N\nplain Dictionary)]
    end

    W0 & W1 & WN -.->|TCS result| IH1 & IH2
    IH1 & IH2 -.->|PipeWriter batch flush| C1 & C2 & CN

    style W0 fill:#f9f,stroke:#333,stroke-width:2px
    style W1 fill:#bbf,stroke:#333,stroke-width:2px
    style WN fill:#bfb,stroke:#333,stroke-width:2px
    style S0 fill:#f9f,stroke:#333
    style S1 fill:#bbf,stroke:#333
    style SN fill:#bfb,stroke:#333
```

**FNV-1a routing** uses `stackalloc Span<byte>` for zero heap allocation per dispatch — no `byte[]` GC pressure on the hot path.

**Multi-Key Commands & Hash Tags** — Commands spanning multiple keys (like `DEL key1 key2`) use a Scatter-Gather engine: the orchestrator splits the command into per-shard sub-tasks, dispatches them concurrently, and aggregates results. Hash Tags (e.g. `{user:1}:name`) force related keys to the same shard.

**References:**
- [Dragonfly Transactions & Scatter-Gather Logic](https://www.dragonflydb.io/blog/transactions-in-dragonfly)
- [Dragonfly FAQ: Shared-Nothing & Vertical Scaling](https://www.dragonflydb.io/docs/about/faq)

---

## Getting Started

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)

```bash
git clone https://github.com/ductai202/Hyperion.git
cd Hyperion

# Multi-threaded mode (default)
dotnet run --project src/Hyperion.Server -c Release -- --port 3000

# Single-threaded mode
dotnet run --project src/Hyperion.Server -c Release -- --port 3000 --mode single
```

For maximum performance, publish as a self-contained binary:

```bash
dotnet publish src/Hyperion.Server/Hyperion.Server.csproj \
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

---

## Benchmark

Tested on WSL2 (Ubuntu) using `redis-benchmark` with 500 clients, 1M requests, 1M random keys — same parameters for all servers for a fair comparison.

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

- [ ] Redis Cluster protocol
- [ ] RDB persistence
- [ ] `ArrayPool<byte>` for response buffers to reduce GC pressure
- [ ] Pre-cached static RESP responses (`+OK`, `:1`, `$-1`) to eliminate hot-path encoding
