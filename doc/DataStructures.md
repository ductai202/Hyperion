# Data Structures Deep Dive

This document explains every custom data structure in Hyperion: the algorithm, the math behind it, its time complexity, and how our implementation follows the design of the original Redis source code.

---

## Table of Contents

1. [Dict (Hash Table with TTL)](#1-dict-hash-table-with-ttl)
2. [Eviction Pool (Approximate LRU)](#2-eviction-pool-approximate-lru)
3. [Skip List](#3-skip-list)
4. [ZSet (Sorted Set)](#4-zset-sorted-set)
5. [Bloom Filter](#5-bloom-filter)
6. [Count-Min Sketch (CMS)](#6-count-min-sketch-cms)

---

## 1. Dict (Hash Table with TTL)

**File:** `src/Hyperion.DataStructures/Dict.cs`

### What It Does
The `Dict` is Hyperion's core storage engine. It is the equivalent of Redis's `redisDb` — a key-value store that also tracks TTL (Time-To-Live) for key expiry and last-access timestamps for LRU eviction.

### Architecture

```
CoarseClock (static)
└── _nowMs: long   ← Refreshed every 10ms by a Timer (eliminates UtcNow syscall on hot path)

Dict (one private instance per Worker — share-nothing, no locks needed)
├── _store:        Dictionary<string, DictObject>   ← Main key → value store
├── _expiryStore:  Dictionary<string, long>         ← Key → expiry timestamp (ms)
├── _keyList:      List<string>                     ← Continuous list of active keys (for O(1) random key sampling)
└── _keyIndexMap:  Dictionary<string, int>          ← Key to index in _keyList (enables O(1) Swap-and-Pop deletion)

DictObject
├── Key: string
├── Value: string
└── LastAccessTime: long (Unix ms)   ← Updated on every Get() via CoarseClock.NowMs
```

### How It Works
- **SET** creates a `DictObject` using `CoarseClock.NowMs` as the initial `LastAccessTime` and stores it. If the key is new, it is appended to `_keyList` and its index is mapped in `_keyIndexMap` in $O(1)$. If a TTL is specified, the expiry time is recorded in `_expiryStore`.
- **GET** retrieves the object and updates `LastAccessTime` to `CoarseClock.NowMs` (LRU touch).
- **PEEK** retrieves the object **without** modifying `LastAccessTime`. This is a critical performance and correctness optimization used during eviction sampling to avoid inadvertently "rescuing" sampled candidate keys (which would reset their idle time).
- **DEL** deletes the key from `_store` and `_expiryStore`. To keep `_keyList` packed without holes in $O(1)$ time complexity, we perform **Swap-and-Pop**: we swap the key-to-be-deleted with the *last* key in `_keyList`, update the index map of the swapped key, and then perform `RemoveAt(last_index)`. This avoids expensive $O(N)$ array-shifting.
- **GET RANDOM KEY** retrieves a random key in $O(1)$ by picking a random index using `Random.Shared.Next(_keyList.Count)` and looking it up in `_keyList`.
- **Expiry Check** (`HasExpired`): Before returning a value, we compare `CoarseClock.NowMs` against the stored expiry. If expired, the key is lazily deleted and `nil` is returned.

### CoarseClock
Instead of calling `DateTimeOffset.UtcNow` on every GET/SET (a syscall), Hyperion maintains a coarse clock refreshed by a background `Timer` every 10ms:

```csharp
public static class CoarseClock
{
    private static long _nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static readonly Timer _timer = new(_ =>
        Interlocked.Exchange(ref _nowMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
        null, 10, 10);

    public static long NowMs => Interlocked.Read(ref _nowMs);
}
```

Redis uses the same approach: `server.unixtime` is refreshed once per event-loop tick, not on every command.

### Why Plain Dictionary (Not ConcurrentDictionary)?
Each `Worker` owns a **private** `Dict` instance. The routing layer (FNV-1a hash) guarantees that all accesses to a given key are dispatched to the same Worker thread — so no two threads ever access the same `Dict` concurrently. Using a plain `Dictionary` eliminates all lock overhead that `ConcurrentDictionary` would otherwise impose.

### Trade-offs & Deep Dive: Why This Design?
- **The Naive Approach (ConcurrentDictionary):** The standard way to build a multi-threaded cache in .NET is to use a `ConcurrentDictionary`. It handles thread safety under the hood using fine-grained locks (lock striping) and CAS (Compare-And-Swap) operations.
- **The Drawbacks:** While easy to use, `ConcurrentDictionary` scales poorly under extreme concurrency (e.g., 100k+ RPS). Multiple threads fighting to update the same dictionary internal structures lead to "lock contention" and "CPU cache-line bouncing". The CPU spends more time synchronizing threads than actually executing commands.
- **The Redis Idea:** Official Redis bypasses concurrency issues entirely by executing all commands on a single, event-driven thread. This guarantees zero lock contention but limits throughput to a single CPU core.
- **Our Solution & Why It's Better:** We adopt a **"Share-Nothing" architecture** with a plain `Dictionary<string, DictObject>`. By hashing the key (using FNV-1a) at the network layer and routing it to a specific worker thread, we guarantee that each dictionary is only ever touched by one thread. This eliminates locks completely. We get the raw, lock-free speed of Redis's single-threaded model, but because we have multiple independent workers, that speed is multiplied across all available CPU cores. The trade-off is that memory isn't perfectly balanced across cores, but the massive gain in throughput makes it optimal.

### Redis Equivalence
In Redis, the `redisDb` struct has two dictionaries:
```c
typedef struct redisDb {
    dict *dict;     /* The keyspace */
    dict *expires;  /* Timeout of keys with a timeout set */
    ...
} redisDb;
```
Our `Dict` mirrors this exact pattern — `_store` is the keyspace, `_expiryStore` is the expires table.

### Complexity
| Operation | Time |
|---|---|
| SET | O(1) |
| GET | O(1) |
| DEL | O(1) |
| TTL check | O(1) |

---

## 2. Eviction Pool (Approximate LRU)

**File:** `src/Hyperion.DataStructures/EvictionPool.cs`

### What It Does
When the database reaches its memory limit, Hyperion must evict keys. Instead of maintaining a full LRU linked list (expensive), Redis uses a **sampling-based approximate LRU** approach. The Eviction Pool is the sorting buffer for sampled candidates.

### How It Works (The Active Eviction Pipeline)
Hyperion performs active memory-constrained eviction on the main command execution loop:
1. **Thread-Safe Local Caps**: When a write command that could increase key space (e.g. `SET`, `HSET`, `LPUSH`) enters `CommandExecutor`, it checks the key count. Since Hyperion uses a multi-threaded Share-Nothing architecture, memory limits are checked locally on the thread without locks:
   `shardLimit = MaxKeyNumber / ListenerNumber`.
2. **Active Eviction Loop**: If the local shard's key count exceeds or reaches `shardLimit`, Hyperion runs an eviction loop:
   - **O(1) sampling**: It randomly selects `EpoolLruSampleSize` keys using `Dict.GetRandomKey()`.
   - **Non-touch Peeking**: It reads the `LastAccessTime` of each candidate key using `Dict.Peek()` (preventing sampling from resetting their idle time).
   - **Pool Sorting**: It pushes candidates into `EvictionPool`, keeping it sorted (oldest first).
   - **Pop and Delete**: It pops the oldest candidate from the pool and calls `Dict.Del()` (which uses $O(1)$ Swap-and-Pop to clean up lists).
   - The loop repeats until the local key count drops below the shard limit.

```
Pool (sorted by LastAccessTime, oldest first):
┌──────────────────────────────────────────────────────┐
│  key_A (1000ms)  │  key_C (1500ms)  │  key_B (2000ms)│
│    ← oldest                              newest →    │
│    ← evict first                                     │
└──────────────────────────────────────────────────────┘
```

### Redis Equivalence
Redis source (`evict.c`) uses the same approach:
```c
struct evictionPoolEntry {
    unsigned long long idle;    /* Object idle time */
    sds key;                    /* Key name */
    ...
};
```
The pool maintains a fixed number of entries (`EVPOOL_SIZE = 16` in Redis). Our implementation uses `ServerConfig.EpoolMaxSize`.

### Why Not Full LRU?
A true LRU would require a doubly-linked list + hash map, adding 16+ bytes of overhead per key. Redis's author (Antirez) found that sampling just 5 keys per eviction cycle achieves **~95% accuracy** compared to perfect LRU, at zero per-key overhead.

### Trade-offs & Deep Dive: Why This Design?
- **The Naive Approach (Strict LRU):** The textbook algorithm for an LRU cache uses a Hash Map paired with a Doubly-Linked List. Whenever a key is accessed (`GET`), it is unlinked from its current position in the list and moved to the "head" (most recently used). When memory is full, the "tail" is evicted.
- **The Drawbacks:** In a managed language like C#, every node requires at least two object references (`Next` and `Previous`). Moving a node to the head on *every single read operation* requires mutating multiple memory locations. This destroys CPU cache locality (pointer chasing) and generates massive cross-generation Garbage Collection (GC) pressure because older objects are constantly being modified to point to newer objects.
- **The Redis Idea:** Redis avoids strict LRU to save memory (two pointers per key is expensive) and save CPU cycles. Instead, it randomly samples a few keys and evicts the oldest among the sample. It uses a small "Eviction Pool" to keep track of the best eviction candidates across samples.
- **Our Solution & Why It's Better:** We implemented the Redis sampling algorithm. By using an `EvictionPool`, we eliminate the need for doubly-linked lists. Our `GET` hot-path simply updates a `long LastAccessTime` integer inside the object. This requires zero allocations and zero pointer shuffling. When memory is full, the O(N) sort is isolated to a tiny array (e.g., 16 items). We deliberately trade ~5% of LRU accuracy to eliminate object reference mutations on reads, which is absolutely critical in .NET to prevent GC pauses and maximize requests-per-second (RPS).

### Complexity
| Operation | Time |
|---|---|
| Push (insert + sort) | O(N) where N = pool size (typically 16) |
| Pop (evict oldest) | O(1) |

---

## 3. Skip List

**File:** `src/Hyperion.DataStructures/Skiplist.cs`

### What It Does
The Skip List is a probabilistic data structure that provides sorted-order operations (insert, delete, rank, range) with O(log N) average complexity — similar to a balanced binary tree, but simpler to implement and with good cache behavior.

### How It Works

A Skip List is a hierarchy of linked lists. The bottom level (Level 0) contains all elements in sorted order. Each higher level is a "fast lane" that skips over elements, allowing binary-search-like traversal.

```
Level 3:  HEAD ─────────────────────────────────────────▶ NIL
Level 2:  HEAD ──────────▶ 3 ──────────────▶ 7 ────────▶ NIL
Level 1:  HEAD ──▶ 1 ───▶ 3 ───▶ 5 ───────▶ 7 ────────▶ NIL
Level 0:  HEAD ──▶ 1 ──▶ 2 ──▶ 3 ──▶ 4 ──▶ 5 ──▶ 6 ──▶ 7 ──▶ NIL
```

**Random Level Assignment**: When inserting a node, its level is determined by a coin-flip loop:
```csharp
private int RandomLevel()
{
    int level = 1;
    while (_random.Next(2) == 1) level++;
    return Math.Min(level, MaxLevel); // MaxLevel = 32
}
```
This gives each level a **50% probability** of being promoted to the next level, producing an expected O(log N) depth.

### Span Tracking
Unlike a basic Skip List, our implementation (following Redis exactly) tracks a **span** value on each forward pointer. The span counts how many nodes are skipped by that pointer. This allows O(log N) **rank queries** — "what is the rank of element X?" — by summing spans during traversal.

```csharp
public class SkiplistLevel
{
    public SkiplistNode? Forward { get; set; }  // Next node at this level
    public uint Span { get; set; }              // # of nodes skipped
}
```

### Redis Equivalence
Our implementation follows `redis/src/t_zset.c`:
```c
typedef struct zskiplistNode {
    sds ele;
    double score;
    struct zskiplistNode *backward;
    struct zskiplistLevel {
        struct zskiplistNode *forward;
        unsigned long span;
    } level[];
} zskiplistNode;
```
The mapping is exact:
- `Ele` → `ele`
- `Score` → `score`
- `Backward` → `backward`
- `Levels[i].Forward` → `level[i].forward`
- `Levels[i].Span` → `level[i].span`

### Trade-offs & Deep Dive: Why This Design?
- **The Naive Approach (Balanced Trees):** When developers need a sorted collection with O(log N) operations, the standard choice is a Balanced Binary Search Tree, such as a Red-Black Tree or AVL Tree (e.g., C#'s `SortedSet<T>`).
- **The Drawbacks:** Balanced trees enforce strict depth rules. When you insert or delete a node, the tree must often perform recursive "rotations" to rebalance itself. These rotations are complex to implement, involve updating multiple parent/child pointers, and cause significant CPU cache misses (pointer chasing across memory). Furthermore, adding support for O(log N) rank queries (e.g., "what is the index of this node?") to a Red-Black tree requires augmenting every node with subtree sizes, making rotations even more expensive.
- **The Redis Idea:** Antirez chose Skip Lists over balanced trees for Redis because they rely on probability rather than strict rebalancing rules. They are much simpler, easier to augment, and use less memory per node on average.
- **Our Solution & Why It's Better:** We implemented a Skip List from scratch. In a managed language like C#, avoiding complex tree rotations prevents unpredictable GC behavior. The Skip List acts like a simple linked list on the hot path. More importantly, its structure makes it incredibly easy to augment with a `Span` field (counting how many nodes a pointer skips). This allows us to support `ZRANK` (finding an element's position) in O(log N) time by simply summing the spans during traversal. This provides the exact same time complexity as a balanced tree, but with significantly lower constant factors and cache overhead.

### Complexity
| Operation | Average | Worst |
|---|---|---|
| Insert | O(log N) | O(N) |
| Delete | O(log N) | O(N) |
| GetRank | O(log N) | O(N) |
| Range | O(log N + M) | O(N + M) |

where M = number of elements in the returned range.

---

## 4. ZSet (Sorted Set)

**File:** `src/Hyperion.DataStructures/ZSet.cs`

### What It Does
ZSet implements Redis's Sorted Set — a collection where each element has an associated score. Elements are unique by name, but sorted by score. It supports both O(1) score lookups and O(log N) rank/range queries.

### The Dual-Structure Design

This is one of Redis's most elegant architectural decisions. A single data structure cannot efficiently support both "get score by name" and "get rank by score". Redis solves this by using **two synchronized structures**:

```
ZSet
├── _dict: Dictionary<string, double>     ← O(1) member → score lookup
└── _zskiplist: Skiplist                  ← O(log N) sorted traversal, rank, range
```

**Every mutation (Add, Rem, UpdateScore) updates both structures atomically.**

### Insert Flow

```
ZADD leaderboard 100 "alice"
         │
         ▼
┌─ Check _dict for "alice" ──────────────────────────────┐
│                                                         │
│  NOT FOUND:                    FOUND (score changed):   │
│  ├─ _zskiplist.Insert(100, "alice")  ├─ _zskiplist.UpdateScore(...)  │
│  └─ _dict["alice"] = 100             └─ _dict["alice"] = newScore    │
└─────────────────────────────────────────────────────────┘
```

### Score Update Optimization
When updating a score, if the new score doesn't change the node's relative position (it's still between its predecessor and successor), we perform an **in-place update** — just modifying `node.Score` without removing and reinserting. This is a direct port of the optimization in Redis's `zslUpdateScore`.

```csharp
// If new position is still valid, just update in place
if ((x.Backward == null || x.Backward.Score < newScore) &&
    (x.Levels[0].Forward == null || x.Levels[0].Forward.Score > newScore))
{
    x.Score = newScore;  // No structural change needed!
    return x;
}
// Otherwise, delete + reinsert
DeleteNode(x, update);
return Insert(newScore, ele);
```

### Redis Equivalence
Redis `t_zset.c`:
```c
typedef struct zset {
    dict *dict;
    zskiplist *zsl;
} zset;
```
Our `ZSet` is a 1:1 equivalent.

### Trade-offs & Deep Dive: Why This Design?
- **The Naive Approach (Single Structure):** To build a leaderboard, one might just use a sorted Array or a single Binary Tree. If you use a sorted array, finding a user by name requires an O(N) scan, and inserting a new score is O(N) because elements must shift. If you use a pure Tree, looking up a user's score by name takes O(log N) or O(N) if the tree is sorted by score (since you'd have to scan the tree to find the name). No single standard data structure provides both O(1) score lookup and O(log N) rank/range queries.
- **The Drawbacks:** Using a single structure forces a massive compromise. Either you punish read latency (`ZSCORE` becomes slow) or you punish write latency (`ZADD` becomes slow).
- **The Redis Idea:** Redis accepts a deliberate memory trade-off: it stores the data twice. It uses a dual-structure design, combining a Hash Table (for O(1) name-to-score lookups) and a Skip List (for O(log N) sorting and ranking), updating both synchronously.
- **Our Solution & Why It's Better:** We embrace this dual-structure approach (`Dictionary` + `Skiplist`). We trade O(N) extra memory footprint to guarantee optimal time complexity for all operations. Furthermore, we ported a critical Redis optimization: **In-place Score Updates**. In leaderboards, users frequently gain a few points, changing their score but rarely changing their relative rank. Instead of doing an expensive O(log N) removal and re-insertion on every point change, our ZSet detects if the new score maintains the current ordering and simply updates the score field in O(1) time. This makes Hyperion exceptionally fast for real-time gaming leaderboards compared to standard sorted collections.

### Complexity
| Operation | Time |
|---|---|
| ZADD | O(log N) |
| ZREM | O(log N) |
| ZSCORE | O(1) — via dictionary |
| ZRANK | O(log N) — via skip list span |
| ZRANGE | O(log N + M) |

---

## 5. Bloom Filter

**File:** `src/Hyperion.DataStructures/Bloom.cs`

### What It Does
A Bloom Filter answers the question "Is this element **possibly** in the set?" with zero false negatives but a controlled false positive rate. It uses no actual storage of elements — only a compact bit array and hash functions.

### How It Works

1. **Reserve**: Given the expected number of entries (n) and desired error rate (p), calculate the optimal bit array size and number of hash functions.
2. **Add**: Hash the element k times, set each corresponding bit to 1.
3. **Exists**: Hash the element k times, check if ALL corresponding bits are 1. If any is 0, the element is **definitely not** in the set.

```
Add("hello"):
  hash_0("hello") = 3   → bits[3]  = 1
  hash_1("hello") = 17  → bits[17] = 1
  hash_2("hello") = 42  → bits[42] = 1

Exists("hello"):
  bits[3] = 1 ✓, bits[17] = 1 ✓, bits[42] = 1 ✓ → "Probably yes"

Exists("world"):
  bits[7] = 1 ✓, bits[17] = 1 ✓, bits[55] = 0 ✗ → "Definitely no"
```

### The Math

The optimal parameters are derived from information theory (see [Wikipedia: Bloom filter](https://en.wikipedia.org/wiki/Bloom_filter)):

**Optimal number of bits per element:**
```
bitsPerEntry = -ln(errorRate) / ln(2)²
```

Where:
- `ln(2)  = 0.693147...` — Natural logarithm of 2
- `ln(2)² = 0.480453...` — Square of ln(2)

**Optimal number of hash functions:**
```
k = bitsPerEntry × ln(2)
```

These are the "magic numbers" `Ln2` and `Ln2Square` in our code. They ensure the filter achieves the minimum possible false positive rate for its size.

### Enhanced Double Hashing

Instead of computing k independent hash functions (expensive), we use the **Kirsch-Mitzenmacker optimization**: compute one 128-bit MD5 hash, split it into two 64-bit halves (A and B), then generate k hash values as:

```
hash_i = (A + B × i) mod bits
```

This has been proven to provide the same false positive guarantees as k independent hashes.

```csharp
public HashValue CalcHash(string entry)
{
    byte[] hashBytes = MD5.HashData(Encoding.UTF8.GetBytes(entry));
    ulong a = BitConverter.ToUInt64(hashBytes, 0);  // First 64 bits
    ulong b = BitConverter.ToUInt64(hashBytes, 8);  // Last 64 bits
    return new HashValue { A = a, B = b };
}
```

### Redis Equivalence
Redis's Bloom Filter module (`RedisBloom`) uses the same optimal sizing formulas and the enhanced double hashing technique. Our implementation directly follows these algorithms.

### Trade-offs & Deep Dive: Why This Design?
- **The Naive Approach (HashSet):** The standard way to check if an element exists in a set (e.g., deduplication, checking if an IP is banned) is to store all elements in a `HashSet<string>`.
- **The Drawbacks:** While a `HashSet` provides O(1) lookups and 100% accuracy, it must store the actual raw strings. If you have 100 million unique strings, this will consume gigabytes of RAM. In C#, this also fills the heap with millions of string objects, forcing the Garbage Collector to freeze the server for seconds at a time to scan the memory graph.
- **The Redis Idea:** RedisBloom introduces a probabilistic bit-array (Bloom Filter). It trades 100% accuracy (accepting a known, tiny false positive rate, e.g., 1%) to achieve massive memory compression, storing zero actual strings. It also uses the Kirsch-Mitzenmacher optimization to generate `k` hashes without running `k` expensive hash algorithms.
- **Our Solution & Why It's Better:** We implemented a highly optimized Bloom Filter. Instead of storing strings, checking existence requires only ~1.2 bytes per item. To make it performant in C#, we avoid calling cryptographic hash functions (like MD5 or SHA) repeatedly, which is extremely CPU-intensive. By using the double-hashing technique, we hash the string exactly *once* using `MD5.HashData`, split the 128-bit result into two 64-bit integers, and compute all `k` required hash positions using simple arithmetic (`A + B * i`). This gives us the mathematical guarantees of multiple independent hashes while burning a fraction of the CPU cycles and zero GC allocations.

### Complexity
| Operation | Time |
|---|---|
| Add | O(k) where k = number of hash functions |
| Exists | O(k) |
| Space | ~1.44 × ln(1/p) bits per element |

For a filter with 1% error rate: ~9.6 bits per element (~1.2 bytes).

---

## 6. Count-Min Sketch (CMS)

**File:** `src/Hyperion.DataStructures/CMS.cs`

### What It Does
A Count-Min Sketch estimates the **frequency** of items in a stream. Unlike a hash map, it uses sub-linear space and can handle massive cardinalities. The trade-off is that estimates may be **slightly over-counted** (never under-counted).

### How It Works

The CMS is a 2D array of counters with `width` columns and `depth` rows. Each row uses a different hash function.

```
                    width (w) columns
               ┌───┬───┬───┬───┬───┬───┐
  depth 0 (h₀) │ 0 │ 3 │ 0 │ 1 │ 0 │ 2 │
               ├───┼───┼───┼───┼───┼───┤
  depth 1 (h₁) │ 1 │ 0 │ 2 │ 0 │ 3 │ 0 │
               ├───┼───┼───┼───┼───┼───┤
  depth 2 (h₂) │ 0 │ 0 │ 1 │ 3 │ 0 │ 0 │
               └───┴───┴───┴───┴───┴───┘
```

**IncrBy(item, value):**
For each row `i`, compute `hash_i(item) % width` to find the column. Increment that cell by `value`.

**Count(item):**
For each row `i`, compute `hash_i(item) % width`. Return the **minimum** across all rows. The minimum is the best estimate because it's least likely to be inflated by hash collisions.

### The Math

Given desired error rate (`errRate`) and error probability (`errProb`):

**Width** (controls accuracy):
```
w = ceil(2 / errRate)
```

**Depth** (controls confidence):
```
d = ceil(log₁₀(errProb) / log₁₀(0.5))
```

Where `log₁₀(0.5) = -0.30103...` is the precomputed constant `Log10PointFive` in our code.

**Intuition**: Each additional row halves the probability of a false over-count. So `d = 7` means the probability of a bad estimate is at most `0.5^7 ≈ 0.8%`.

### Hash Functions
We simulate `d` independent hash functions by concatenating the item with the row index and computing MD5:

```csharp
private uint CalcHash(string item, uint seed)
{
    byte[] inputBytes = Encoding.UTF8.GetBytes(item + seed.ToString());
    byte[] hashBytes = MD5.HashData(inputBytes);
    return BitConverter.ToUInt32(hashBytes, 0);
}
```

This is simpler than the Bloom Filter's double-hashing approach because each CMS row only needs one hash value (not k).

### Redis Equivalence
Redis's CMS module (`RedisBloom/CMS`) uses the same width/depth formulas and the minimum-of-rows query strategy. The space-efficiency trade-off is identical.

### Trade-offs & Deep Dive: Why This Design?
- **The Naive Approach (Dictionary Frequency Map):** To track the frequency of events (e.g., "how many times has this video been viewed?"), the normal algorithm is to use a `Dictionary<string, int>`. Every time an item is seen, you find it in the dictionary and increment the integer.
- **The Drawbacks:** If you are tracking billions of events across hundreds of millions of unique items (high cardinality), the dictionary will grow infinitely. It will consume unbounded memory and eventually crash the server with an `OutOfMemoryException`. Furthermore, as the dictionary grows, it must periodically resize its internal arrays, causing massive CPU latency spikes.
- **The Redis Idea:** The Count-Min Sketch (CMS) trades perfect accuracy for sub-linear, fixed space. It hashes items into a fixed-size 2D array of counters. It deliberately allows slight over-counting (but never under-counting), keeping memory usage constant regardless of how many unique items are tracked.
- **Our Solution & Why It's Better:** In a high-throughput .NET server, memory stability is crucial. By using CMS, we allocate a single, fixed-size 2D array (e.g., 56 KB) upfront upon creation. This guarantees an O(1) memory footprint. The server is completely immune to traffic spikes or infinite cardinality because it never allocates new memory for new items. The query strategy (taking the minimum across `d` rows) heavily mitigates hash collisions. For analytics like "Top 10 Heavy Hitters", CMS provides incredible speed and 100% memory safety at the cost of a statistically controlled error rate.

### Complexity
| Operation | Time | Space |
|---|---|---|
| IncrBy | O(d) | — |
| Count | O(d) | — |
| Total space | — | O(w × d) counters |

For a sketch with 0.1% error rate and 1% error probability: w=2000, d=7 → 14,000 uint32 counters = ~56 KB.

---

## Summary Table

| Structure | Purpose | Space | Key Operation | Complexity |
|---|---|---|---|---|
| **Dict** | Key-value store with TTL | O(N) | GET/SET | O(1) |
| **Eviction Pool** | Approximate LRU | O(1) fixed | Evict oldest | O(pool_size) |
| **Skip List** | Sorted element storage | O(N) | Insert/Rank | O(log N) |
| **ZSet** | Sorted Set (Dict + SkipList) | O(N) | ZADD/ZRANK | O(log N) |
| **Bloom Filter** | Membership testing | ~1.2 B/element | Add/Exists | O(k) |
| **CMS** | Frequency estimation | O(w × d) | IncrBy/Count | O(d) |

All implementations are written from scratch without third-party dependencies, following the algorithms and design patterns established in the original Redis source code.
