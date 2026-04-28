# Hyperion Benchmark Report

## Environment

### Machine
| Property | Value |
|---|---|
| **CPU** | Intel Core i5-1135G7 @ 2.40GHz (11th Gen) |
| **Physical Cores** | 4 |
| **Logical Cores (HT)** | 8 |
| **RAM** | ~40 GB |
| **OS** | Windows 11 Pro |
| **Runtime** | .NET 10 (Preview) |
| **Build** | Release |

### Hyperion Thread Configuration (Multi-Thread Mode)
Hyperion automatically derives its thread layout from `Environment.ProcessorCount` (= 8 on this machine):

| Thread Pool | Count | Formula |
|---|---|---|
| **IO Handler threads** | 4 | `ProcessorCount / 2` (default) |
| **Worker threads** | 8 | `ProcessorCount` (optimized for this run) |
| **Total threads** | 12 | leveraging logical core count |

Each Worker owns a **private Storage shard**. Keys are consistently routed to the same worker via FNV-1a hash, so no locking is ever needed.

## Benchmark Commands

**Tool:** `redis-benchmark` (Redis for Windows v8.6.2 - Multi-threaded mode)

| Parameter | Value |
|---|---|
| Concurrent clients (`-c`) | 500 |
| Total requests (`-n`) | 1,000,000 |
| Key space (`-r`) | 1,000,000 unique keys |
| Payload | 3 bytes (default) |
| Keep-alive | Yes |

```bash
redis-benchmark -p 3001 -t set,get -c 500 -n 1000000 -r 1000000 --threads 3
```

> **How each mode was started:**
> ```
> # Origin Redis
> redis-server.exe --port 3000
>
> # Hyperion (single-thread mode)
> Hyperion.Server.exe --port 3000 --mode single
>
> # Hyperion (multi-thread share-nothing mode)
> Hyperion.Server.exe --port 3000 --mode multi
> ```

---

## Results: Throughput (1M requests)

| Environment | Mode | SET (req/s) | GET (req/s) |
|---|---|---|---|
| Windows 11 | Origin Redis 8.6.2 | 17,777 | 18,221 |
| Windows 11 | Hyperion Single-Thread | 15,841 | 15,389 |
| Windows 11 | Hyperion Multi-Thread | 30,921 | 26,978 |
| Windows 11 | Hyperion Single-Thread (100µs Delay) | 15,686 | 18,232 |
| Windows 11 | Hyperion Multi-Thread (100µs Delay) | 24,374 | 25,308 |
| **WSL (Linux)** | **Origin Redis 7.4.1** | **78,186** | **117,412** |
| **WSL (Linux)** | **Hyperion Single-Thread** | **45,785** | **43,425** |
| **WSL (Linux)** | **Hyperion Multi-Thread** | **94,679** | **113,856** |
| **WSL (Linux)** | **Hyperion Single-Thread (100µs Delay)** | **39,086** | **36,995** |
| **WSL (Linux)** | **Hyperion Multi-Thread (100µs Delay)** | **92,618** | **106,929** |

### Detailed Latency Summaries (WSL)

**Hyperion Multi-Thread (8 Workers, 4 IO Handlers, No Delay)**
```text
====== SET ======
  throughput summary: 94679.04 requests per second
  latency summary (msec):
          avg       min       p50       p95       p99       max
        3.943     0.016     2.119    15.607    44.831    84.159

====== GET ======
  throughput summary: 113856.31 requests per second
  latency summary (msec):
          avg       min       p50       p95       p99       max
        2.617     0.104     2.263     5.143     7.711    28.703
```

**Hyperion Multi-Thread (100µs Delay Simulation)**
```text
====== SET ======
  throughput summary: 92618.32 requests per second
  latency summary (msec):
          avg       min       p50       p95       p99       max
        4.169     0.024     2.023    16.863    44.671    87.167

====== GET ======
  throughput summary: 106929.00 requests per second
  latency summary (msec):
          avg       min       p50       p95       p99       max
        3.015     0.024     2.263     7.359    15.079    70.271
```

---

## Analysis

### Normal Workload (No Delay)
- **Windows Limitation**: On Windows, pure I/O overhead caps both Origin Redis and Hyperion at around 15k-25k requests per second. At this ceiling, Hyperion Multi-Thread performs noticeably better (~23.8k GET) than Single-Thread (~13.8k GET) and Origin Redis (~18.2k GET).
- **WSL Linux Performance**: When tested on WSL, the network stack overhead disappears. Origin Redis jumps to **117k GET req/s**. Hyperion Multi-Thread jumps to **99.6k GET req/s** (achieving ~85% of official Redis throughput). Hyperion Single-Thread bottlenecks at around 39k, demonstrating that C#'s single-threaded event loop is compute-bound at that level.

### Why Multi-Thread Wins Under Load (100µs Delay)
While single-thread mode is fast for raw I/O, the true power of the "share-nothing" architecture becomes obvious when simulating slow commands (like complex Lua scripts or large dataset scans). By injecting a synthetic **100µs delay** into execution, we observe:

| Environment | Mode | SET (req/s) | GET (req/s) |
|---|---|---|---|
| **Windows 11** | Hyperion Single-Thread (100µs) | 15,686 | 18,232 |
| **Windows 11** | **Hyperion Multi-Thread (100µs)** | **24,374** | **25,308** |
| **WSL (Linux)** | Hyperion Single-Thread (100µs) | 39,086 | 36,995 |
| **WSL (Linux)** | **Hyperion Multi-Thread (100µs)** | **92,618** | **106,929** (2.89x faster) |

In a Single-Thread model, a slow command blocks the entire event loop, destroying throughput for all connected clients. In Hyperion's Multi-Thread model, a 100µs delay on one shard only affects that specific worker. The other 7 workers continue processing commands at full speed, allowing the server to maintain **106,000+ GET req/s**.

---

## Conclusion

Hyperion successfully proves the "share-nothing" architectural concept in C#/.NET. It reaches **~85% of official Redis throughput** on Linux for pure read workloads (hitting ~100k req/s), and demonstrates massive resilience under load, running **2.8x faster** than a single-threaded server when faced with slow commands.

> Benchmark results were collected using the `run_benchmarks.ps1` and `run_benchmarks.sh` scripts.
