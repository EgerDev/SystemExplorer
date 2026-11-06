# Bounded developer diagnostics

The new frontend uses narrow managed contracts and documented local Windows APIs, not the preserved AgentTools endpoint registry. Original source and notices remain unchanged. There are no process/thread controls, memory-content reads, service mutations, target execution, elevation, privilege enabling, drivers or external lookups.

## Recent selected-process history

Select a process member to retain that observed `(PID, creation FILETIME)` lifetime. **Recent history** in the inspector or on Memory opens a frozen, searchable/sortable inventory. At most eight recently selected lifetimes and 180 samples each are retained in memory for this application session; nothing is written to disk. Selecting a new lifetime can evict the least-recently-selected one. This is not a complete process-event recorder or post-mortem reconstruction.

Private commit, working set, private working set and machine-normalized CPU preserve unavailable readings. Working sets contain shared resident pages and cannot be summed into physical RAM. One missing sample marks a transition to **not observed**, not confirmed exit. Pause, collection errors and long sampling intervals break continuity; gaps are not zero. The PID may be reused without sharing history with its previous lifetime. Growth alone is not a leak diagnosis.

## Threads and selected wait chains

**Threads / waits** captures at most 256 threads of the selected stable process (at most 32,768 system entries visited). Retained process/thread handles request only query-limited information and SYNCHRONIZE. Owner PID, creation FILETIMEs and zero-duration liveness waits are checked. Two counter samples approximately 300 ms apart yield CPU as `100 * (delta kernel + delta user) / elapsed 100-ns ticks`, using each thread's monotonic interval. Unlike the Processes table, 100% means one logical CPU. Kernel/user totals are cumulative CPU seconds. Invalid deltas, failed reads and over-100 samples remain unavailable rather than clamped.

Selecting one stable thread enables **Inspect selected wait chain**. WCT runs asynchronously with flags 0, at most 16 nodes and one outstanding request across provider instances. No out-of-process expansion, COM activation callbacks, stacks, symbols or privilege changes are enabled. Callback state and buffers stay alive until `CloseThreadWaitChainSession` returns; cancellation and timeout cleanup run off the UI thread and may outlast the cooperative budget. Reserved fields and undocumented-duration measurements are omitted.

A single WCT node means unblocked **or** an unsupported wait; a reported cycle is not a root-cause diagnosis. Foreign-node PID/TID values are not attributed to current snapshots. Missing privileges, exited identities, partial/truncated results and denied access are explicit. Native testing observed busy/waiting owned-thread counter differences and one-node WCT; it did not establish multi-process deadlock coverage or standard-user WCT availability.

## Pagefiles

Memory's **Pagefiles** action invokes `EnumPageFilesW` on demand. `TotalSize`, `TotalInUse` and `PeakUsage` are page counts, each independently multiplied by the actual `GetSystemInfo` page size with checked arithmetic. The table shows size, current usage and historical peak; selected details include exact bytes. Peaks are not clamped to current sizes. These quantities are neither system commit nor additional physical RAM.

Limits: 128 records, bounded name decoding and a three-second cooperative budget. Native callbacks contain managed exceptions and remain rooted until native return. Cancellation is surfaced after the synchronous call returns, not as a hard native-call deadline. A successful empty enumeration is explicitly different from denied/failed collection; partial prefixes do not establish absence. The current Server VM returned a successful empty inventory, so live nonempty enumeration still needs an appropriate host.

## Services and startup

Click a Services row to read its SCM configuration and dependencies by service name, independently of any observed process owner. Only `SC_MANAGER_CONNECT | SC_MANAGER_ENUMERATE_SERVICE` and, per service, `SERVICE_QUERY_CONFIG | SERVICE_ENUMERATE_DEPENDENTS` are requested; if that open is denied, one retry uses `SERVICE_QUERY_CONFIG` alone and dependents are reported as denied. Binary command, account/object name, load-order group, service type, base start type and error control are unexpanded configuration values for a future start—not confirmation of current runtime settings. Binary paths are never expanded, resolved, opened or executed.

`QueryServiceConfig2W` extensions are separately statused observations: delayed auto-start flag, trigger metadata (type, action, subtype GUID with known winsvc.h names, data-item count only; data blobs are never decoded; 64 triggers), description (2048 characters) and service SID type. A failed or timed-out query leaves that value unavailable with a reason; it never means "not delayed" or "no triggers". `EnumDependentServicesW(SERVICE_STATE_ALL)` lists up to 512 dependents within 64 KiB and three calls.

`ServiceGraph.Build` is pure Core logic over inventory rows: forward dependencies and reverse dependents, bounded by depth (default 16) and node count (default 512). `+group` entries stay group leaves listing matching members; services depending on the root's own load-order group appear as dependents of that group, not of the root by name. Cycles (up to 32) are reported as observed configuration cycles, not deadlocks.

Driver services come from `EnumServicesStatusExW(SERVICE_DRIVER, SERVICE_STATE_ALL)` (2048 rows, 256 KiB, 16 paging calls) plus a per-driver bounded `QueryServiceConfigW` with `SERVICE_QUERY_CONFIG` only; start type, error control, unexpanded image path and load-order group are null with a per-row status when unavailable. Driver files are never accessed.

The persistence inventory statuses each source and registry view separately (Run/RunOnce, Startup folders and scheduled tasks live elsewhere). Registry reads use `RegOpenKeyExW(KEY_READ)` with explicit 64- and 32-bit views for HKLM Software keys (HKCU keys used here are shared and read once): Winlogon Shell/Userinit/Notify, Image File Execution Options Debugger/GlobalFlag, SilentProcessExit monitors, AppInit_DLLs/LoadAppInit_DLLs, Active Setup StubPath, KnownDLLs (reference only), LSA Authentication/Security/Notification Packages, Print Monitors and Netsh helpers. Values are raw and unexpanded (DWORD as hex, one row per MULTI_SZ item); a missing key is an available empty observation. Bounds: 8 KiB per value, 2048 subkeys, 4096 rows. WMI reads local `root\subscription` through `WbemScripting.SWbemLocator` with forward-only/return-immediately queries (1024 rows per class): `__EventFilter` name/query language/query, `__EventConsumer` subclass and name (only ExecutablePath/CommandLineTemplate for CommandLineEventConsumer, only ScriptingEngine/ScriptFileName for ActiveScriptEventConsumer; ScriptText is never read) and `__FilterToConsumerBinding` references. Registry and WMI each have a five-second cooperative budget.

Dependency prefixes are preserved: `+group` is a load-order group, not a service link. The provider bounds configuration to 8192 bytes, three total query calls and 256 returned dependencies; strings and MULTI_SZ terminators must lie inside the owned buffer. Malformed fields are partial, missing/deleted services are explicit, and denied access does not become an empty success. The three-second cooperative budget cannot interrupt SCM calls. Paths/accounts can contain sensitive local metadata; the app does not upload or log them.

Startup folders omit only case-insensitive `desktop.ini`. Registry values with that name remain visible. Other folder entries and shortcuts remain unresolved inventory, not claims that each item is executable.

## Scheduled tasks

Startup's **Scheduled tasks** opens a separate local current-token inventory; it is not exclusively logon/startup tasks. `Schedule.Service.Connect()` has no remote server or credentials. `GetTasks(1)` requests hidden tasks and `GetFolders(0)` traverses local folders. Only name, path, state, enabled, action types/executable paths and trigger types are read. Arguments, dates, XML and security descriptors are not collected. COM handlers are labeled, never activated; tasks are never run, stopped, registered, deleted or edited.

Bounds: 1000 tasks, 256 attempted folders including root, depth 16, 32 actions/triggers and 4096 UTF-16 characters per field. Calls are serialized per provider and owned COM wrappers are released on cleanup. The three-second budget is cooperative; native COM allocation/calls are not hard-bounded. Inaccessible entries can be omitted by Windows, so even an Available result is limited to what the current token can enumerate.

## Lifetime validation

Process/module inspection and relationship-owner validation use creation FILETIME plus zero-duration waits on retained handles requesting `SYNCHRONIZE`. A running object's exit FILETIME is undefined; exit code 259 can also be an actual exit code. Neither is used as a liveness shortcut. Handle waits do not change process/thread state or enable privileges.

## Verification boundaries

Focused deterministic/provider smokes exercise conversion, malformed native buffers, denial, missing services, count/byte limits, cancellation and cooperative budgets. Native Server 2022 evidence is not Windows 11 standard-user, accessibility, physical-GPU, release or merge acceptance. Disk latency, per-process bandwidth and GPU collection remain outside this increment; counter availability alone does not validate attribution or aggregation.
