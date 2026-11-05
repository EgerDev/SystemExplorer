# Legacy native build repair

## Scope and baseline

This branch starts from `master` at
`0a4975a4763ad202747397ef5daf5893849b5b00`. It does not import the WinUI integration
branch. The prior Windows baseline was read with:

```text
git show origin/feature/systemexplorer-integration:docs/development/WINDOWS_BASELINE.md
```

Only original user-mode native sources/settings and diagnosed native build files
are in scope. Original notices, warnings-as-errors, compiler security options,
driver policy, CLA configuration, and release/signing controls remain unchanged.
No driver loading or application runtime acceptance is part of this build repair.

## Root causes and repairs

### Bulk memory-string search progress

`PH_MEMORY_STRING_OPTIONS` in `SystemInformer/include/memsrch.h` exposes progress
as pointers to `volatile ULONG`. `PhSearchMemoryString` copies those pointers into
`MEMORY_STRING_BULK_CONTEXT`, whose original `PULONG` fields lost the pointed-to
qualifier and produced C4090/C2220. The bulk context now preserves the same
`volatile ULONG *` contract. The existing signed-pointer adaptations for
`InterlockedExchange` now also retain `volatile`; no cast removes the qualifier.

The dialog owns the counters in `MEMORY_STRING_CONTEXT`; the worker publishes
progress through `InterlockedExchange`, and the dialog reads it through
`ReadULongAcquire`. This repair leaves that synchronization and ownership intact;
it does not substitute volatile access for atomic operations.

### Shadow-stack dialog persistence

The baseline commit added load/save consumers in `SystemInformer/thrdshstk.c`
without the corresponding built-in setting names or registration. Adding macros
alone would only move the failure from compilation to a missing runtime setting.
The repair adds names in `SystemInformer/include/phsettings.h` and registers all
three in `PhAddDefaultSettings` in `SystemInformer/settings.c`:

| Setting | Type | Default | Consumer |
| --- | --- | --- | --- |
| `ThreadShadowStackListViewColumns` | String | empty | `PhLoadListViewColumnsFromSetting` / `PhSaveListViewColumnsToSetting` |
| `ThreadShadowStackWindowPosition` | IntegerPair | `0,0` | placement validity and position load/save |
| `ThreadShadowStackWindowSize` | ScalableIntegerPair | `@96\|0,0` | DPI-aware size load/save |

These types follow the implementations in `phlib/settings.c`, not guesses from
the macro names. A fresh `0,0` position follows the dialog's existing centering
path, preserving the 550-by-290 dialog-unit resource dimensions in
`SystemInformer/SystemInformer.rc`. The zero-size default follows the existing
unset-size convention (for example the token window). On close, the existing
save helper records both real position and size with DPI, so subsequent opens
restore the persisted geometry. Empty columns retain the resource/code defaults.

### Clang PE Viewer include paths

After the initial fixes, both x64 MSBuild configurations and CMake Debug MSVC
built successfully. CMake Debug Clang then exposed `-Wnonportable-include-path`
errors promoted by the existing `/WX` in the bundled PE Viewer CLR headers.
`clrprptables.c` and `clrtableimports.cpp` referred to `metahost/corhdr.h`, but
the tracked file is `metahost/CorHdr.h`. The bundled `metahost/cor.h` similarly
referred to `corerror.h` instead of `CorError.h`.

The fallback includes now match the tracked casing, including both conditional
fallback branches. SDK include selection and header contents/ABI are unchanged.
No compiler-warning suppression, CMake flag change, or workflow edit was needed.

## Verification environment

The independent child VM initially had Windows Server 2022 Standard, build
20348, and an inherited high-integrity Administrator token, but none of the
parent baseline's VS/.NET/SDK tools or generated caches. This is not Windows 11
or standard-user acceptance evidence.

Official Microsoft prerequisites were installed once successfully. An initial
VS invocation incorrectly added an unsupported `--log` option and exited 87;
the default installer log explicitly says `Option 'log' is unknown.` The same
signed installer and component arguments succeeded after removing only that
option. This was an invocation error, not an unsupported OS or broken compiler.
Both attempts and their native/default logs are retained in the evidence archive.

| Component | Observed version |
| --- | --- |
| VS Build Tools 2026 | 18.11.3 / installation 18.11.12328.437 |
| MSBuild | 18.11.1.51303 |
| MSVC | 19.51.36260.0 / tools 14.51.36231 |
| Installed Windows SDK | 10.0.28000 |
| SDK/WDK NuGet packages | six pinned packages at 10.0.28000.2526 |
| .NET SDK / runtime | 10.0.403 / 10.0.14 |
| NuGet | 7.9.0.83 |
| CMake | 4.3.1-msvc1 |
| Ninja | 1.13.2 |
| Clang | 22.1.3 |

The .NET installer matched official release-metadata SHA-512 and Microsoft
Authenticode; the VS and NuGet executables also had valid Microsoft signatures.
No elevation was requested. No driver was loaded or signed. Build-time SDK/WDK
package restore does not imply kernel-driver runtime validation.

## Commands and results

From repository root in CMD, with `PATH` including the installed .NET and NuGet
locations, `GITHUB_ACTIONS=true`, `DOTNET_CLI_TELEMETRY_OPTOUT=1`, and
`RestoreSources=https://api.nuget.org/v3/index.json`:

```bat
nuget restore .\packages.config -PackagesDirectory .\packages\ -Source https://api.nuget.org/v3/index.json
call build\build_init.cmd INIT
call build\build_msbuild.cmd SystemInformer.sln /m /p:Configuration=Debug /p:Platform=x64
call build\build_msbuild.cmd SystemInformer.sln /m /p:Configuration=Release /p:Platform=x64
call build\build_cmake.cmd "Ninja" Debug msvc-amd64 generate
call build\build_cmake.cmd "Ninja" Debug msvc-amd64 build
call build\build_cmake.cmd "Ninja" Debug clang-msvc-amd64 generate
call build\build_cmake.cmd "Ninja" Debug clang-msvc-amd64 build
```

Restore, init, both solution builds, both CMake generation commands and the
MSVC CMake build exited 0. The original Clang build exited 1 on the include
casing defects above. After correcting the paths, incremental checks reused
the same generated trees (no restore/init/regenerate or warning changes):

| Final check | Exit | Result |
| --- | --- | --- |
| CMake Ninja Debug Clang-MSVC x64 build | 0 | 271/271 remaining/rebuilt steps |
| MSBuild Debug x64 solution | 0 | 0 warnings, 0 errors |
| MSBuild Release x64 solution | 0 | 0 warnings, 0 errors |
| CMake Ninja Debug MSVC x64 build | 0 | 17/17 rebuilt steps, including PE Viewer |
| `git diff --check` | 0 | no whitespace errors |

The earlier complete MSVC CMake build passed 925/925 steps. The final incremental
counts are not independent clean-build counts. Logs ending in
`after-include-fix` identify the final runs; earlier failures remain preserved.

Full native stdout/stderr are separate files, not PowerShell-transcript-only
evidence. Each `.result.txt` records command, environment, timestamps, exit code
and stream paths. Setup logs and an evidence index are provided separately from
git; no binaries are distributed by this branch.

### Remaining verification boundaries

- No original application launch, shadow-stack UI persistence round-trip,
  memory-search runtime, or standard-user test was performed.
- CMake Release, x86/ARM64, driver builds/analysis, broader release wrapper,
  installer production and release packaging are not claimed.
- The CMake wrapper includes plugins; the solution commands cover the original
  `SystemInformer.sln`, not the separate all-configuration release workflow.
- No CI run was watched or retriggered, and no PR was created or merged.
- The batch-only raw-byte CRLF check passed for 28 batch files. This host lacks
  `pwsh`; the check used explicit extension filtering because PowerShell 5.1's
  `-LiteralPath`/`-Include` enumeration included non-batch files. The exact
  PowerShell 7 workflow runner behavior was not verified; no gate was edited.

## CLA Assistant: administrative handoff, no policy changes

The checked-in `.github/workflows/cla.yml` pins CLA Assistant at
`ca4a40a7d1004f18d9960b404b97e5f30a505a08` (2.6.1). Its configured signature store
is **`winsiderss/winsiderss-cla`**, branch **`main`**, path
**`systeminformer/cla-signatures.json`**; its legal document is the upstream
`winsiderss/systeminformer` CLA, not a fork-specific EgerLabs document. It passes
the repository secret **`CLA_ASSISTANT_TOKEN`** as `PERSONAL_ACCESS_TOKEN`.

The pinned action's `src/persistence/persistence.ts` selects the PAT client for
both reads and writes when either remote repository/organization input is set.
`src/octokit.ts` explicitly fails when the PAT is absent. The action README
documents a PAT with `repo` scope for a remote store. A fork's `GITHUB_TOKEN`
cannot replace authorization to an unrelated organization's repository.

The maintainer must decide whether the fork is authorized to use that upstream
legal document and remote signature store, or requires its own approved CLA and
store. For the configured remote store, the credential owner needs access to
read and update its contents on the configured branch; organization authorization
and repository/branch rules must permit the intended writes. A maintainer with
secret-management permission must provision/verify the repository or allowed
organization secret. Do not send a PAT in chat. If a dedicated signature branch
is required, its policy needs deliberate maintainer approval, not a blanket
relaxation of protections.

No secrets, token scope, organization membership, remote branch protection, or
historical failure logs were inspected in this child. Therefore this documents
verified configuration requirements, **not** a claim that the specific observed
CLA run failed because of a missing or invalid secret. The parent should correlate
the failed step's log with those requirements. No CLA bypass, allowlist expansion,
automatic legal signature, workflow permission edit, or gate removal was made.

## Follow-up: CI entrypoint target tokenization

The first receipt above used direct solution builds and the CMake wrappers. It
did not exercise the original `build_debug.cmd` / `build_release.cmd` entrypoints.
Those entrypoints use CustomBuildTool's graph-build command generator, which
contained `/t:All/nologo` rather than separate `/t:All` and `/nologo` arguments.
The sequential command generator likewise contained `/t:Rebuild/nologo`.
MSBuild treats the joined text as a target name, causing MSB4057 rather than
selecting the intended target plus the no-logo option.

Both malformed literals are also present in the original `origin/master`
baseline. The parent separately confirmed the same `All/nologo` failure in base
CI jobs `127226724005` / `127226724437`; this child did not poll those jobs. This
is a preexisting invocation defect, not a regression from the native repairs.

The follow-up behavior change is exactly two inserted spaces in
`tools/CustomBuildTool/Build.cs`: `/t:Rebuild /nologo` and `/t:All /nologo`. Target
selection, matrix configuration, compiler flags, security settings and CI gates
are otherwise unchanged. CustomBuildTool must be rebuilt for the fix to take
effect; invoking the existing cached executable would still exercise old code.

Verification uses the existing VS/MSVC/.NET/SDK install and caches, rebuilding
only the build tool with `build\build_tools.cmd INIT`, then running the actual
`build\build_debug.cmd` and `build\build_release.cmd` entrypoints. The wrappers
recognize `GITHUB_ACTIONS=true` to suppress interactive pauses. No application or
driver is launched, no signing credentials are supplied, and no new architecture
toolchain is installed. These entrypoints and the final results are recorded in
a separate follow-up evidence archive so the earlier build receipt stays intact.

### Follow-up command receipt

| Command / check | Exit | Observed result |
| --- | ---: | --- |
| `call build\build_tools.cmd INIT` | 0 | Rebuilt the changed CustomBuildTool with the existing installed toolchain. |
| `call build\build_debug.cmd` | 0 | Actual Debug entrypoint built SystemInformer and Plugins for Win32 and x64. |
| `call build\build_release.cmd` | 1 | SystemInformer and Plugins completed for Win32 and x64, then local archive packaging failed; this is not a complete release-workflow pass. |
| `call build\build_msbuild.cmd SystemInformer.sln /m /p:Configuration=Release /p:Platform=x64` | 0 | Bounded direct-solution fallback: zero warnings and zero errors. |
| Source argument-token assertions | 0 | Both targets and `/nologo` are separate tokens; neither malformed joined literal remains in `Build.cs`. |
| `git diff --check` | 0 | No whitespace errors. |

The Release packaging error is separate from the fixed MSBuild invocation:
7-Zip was unavailable, and the existing fallback reported
`libarchive compression failed (-25): 'lzma1' compression not supported on this platform`.
No new prerequisites or packaging changes were introduced. The Debug entrypoint
completed, so a redundant direct Debug solution build was not repeated. The
captured output contains no `MSB4057`, joined `All/nologo` / `Rebuild/nologo`
argument, or SignTool invocation.

Both wrappers explicitly list `Win32;x64` for their SystemInformer and Plugins
builds. No ARM64 build is claimed: copies into `ReleaseARM64\x86` staging paths
come from Release32 and do not constitute an ARM64 build. Release installer
production was not reached after the packaging failure. Earlier runtime,
Windows 11/standard-user, driver and CLA limitations remain unchanged.

Full native stdout/stderr and exact command/exit records are in the separate
`legacy-argument-fix-evidence` archive, including `build-tools-init.*`,
`build-debug-wrapper.*`, `build-release-wrapper.*`,
`systeminformer-release-x64-fallback.*`, `argument-tokenization-assertion.txt`,
`actual-msbuild-argument-audit.txt` and `final-repo-check.*`. The receipt preserves
the failed Release check rather than treating a direct-solution pass as a full
wrapper pass. No CI was monitored, and the parent retains PR updates and CI review.

## Follow-up: setup console declaration

The parent reported that CI job `127471293936` passed archive creation and then
failed canary setup compilation at `tools/CustomSetupTool/main.c:597`:
`PhConsoleSetForeground` was undeclared (C4013, promoted by C2220). This child
did not monitor that CI run. The declaration is owned by `phlib/include/phconsole.h`:
`NTSTATUS NTAPI PhConsoleSetForeground(HANDLE ProcessHandle, BOOLEAN Foreground)`.
It matches the existing `PhConsoleSetForeground(NtCurrentProcess(), TRUE)` call.
`setup.h` does not include this header; `tools/simcp/main.c` demonstrates the
existing direct-include convention for the same API.

The focused repair adds only `#include <phconsole.h>` immediately after
`#include "setup.h"` in setup's `main.c`. It preserves the call, compiler warning
policy and security settings. The missing include and call are present in the
master source baseline, but no equivalent base CI run was shown to reach this
stage; source provenance is not baseline execution proof.

Verification is limited to Release Win32 `ClCompile`, using the existing native
toolchain and outputs. This matches the setup build's release/32-bit compilation
scope, not its complete packaging/linking workflow. No installer, application or
driver is launched, and no prerequisite is installed.

The exact requested command was:

```bat
call build\build_msbuild.cmd tools\CustomSetupTool\CustomSetupTool.vcxproj /t:ClCompile /p:Configuration=Release /p:Platform=Win32
```

It exited **1**, with **0 warnings and 1 error**. `main.c` appeared in the CL
source list and compiler output, produced `obj\Release32\main.obj` (1,022,196
bytes), and emitted no diagnostic. The target failed separately at
`tools\CustomSetupTool\util.c(31,1)` with C1189:
`#error: PH_RELEASE_CHANNEL_ID undefined`. The smallest missing build input is
the release-channel preprocessor definition; no guessed define, generated stub
or project/source workaround was added. This verifies that the changed source
compiles, **not** that the entire target, installer or packaging workflow passes.

`git diff --check` exited **0**. Full stdout/stderr and exact command/exit records
are preserved in `legacy-setup-include-evidence`, under
`customsetuptool-release-win32-clcompile.*` and `git-diff-check.*`. The error is
in stdout; compiler stderr is empty. No full matrix was rerun, no executable was
linked or launched by this compile-only command, and the parent retains PR and
CI handling.

## References

- [MSVC C4090 documentation](https://learn.microsoft.com/en-us/cpp/error-messages/compiler-warnings/compiler-warning-level-1-c4090)
- [Upstream settings architecture research](https://deepwiki.com/search/where-are-builtin-setting-name_ceeffc8f-ac18-4739-932f-abd7fe6454b1) (verified against this checkout)
- [Pinned CLA action README](https://github.com/cla-assistant/github-action/blob/ca4a40a7d1004f18d9960b404b97e5f30a505a08/README.md)
- [Pinned remote-store implementation](https://github.com/cla-assistant/github-action/blob/ca4a40a7d1004f18d9960b404b97e5f30a505a08/src/persistence/persistence.ts)
- [Pinned PAT selection](https://github.com/cla-assistant/github-action/blob/ca4a40a7d1004f18d9960b404b97e5f30a505a08/src/octokit.ts)
