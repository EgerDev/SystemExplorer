# Read-only object inventories

All five collectors are on-demand and local-first. They do not control processes,
enable privileges, elevate, use drivers, read process memory, mutate services or
registry, or use ETW/network lookups. Native calls for these features live in
`NativeSystemInformation.cs`; module searches reuse the existing process-inspection
provider rather than implementing another module enumerator.

| Collector | Observations and source | Bounds |
| --- | --- | --- |
| Handle inventory | Per-process handle values/access/attributes; complete process/system counts by type from `NtQuerySystemInformation(64)`; optional names of **types** from `NtQueryObject(NULL, 3)` | 4096 rows; three-second cooperative budget |
| Pool tags | Tag bytes, allocation/free counts, paged/nonpaged used **bytes** from system information class 22 | 4096 rows; three-second cooperative budget |
| Thread activity | Two class-5 process/thread snapshots, context-switch counts/deltas/rates, state/reason/priorities; `QueryProcessCycleTime` / `QueryThreadCycleTime` raw CPU cycles | Approximately 300 ms apart; measured Stopwatch interval; 1024 threads/held thread handles, 1024 union rows; five-second cooperative budget |
| File users | Restart Manager registered-file users (`RmStartSession`, `RmRegisterResources`, `RmGetList`, `RmEndSession`), app/service names, app type, restartable flag | Four list attempts, 1024 users; ten-second caller timeout |
| Module users | OrdinalIgnoreCase name/path substring matches against sequential `IProcessInspectionProvider` module inspections; class-5 process identities | 2048 inspections, 4096 match rows, ten-second wall-time bound |

NT queries retry length mismatch at most eight times, with 64 MiB maximum buffers
(4 MiB for object types). Successful buffers are copied using the **returned**
length; unmanaged buffers are freed in `finally`. All parsers require Windows x64
layouts, use checked bounds and managed spans, and reject malformed counts/chains.
Native calls cannot be interrupted. Cancellation is cooperative except that file
user callers and in-flight module inspections can stop waiting immediately.

## Layouts checked

`phnt/include/ntexapi.h` and `ntobapi.h` establish these x64 layouts:

- Extended handles: 16-byte header, 40-byte entries; PID +8, handle +16,
  access +24, type index +30, attributes +32. Object pointers are never exposed.
- Pool tags: eight-byte aligned header, 40-byte entries; paged used +16,
  nonpaged used +32, both SIZE_T bytes.
- Object types: first record +8, 104-byte fixed record, TypeIndex +90;
  advance by fixed size plus name MaximumLength, rounded to eight-byte alignment.
  Name pointers are interpreted only as bounds-checked offsets into the copied
  original allocation. There is no ordinal type-index fallback.
- Process information: 256-byte fixed header, CreateTime +32, PID +80;
  NextEntryOffset advances a bounded forward chain. Each thread entry is 80 bytes:
  CreateTime +16, ClientId process/thread +40/+48, priorities +56/+60,
  context switches +64, state +68, wait reason +72.

Restart Manager's Unicode Win32 layout has a 12-byte RM_UNIQUE_PROCESS followed by
256-WCHAR app and 64-WCHAR service buffers; app type +652 and BOOL restartable
+664, total 668 bytes. Deterministic tests verify its marshalled size and decode
fixed bytes, including non-ASCII text.

## Identity, completeness and status

Handle/thread collections require a positive creation FILETIME, retain a process
handle opened query-limited plus synchronize, and compare creation time and
zero-wait liveness before and after observation. Mismatch/exit discards rows.
Thread cycle handles are query-limited plus synchronize, retained between samples,
and checked against thread creation time and owner. Changed thread creation times
never yield deltas. Zero times are uncertain. Threads appearing/disappearing are
labelled started/exited only when the relevant sample was not capped. Decreasing
context-switch or cycle counters produce null deltas with a reason, not guessed
wrap corrections. Cycles are not time and have no Hz conversion.

Restart Manager start times are reported as observed, **not** revalidated against
live processes. Zero FILETIMEs become null, with Partial status. Restart Manager
is not an exhaustive inventory of every file handle. Only `X:\...` paths are
accepted: UNC, device, relative, remote-drive, unknown-drive and missing-root paths
are refused before a session opens. A dedicated background worker owns each RM
session and always attempts EndSession after successful StartSession, even after
an error, caller cancellation or timeout. A blocked native call may retain that
worker/session until it returns; returning TimedOut does not claim it was stopped.

Module searches skip Idle/System and unstable identities without inspection.
Counts partition all enumerated processes into Searched, Denied, Exited, Failed
and NotSearched; Matched counts processes, while match rows count modules. Partial
and Truncated module inspections count as Searched but do not establish complete
coverage. Idle/System skips alone permit Available; unstable skips or refusals do
not. An in-flight inspection that exceeds the wall-time bound counts as Failed;
remaining unattempted processes count as NotSearched. Limits retain honest counts
and report Truncated/TimedOut. If initial process
enumeration fails/times out, zero counters mean unavailable enumeration, not proof
that the machine has no processes.

Available means successful observation within the stated scope, not permanent
absence. Partial means some values/coverage are missing; Truncated means a bound
was reached; TimedOut means a time bound was reached. AccessDenied, Unsupported,
Exited, IdentityMismatch and Failed preserve the actual refusal/failure rather
than inventing data. Cancellation throws OperationCanceledException. Missing
values remain null with reasons in detail fields.

Deliberately excluded: handle **names** (would require DuplicateHandle),
pooltag.txt/driver attribution, SE_DEBUG/privilege adjustment, symbols, network
lookups, shutdown/restart operations and process-memory APIs.

## Smoke tests

```powershell
$env:PATH = "$env:USERPROFILE\.dotnet;$env:PATH"
dotnet run --project tests/SystemExplorer.ObjectInventories.Smoke -c Release
dotnet run --project tests/SystemExplorer.Core.Tests -c Release
```

The inventory smoke covers fixed layouts, malformed data, bounds, identity,
classification/math, fake refusals/timeouts/cleanup, and live local observations.
Windows Server or elevated-token evidence is not Windows 11 standard-user
acceptance. Live refusals must be reported honestly, never bypassed.
