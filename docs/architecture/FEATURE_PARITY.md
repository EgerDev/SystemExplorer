# SystemExplorer feature parity: source-grounded Phase 2 checkpoint

## Scope and evidence rules

This matrix is pinned to **`9101b5580d10531f49ec566e0837c7f28339a3dd`**, including
the original System Informer source preserved in this fork. It is not a claim
about a different upstream release, a successful native build, or runtime visual
acceptance. Reviewed 2026-11-05. Only this document is changed at this checkpoint.
The existing WinUI 3 / .NET 10 boundary and upstream notices remain intact.

**Status vocabulary:** **Implemented** = source contains collection and a consuming
surface for the precisely named subset; **Partial** = some layers/subfeatures are
present but not full parity; **Unsupported** = no implementation in the current
provider/UI boundary; **Privileged/driver** = the cited advanced path needs access
or driver facilities outside the standard-user/no-driver contract. Access denial
can still occur for otherwise implemented features. A nullable field or navigation
item alone is not implementation. Planned means proposed, not shipped.

Original capabilities below are verified in checked-out source, not exercised in
a running legacy build. Plugin capabilities are explicitly labeled: plugin source
presence does not prove default loading, portable packaging, or a WinUI surface.
Full parity is **not** the product objective where it would require destructive
actions, a kernel driver, unsupported NT internals, or undisclosed network access.

Environment of this research: Windows Server 2022 Standard, version 10.0.20348;
inherited Administrator/high-integrity token, with SeDebugPrivilege enabled.
No elevation was requested and no privilege was changed. This is **not Windows 11
standard-user acceptance**. No app was launched, benchmark authored, or process
controlled for this documentation task.

Parent update: private-working-set implementation is being developed separately
on `feature/phase2-private-working-set`. It is **pending review/integration**, not
implemented at this matrix's frozen baseline. Do not duplicate that work.

## Current implementation source map

All paths are relative to the repository root; symbols disambiguate the relevant
code. Relative links can be followed in a checkout at the pinned revision.

| Key | Source and verified responsibilities |
| --- | --- |
| C | [Core/Monitoring.cs](../../src/SystemExplorer.Core/Monitoring.cs): `ProcessIdentity`, `ProcessSnapshot`, `MemorySnapshot`, `ServiceSnapshot`, `ConnectionSnapshot`, `IMonitorProvider`, `IRelationshipProvider`. Contracts, not proof of population. |
| W | [Windows/WindowsMonitorProvider.cs](../../src/SystemExplorer.Windows/WindowsMonitorProvider.cs): `Capture`, `ReadProcess`, `ReadOwner`, `ReadMemory`, `ReadSystemCpu`, `ReadDisk`; [NativeMethods.cs](../../src/SystemExplorer.Windows/NativeMethods.cs): `MemoryCounters`, `PerformanceInfo`, PSAPI/Toolhelp/PDH imports. |
| R | [Relationships/WindowsRelationshipProvider.cs](../../src/SystemExplorer.Relationships/WindowsRelationshipProvider.cs): `Capture`, `ReadEndpoints`, `ReadServices`, `IdentityMatches`; [EndpointTable.cs](../../src/SystemExplorer.Relationships/EndpointTable.cs): `Parse`; [NativeMethods.cs](../../src/SystemExplorer.Relationships/NativeMethods.cs): SCM/IP Helper/trust imports. |
| L | [Relationships/LocalInventory.cs](../../src/SystemExplorer.Relationships/LocalInventory.cs): `GetStartupItems`, `GetPublisher`, `Verify`, `ReadVerifiedSigner`, `RequireLocalPath`. |
| G | [Core/ProcessGrouping.cs](../../src/SystemExplorer.Core/ProcessGrouping.cs): `VerifiedParent`, `Create`, `ProcessGroup.PrivateBytes`, `CpuPercent`. |
| H | [Core/Diagnostics.cs](../../src/SystemExplorer.Core/Diagnostics.cs): `DiagnosticsHistory.Add`, `Explain`, `HistoryPoint`; [Sampling.cs](../../src/SystemExplorer.Core/Sampling.cs): `Sampling`. |
| A | [App/MainWindow.xaml.cs](../../src/SystemExplorer.App/MainWindow.xaml.cs): `RefreshInspector`, `RefreshDiagnostics`, `DrawHistory`, `PublisherClicked`; [MainWindow.xaml](../../src/SystemExplorer.App/MainWindow.xaml): inspector and diagnostics surfaces; [ShellViewModel.cs](../../src/SystemExplorer.App/ShellViewModel.cs): `ProcessRow`, `Apply`, `Rebuild`. |

`W`, `R`, etc. in the matrix refer to these exact paths and symbols. Unsupported
claims were checked against the provider implementations and the consuming App,
not inferred from missing text in a README.

## Capability matrix

### Identity, ownership, sampling, and memory

| Capability | Original System Informer source | SystemExplorer at baseline: collector and surface | Safe planned direction / parity boundary |
| --- | --- | --- | --- |
| Process enumeration and lifetime identity | [SystemInformer/procprv.c](../../SystemInformer/procprv.c), `PhProcessProviderUpdate`, `PhReferenceProcessItemForParent`, process records | **Implemented subset:** W uses Toolhelp and same-handle creation FILETIME; unstable rows remain visible but cannot acquire trusted history/relationships. A displays PID, metadata and availability. | Preserve PID + exact creation FILETIME, handle lifetime, capture-start guards and null semantics. No bare-PID caches. Original's richer process records are not migrated. |
| User/SID/session ownership | `procprv.c`, `PhpFillProcessItem`, plus [prpgtok.c](../../SystemInformer/prpgtok.c), `PhpOpenProcessTokenForPage` | **Implemented subset:** W `ReadOwner` opens token with TOKEN_QUERY, reads SID/session and resolves account; A inspector/user column consume it. Denied account lookup can coexist with usable SID. | Read-only token metadata; do not infer owner from path/name or use owner visibility as proof of authorization. Rich token/security UI remains separate. |
| Parent tree versus application grouping | [procgrp.c](../../SystemInformer/procgrp.c), `PhCreateProcessGroupList`, `PhpAddGroupMembersFromRoot`: shared ancestor/file/user plus visible-window heuristics over UI process nodes | **Partial:** G validates strictly older parent identity; groups matching full path, SID and session along that ancestry. A expands groups and shows inferred-parent text. No window/app/package identity inference. | Retain conservative inferred groups, not universal application ownership. Distinct executables in a browser/app remain separate. Audit missing-session behavior: G compares nullable sessions, so two null sessions can compare equal; unlike H's replacement heuristic it does not require `SessionId.HasValue`. Parent-owned clarification/fix before stronger claims. |
| Whole-machine and process CPU | `procprv.c`, `PhpUpdateCpuInformation`, `PhpUpdateCpuCycleInformation`, `PhProcessProviderUpdate` | **Implemented subset:** W + H/Sampling use elapsed CPU deltas, whole-machine normalization, first sample null; A CPU/table. System CPU intentionally null across multiple processor groups. | Preserve monotonic intervals and identity reset; processor-group-aware system/per-core data is future work, not inferred from existing process percentage. |
| Private committed bytes | `procprv.c`, `PhpUpdateDynamicInfoProcessItem`, `VmCounters`; [proctree.c](../../SystemInformer/proctree.c), private-memory columns | **Implemented:** W reads PSAPI `PrivateUsage`; G all-or-unknown sum; A table/Memory/inspector explicitly say private commit, not RAM. | Keep separate from residency and global commit. Never label group commit as physical memory owned. |
| Working set including shared pages | `procprv.c`, `VmCounters.WorkingSetSize`; [phlib/nativeprocess.c](../../phlib/nativeprocess.c), `PhGetProcessWorkingSetInformation` | **Implemented subset:** W EX working set; A selected-process inspector. No global distinct-page attribution. | Do not sum working sets into system used RAM. Detailed page classification is separate, potentially expensive work. |
| Private working set | `procprv.c`, `PhpUpdateDynamicInfoProcessItem` copies `SYSTEM_PROCESS_INFORMATION.WorkingSetPrivateSize`; [proctree.c](../../SystemInformer/proctree.c) consumes it | **Unsupported collection:** C nullable field and A inspector label exist, but W never assigns it; W limitation says unsupported. | Parent-owned PSAPI EX2 implementation pending integration, with honest old-OS fallback. Candidate A below. Native source's NT system-information path is not the proposed API. |
| Physical versus committed system memory | `procprv.c`, `PhpUpdatePerfInformation`, `PhpUpdateSystemHistory`; [sysinfo.c](../../SystemInformer/sysinfo.c) system-information UI | **Implemented subset:** W `GetPerformanceInfo`: total/available, commit/limit, cache, paged/nonpaged pools; C physical used = total - available; A Memory/Overview. | These are overlapping views, not additive buckets. Neither summed private commit nor summed resident sets explains all physical usage. Cache is API-defined SystemCache, not all reclaimable memory. |
| Standby/page lists, detailed memory regions | [memprv.c](../../SystemInformer/memprv.c), `PhQueryMemoryItemList`; [prpgmem.c](../../SystemInformer/prpgmem.c) | **Unsupported:** C standby remains null; no memory-region contract/provider/UI. | Later bounded selected-process `VirtualQueryEx` read-only region inventory [D10], with access/architecture limits. It does not supply global standby lists. No RAM clearing, working-set trimming, remote memory editor or allocation actions. |
| System CPU/RAM history | `procprv.c`, `PhpInitializeProcessStatistics`, `PhpUpdateSystemHistory`, per-CPU and system circular buffers | **Partial parity:** H stores 180 points of physical used, commit, CPU. A `DrawHistory` renders CPU or physical used only, sample-index spacing and gaps >10s/null; no commit graph/per-core graph/persistence. | Expose retained commit series only after UI acceptance; no claim of time-proportional chart spacing or persistent history. |
| Per-process trends, restart observations | `procprv.c`, `PrivateBytesHistory`, `CpuKernelHistory`, `CpuUserHistory`; [prpgperf.c](../../SystemInformer/prpgperf.c), `PhProcessPerformanceUpdateHandler` | **Partial:** H retains private-commit history per stable lifetime, growth/replacement heuristics; A Insights/Memory show explanations. No per-process CPU history or per-process graph. Replacement requires exactly one path/SID/session instance in adjacent samples. | Keep trend != leak and replacement != crash. Future per-process CPU/RAM graph needs explicit bounded identity-owned series contract. Do not reuse one PID's history for its successor. |

### Services, networking, provenance, and background activity

| Capability | Original System Informer source | SystemExplorer at baseline: collector and surface | Safe planned direction / parity boundary |
| --- | --- | --- | --- |
| Process/service association and current state | [srvprv.c](../../SystemInformer/srvprv.c), service provider; [prpgsrv.c](../../SystemInformer/prpgsrv.c) process services page | **Implemented subset:** R enumerates Win32 services, handles valid PID states and verifies process creation/liveness; A Services and selected-process inspector. Driver services not enumerated (`0x30` type mask). Inaccessible services may be silently omitted by Windows. | Keep service identity (name), service state and process lifetime distinct; no claim that a shared host PID identifies a unique service. No start/stop/configuration actions. |
| Service start type/configuration | `srvprv.c`, `PhUpdateServiceItemConfig`; [phlib/svcsup.c](../../phlib/svcsup.c), `PhGetServiceConfig`; [srvprp.c](../../SystemInformer/srvprp.c) | **Unsupported collection:** C `StartType` nullable and A `Start: Unavailable` exist; R never calls `QueryServiceConfig`. | Candidate B: `SC_MANAGER_CONNECT` + per-service `SERVICE_QUERY_CONFIG`, bounded buffers/work, base start-type only. Delayed/trigger settings are distinct, not guessed. |
| Service dependencies and dependents | [plugins/ExtendedServices/depend.c](../../plugins/ExtendedServices/depend.c), `EspServiceDependenciesDlgProc`, `lpDependencies`, `PhEnumDependentServices`; [phlib/svcsup.c](../../phlib/svcsup.c), `PhEnumDependentServices` | **Unsupported:** no dependency collection, contract or UI. | Later on-demand `QueryServiceConfigW` dependency MULTI_SZ and `EnumDependentServicesW` reverse edges [D3-D5]. Preserve group dependencies prefixed `+`, bounds/cycles/denial. New Core result contract belongs to parent. |
| IPv4/IPv6 TCP and UDP endpoint ownership | [netprv.c](../../SystemInformer/netprv.c), `PhNetworkProviderUpdate`, owner-module IP Helper tables | **Implemented subset / partial parity:** R reads four owner-PID tables with four-attempt/16 MiB bounds; parses scoped IPv6, ports and TCP states; PID-to-live-identity check. A Network and selected count. | Owner identity is separately verified, not atomic truth. UDP is local endpoint, not remote connection. Service/module attribution from owner-module tables is absent; do not imply it from the PID. |
| Network names, bandwidth, geography and diagnostics | `netprv.c` provider; [plugins/NetworkTools/main.c](../../plugins/NetworkTools/main.c), `NetworkItemEnableStats`, `UpdateNetworkNode`, `MenuItemCallback`; [plugins/ExtendedTools/etwmon.c](../../plugins/ExtendedTools/etwmon.c), `EtStartEtwSession` | **Unsupported:** no DNS, geography, traffic rates, ping/traceroute; A explicitly says throughput unavailable. | Local interface totals could be added separately; per-process/per-flow ETW/EStats needs a rights/overhead study. DNS and active probes are network activity and require explicit user intent, not background enrichment. |
| Executable path and local file actions | `procprv.c`, `PhpFillProcessItem`; original process properties | **Implemented subset:** W `QueryFullProcessImageNameW`; A copy path and guarded local directory opening. | Path is observed metadata, not proof of current on-disk image identity or safe executable content. No launching arbitrary process commands. |
| Embedded signature/publisher | `procprv.c`, `PhpFillProcessItemStage2` calls `PhVerifyFileCached`; [phlib/verify.c](../../phlib/verify.c), `PhVerifyFileCached`, `WinVerifyTrust_I` | **Partial:** L on-demand fixed-local-file verification, <=512 MiB, locked handle, offline cache-only trust and verified signer; A Publisher button with selection-generation guard. | Preserve explicit revocation freshness limitation. A successful certificate-name extraction alone is not verification; no automatic upload/reputation query. Reparse/network paths intentionally refused. |
| Catalog signatures and trust cache | `phlib/verify.c`, `CryptCATAdminAcquireContext2`, `CryptCATAdminCalcHashFromFileHandle2`, `CryptCATAdminEnumCatalogFromHash` | **Unsupported catalog verification:** L reports it explicitly; no reusable metadata cache shown in publisher provider. | Later bounded offline catalog lookup [D8], file identity/size/write-time invalidation and failure/denial distinct from unsigned. Do not call unverified catalog-only system binaries malicious. |
| Command line where permitted | [phlib/nativeprocess.c](../../phlib/nativeprocess.c), `PhGetProcessCommandLine`: `NtQueryInformationProcess(ProcessCommandLineInformation)` and older PEB fallback; `procprv.c` consumes | **Unsupported collection:** C field + A placeholder only, W explicitly unsupported. | Future opt-in selected-process WMI `Win32_Process.CommandLine` [D6], with finite query budget, privacy care and creation identity validation. No remote PEB reads, injection, or unsupported NT path; provider denial stays unavailable. WMI overhead/precision must be evaluated, not assumed equivalent. |
| Run keys/startup folders | [plugins/AgentTools/toolstart.c](../../plugins/AgentTools/toolstart.c), `AtListStartupEntries`, `AtpReadRunKeys`, `AtpReadStartupFolder` (plugin tool, not proof of original main-window startup page) | **Implemented subset:** L HKCU/HKLM, 32/64-bit Run/RunOnce, current/common startup folders, <=4096 entries; no shortcut target resolution or execution. A Startup refresh/stale display. | Configuration inventory is not enabled status, measured startup impact or proof something executed. Registry-view duplicates are not distinct launches. Preserve scope/source labels. |
| Broader autostarts, scheduled/background work | `toolstart.c`, `AtpReadWinlogonValues`, `AtpReadIfeoDebuggers`, `AtpReadAppInitDlls`, `AtpReadLsaPackages`, `AtpReadShellHooks`; [plugins/AgentTools/tooltask.c](../../plugins/AgentTools/tooltask.c), `AtpListScheduledTasks` | **Partial overall:** startup subset above + services; no scheduled tasks, shell hooks, WMI subscription inventory, execution history or background-task attribution. | Prefer local Task Scheduler COM read-only inventory [D7] on demand before broad persistence inventory. Explicit source coverage/ACL omissions. No disable/delete/run operation, no startup-impact guess. |

### Advanced diagnostics and intentionally excluded controls

| Capability | Original System Informer source | SystemExplorer at baseline | Safe planned direction / parity boundary |
| --- | --- | --- | --- |
| Thread counts and per-thread diagnostics | [thrdprv.c](../../SystemInformer/thrdprv.c), `PhpThreadProviderUpdate`, cycle/start-address/affinity and KPH branches; [prpgthrd.c](../../SystemInformer/prpgthrd.c) | **Partial:** W Toolhelp count; A count only, no thread records/stacks/waits. | Later selected-process Toolhelp thread enumeration + `OpenThread`/`GetThreadTimes` [D9]; verify thread lifetime and owning process. No suspension to capture stacks. Native start-address/WSL/thread-I/O branches are not automatically documented-API equivalents. |
| Handle count versus object/handle inventory | [hndlprv.c](../../SystemInformer/hndlprv.c), `PhHandleProviderUpdate`, `PhEnumHandlesGeneric`, `PhGetHandleInformationEx`; KPH file/object comparison branches | **Partial:** W `GetProcessHandleCount`; A selected count only. No object names/types/access table. | Keep count. Full arbitrary-process handle inventory/name resolution has no equivalent implemented documented Win32 table here; NT/driver routes and potentially blocking object queries need separate approval. Never close a target handle. |
| Modules/DLLs and loaded-image diagnostics | [modprv.c](../../SystemInformer/modprv.c), `PhModuleProviderUpdate`, `PhEnumGenericModules`, enclave modules, `KphReadVirtualMemory` path; [prpgmod.c](../../SystemInformer/prpgmod.c) | **Unsupported:** no module contract, provider or App table. | High-value later selected-process `EnumProcessModulesEx`/Toolhelp modules [D9], bounded retries and per-module path/version. Requires suitable process rights and correct WOW64 behavior; not full kernel/enclave/unloaded module parity. |
| Memory regions/content/search | [memprv.c](../../SystemInformer/memprv.c), `PhQueryMemoryItemList`; [memsrch.c](../../SystemInformer/memsrch.c) memory search | **Unsupported:** memory counters are not a memory map or content reader. | Read-only `VirtualQueryEx` region summaries [D10] may be useful later. Content search/dump/editor expands privacy and read-right requirements; no automatic migration. |
| Token/security diagnostics | [prpgtok.c](../../SystemInformer/prpgtok.c), `PhpOpenProcessTokenForPage`, `PhpProcessTokenHookProc` | **Partial:** SID/account/session only. No integrity, privileges, group or ACL editor. | Documented `GetTokenInformation` with TOKEN_QUERY for integrity/elevation/privilege display is feasible under a new read-only result contract. No privilege enabling or security descriptor mutation. |
| Jobs and protected process access | [prpgjob.c](../../SystemInformer/prpgjob.c), `PhpOpenProcessJobForPage` directly calls `KphOpenProcessJob`; module/handle/thread providers contain `KsiLevel` gates | **Privileged/driver path excluded:** no arbitrary-job inspector or protected-process bypass in new boundary. | Do not label every original diagnostic driver-only: many ordinary queries work without it. This specific arbitrary process-job opening path and advanced KPH paths are not migrated. Protected access failure is a valid observation. |
| Wait chains/hang investigation | [plugins/ExtendedTools/waitchain.c](../../plugins/ExtendedTools/waitchain.c), `WaitChainCheckThread` calls `GetThreadWaitChain` | **Unsupported:** no wait-chain provider/UI. | Later on-demand documented WCT [D11], bounded asynchronous result with ACL/time limits. A wait graph is evidence, not automatic hang/kill advice. No debugger attach required for the initial proposal. |
| GPU engines/memory and NPU | [plugins/ExtendedTools/gpumon.c](../../plugins/ExtendedTools/gpumon.c), `EtpGpuInitializeD3DStatistics`, `EtGpuProcessesUpdatedCallback`, `D3DKMTQueryStatistics`; [npumon.c](../../plugins/ExtendedTools/npumon.c) | **Unsupported:** C optional GPU metric unassigned; A GPU page truthfully says unavailable. No dedicated/shared GPU memory, engine or NPU series. | Research documented PDH GPU Engine/GPU Process Memory counters with hardware/OS availability, adapter identity and aggregation semantics. Never sum engine percentages blindly or substitute CPU. Requires parent-owned richer contracts and actual supported GPU validation; not guaranteed in this VM. |
| Physical disks and process I/O | [plugins/ExtendedTools/etwmon.c](../../plugins/ExtendedTools/etwmon.c), `EtStartEtwSession`; [etwdisk.c](../../plugins/ExtendedTools/etwdisk.c), `EtInitializeDiskInformation`; [plugins/HardwareDevices/disk.c](../../plugins/HardwareDevices/disk.c), `DiskDevicesUpdate` | **Partial:** W PDH PhysicalDisk(_Total) read/write rates, two-sample warmup; W process `GetProcessIoCounters`; A Disks separates them. No per-disk latency/queue, per-file/process physical-disk attribution. | Add documented per-device counters only after correct instance/lifetime mapping. Generic I/O includes cached/device/network operations. Kernel ETW sessions can require privileges [D12]; no implicit session takeover/elevation. |
| Hardware/adapters/devices | `plugins/HardwareDevices/disk.c`, `DiskDeviceUpdateDeviceInfo`; [SystemInformer/devprv.c](../../SystemInformer/devprv.c) device provider | **Unsupported:** disk totals are not hardware inventory or device management. | Optional documented SetupAPI/Configuration Manager read-only inventory after core diagnostics. Avoid changing drivers, firmware, devices or policy. |
| Managed runtime details | [plugins/DotNetTools/asmpage.c](../../plugins/DotNetTools/asmpage.c), `DotNetAsmPageDlgProc`, `DotNetAsmRefreshTraceQuery` | **Unsupported:** no CLR assemblies/runtime diagnostics surface. | Defer; debugger/ETW/runtime-version and overhead contracts require an independent study. Never initialize this merely because target is .NET. |
| Process controls, priority, memory trimming and service changes | [SystemInformer/actions.c](../../SystemInformer/actions.c), `PhUiTerminateProcesses`, `PhUiSuspendProcesses`, `PhUiSetPriorityClassProcesses`; `srvprp.c` config UI | **Unsupported intentionally; access-sensitive:** new interfaces are read-only. | Excluded from Phase 2 safe parity. No terminate/suspend, priority/affinity mutation, service start/stop/delete, remote execution, driver installation or elevation endpoints. |
| Online checks/plugins | [plugins/OnlineChecks/main.c](../../plugins/OnlineChecks/main.c); [plugins/NetworkTools/main.c](../../plugins/NetworkTools/main.c), `MenuItemCallback` | **Unsupported intentionally:** frontend does not load original plugins or upload process/file data. | Retain local-first boundary. Online reputation/active network tools, generic plugin hosting and cloud agents are separate product decisions, not prerequisites for diagnostics. |

## Findings that constrain future claims

1. **Collection gaps already have UI labels:** private working set, command line,
   GPU and service start type. These must not be counted as working capabilities.
2. **Current grouping is deliberately narrower than original grouping**, but the
   nullable-session equality noted above needs parent judgment before describing
   missing metadata as always singleton. Parent timestamp ordering cannot prove
   an atomic ancestry relationship; keep the inferred qualifier.
3. **Availability is unevenly modeled.** Process rows have `DataAvailability` and
   a combined reason; relationships have only snapshot-wide limitations. A null
   service start type has no machine-readable per-service cause. A configuration
   cache would additionally need age/source semantics before retaining old values.
4. **A PID is not a service, module, network flow or application identity.** R
   validates process creation after table reads and rejects post-capture/exited
   owners; it does not make the overall observation atomic. The inspector limits
   association use to observations within ten seconds.
5. **Legacy capability is not a reusable library contract.** The providers depend
   on global callbacks, settings, reference-counted process items and plugin/UI
   lifetimes. Reuse algorithms or narrow reviewed wrappers, not raw structs/global
   providers. Preserve [ADR-001](ADR-001-MONITORING-BOUNDARY.md) and [CONTRACTS](CONTRACTS.md).

## Candidate A: private working set (parent-owned; no duplicate implementation)

**Independent improvement:** fill only `ProcessSnapshot.PrivateWorkingSetBytes`
from documented PSAPI EX2 when supported. The inspector already consumes it;
no Core shape change and no switch of the table from private commit to resident
memory. Parent's approved design is authoritative: probe returned size/sentinel,
preserve EX fallback and exact lifetime validation for any higher-rights fallback
handle, never turn unsupported output into zero.

Evidence: [D1-D2] document EX2's `SIZE_T PrivateWorkingSetSize` followed by
`ULONG64 SharedCommitUsage`, and `GetProcessMemoryInfo` accepting EX2 with
QUERY_INFORMATION **or QUERY_LIMITED_INFORMATION** on modern Windows. The EX2
page requires Windows 10/11 22H2 with the September 2023 cumulative update; it
does not state a Server minimum. API availability must not be inferred from
the .NET target or a broad Windows 10/11 label. Server 2022 can only provide a
fallback observation, not the target client acceptance result.

Keep missing/unsupported EX2 private working set null while retaining successful
EX commit/working-set fields. Do not conflate a returned measured zero with an
untouched extended field. Do not cache process values by PID. Do not change rights
or debug privilege automatically. The older documented `QueryWorkingSet` alternative
requires QUERY_INFORMATION + VM_READ, a variable page buffer and bounded retries;
its `Shared` bit says **sharable**, not simply `ShareCount == 1`. It is more expensive
and a separate proposal, not an automatic all-process fallback [D13].

**Acceptance proposal for parent's implementation:** test actual x64 struct layout,
populated EX2 vs unchanged/smaller output, failed EX2 + successful EX fallback,
denial, legitimate zero, and reused PID on reopened memory handle. Live Windows 11
standard-user self/owned-child measurements must prove a supported path; an older
host must remain truthful on unsupported output. Compare like-for-like private
resident bytes, not Task Manager's unrelated default columns. Tests and native
smoke evidence belong to parent's branch, not this documentation checkpoint.

## Candidate B: read-only base service start type (proposal, not authorized code)

### Proposed scope and existing seams

Fill existing `ServiceSnapshot.StartType` in R `ReadServices`, consume the existing
A Services row. Change only Relationships native imports/provider and its tests
after ownership is granted. Do **not** alter Core, App, CONTRACTS, parent work or
the original native files. This is base configured start type, not delayed-auto,
trigger-start, dependency order, enabled/running state or a startup-impact score.

Official basis [D3-D5]: `OpenServiceW` requires `SC_MANAGER_CONNECT` on the manager;
`QueryServiceConfigW` requires `SERVICE_QUERY_CONFIG` on the service and supports
Windows XP/Server 2003 onward. R currently asks the manager only for enumeration
(`0x4`); proposed explicit manager mask is `SC_MANAGER_CONNECT | SC_MANAGER_ENUMERATE_SERVICE`
(`0x5`). Never request `ALL_ACCESS`, change configuration rights or elevation.
Default local authenticated-user rights often permit this, but each service DACL
is authoritative; enumerate permission is not configuration permission.

### Bounded algorithm

1. Preserve the current bounded status enumeration and owner validation. Open each
   selected service by **service name**, not PID or display name, with query-config
   only. Use the existing SafeServiceHandle ownership pattern and System32 imports.
2. Query required size with a zero buffer; accept only expected insufficient-buffer
   result and a required size between native header size and **8192 bytes**. Allow
   at most three total calls (probe plus up to two data calls) with strictly growing
   valid sizes. Do not parse or trust an insufficient-buffer output.
3. Marshal only the fixed header after successful collection; read `dwStartType`
   (DWORD) from a correctly pointer-aligned x64 `QUERY_SERVICE_CONFIGW`. This slice
   does not dereference binary-path/dependency/account strings or expose them.
4. Map 0/1/2/3/4 to `Boot`/`System`/`Automatic`/`Manual`/`Disabled`; preserve unknown
   successful values as `Unknown (<number>)`, not Manual. Boot/System apply to
   driver services; current Win32-service enumeration normally will not yield
   these, but the mapper should remain faithful. Document that this is configured
   behavior, potentially for the next service start, not runtime state.
5. On denied/deleted/error/oversized/retry-exhausted/skipped queries keep StartType
   null and preserve the service status row. Use `RelationshipSnapshot.Limitations`
   for categorized counts and a small bounded set of service-name examples; never
   put error prose inside a successful start-type value or poison unrelated rows.
6. Check cancellation before/after every native call; use try/finally or SafeHandle
   for every buffer/handle. Synchronous SCM calls are not interruptible by a .NET
   CancellationToken: a between-call deadline is not a hard call timeout. No
   abandon-and-overlap Task.Run loop or blocking the UI dispatcher.

### Work budget and availability decision for parent

Recommended first slice: **no persistent config cache** (the shared type has no
per-field timestamp); cap attempts at 256 services or 100 ms between native calls
per relationship capture, whichever is reached first. These are proposed guardrails,
not measured acceptable costs. Rotate the starting service name across captures
to avoid starving the same tail. Skipped rows stay null in that capture, with
explicit coverage counts. The tradeoff is changing availability between snapshots;
the parent should accept this behavior or choose a separate on-demand config
contract before implementation. Do not silently retain stale cached values under a
fresh `CapturedAt`. A hard wall-time guarantee would require another isolation
design, outside this small slice.

Snapshot-level reasons are compatible with the existing contract but not ideal.
If per-service `Availability`, `UnavailableReason`, `ConfigCapturedAt`, delayed-auto
or dependency results are required, **stop for parent-owned Core/API agreement**.
Do not smuggle these through `StartType` strings. For full start-mode UX use
`QueryServiceConfig2W(SERVICE_CONFIG_DELAYED_AUTO_START_INFO)` and trigger info
only as separately nullable supported details, never guess false after denial.

### Independently testable acceptance

- Internal fake SCM seam (not public Core): valid base enum/unknown enum; denied
  open, service disappearance, oversize/changing buffer; all preserve status row,
  null failures and release resources. Assert no write access bits requested.
- Budget/cancellation test: stop new native calls at the count/time boundary,
  rotate coverage, preserve unqueried nulls and limitations, dispose on cancellation.
  These deterministic failures must not require modifying real service ACLs.
- Windows smoke: read an existing permitted service and independently compare
  its base config with `sc.exe qc <name>` or a separate direct SCM observation.
  No installing/stopping/reconfiguring services, no elevated test as a substitute
  for standard-user evidence. Include at least one real standard-user run later.
- After implementation, run `dotnet run --project tests/SystemExplorer.Relationships.Smoke -c Release`
  and `dotnet run --project tests/SystemExplorer.Core.Tests -c Release` (SDK 10.0.403).
  Exact assertions/new internal seam belong to the authorized implementation,
  not to this source-only checkpoint. Parent owns UI review and integration gates.

## Prioritized safe roadmap

| Priority | Deliverable | Dependency / independently verifiable exit gate |
| --- | --- | --- |
| P0 | Integrate/review parent's private-working-set slice, keep honest commit/residency labels | Candidate A; Win11 standard-user supported path plus old-OS null fallback. No full parity claim. |
| P0 | Decide SCM StartType budget and absence semantics; then implement Candidate B | Existing shape can carry base value only. Deterministic failure/resource tests and read-only service smoke. |
| P0 | Resolve nullable-session grouping wording/behavior | Parent owns G/Core; one missing-session regression case before broad accurate-grouping claims. No new package/window heuristic by default. |
| P1 | Selected-service dependencies and optional start modes | Parent-designed per-service availability/age/dependency contract; bounded MULTI_SZ parsing, group nodes, cycle and permission tests. |
| P1 | Catalog signature coverage | Bounded offline local file identity/trust work; embedded/catalog/unsigned/unreadable fixtures, cache freshness, no network. |
| P1 | Selected-process module list and thread timing; token integrity read-only | Separate on-demand diagnostics contract, identity/lifetime validation and bounds; native architecture/access tests. Counts alone do not satisfy these gates. |
| P2 | Command line via WMI and scheduled-task inventory | Approve privacy/timeout/provider/creation-time semantics; known owned-process comparison and denied/no-result cases; no PEB or service control. |
| P2 | Richer process/commit history and timestamp-correct graphing | Bounded model contract + parent visual/accessibility acceptance; missing data and PID replacement tests. |
| P2 | Per-device disk/interface counters and GPU research | Hardware-backed counter semantics and overhead validation; parent-authored measurements. No synthetic or summed generic-I/O substitution. |
| Deferred | Wait chains, detailed regions, managed-runtime diagnostics | On-demand independent contracts and rights/overhead review. No raw native provider migration. |
| Excluded | Kernel/driver protected access, mutation, injection, automatic elevation, online plugins | Remain outside read-only local-first architecture unless an explicit future product/security decision changes the boundary. |

No cloud benchmarking infrastructure, Figma-derived claims, binary publication or
kernel migration is needed for this roadmap. Original-source coverage above is
broad but not a promise to reproduce every plugin menu, action or OS-specific
kernel internal.

## Official API references and research provenance

- **D1:** [PROCESS_MEMORY_COUNTERS_EX2](https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-process_memory_counters_ex2).
- **D2:** [GetProcessMemoryInfo](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-getprocessmemoryinfo).
- **D3:** [QueryServiceConfigW](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-queryserviceconfigw), [QUERY_SERVICE_CONFIGW](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-query_service_configw).
- **D4:** [OpenServiceW](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-openservicew), [Service security and access rights](https://learn.microsoft.com/en-us/windows/win32/services/service-security-and-access-rights).
- **D5:** [QueryServiceConfig2W](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-queryserviceconfig2w), [EnumDependentServicesW](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-enumdependentservicesw).
- **D6:** [Win32_Process](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-process) documents CommandLine and CreationDate; it is not an unconditional permission guarantee.
- **D7:** [Task Scheduler](https://learn.microsoft.com/en-us/windows/win32/taskschd/task-scheduler-start-page).
- **D8:** [WinVerifyTrust](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/nf-wintrust-winverifytrust).
- **D9:** [Toolhelp snapshots](https://learn.microsoft.com/en-us/windows/win32/toolhelp/taking-a-snapshot-and-viewing-processes), [EnumProcessModulesEx](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-enumprocessmodulesex), [GetThreadTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getthreadtimes), [GetProcessHandleCount](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocesshandlecount).
- **D10:** [VirtualQueryEx](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-virtualqueryex).
- **D11:** [GetThreadWaitChain](https://learn.microsoft.com/en-us/windows/win32/api/wct/nf-wct-getthreadwaitchain).
- **D12:** [Configuring/starting ETW sessions](https://learn.microsoft.com/en-us/windows/win32/etw/configuring-and-starting-an-event-tracing-session).
- **D13:** [QueryWorkingSet](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-queryworkingset), [PSAPI_WORKING_SET_BLOCK](https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-psapi_working_set_block).

Context7 resolved the official Win32 API library but returned irrelevant snippets
for EX2/SCM questions; those results are not evidence for this design. Direct
Microsoft Learn pages above were used instead. [DeepWiki research](https://deepwiki.com/search/identify-source-files-and-symb_91fe792e-fc56-4e08-9af6-4ff6f102c3ef)
provided pointers, not the authority: key claims were checked against this fork.
In particular the ordinary original process private-working-set field comes from
`SYSTEM_PROCESS_INFORMATION`, and actual dependency UI is in ExtendedServices;
neither was inferred from DeepWiki's broad descriptions.

## Checkpoint verification and limitations

Research inspected C/W/R/L/G/H/A source and the cited native/plugin symbols at the
pinned baseline, plus official docs. No source provider, contract or App edits;
no new diagnostic executed. Documentation validation checks relative source links,
symbols, whitespace and owned-path-only diff. No build/test/GUI/performance pass is
claimed for a documentation-only change. Standard-user Windows 11 and hardware
gates remain outstanding with the parent. Implementation starts only after parent
agrees the remaining contract/budget decisions; this branch stops at its push.
