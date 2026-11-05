# SystemExplorer development status

## Authority and scope

Parent: https://app.devin.ai/sessions/37e4768d32814d74b2e5fb4b51515a6e

Target: Windows 11 x64; parent verification host: Windows Server 2022.
Preserve the original System Informer tree and all upstream/third-party notices. No master merges,
public releases, signing assumptions, cloud telemetry, or driver-policy changes.
Integration branch: `feature/systemexplorer-integration` (base `0a4975a47`).

## Milestones

| Milestone | Status | Gate |
| --- | --- | --- |
| M0 repository / Windows baseline | In progress | Reproducible original user-mode build, logs, driverless scope |
| M1 architecture / contracts | Core and WinUI feasibility verified | Win32 lifetime proof, 15 contract/history tests and published WinUI keyboard interaction |
| M2 Processes | Production launch verified; acceptance in progress | Live, searchable, sortable grouped process table and inspector |
| M3 memory | Core history and Windows provider verified | Precise memory categories, attribution and bounded history |
| M4 network / services | Relationships provider verified | Real endpoint ownership and verified relationships |
| M5 overview / monitors | UI implementation in progress | CPU, supported GPU/disk, startup, tray, themes, history |
| M6 insights | Core implemented; history regression tests passing | Deterministic evidence-backed local explanations |
| M7 release validation | Pending functional app | Tests, GUI evidence, performance, security and unsigned binaries |

## Independent initial assignments

| Role / session | Branch | Exclusive ownership | Deliverable |
| --- | --- | --- | --- |
| [Build](https://app.devin.ai/sessions/5fb62d4da8ef4b699ec0df1687a73782) | `feature/windows-build-baseline` | Essential build/CI repairs; `WINDOWS_BASELINE.md` | Original Windows build and recovery recipe |
| [Native architecture](https://app.devin.ai/sessions/9288efc881b24c83b00096b5d141254a) | `feature/native-boundary-study` | `docs/architecture/NATIVE_ENGINE_STUDY.md`, `prototypes/native-boundary/` | Evidence-based reuse options and boundary proposal |
| [Frontend feasibility](https://app.devin.ai/sessions/ff9121fdee614303855f6ab81817d312) | `feature/winui-feasibility` | `docs/architecture/UI_FEASIBILITY.md`, `prototypes/winui-feasibility/` | Native WinUI build/launch proof and reference-derived design |
| [QA / security](https://app.devin.ai/sessions/84a58efa35894634b90a1e35a3e4ab93) | `feature/windows-quality-strategy` | `docs/testing/QUALITY_GATES.md`, `docs/testing/THREAT_MODEL.md` | Executable quality gates and threat model |

Parent owns architecture decisions, production shared contracts, integration,
this status file, PR creation/review, and acceptance. Child branches are reviewed
before integrating; independent work does not imply acceptance.

## Evidence and next integration steps

- Full supplied specification and UI reference reviewed by parent.
- Existing repository and build scripts inspected; no baseline success claimed yet.
- Build entry points are `build/build_init.cmd` and `build/build_release.cmd`.
- Existing CI targets a Windows 2025 / VS2026 runner; local support must be measured.
- Integration PR: https://github.com/EgerDev/SystemExplorer/pull/1 (no merge authorized).
- Selected provider boundary: documented Win32 APIs, immutable .NET 10 contracts,
  and provisional WinUI 3 frontend. Legacy provider extraction was rejected because
  `procprv.c` and `procgrp.c` depend on global state and UI-node lifetimes.
- Native Toolhelp/GetProcessTimes proof compiled and ran: 131 observed rows,
  130 readable creation times, high-integrity Server 2022 token; not standard-user evidence.
- Core verification: 15/15 tests; Release build with warnings as errors passed.
  The published WinUI feasibility proof now launches and responds to keyboard input;
  production UI also compiles/publishes, shows updating real process data and supports
  native tray restore. Broad UI and Windows 11 acceptance are not claimed.
  A project publish target includes the required application PRI resource.
- Windows provider smoke passed (133 rows, 132 stable identities, 5 partial readings);
  its Release build passed with warnings as errors and no warnings.
- Relationships provider smoke passed 43 checks (72 endpoint rows, 219 services,
  7 startup entries, 4 limitations). Embedded signature verification used offline
  cached revocation data, not a fresh online revocation check.
- Real bounded churn check passed: 16 child lifetimes across 38 snapshots, with
  no stale exact identity after exit. Actual PID reuse and prolonged stress were not induced.
- Original baseline initialization failed: Visual Studio absent. Build child is
  making one official supported recovery attempt. VS2026 Build Tools / MSVC and
  .NET 10 installation succeeded; pinned SDK/WDK restore and original build are pending.
- Production provider: native child owns `src/SystemExplorer.Windows` and focused tests.
- Relationships: QA child owns `src/SystemExplorer.Relationships` and focused tests.
- Frontend child owns host/project recipe; parent owns production XAML and view models.
- PR tip d59deabb had zero Actions runs and zero check runs; this is not passing CI.
  A dedicated managed-app Windows build/test/artifact workflow is being added.
- Next: review/integrate provider and host branches, compile live UI, test on Windows,
  then publish unsigned local test artifacts and record hashes. No functional UI or
  release artifact acceptance is claimed before those gates pass.
- Windows Server results do not replace Windows 11-specific acceptance.
