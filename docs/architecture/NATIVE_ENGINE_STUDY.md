# Native engine boundary study (M0/M1 evidence)

**Status:** bounded study, not an architecture decision or a reusable provider implementation.
Parent owns the final architecture and shared contracts. The parent's provisional direction is a
standalone .NET 10 Core + documented-Windows-API provider + WinUI 3 app, preserving the original tree.
No C ABI is required for that baseline. This study supports assessing that direction; it does not
demonstrate WinUI integration, full System Informer extraction, or M1 acceptance.

- Source baseline: `0a4975a4763ad202747397ef5daf5893849b5b00`, `EgerDev/SystemExplorer`.
- Owned branch: `feature/native-boundary-study`.
- Session: https://app.devin.ai/sessions/9288efc881b24c83b00096b5d141254a
- Scope: this file and the isolated `prototypes/native-boundary/` probe only.
- Original projects, driver policies, release configuration and cloud integrations are unchanged.

## 1. Observed source evidence

References below are repository paths and baseline line ranges, not claims inferred from filenames.
Use the [baseline tree](https://github.com/EgerDev/SystemExplorer/tree/0a4975a4763ad202747397ef5daf5893849b5b00)
to inspect the referenced code without drift.

| Surface | Observed behavior | Boundary implication |
|---|---|---|
| `SystemInformer/procprv.c:14-41,44-58,270-334` | Provider keeps process/system statistics, queues expensive metadata, stores historical records; includes app settings, extension manager, plugin, service, KPH and trace headers. Initialization allocates globals and processor-sized history/delta buffers. | This is an application subsystem, not an independently instantiated library. |
| `SystemInformer/procprv.c:372-464` | Items use the PH object manager, extension-sized allocation and object-create/delete notifications. Items own circular buffers, strings, SIDs, service references, handles, records and icon references. | A managed caller must not marshal `PH_PROCESS_ITEM` as a public mutable struct. |
| `SystemInformer/include/procprv.h:148-319` | Model contains pointers, locks/events, native handles, architecture-sized values, UI icon indexes, history and implementation flags. | Layout is unsuitable for a stable external ABI or cross-process transport. |
| `SystemInformer/procprv.c:1234-1263,2254-2288` | Query work acquires item references and uses the global work queue; completion flows through an S-list, fills items, signals stage one, and releases refs. | Stopping the periodic timer is not sufficient to finish asynchronous work or unload code safely. |
| `SystemInformer/procprv.c:2586-2639,2874-2936` | Update uses caches/settings, initializes statistics lazily, calls `PhEnumProcesses`, creates records, establishes initial deltas, queues/runs metadata, associates services, inserts items and fires plugin callbacks. | Extracting just the enumeration function is different from extracting the entire provider. |
| `phlib/native.c:1623-1706` | `PhEnumProcesses` delegates to `PhEnumProcessesEx(SystemProcessInformation)`, which allocates/retries `NtQuerySystemInformation` buffers and requires caller `PhFree`. | A useful narrow algorithm, but still PH allocation and Native API semantics; not documented-Win32-only. |
| `SystemInformer/procprv.c:2744-2771,4080-4113` | Removal uses process sequence numbers when extension support is enabled; otherwise compares PID and creation time. Parent lookup also validates temporal ordering. | PID alone is not lifetime identity. Preserve that lesson even with different APIs. |
| `SystemInformer/procprv.c:2641-2660,2781-2803,2981-3022` | Cycle CPU normalization sums individual cycle deltas, accounts for exits where possible, and estimates kernel/user split in some cases; fallback uses CPU time deltas. | A simpler documented time-based implementation must label its method rather than claim numerical equivalence. |
| `SystemInformer/procprv.c:1784-1794,2960-2975` | Copies private working set separately from VM counters; histories record `VmCounters.PagefileUsage` as private bytes. | Different memory dimensions must remain separate through the new model. |
| `SystemInformer/proctree.c:107-149,423-534,563-626,707,6448-6457` | Initializes an HWND-backed TreeNew control; nodes own referenced process items, children, presentation caches and extension state. Parent validation precedes linking. Duplicate-node-list copies pointers, not immutable snapshots. | `proctree.c` is presentation plus topology, not a headless process graph. Never consume its node pointers from an independent worker. |
| `SystemInformer/procgrp.c:101-244,248-339` | Grouping enumerates visible titled windows, consumes duplicated process-tree nodes, compares filename or path and SID, walks ancestors/children, and stops at `MaximumGroups`. Members acquire item refs; free releases them. | Reusing it unchanged requires the original UI graph and lifetime discipline. It is a heuristic bounded group list, not a complete application-identity service. |
| `SystemInformer/include/procgrp.h:15-41` | Representative is borrowed from the referenced member list; `HWND` and `PPH_LIST` are exposed. Filename vs full-path grouping is controlled by a flag. | Do not export this object ownership or a transient HWND as an application identifier. |
| `phlib/phlib.vcxproj`; `phlib/include/phbase.h:15-52`; `phlib/global.c:32-62` | phlib builds as a static library, includes phnt, references `kphlib_um.lib`, `samlib.lib`, `thirdparty.lib`; initialization configures runtime/system info, heap, locks, reference types and crypto. | Linking it is not the same as consuming a standalone supported SDK. The app's exported symbols are not evidence of an independent provider DLL. |
| `phnt/README.md`; `phnt/phnt.h` | phnt supplies Native API definitions; README explicitly describes definitions gathered from headers/symbols and reverse engineering/guessing. | phnt is headers, not a runtime monitoring library or a guarantee of documented API stability. |

### Initialization and lifetime, in order

1. `SystemInformer/main.c:66-121` initializes phlib, application policies, privileges, GUI support,
   settings/callbacks, optional KSI, auto-pool and controls, then application providers and extension
   management. This is the observed app sequence, **not** a recipe to copy into the new app (especially
   privilege and security-policy behavior).
2. `main.c:1459-1472` initializes process, service and network providers. `mainwnd.c:392-423` initializes
   three provider threads, registers process on primary and service/network on secondary, enables
   process/service and starts threads. Network registration is not enabled in this shown block.
3. `mwpgproc.c:1154-1218` references added items before queuing to the main thread, queues modified and
   removed events, and invokes UI processing on update. This is the existing thread handoff seam,
   not an immutable snapshot interface. `proctree.c:470,707` adds/releases the node's item reference.
4. `phlib/provider.c:491-525` unregisters with rundown protection. `provider.c:362-396` stops/waits for
   the thread; debug assertions require prior unregistration. `procprv.c:2254-2288` separately drains
   metadata completions. A new extraction must account for callbacks, queued UI events, work items,
   caches, records and all outstanding references before unloading or reinitializing.

**Unproven:** a complete provider-global teardown/restart protocol. This study did not link an
extracted provider or verify repeated phlib/application initialization. Treat DLL unload/reload and
multi-instance monitoring as risks, not supported features. A PH object reference preserves lifetime;
it does not by itself make concurrent mutable fields coherent.

### Grouping limitations (observed vs interpretation)

The comment in `procgrp.c:262-268` describes common ancestry, same filename/user and a visible root
window, and explicitly calls the algorithm greedy. Actual `PhpFindGroupRoot` can return a root with
no window; a visible window is a stopping signal, not proof that every returned group has one.
Missing filename/SID yields a singleton. Distinct executable helper processes and multi-user/session
relationships need more than an executable-name match. These observations imply that the screenshot's
Chrome/Discord/Spotify application groups cannot be promised by wrapping this function unchanged.

## 2. Architecture comparison (recommendations, not measurements)

| Option | Benefits | Principal cost/risk | Evidence required before acceptance |
|---|---|---|---|
| Modernize original native UI | Preserves advanced functionality, provider ownership and existing control integration; least extraction risk. | Large HWND/TreeNew presentation surface; translating the approved design, accessibility and MVVM-style separation requires substantial UI work. | Real redesigned screen with keyboard/high-DPI/performance evidence; regression coverage of retained operations. |
| Existing engine behind new C ABI | May retain deep diagnostics and minimize reimplementation of complex metrics. | Current provider is app-global; grouping depends on UI; no stable shutdown/public POD boundary demonstrated. Native API and driver-adjacent dependencies remain. | Headless executable/DLL linking proof, initialization and repeated shutdown, driver-off behavior, snapshot ownership/concurrency and sanitizer/leak coverage. |
| Standalone WinUI 3 + documented APIs | Clean view models; narrow permissions; independent testing; avoids making PH internals into a product ABI. | Must implement sampling/identity/groups carefully; basic APIs do not supply every advanced metric or guaranteed application association. | Real enumeration proof (below), then parent contracts + provider integration + actual WinUI build/UI tests. |
| Scoped hybrid | Start with documented API baseline; selectively introduce a native enrichment adapter only where a justified capability gap remains. Preserve original app for advanced compatibility. | Two semantics/backends can drift; avoid silently merging snapshots with unlike clocks/identity/error definitions. Native deployment/ABI adds maintenance. | Capability-by-capability justification, explicit provenance, availability and comparison tests; no wholesale PH object sharing. |

**Recommendation:** continue the parent's documented-API baseline feasibility path, retaining a
future scoped native adapter option. This is supported by the small managed-to-Win32 proof, not by
proven WinUI performance or feature parity. Revisit native UI modernization if the clean frontend
cannot meet required functionality/performance. Do not embed the old app or extract its entire
provider just to obtain a process list.

## 3. Model requirements for the parent-authored contracts

These are design constraints, **not** new shared contracts or source files.

### Identity, availability and relationships

- Stable key: `(uint32 PID, uint64 creation FILETIME in 100ns UTC ticks)` within a machine/boot scope.
  Include boot/session scope if persisting histories. Executable path/file identity, package identity,
  SID and session ID are metadata with availability, not substitutes for process lifetime.
- If creation time cannot be read, keep the observed PID as a provisional row scoped to that refresh.
  Do not merge histories, apply cached metadata from a previous PID occupant, or enable actions.
  Do not invent creation time `0` as a valid lifetime key.
- PID enumeration and later handle opening are not atomic. Metadata read through one handle belongs
  to the lifetime validated through that same handle. Snapshot parent/thread/name fields can describe
  a prior occupant; reject uncertain cross-source joins rather than presenting them as established.
- Parent PID is a hint. Validate parent lifetime, temporal ordering and absence of cycles; same-session
  and owner checks strengthen application grouping. Creation ordering is necessary but does not prove
  that a later observed parent is the historical parent when snapshots raced or old parent exited.
- For every metric/metadata item carry value-or-null, status (`available`, `access_denied`, `exited`,
  `not_supported`, `not_sampled`, `error`), native error domain/code, source and observation time.
  Map only actually observed errors; a generic open failure is not proof of exit. Missing != zero.
- Group results need stable group identity, member lifetime keys, representative, rule/version,
  evidence and confidence (`verified`, `inferred`, `unknown`). Filename similarity alone is weak;
  user/session boundaries and mismatched identities block merging. Default uncertain items to
  singletons; expose why an inferred group exists. Do not manufacture browser subprocess roles.
- A complete enumeration failing must not turn the previous snapshot into an empty success and
  mark all rows exited. Keep last successful data marked stale with coverage/error information.

### CPU, memory and I/O

- Time-based CPU: for the **same lifetime**, `100 * delta(kernel100ns + user100ns) /
  (delta(monotonic_seconds) * 10,000,000 * active_logical_processors)` yields machine-capacity percent.
  Collect sample timestamps around the per-handle read; use measured time, not requested timer delay.
  The first sample is null/warming-up. Reset on counter regression, identity change, nonpositive
  interval, CPU-topology change or invalid denominator. Do not silently clamp broken data to zero.
  Label method and normalization; processor groups/affinity and Task Manager's differing utilization
  definitions require explicit tests. Do not claim parity with System Informer's cycle method.
- Total working set: resident pages attributed to a process, including shared pages. Sum across
  processes is **not distinct physical RAM** and must never be labeled as such.
- Private bytes: private committed virtual memory; not necessarily resident and not actual pagefile
  disk occupancy. A documented provider can use `PROCESS_MEMORY_COUNTERS_EX.PrivateUsage` with the
  documented access rights, while keeping failure separate from success with value zero.
- Private working set: resident nonshared pages; distinct from private bytes. Keep null until a
  separately validated supported API/source supplies it; do not relabel total WS or private bytes.
- System physical available/total, system commit charge/limit, cache/standby and kernel metrics are
  distinct categories. Do not derive system physical usage by summing process rows.
- Group totals require member-key deduplication, consistent sampling windows, contributing/expected
  member counts and a partial indicator. A sum of available private bytes can be useful but is not
  a complete total when members are unreadable. Show WS sums only as such, with the sharing caveat.
- `GetProcessIoCounters` supplies cumulative process I/O, not guaranteed physical disk traffic or
  network bandwidth. Derive rates only from same-lifetime monotonic deltas. Accurate per-process
  disk/network throughput needs a separately justified source; show unavailable in the baseline.

### Services, connections and scheduling

- Service associations should come from SCM service PID observations plus process-lifetime checks,
  with collection time and uncertainty. A service's executable/publisher is not proof of membership.
- Connection records need protocol, address family, local address/port, nullable remote endpoint,
  TCP state when applicable, observed PID, optional validated process key, source and timestamp.
  Owner-PID TCP/UDP tables are point-in-time evidence, not throughput. UDP listeners do not imply
  remote peers; missing DNS/country information must remain missing. Deduplicate before counts.
- Use one cancellable background refresh owner. Timer ticks coalesce instead of spawning overlapping
  scans; at most one latest pending snapshot goes to the UI. Publish immutable snapshots with a
  monotonic sequence, capture start/end, coverage and error state. Dispatcher applies keyed diffs;
  preserve selection by lifetime key and drop superseded UI updates.
- Cancellation checks between enumeration/enrichment units and before publication; cancellation of
  a request does not imply synchronous native APIs can be interrupted. Do not free handles/buffers
  beneath an in-flight call. Isolate potentially slow metadata, bound concurrency, discard late
  completions by generation/key, and complete shutdown only after worker ownership is settled.
- Minimal access, local-only data, no automatic elevation/driver requirement. No terminate/suspend
  operations in this proof or baseline monitoring boundary. A later action needs fresh identity
  validation and separate authorization policy, not just a selected PID.

## 4. Optional future C ABI shape (proposal only)

If a native enrichment adapter becomes justified, expose versioned operations over opaque session
and request handles, not phlib or `PH_PROCESS_ITEM` types. Suggested operation responsibilities:
create/configure; begin asynchronous read-only capture; request cancellation; obtain completed
snapshot; release snapshot; close/join session. Closing must prevent new work and guarantee no
subsequent callback or use of caller memory. Prefer polling/completion-handle delivery over callbacks
into managed code until callback threading and lifetime are explicitly solved.

Use fixed-width integers, explicit struct size/version, documented alignment/calling convention and
UTF-16 length+offset strings in immutable owned buffers. No C++ STL, exceptions, PH references,
borrowed HWNDs or CRT allocator crossing. A single exported release routine frees native-owned
buffers; C# uses SafeHandle and copies to immutable managed records before release. Validate all
offsets/counts/versions and record lengths. Errors carry domain+code; validity bits/status accompany
nullable values. Snapshot sequence/clock scope/lifetime identity must match the parent model.

Contract tests needed before adoption: layout across C#/C x64, unknown version rejection, string and
offset bounds, access denied/null round-trip, PID reuse without history transfer, cancel-vs-complete,
close-vs-read, no use-after-free, bounded pending work and repeat start/stop without leaked resources.
**None of that ABI exists or is tested by the probe below.**

## 5. Isolated real Win32 proof

`prototypes/native-boundary/Enumerate.cs` compiles as a Windows x64 .NET Framework console program
without changing any existing project. It P/Invokes `CreateToolhelp32Snapshot`, `Process32FirstW`,
`Process32NextW`, `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)`, `GetProcessTimes`, `CloseHandle` and
`GetCurrentProcessId`. It enumerates real local processes, prints PID + nullable raw creation FILETIME
+ native error, and checks x64 structure size, unique snapshot PIDs, live self creation-time success,
and normal enumeration termination. SafeHandle scopes close snapshot/process handles.

Only PID from the enumeration is joined with the opened process; name/thread/parent fields are
deliberately not published because a snapshot-to-open PID reuse race cannot be ruled out here.
The handle-observed creation time describes that opened lifetime; the probe does not prove it was
the occupant at snapshot capture. It neither tracks CPU nor performs historical relationship joins.

Reproduce from repository root in PowerShell (Windows x64, .NET Framework compiler present):

```powershell
New-Item -ItemType Directory -Force prototypes/native-boundary/artifacts | Out-Null
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /warn:4 /warnaserror+ /platform:x64 /out:prototypes\native-boundary\artifacts\Enumerate.exe prototypes\native-boundary\Enumerate.cs
$run = Start-Process .\prototypes\native-boundary\artifacts\Enumerate.exe -Wait -PassThru -NoNewWindow -RedirectStandardOutput prototypes\native-boundary\artifacts\processes.tsv -RedirectStandardError prototypes\native-boundary\artifacts\checks.log
$run.ExitCode
```

### Observed build/run results (2026-11-05)

- OS: Windows Server 2022 Standard x64, version `10.0.20348`, build `20348` (not Windows 11).
- Compiler: .NET Framework C# `4.8.4161.0` (`NET48REL1`). Compilation exited **0** with
  `/warn:4 /warnaserror+ /platform:x64`; no compiler diagnostics. This is also the probe's type check;
  no separate project linter exists for this isolated source.
- Probe exited **0**. Captured real run: **131 rows**, **130 readable creation times**, **0 access-denied**.
  The single null identity was **PID 0 / Win32 error 87**. Null/error consistency was independently
  inspected: no non-null identity with nonzero error, and no null identity with zero error.
- Probe assertions passed: x64 layout (`PROCESSENTRY32W` size 568), unique enumerated PIDs, live self
  with nonzero creation time, and enumeration ending with `ERROR_NO_MORE_FILES` (18).
- `whoami /groups` showed **High Mandatory Level**. No privileges or token settings were changed.
  This run does **not** prove ordinary-user coverage, protected-process access-denied handling,
  or successful reads of every Windows process. Zero denied rows is a run observation, not a guarantee.
- Executable SHA-256: `4FD4F57537B1CC0A829A2332A11AA15BD5959D41A41C247C8260AB599D6DF909`.
- Captures under `prototypes/native-boundary/artifacts/`: `build.log`, `checks.log`, `processes.tsv`,
  `token.txt`, `Enumerate.exe`. The initial Windows PowerShell `2>` capture wrapped native stderr;
  the final capture used direct child-process stdout/stderr streams (the reproduction command above).

Generated binaries/logs/telemetry are ignored, not committed. Raw process output is local test evidence,
not fixture or product data. No live PID-reuse, CPU/memory measurement, cancel, leak/stress or GUI tests
were run by this probe; the assertions are intentionally narrower than production acceptance.

### API reference links

These Microsoft references define the documented-API surface for subsequent implementation review:
- [Process32FirstW](https://learn.microsoft.com/en-us/windows/win32/api/tlhelp32/nf-tlhelp32-process32firstw)
- [GetProcessTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocesstimes)
- [GetProcessMemoryInfo](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-getprocessmemoryinfo)
- [PROCESS_MEMORY_COUNTERS_EX](https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-process_memory_counters_ex)
- [GetProcessIoCounters](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getprocessiocounters)
- [GetExtendedTcpTable](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedtcptable)
- [EnumServicesStatusExW](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-enumservicesstatusexw)

## 6. Integration gate / limitations

Parent must approve shared contracts before implementing `src/SystemExplorer.Windows`. The small
proof establishes managed-to-documented-Win32 enumeration feasibility only: not .NET 10 build support,
WinUI binding, native C DLL extraction, CPU/memory correctness, robust application grouping, refresh
cancellation or production performance. Windows 11 standard-user validation remains required even
if the proof succeeds on this Windows Server VM. Original baseline build/driver-off feature coverage
belongs to the separate build specialist; this branch makes no claims about it.

Next acceptance evidence: same-lifetime delta tests, forced process churn/reuse seams, denied/null
metadata, incomplete enumeration behavior, conservative grouping explanation tests, cancel/shutdown
and dispatcher backlog tests, live Windows 11 process comparisons, high-DPI/keyboard UI testing and
measured overhead. Synthetic contract cases must be labeled as tests, never presented as real telemetry.
