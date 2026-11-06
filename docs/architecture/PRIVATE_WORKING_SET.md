# Private working set collection

The Windows provider fills the existing nullable `ProcessSnapshot.PrivateWorkingSetBytes`
using documented `GetProcessMemoryInfo` / `PROCESS_MEMORY_COUNTERS_EX2` support.
It is resident private memory, **not** private commit (`PrivateBytes`) or total
working set (`WorkingSetBytes`, which includes shared pages). The existing process
inspector displays these separately; group private commit remains unchanged.

## API and availability

- [EX2 structure](https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-process_memory_counters_ex2):
  Microsoft documents Windows 10/11 22H2 with the September 2023 cumulative update
  as minimum client support; the page does not promise Server 2022 support.
- [GetProcessMemoryInfo](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-getprocessmemoryinfo):
  accepts EX2 and a process query-information or query-limited-information handle.
- Probe actual API behavior rather than assuming support from an OS product name.
  The EX2 buffer extends the EX layout with pointer-sized private working set and
  64-bit shared commit. Shared commit is not exposed by this change.
- Initialize the optional private-working-set field to an impossible sentinel and
  require a full returned structure. Older implementations that accept a larger
  buffer but fill only the prefix must not turn an untouched field into zero.
- If EX2 fails, query the original EX layout. If only the prefix is returned,
  preserve its private-commit/total-working-set readings. Unavailable private
  working set remains null with a per-process reason and partial availability.
  A successfully returned zero is a real value, not missing data.

## Safety and identity

Reads use the already-open process handle and validated creation FILETIME.
The existing fallback for a denied basic-memory query opens a scoped stronger
read handle and verifies the exact creation time before joining any fields.
No new driver, privilege enabling, page enumeration, remote memory read, cache of
PID-only data, or process mutation is introduced. Snapshots remain immutable.

## Verification

`dotnet run --project tests/SystemExplorer.Windows.Smoke -c Release` checks native
structure layout, untouched/short responses, supported zero, and availability
against a separate PSAPI declaration on the live test process. It also retains
the existing identity, memory, cancellation, concurrency and process-churn checks.
The output records `private_working_set_available`; a passing unsupported-host
fallback is **not** proof of a successful value on a supported Windows 11 host.
That live acceptance gate remains separate. These are functional checks, not
measurements of collection overhead or proof of memory-accounting parity.
