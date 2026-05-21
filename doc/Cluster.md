# Redis Cluster Protocol (Phase 2)

Hyperion implements a master-only subset of the Redis Cluster protocol, supporting dynamic hash slot distribution, live gossip-based node discovery, and MOVED redirection for clients.

## Architecture

```mermaid
flowchart TD
    subgraph "Client Layer"
        CL[Redis Client] -->|"GET key"| NODE_A
    end

    subgraph NODE_A["Node A (owns slots 0-5460)"]
        RA["Slot Router\nCRC16(key) % 16384"]
        RA -->|"slot in 0-5460"| EXEC_A["Execute locally"]
        RA -->|"slot in 5461-10922"| MOVED["-MOVED 7123 nodeB:6379"]
    end

    subgraph NODE_B["Node B (owns slots 5461-10922)"]
        EXEC_B["Execute locally"]
    end

    subgraph "Cluster Bus (port+10000)"
        BUS["Binary Gossip Protocol"]
        NODE_A <-->|"PING/PONG\n+ gossip entries"| BUS
        NODE_B <-->|"PING/PONG"| BUS
    end
```

## Hash Slots and Internal Sharding

Like Redis, Hyperion divides the keyspace into 16,384 hash slots using `CRC16(key) % 16384`.

Because Hyperion is a multi-threaded server using a **share-nothing** architecture, the hash slot is further divided across the internal Workers.

`Internal Worker ID = (CRC16(key) % 16384) % NumWorkers`

This ensures that all keys belonging to a specific hash slot are always processed by the exact same internal thread, maintaining thread safety without locks.

## Gossip Protocol

Each Hyperion cluster node listens on a secondary port (default: client port + 10000) for binary cluster bus messages.

Every 1 second, the `GossipEngine`:
1. Randomly selects a subset of nodes (N/10).
2. Sends a binary `PING` message containing its own state (slots, epoch, flags) and a list of randomly selected gossip entries about other nodes.
3. The receiver updates its cluster state and replies with a `PONG`.

## Supported Cluster Commands

- `CLUSTER INFO` - Returns cluster state, slot assignment stats, and epoch.
- `CLUSTER NODES` - Returns the full node table in Redis-compatible format.
- `CLUSTER MYID` - Returns the node's unique 40-character ID.
- `CLUSTER MEET <ip> <port>` - Initiates a handshake to join a new node into the cluster.
- `CLUSTER ADDSLOTS <slot> [slot ...]` - Assigns specific hash slots to the node.
- `CLUSTER KEYSLOT <key>` - Returns the CRC16 hash slot for a key (supports `{hash tags}`).
- `CLUSTER SETSLOT <slot> <IMPORTING|MIGRATING|STABLE|NODE> [node-id]` - Changes the state of a hash slot for live migration.
- `CLUSTER GETKEYSINSLOT <slot> <count>` - Returns an array of keys found in the specified slot.
- `CLUSTER COUNTKEYSINSLOT <slot>` - Returns the number of keys mapping to the specified slot.
- `MIGRATE <host> <port> <key> <db> <timeout>` - Atomically transfers a key from a source Redis instance to a destination instance.
- `ASKING` - Instructs the server to serve the next query even if it is not the authoritative owner of the slot (used during migrations).

## Live Slot Migration

Hyperion supports zero-downtime live slot migration matching the Redis cluster specification. 

1. **Source Node:** `CLUSTER SETSLOT <slot> MIGRATING <dest-node-id>`
2. **Dest Node:** `CLUSTER SETSLOT <slot> IMPORTING <src-node-id>`
3. **Execution:** Keys are pulled via `CLUSTER GETKEYSINSLOT` and pushed via `MIGRATE`.
4. **Finalization:** Both nodes are sent `CLUSTER SETSLOT <slot> NODE <dest-node-id>`.

During migration, if a client requests a key from the Source node that has already been migrated, the Source returns a `-ASK <port> <ip>` redirect. The client must then issue an `ASKING` command to the Destination node before re-issuing the query, telling the Destination node to execute the query despite not fully owning the slot yet.

## State Persistence

Cluster state (Node ID, configuration epochs, known nodes, and slot ownership) is persisted atomically to a `nodes.conf` file. Upon startup, Hyperion detects the `nodes.conf` file and natively rehydrates the cluster topology, bypassing the need for ZooKeeper or other external consensus mechanisms. Changes to the cluster topology (like `ADDSLOTS` or `SETSLOT`) trigger automatic atomic writes (`.tmp` → OS-level `rename`) ensuring crash-proof consistency.

## Failure Detection

Hyperion's cluster features a robust, two-phase distributed failure detection mechanism over the binary gossip bus:
1. **PFAIL (Probable Fail):** A node marks another node as `PFAIL` locally if it doesn't receive a `PONG` within the `ClusterNodeTimeout`.
2. **FAIL (Cluster Consensus):** Nodes share `PFAIL` flags via gossip `PING` packets. Once a node sees that the majority of cluster masters have flagged a node as `PFAIL` within a time window, the failure is promoted to `FAIL` and a broadcast is emitted across the cluster.

---

## Architectural Trade-offs & Self-Learning Notes

1. **MIGRATE Command (Internal RESP vs Binary Serializer)**
   - *Implementation:* When `MIGRATE` is executed, Hyperion encodes the key and value dynamically into standard RESP `SET PX` network commands, pushing them over a temporary TCP socket to the destination.
   - *Trade-off:* We sacrifice a tiny bit of CPU efficiency (due to RESP string-formatting) compared to a proprietary binary stream transfer.
   - *Learning:* This drastically simplified the implementation and kept it perfectly compliant with stock Redis clusters. The destination node doesn't need a special parsing channel—it re-uses the exact same robust `StringCommands.Set` logic it uses for normal clients.
2. **Lock-Free GETKEYSINSLOT Routing**
   - *Implementation:* Unlike single-threaded Redis, Hyperion is multi-threaded. Finding keys in a slot could theoretically require taking a read-lock across all `Worker` thread shards. Instead, `CLUSTER GETKEYSINSLOT` is intercepted within the `HyperionServer` dispatcher and forcefully routed to the *exact* Worker thread that mathematically owns that CRC16 slot.
   - *Trade-off:* Requires intercepting commands at the parsing layer, tightly coupling `HyperionServer` to some cluster protocol commands.
   - *Learning:* Strict adherence to the "Share-Nothing" architecture pays off. By routing mathematically, we achieve lock-free shard iteration without halting throughput on other cores.
3. **Pipelined ASKING Execution**
   - *Implementation:* The `ASKING` flag is tracked as `_askingNext` locally inside the `IOHandler` event loop. It attaches specifically to the immediate next `WorkerTask` via a boolean property.
   - *Trade-off:* We lose the ability to easily query the "global connection state" deep inside the command engine.
   - *Learning:* Tracking connection-level state inside an async multi-threaded pipeline is notoriously difficult and race-condition prone. Pushing the flag down transactionally into the execution engine ensures `CommandExecutor` remains stateless and thread-safe.
4. **CROSSSLOT Hardening**
   - *Implementation:* Multi-key operations (`DEL` and future `MGET`/`MSET`) must enforce that all keys resolve to the exact same CRC16 hash slot in Cluster Mode.
   - *Learning:* A multi-key command bypasses standard split-scatter-gather routing when `ClusterProvider` is enabled, deferring to the inner `CommandExecutor` which sequentially runs `Crc16.Compute` against all trailing arguments. If any mismatch occurs, it throws a `-CROSSSLOT` error, enforcing the strict Redis Cluster contract.
