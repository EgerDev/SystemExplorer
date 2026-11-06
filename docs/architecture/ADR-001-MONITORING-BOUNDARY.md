# ADR 001: isolate the read-only monitoring boundary

## Decision

Use immutable .NET 10 Core contracts and a documented Win32 API provider for the
new SystemExplorer application. Preserve the original System Informer projects
and capabilities as a separate build, rather than silently loading their plugins
or extracting application-owned native structures. WinUI 3 is the selected
frontend candidate, subject to an actual build and launch feasibility gate.

## Evidence

`NATIVE_ENGINE_STUDY.md` records that `procgrp.c` consumes `PH_PROCESS_NODE` UI
nodes, while `procprv.c` depends on global state, settings, extensions, service
references, caches and asynchronous object lifetimes. The standalone Win32
Toolhelp/GetProcessTimes proof compiled and enumerated real process identities
on Windows Server 2022. Core identity/sampling/grouping tests also pass.

## Alternatives

- A wholesale native C ABI extraction would need unsupported lifetime and
  initialization assumptions; a production-safe standalone SDK was not proven.
- Replacing the original fork would discard advanced functionality and history.
- A browser dashboard would not satisfy the requested native Windows UI.

## Consequences

- Shared identity is PID plus creation FILETIME, never PID alone.
- Sampling, ownership checks and diagnostics remain independently testable.
- Unknown or denied measurements remain unavailable, not fabricated estimates.
- The frontend has no required driver, automatic elevation, online reputation
  query or process-control action.
- Some advanced legacy features are not initially exposed in the new frontend.
  A future narrowly scoped native adapter requires separate ABI/lifetime tests.
- New portable artifacts must be inventoried separately from the original tree;
  source presence is not evidence of binary linkage or blanket MIT licensing.
- Server 2022 evidence does not satisfy Windows 11 standard-user acceptance.

See `CONTRACTS.md` and `../testing/QUALITY_GATES.md` for binding correctness gates.
