# Bounded selected-process inspection

The new frontend restores two read-only diagnostic capabilities through an explicit managed contract, not by enabling the preserved AgentTools registry. Original native sources and licenses remain unchanged.

## Surface and lifetime

The selected member's inspector offers **Modules** and **Memory regions**. Both are on-demand, frozen inventories with filtering, numeric address/span sorting and copyable selected-row details. A positive PID **and observed creation FILETIME** are mandatory. The adapter opens one process handle, validates creation time and zero-time handle waits before and after enumeration, and discards results on mismatch or exit. Unknown creation time never falls back to PID matching. SYNCHRONIZE is requested solely for read-only liveness checks: a running process's exit FILETIME is undefined, and exit code 259 is ambiguous. No privileges are enabled.

Modules use `CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32)` and `Module32FirstW`/`Module32NextW`. The lifetime handle requests `PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE`. The snapshot may omit data-file-only modules; Windows loader changes can make it fail. Paths are limited by Toolhelp's MAX_PATH field. No module file is executed, hashed, uploaded, or signature-verified here.

Regions use `VirtualQueryEx` with `PROCESS_QUERY_INFORMATION | SYNCHRONIZE`; the adapter does not request VM_READ or VM_WRITE and never reads memory contents. It exposes virtual span, state, mapping type, protection and allocation base. Undefined free/reserved fields remain null. Region spans are **not resident RAM**. MEM_IMAGE/MEM_MAPPED can include copy-on-write pages; MEM_PRIVATE is not a private-working-set measurement. The map can change during collection and is not an atomic address-space snapshot.

## Bounds and unavailable states

- x64 Windows adapter; protected/inaccessible targets may be denied. No elevation, debug-privilege adjustment, driver or remote fallback.
- At most 4096 records per request, three module snapshot attempts, and a three-second **cooperative** budget. Cancellation and budget checks run between native calls; an individual Windows call cannot be forcibly interrupted. This is not a hard wall-clock deadline.
- Truncated/timed-out/partial inventories retain an explicitly incomplete prefix. Exit or identity mismatch returns no rows. Access denied, unsupported, failed, and missing identity have explicit states. Cancellation closes/discards the UI request.
- Closing the dialog or application cancels pending work. Collection never runs as background polling for every process.

## Validation

`dotnet run --project tests/SystemExplorer.Windows.Smoke -c Release -- --inspection-only` verifies native x64 layout, the real host executable, a known allocated address, undefined-field nullability, ordered regions, mismatched/unknown identities, bounds, timeout and cancellation. The regular Windows smoke command includes these tests in CI. Native rendered checks are separate; this host does not establish Windows 11 standard-user acceptance.

## Deferred

Threads/stacks/wait chains, handle/file-use search, token internals, drivers, memory contents, command-line collection and all mutating/egress endpoints remain outside this adapter. The preserved 119 AgentTools definitions are not automatically connected.

API references: [Toolhelp snapshot](https://learn.microsoft.com/en-us/windows/win32/api/tlhelp32/nf-tlhelp32-createtoolhelp32snapshot), [MODULEENTRY32W](https://learn.microsoft.com/en-us/windows/win32/api/tlhelp32/ns-tlhelp32-moduleentry32w), [VirtualQueryEx](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-virtualqueryex), [MEMORY_BASIC_INFORMATION](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-memory_basic_information).
