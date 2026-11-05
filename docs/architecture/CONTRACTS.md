# Monitoring contracts

The production boundary lives in `src/SystemExplorer.Core/Monitoring.cs` and has
no Windows or UI dependency. Initial implementations target .NET 10, matching the
existing build tool's runtime generation. The existing native solution remains
separate and unchanged.

## Identity and availability

`ProcessIdentity` is PID plus UTC creation FILETIME (100 ns ticks since 1601).
An inaccessible creation time is null, not zero or an invented timestamp. Such
rows can be displayed but cannot accumulate CPU/history or imply a stable parent.
Executable identity, SID and session are metadata used for conservative grouping.
Missing values stay null; each provider emits limitations and access/error states.

`VerifiedParent` requires a different PID and an observed creation time strictly
older than its child. Equal timestamps are treated conservatively as unverified.
Application groups follow only that ancestry with equal full path (ordinal case
insensitive), equal SID, and equal session. Independent launches are not silently
combined; inaccessible metadata remains a singleton. This deliberately undergroups
complex applications instead of asserting unproven ownership.

## Metrics

- Private bytes mean private committed virtual memory, not physical RAM.
- Working set includes shared resident pages and is never summed as physical RAM.
- Private working set is resident private memory, independently nullable.
- Group private bytes and CPU are unavailable if any member value is missing.
- CPU uses kernel+user execution delta / monotonic elapsed time / logical CPUs,
  expressed as percent of total machine capacity. First sample, counter rollback,
  invalid interval, or changed identity yields null, not zero.
- Process I/O is read/write transfer rate, **not physical disk or network rate**.
- System disk and GPU rates are separate optional measurements; never derive them
  by summing generic process I/O. Unsupported metrics remain visibly unavailable.
- Physical used = total - available. Commit, cache, standby and kernel pools are
  different (potentially overlapping) views, never additive categories.
- `SystemCacheBytes` follows GetPerformanceInfo's SystemCache definition; it is
  not a claim about all reclaimable cache or standby. Standby may remain unsupported.

## Providers and refresh

`IMonitorProvider.CaptureAsync` returns processes, memory and optional system rates.
`IRelationshipProvider` returns TCP/UDP endpoints and services, plus on-demand
publisher verification and startup inventory. Implementations must bound native
buffers/retries, dispose handles, observe cancellation, and serialize own samples.
The UI runs polling off the dispatcher, never overlaps refreshes, retains the last
successful snapshot on errors with visible stale/error state, and cancels on close.

Relationship owners are validated against current identity using a process handle
and creation time. PID-only ownership remains uncertain (`OwnerIdentity = null`)
and is not silently attached to an unrelated selected process. Enumeration is not
an atomic OS snapshot: expose observation timestamps and limitations.

Publisher verification is on demand and local/offline; a certificate subject alone
is not a trusted signature. Do not upload images/hashes or perform background DNS.
Unknown endpoints do not receive fabricated geography/hostnames or bandwidth.

All providers are read-only. No terminate/suspend/driver/elevation endpoint is part
of this contract. File-location opening must use a verified local path through
the Windows shell, not command-line interpolation into a shell interpreter.

## Verification

`dotnet run --project tests/SystemExplorer.Core.Tests -c Release` is a zero-package
contract test executable. It returns nonzero on failed assertions in both Debug
and Release (no conditional `Debug.Assert`). These synthetic tests supplement,
never replace, real Windows provider and GUI acceptance.
