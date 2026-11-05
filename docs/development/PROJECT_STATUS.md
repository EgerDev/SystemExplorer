# SystemExplorer development status

## Authority and scope

Parent: https://app.devin.ai/sessions/37e4768d32814d74b2e5fb4b51515a6e

Target: Windows 11 x64; parent verification host: Windows Server 2022.
Preserve the original System Informer tree and MIT attribution. No master merges,
public releases, signing assumptions, cloud telemetry, or driver-policy changes.
Integration branch: `feature/systemexplorer-integration` (base `0a4975a47`).

## Milestones

| Milestone | Status | Gate |
| --- | --- | --- |
| M0 repository / Windows baseline | In progress | Reproducible original user-mode build, logs, driverless scope |
| M1 architecture / contracts | Studies in progress | Working native integration proof and contract tests |
| M2 Processes | Pending M1 | Live, searchable, sortable grouped process table and inspector |
| M3 memory | Pending contracts | Precise memory categories, attribution and bounded history |
| M4 network / services | Pending contracts | Real endpoint ownership and verified relationships |
| M5 overview / monitors | Pending providers | CPU, supported GPU/disk, startup, tray, themes, history |
| M6 insights | Pending measurements | Deterministic evidence-backed local explanations |
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
- Architecture remains undecided until feasibility evidence is available.
- No production features or release artifacts accepted yet; no PR opened yet.
- Next: inspect specialist diffs, settle the frontend/provider boundary, publish
  shared contracts, then assign disjoint provider/frontend implementation.
- Windows Server results do not replace Windows 11-specific acceptance.
