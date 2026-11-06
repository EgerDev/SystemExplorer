# Original Windows build baseline

## Verdict and scope

On 2026-11-05, the original fork at `0a4975a4763ad202747397ef5daf5893849b5b00`
was tested on Windows Server 2022 Standard x64. **Initialization passed;
the original SystemInformer executable did not compile.** Both Debug and Release
x64 reached application compilation and failed on the source diagnostics below.
This is a reproducible source blocker, not a successful application baseline.

Branch: `feature/windows-build-baseline`. Only this document is committed. No
original source, project, workflow, security policy, or signing configuration was
changed. No driver was installed or loaded. No PR, merge, or release was created.

Build session: https://app.devin.ai/sessions/5fb62d4da8ef4b699ec0df1687a73782

## Observed environment

| Component | Verified version/state |
| --- | --- |
| OS | Windows Server 2022 Standard x64, build 20348 |
| Visual Studio Build Tools | 18.11.3+12328.437 |
| MSBuild | 18.11.1.51303 |
| MSVC tools directory | 14.51.36231 |
| C/C++ compiler | 19.51.36260.0 |
| .NET SDK / runtime | 10.0.403 / 10.0.14 |
| NuGet CLI | 7.9.0.83 |
| Installed Windows SDK | platform directory 10.0.28000.0; component catalog title 10.0.28000.2705 |
| Repository NuGet SDK/WDK packages | all six `packages.config` entries at 10.0.28000.2526 restored |
| Spectre libraries | x86 and x64 present; Release mitigation settings unchanged |

The initial environment had none of VS, MSBuild, cl, Windows SDK, .NET, or
NuGet. The initial
`build\build_init.cmd INIT` returned 1: `No Visual Studio installation detected.`
Supported tooling installation recovered that environmental failure.

### Installed components and exact installer arguments

The Microsoft-signed Build Tools bootstrapper came from
https://aka.ms/vs/stable/vs_buildtools.exe (saved as `vs_BuildTools_official.exe`).
The original version-style URL checked during discovery was not used as an
installer; the official stable bootstrapper's Authenticode signature was verified.

Executed command (CMD syntax, from the evidence/download directory):

```bat
vs_BuildTools_official.exe --quiet --wait --norestart --installPath "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools" --add Microsoft.Component.MSBuild --add Microsoft.VisualStudio.Component.VC.Tools.x86.x64 --add Microsoft.VisualStudio.Component.VC.Runtimes.x86.x64.Spectre --add Microsoft.VisualStudio.Component.NuGet.BuildTools
```

NativeAOT later exposed the missing standalone Windows SDK (`Advapi32.lib`), so
the same supported installation was extended with the catalog-confirmed component:

```bat
vs_BuildTools_official.exe --quiet --wait --norestart --installPath "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools" --add Microsoft.VisualStudio.Component.Windows11SDK.28000
```

No workload IDs, ARM64 toolchain, or driver installation were selected. Wait for
the installer child processes to finish: this environment's wrapper returned
before the SDK installer completed. Verify installed components with `vswhere`
and verify `C:\Program Files (x86)\Windows Kits\10\Lib\10.0.28000.0` before building.

The .NET SDK installer was downloaded from
https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.403/dotnet-sdk-10.0.403-win-x64.exe
(HTTP 200), validated against official release-metadata SHA-512 and Microsoft
Authenticode, then invoked as:

```bat
dotnet-sdk-10.0.403-win-x64.exe /install /quiet /norestart
```

Installer versions are observations, not immutable pins on the moving VS stable
URL. A future environment should record the versions it actually installs.

## Executed build recipe

Repository: `C:\Users\Administrator\repos\SystemExplorer`.
Evidence/download directory: `C:\Users\Administrator\baseline-evidence`.

The **user-level** NuGet configuration had no package sources. No repository
`NuGet.Config` was present. Neither was edited. Standalone NuGet's explicit source
does not automatically configure later MSBuild restores, so both overrides below
are necessary in this environment.

Run from the repository root in CMD (the actual runs invoked these commands via
`cmd.exe /d /c` from PowerShell):

```bat
set "PATH=C:\Program Files\dotnet;C:\Users\Administrator\baseline-evidence;%PATH%"
set "GITHUB_ACTIONS=true"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "RestoreSources=https://api.nuget.org/v3/index.json"
nuget restore .\packages.config -PackagesDirectory .\packages\ -Source https://api.nuget.org/v3/index.json
call build\build_init.cmd INIT
call build\build_msbuild.cmd SystemInformer.sln /m /p:Configuration=Debug /p:Platform=x64
call build\build_msbuild.cmd SystemInformer.sln /m /p:Configuration=Release /p:Platform=x64
```

Check `%ERRORLEVEL%` after each command; stop on failure in an automated recipe.
`GITHUB_ACTIONS=true` suppresses interactive pauses; it does not publish anything.
The direct solution commands are the documented Visual Studio/MSBuild alternative,
scoped to x64. The broader `build_debug.cmd` and `build_release.cmd` workflows were
read but not executed; plugins, all-architecture packaging, installer production,
and release ZIP generation are **not** verified by this run.

For durable compiler diagnostics, the incremental Debug confirmation used the
MSBuild file logger:

```bat
call build\build_msbuild.cmd SystemInformer.sln /m /p:Configuration=Debug /p:Platform=x64 "/flp:logfile=C:\Users\Administrator\baseline-evidence\systeminformer-debug-x64-msbuild.log;verbosity=normal"
```

### Results

| Stage | Exit | Evidence/result |
| --- | --- | --- |
| Initial init, no tooling | 1 | No Visual Studio installation detected |
| Official pinned NuGet restore with explicit source | 0 | Six SDK/WDK packages restored |
| Init without MSBuild source override | 1 | NU1100 unresolved build-tool packages |
| Init with source override, before standalone SDK | 1 | NativeAOT LNK1181: Advapi32.lib missing |
| Init with source override and installed SDK | 0 | CustomBuildTool x86/x64; thirdparty Debug/Release x86/x64 |
| Direct Debug x64 solution | 1 | SystemInformer source compilation errors |
| Direct Release x64 solution | 1 | Same source compilation blockers |
| Incremental Debug with file logger | 1 | Failure reproduced with persisted MSBuild diagnostics |

Initialization reported tools identifier:
`CB635B23CA0C022DA711C1DFDAE8AA99D3C3FAEB61F40E3B74D72A559D4947D5`.

## Confirmed remaining source blockers

1. `SystemInformer/memsrch.c:582-583`: C4090, different `volatile` qualifiers;
   C2220 because warnings are errors. `SystemInformer/include/memsrch.h:43-44`
   declares the options' progress pointers as `volatile ULONG*`, but the bulk
   context fields at `memsrch.c:92-93` are `PULONG`. These assignments discard the
   qualifier. Do not suppress warnings or weaken the build to hide this.
2. `SystemInformer/thrdshstk.c:113-125`: C2065 for
   `SETTING_THREAD_SHADOW_STACK_LISTVIEW_COLUMNS`,
   `SETTING_THREAD_SHADOW_STACK_WINDOW_POSITION`, and
   `SETTING_THREAD_SHADOW_STACK_WINDOW_SIZE`. Repository-wide tracked-source
   search finds their uses but no definitions at the tested commit.

**Recommendation, not an implemented fix:** the original-source owner should
preserve the progress pointer qualifiers and restore the missing shadow-stack
settings declarations/registration using the existing settings conventions.
Then rerun both x64 commands above. This will validate these fixes; it does not
guarantee that no subsequent link/runtime issue remains. Repair is outside this
build-only assignment, and no original C/C++ files were changed.

## Driverless scope: source evidence, not runtime acceptance

The original program has a supported `-nokph` argument:
`SystemInformer/main.c:1595` gates `PhEnableKsiSupport` on `!NoKph`, and
`main.c:102-105` gates kernel support initialization on that flag.

Source inspection supports ordinary user-mode monitoring without KSI:

- `SystemInformer/procprv.c:2635` calls `PhEnumProcesses`; `phlib/native.c:1623-1677`
  implements this through `NtQuerySystemInformation(SystemProcessInformation)`.
  CPU/I/O/private-byte deltas are updated from these process records in
  `procprv.c:2956-2975`.
- `phlib/nativeprocess.c:25-69` uses `NtOpenProcess` when the KSI level does not
  permit the kernel route. Normal access checks still apply; protected/inaccessible
  process details must not be promised.
- `SystemInformer/netprv.c:1770-1830` uses IP Helper TCP/UDP enumeration APIs.
- `SystemInformer/srvprv.c:842` uses `PhEnumServices`, implemented with
  `EnumServicesStatusEx` in `phlib/svcsup.c:161-205`.

This is evidence of implemented user-mode paths, **not** proof that all UI features
work driverlessly: no SystemInformer executable was available to launch. After
repair, run `bin\Debug64\SystemInformer.exe -nokph` for the first safe smoke test.
That launch command is recommended, not executed here. Driver-enabled inspection,
protected-process access, and Windows 11 UI acceptance remain untested. Never use
the kernel README's debug/test-signing instructions to satisfy this baseline.

## CI and integration requirements

Observed configuration, not a claim of green CI:

- `.github/workflows/msbuild.yml` uses `windows-2025-vs2026`; separate jobs cover
  Debug, Release, driver analysis and driver release. It restores `packages.config`
  and runs the original init wrappers. Release uploads `build/output/*.zip`.
- `.github/workflows/cmake.yml` uses `windows-latest` with MSVC/Clang and
  x86/x64/ARM64 Debug/Release combinations. It was not run in this baseline.
- No workflow was changed or triggered. The local source failures are not evidence
  of any specific remote workflow result.

Parent integration should cherry-pick only this documentation commit. Source fixes
need separately assigned ownership. Keep the installed toolchain for the rerun;
do not repeat downloads or rebuild thirdparty unless its inputs/toolchain change.
No kernel-driver permission or signing exception is needed for the x64 user-mode
compile recipe.

## Evidence and partial artifacts

Evidence is preserved outside git in `C:\Users\Administrator\baseline-evidence`;
the logs ZIP was delivered separately from this document.
Important files:

- `inventory.txt`, `toolchain-detail.txt`, `recovery-validation.txt`
- `vs2026-install-attempt.txt`, `vs-sdk28000-component-add.txt`,
  `vs-sdk28000-install-verification.txt`, `dotnet10-install.txt`
- `nuget-restore.txt`, `build-init.txt`, `build-init-recovered.txt`,
  `build-init-restore-source.txt`, `build-init-restore-source-sdk.txt`
- `build-init-restore-source-sdk-console.txt`,
  `systeminformer-debug-x64-console.txt`, `systeminformer-release-x64-console.txt`
- `systeminformer-debug-x64-msbuild.log`, `artifact-hashes.txt`

PowerShell transcripts did not retain all subprocess console output. The named
`*-console.txt` files preserve that output separately; the incremental Debug log
was emitted by MSBuild itself. Consult both the command/exit transcript and the
compiler output rather than treating a short transcript as a complete build log.

Selected SHA-256 hashes (partial outputs only; **not** a SystemInformer release):

| Repository-relative artifact | SHA-256 |
| --- | --- |
| `tools/CustomBuildTool/bin/Release/amd64/CustomBuildTool.exe` | `25C2C59EBB15D12F46ACB9B441A6E8AB2A03A636DB9BCF619E72D06EFAF018BB` |
| `tools/thirdparty/bin/Debug64/thirdparty.lib` | `9C7266C3655FB709B28DCAEA47AFBEEE183F63F1671E14132A1B0F6400943DD6` |
| `tools/thirdparty/bin/Release64/thirdparty.lib` | `568434BD4BFB5CAF13A987FBD5344E3343A2A5C1555694D37F03164CABF9A8F1` |
| `bin/Debug64/peview.exe` | `02BF30ED68363494FF84FB28BCE334267BF07465A4790C304AC8F993C380E7BF` |
| `bin/Release64/peview.exe` | `15EFC0FB1A3B9D7234397F580275F496FE5CDC05E8460849FE5D2AF68B2DEE9E` |

No hash is supplied for SystemInformer.exe because its build did not succeed.
No application runtime, WinUI/frontend behavior, performance, or installer test
result is implied by these partial artifacts.
