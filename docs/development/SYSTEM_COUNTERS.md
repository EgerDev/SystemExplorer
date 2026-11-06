# Read-only system counters

`WindowsSystemCounterProvider` implements the stateful `ISystemCounterProvider` contract. It reports system activity, not per-process disk/network attribution. Captures run off the caller thread and are serialized; dispose the provider to close its PDH queries. No UI integration is included.

## Local sources

PDH uses **English counter paths**, independent of the display language, and `PDH_FMT_DOUBLE | PDH_FMT_NOCAP100`:

- Disk: `\PhysicalDisk(*)\Avg. Disk sec/Read`, `\PhysicalDisk(*)\Avg. Disk sec/Write`, `\PhysicalDisk(*)\Disk Reads/sec`, `\PhysicalDisk(*)\Disk Writes/sec`, `\PhysicalDisk(*)\Current Disk Queue Length`, `\PhysicalDisk(*)\Disk Read Bytes/sec`, `\PhysicalDisk(*)\Disk Write Bytes/sec`, `\PhysicalDisk(*)\% Idle Time`.
- CPU: `\Processor Information(*)\% Processor Utility`, `\Processor Information(*)\% Privileged Time`, `\System\Context Switches/sec`, `\System\Processor Queue Length`.
- Memory: `\Memory\Standby Cache Reserve Bytes`, `\Memory\Standby Cache Normal Priority Bytes`, `\Memory\Standby Cache Core Bytes`, `\Memory\Modified Page List Bytes`, `\Memory\Free & Zero Page List Bytes`, `\Memory\Page Faults/sec`, `\Memory\Page Reads/sec`, `\Memory\Pages Input/sec`.
- Compression: `\Process(MemCompression)\Working Set`, `\Process(MemCompression)\ID Process`.
- GPU: `\GPU Engine(*)\Utilization Percentage`, `\GPU Adapter Memory(*)\Dedicated Usage`, `\GPU Adapter Memory(*)\Shared Usage`, `\GPU Process Memory(*)\Dedicated Usage`, `\GPU Process Memory(*)\Shared Usage`.

Network uses `GetIfTable2` byte counters and a monotonic timestamp at each table read; `FreeMibTable` always releases the table. DXGI uses `CreateDXGIFactory1`, `EnumAdapters1`, and `GetDesc1` for adapter descriptions, LUIDs, memory capacities and software flags; no device is created, and COM pointers are released. Inventory is cached, refreshed for unknown counter LUIDs at most once per 30 seconds.

All sources are read-only and local-first. No network requests, elevation, enabled privileges, drivers, process control, process-memory reads, duplicate handles, service/registry/WMI mutations or ETW sessions are used. Process identity opens request only query-limited information plus synchronization for zero-time liveness waits; compression image attribution requests only query-limited information.

## Values and aggregation

- The first capture is a **warmup sample; rates available from the next refresh**. Each rebuilt section warms up independently. Instantaneous gauges may already be measured. A new network interface also requires two observations. Capture interval and interface-table interval are separate monotonic measurements.
- `CounterValue` states are `Measured`, `Warmup`, `NoActivityInInterval`, and `Unavailable`. Null values have a non-Measured state and a reason. Only valid/new PDH item statuses (0/1) are accepted; other statuses remain unavailable with hexadecimal diagnostics. First-query invalid-data/no-data function statuses represent warmup, not measured zero.
- Disk latency converts seconds to milliseconds (x1000). Zero completed reads/writes produces `NoActivityInInterval`, **not 0 ms**; missing operation rate cannot establish latency. `_Total` is separate from disk rows and represents the whole system. Idle time is not clamped.
- Processor Utility can exceed **100%** under turbo/above-nominal frequency and is shown as measured. `_Total` supplies totals; group totals such as `0,_Total` are excluded from logical-processor rows.
- Network classification priority: filter flag, loopback type 24, tunnel type 131/nonzero TunnelType, hardware flag, otherwise virtual. Only hardware rows enter totals, including observed zero traffic on disconnected hardware. Virtual/filter/loopback/tunnel rows remain visible. Any missing included rate prevents publishing a partial sum as a total. Index reuse with a changed LUID, counter decreases/resets and non-positive elapsed time are unavailable. Zero/`ulong.MaxValue` link speed is unknown, not measured speed.
- GPU adapter memory sums physical-adapter instances for each LUID. For each `(LUID, physical adapter, engine index)`, utilization sums across PIDs; each engine type then takes the **maximum** engine utilization. Per-process `(PID, LUID)` utilization takes the maximum across its engines of each type. Headlines take the maximum across engine types: **never sum engine types**. Measured utilization is not clamped. Malformed names are skipped with a count and first example.
- GPU process identity is non-null only when query-limited creation FILETIME and before/after liveness checks succeed and the creation time matches the previous capture. First sight has only one-sample confirmation; denied, exited and changed identities remain explicit. Identity reads are limited to the capped process set.
- **Shared GPU memory is system RAM shared with the GPU, not additional physical RAM.** Inventory-only adapters are listed with unavailable counters and identified in the detail, but do not themselves downgrade counter availability.
- Compression is labelled **Memory Compression store resident working set (not bytes saved, not a compression ratio)**. An absent store is `Unsupported`; a real file path from `QueryFullProcessImageNameW` rejects an ordinary process named MemCompression. An opened process with no image path uses the store-attribution heuristic; access-denied/failed opens do not establish attribution. Compression has a separate status and does not downgrade the eight memory-list/paging counters when unsupported.

## Bounds and lifetime

At most 256 disk/interface/core/adapter/process output rows per section (disk aggregate is separate); DXGI retains at most 16 adapters. PDH wildcard input has a bounded prefix (257 disk/CPU items, up to 65,536 GPU items before aggregation), at most three buffer-fill attempts after probing, and an **8 MiB** allocation ceiling. Buffers are freed in `finally`; truncation is explicit and incomplete network tables cannot produce a hardware total.

Each PDH section owns its query. Wildcard missing-instance/object/counter or unexpected function failures dispose that query and rebuild it next capture without resetting other sections. The cooperative capture budget is approximately **3 seconds**, checked between operations; individual native calls cannot be forcibly interrupted. Unfinished sections report `TimedOut`. Cancellation throws without publishing a snapshot; disposal rejects further captures.

## Observed on the development host

Windows Server 2022 VM: DXGI enumerated Microsoft Basic Render Driver software adapter LUID `0x4F22`, plus two other Basic Render Driver adapter LUIDs (`0x4EEB`, `0xB9C5`) without counter instances. `Get-PnpDevice -Class Display` reported `IddSampleDriver Device` (OK) and Microsoft Basic Display Adapter (Unknown). The IddSampleDriver virtual display was **not enumerated as a named DXGI adapter**; no stronger device-to-LUID association was established. Memory compression was disabled (`Get-MMAgent`); no MemCompression instance existed, so compression was `Unsupported`. Physical-GPU accuracy and Windows 11 standard-user acceptance remain unverified.

Validation: `dotnet run --project tests/SystemExplorer.SystemCounters.Smoke -c Release` covers fixed native buffers/counter inputs, lifecycle/budget/cancellation and three live captures; `dotnet run --project tests/SystemExplorer.Core.Tests -c Release` covers the core regression suite. No native UI is changed or visually verified here.
