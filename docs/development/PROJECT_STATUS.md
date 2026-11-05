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
| M1 architecture / contracts | Core verified; WinUI feasibility pending | Win32 lifetime proof and 13 passing contract/history tests; frontend compile gate remains |
| M2 Processes | Implementation in progress | Live, searchable, sortable grouped process table and inspector |
| M3 memory | Core history implemented; provider pending | Precise memory categories, attribution and bounded history |
| M4 network / services | Provider implementation in progress | Real endpoint ownership and verified relationships |
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
- Core verification: 13/13 tests; Release build with warnings as errors passed.
  Production UI source files are authored but unbuilt; the frontend compile gate remains.
- Original baseline initialization failed: Visual Studio absent. Build child is
  making one official supported minimal toolchain installation attempt.
- Production provider: native child owns `src/SystemExplorer.Windows` and focused tests.
- Relationships: QA child owns `src/SystemExplorer.Relationships` and focused tests.
- Frontend child owns host/project recipe; parent owns production XAML and view models.
- Next: review/integrate provider and host branches, compile live UI, test on Windows,
  then publish unsigned local test artifacts and record hashes. No functional UI or
  release artifact acceptance is claimed before those gates pass.
- Windows Server results do not replace Windows 11-specific acceptance.
