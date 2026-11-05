# SystemExplorer quality gates

## Scope and evidence baseline

Bounded M0/M1 strategy, inspected at `0a4975a4763ad202747397ef5daf5893849b5b00` on 2026-11-05. This is not a release approval or a full legacy audit. **Observed** below means source/configuration inspection; **required** means future acceptance work, not a passing result. Preserve System Informer separately from the new SystemExplorer frontend/provider boundary.

The inspection VM reports Windows Server 2022 Standard, `10.0.20348`, x64 (`Get-CimInstance Win32_OperatingSystem`). It is a real Windows environment, but not Windows 11 acceptance evidence. No application build, runtime contract tests, GUI tests, destructive actions, driver-policy changes, or performance measurements were performed for this document.

### Existing verification, observed

| Evidence | What it establishes / limitation |
| --- | --- |
| `.github/workflows/msbuild.yml` | Debug, release/archive, driver and driver-release build jobs on `windows-2025-vs2026`; release reflows then validates exports and uploads ZIPs. Configuration is not evidence of a successful run. |
| `.github/workflows/cmake.yml` | Debug/Release, MSVC/Clang, x64/x86/ARM64 Ninja builds; release output uploaded. Cross-building ARM64 does not prove ARM64 execution. |
| `.github/workflows/line-endings.yml` | Byte-level CRLF gate for batch files, build-scoped automatic triggers. |
| `.github/workflows/scan.yml` | Scheduled/manual C++ CodeQL software and driver analysis, not a PR-triggered acceptance test. Driver query pack `microsoft/windows-drivers` is not version-pinned in the download command. |
| `tools/tests/phlib-test/main.c` | Runs base-support, AVL, formatting and utility tests; no process-provider contract suite. No invocation found in the checked-in workflows or top-level solutions/CMake entry points. |
| `tools/tests/phlib-test/*.c`, `phlib-test.vcxproj` | Assertions are extensively used; Release defines `NDEBUG`, so Release exit zero is not sufficient assertion evidence. Run Debug. The project requests `RequireAdministrator` while its explicit manifest says `asInvoker`: inspect the built manifest rather than assuming standard-user coverage. |
| `build/README.md`, `build/build_ubsan.cmd` | Existing isolated user-mode Clang UBSan trap build; a successful compile is not sanitizer runtime coverage. No kernel bypass is required. |
| `SystemInformer/procprv.c` | Existing identity checks use process sequence number where supported, creation time otherwise; private working set and VM counters are distinct. Reuse these semantics deliberately rather than reducing identity to PID. |

## Commands supported by the current repository

Run from the repository root in a provisioned Windows developer shell. Build specialist owns baseline execution and logs; these recipes were inspected, **not executed here**. Prerequisites: `.vsconfig`, v145 toolset used by projects, .NET 10 for CustomBuildTool, and SDK/WDK NuGet versions from `packages.config`. Do not substitute policy bypasses for missing tooling.

```powershell
nuget restore .\packages.config -PackagesDirectory .\packages\
if ($LASTEXITCODE -ne 0) { throw 'NuGet restore failed' }
& .\build\build_init.cmd INIT
if ($LASTEXITCODE -ne 0) { throw 'Initialization failed' }
& .\build\build_debug.cmd
if ($LASTEXITCODE -ne 0) { throw 'Debug build failed' }
& .\build\build_release.cmd
if ($LASTEXITCODE -ne 0) { throw 'Release build failed' }
& .\build\build_exports.cmd validate
if ($LASTEXITCODE -ne 0) { throw 'Export validation failed' }
```

These are build/package operations, not permission to publish. Do not invoke deploy, release-upload, online-scan or signing-key generation commands. For interactive shells the debug/release wrappers may pause; in CI use their existing CI detection, not patched scripts.

Existing test-project build, after matching Debug native libraries have been built:

```powershell
msbuild .\tools\tests\phlib-test\phlib-test.sln /m /t:Build /p:Configuration=Debug /p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw 'phlib-test build failed' }
# Use the exact TargetPath in the build log; do not run a stale Release executable.
# Inspect the embedded manifest with the Windows SDK mt.exe before choosing test privileges.
```

The test solution has only the test project, not native dependency project references. Its library search paths include `$(SystemInformerRoot)thirdparty\bin`, whereas the repository contains `tools/thirdparty`; a fresh isolated test build must resolve actual library paths. Record a precise build failure rather than silently repairing production code in QA. After build/manifest review, run that Debug TargetPath, capture stdout/stderr and exit status, and require assertions enabled. This is an existing test entry point, not a claim that the standalone command currently succeeds.

Optional bounded sanitizer lane, after baseline succeeds:

```powershell
& .\build\build_ubsan.cmd
if ($LASTEXITCODE -ne 0) { throw 'UBSan build failed' }
```

## Required acceptance matrix

Each result records commit, executable SHA-256, Windows build, architecture, privilege/driver state, refresh interval, command or GUI steps, expected/actual outcome and raw evidence. `Blocked` and `not run` are not passes. Synthetic fixtures may prove deterministic contracts; **only real Windows collection and GUI runs prove live telemetry**.

| Gate / owner | Bounded test and pass condition | Dependency / evidence |
| --- | --- | --- |
| M0 / build | Clean x64 Debug and Release build; logs show toolchain and exit codes; upstream launch and driverless available/unavailable features documented. Do not conflate driver compile with successful loading. | Build logs, archive manifest/hash, genuine Windows launch; Windows 11 still required. |
| M1 identity / engine + QA | Deterministic snapshots with same PID and different creation identity must create a new row identity; old asynchronous metadata, CPU deltas and selection must not attach to replacement. Missing creation identity remains explicitly uncertain; path/user are metadata, not sufficient uniqueness keys. | Agreed contract and executable test project are not yet present at baseline. Include reordered/completed-after-cancel responses. |
| M1 lifetime / engine + QA | Exit between enumeration and detail query returns Exited/Unavailable, not zero or a fatal refresh. Disposing/cancelling provider stops publication; retained snapshots remain valid across next refresh. | Contract tests plus real short-lived children; no handle/pointer retained past documented ownership. |
| M1 permissions / engine + QA | Standard-user collection: deny a detail query in deterministic tests, then observe an actually denied query on Windows. Preserve row and error provenance; distinguish Denied, Unsupported, Exited and not-yet-sampled. No implicit elevation/retry via driver. | Capture token/target identity and actual Windows status. If host offers no denied case, mark live case blocked, not simulated-as-live. Do not manipulate protected processes. |
| M1 sampling / engine + QA | First CPU sample unknown; elapsed monotonic time > 0; delta formula documented as process CPU seconds / elapsed seconds / logical processor count for whole-machine percent. Reset on identity change/counter reset. I/O byte deltas are rates only with valid elapsed time; disk I/O is not network I/O. | Deterministic clock/counter tests, then own benign CPU worker; record normalization, interval and processor count. |
| M1 memory / intelligence + QA | Explicit bytes and nullable availability for private bytes (private commit), private working set (resident private), and total working set (resident including shared). Fixture with shared pages proves total WS sums are not labeled physical RAM. Group private WS sum is labeled and covers unique known members only; denied members make totals partial, not zero. | Contract assertions and live comparison of the same identity/metric; never compare Task Manager default Memory to private bytes as if identical. |
| M1 relationships / engine + QA | Parent PID alone is insufficient: reject newer parent creation identity; exited parent is unknown. Group members counted once. Path/name similarity is not proof of same publisher/application. Service/TCP owner joins retain identity/snapshot timing and indicate races. | Deterministic reuse/ambiguity cases; real service mapping only where provider can verify it. UDP endpoint count is not a connection count. |
| M2 live churn / QA | Run 50 sequential benign children, each self-exits; then 20 concurrent children, all self-exit. Poll while searching/selecting. No crash, duplicate identity, stale inspector or stuck refresh; processes living across two sample boundaries appear. Processes shorter than polling cadence may be missed and must not be claimed complete. | Log PID + creation/exit timestamps. Natural churn does not guarantee PID reuse; keep deterministic reuse test separate. No mass termination, system stress or unbounded spawning. |
| M2 native GUI / frontend + QA | On Windows 11 x64, standard-user/no driver: launch real binary; search, sort, expand, select and inspect real processes. Verify inspector follows identity through sort/filter/exit. All advertised sections/actions must work or be explicitly unavailable. | Real screen recording/screenshots with version/hash; approved image is a design reference, never execution evidence. |
| M2 accessibility/layout / frontend + QA | Keyboard-only Tab/Shift+Tab traversal, Ctrl+K if advertised, arrows, Enter/Space expand/activate, Esc dismiss; visible focus and no trap. Inspect accessible names/state/row identity with Narrator/Accessibility Insights. Test 100/150/200% DPI, supported minimum size, maximize/restore and cross-monitor DPI where hardware permits. Selected row/inspector/buttons must remain reachable without clipped essential text. | Record physical resolution, scale and window size; proposed minimum 1024x768 logical pixels requires design approval. A single-scale Server VM is not all-DPI coverage. |
| M3 accuracy / intelligence + QA | System memory categories do not double count cache/standby; commit is distinct from physical usage. Explain missing counters and sampling skew. Growth trend alone must not be called a leak. | Provider mapping to Windows counter definitions, known-allocation test worker and raw samples. |
| M7 packaging / release + QA | Inspect ZIP contents, clean extract/launch offline under standard user, correct x64 dependencies/version/brand, notices and no private keys/secrets. On disposable Windows 11 machine test install/uninstall only when authorized; preserve user data and upstream installation. | Actual artifact, hashes, dependency list, signatures, manifest and launch evidence; source-tree launch is insufficient. |

### Non-destructive churn recipe

Run only after a real monitoring build is ready. This creates current-user PowerShell children; they exit themselves. It does not suspend/kill anything. Keep recorded creation identity, since PID alone is not proof of which instance was observed.

```powershell
$worker = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
1..50 | ForEach-Object {
    $p = Start-Process $worker -ArgumentList '-NoProfile -NonInteractive -Command "Start-Sleep -Seconds 2"' -PassThru
    [pscustomobject]@{ PID=$p.Id; CreatedUtc=$p.StartTime.ToUniversalTime() }
    $p.WaitForExit()
    $p.Dispose()
}
# Separate run: replace sequential launch/wait with at most 20 children,
# sleeping 10 seconds each; await every child and dispose its Process object.
```

## Performance protocol (proposed gates, not measured claims)

Use the same Windows 11 x64 machine, Release artifact, standard-user/no-driver state, process population and one-second refresh. Record CPU model/count, RAM, display scale and power mode. Compare preserved upstream and new app separately; they do not yet have feature parity. Exclude benchmark collector overhead from app CPU but report it.

1. Five cold-process launches (not claimed cold OS cache): timestamp process start to first **complete usable live snapshot** and separately first paint. Proposed median <=2 s and max <=5 s on agreed reference hardware.
2. Five-minute warm-up then ten-minute idle observation, repeat three times. Sample target by PID **and creation time**, total CPU time, private bytes, working set, handle/thread count each second. Whole-machine CPU % = delta CPU seconds / elapsed monotonic seconds / logical processor count * 100. Proposed mean <=1%, p95 <=3%; report absolute values and change from baseline, not only percentage regressions.
3. Real search/sort/expand interaction under normal and bounded churn population: proposed input-to-present p95 <=100 ms, no pause >500 ms. Use UI timing/ETW evidence, not background provider timing as a UI surrogate. Report sample count and percentile definition.
4. One-hour soak, warm-up excluded: compare first/last five-minute medians and time series of private bytes/handles. Investigate sustained growth >10 MiB or >5% (whichever larger), or >10 handles. These are investigation triggers, not proof of a leak; confirm with ownership/allocation evidence. No crashes, unbounded queues or monotonically accumulating exited rows.

Parent approves reference hardware/budgets before enforcement. A missing timer/harness is a blocked measurement, not an estimated pass. Do not add cloud telemetry or CodSpeed infrastructure merely to obtain these local measurements.

## Artifact and release gate

Read-only inventory after build (outputs may contain multiple architectures; validate the exact artifact intended for distribution):

```powershell
Get-ChildItem .\build\output -File | Get-FileHash -Algorithm SHA256
Get-ChildItem .\bin -Recurse -File -Include *.exe,*.dll,*.sys |
    ForEach-Object { Get-AuthenticodeSignature -LiteralPath $_.FullName } |
    Select-Object Path,Status,StatusMessage
```

Inspect archived binaries as well as build-directory binaries. Extract to a fresh, uniquely named test directory; compare extracted hashes, PE architecture/dependencies (`dumpbin /headers`, `dumpbin /dependents` from the developer shell), embedded manifest and version metadata. `NotSigned` is allowed only for explicitly labeled internal unsigned test builds; invalid signatures must not be described as signed. A `.sig` driver-client trust file is **not** an Authenticode signature. Preserve Secure Boot and code-integrity policy. Never install an untrusted driver to complete acceptance.

Before public redistribution, require the component-to-binary license inventory in [THREAT_MODEL.md](THREAT_MODEL.md), updated notices, provenance, and approved handling of copyleft/redistributable obligations. No public release is authorized by this plan. No CI configuration or dependency version alone establishes vulnerability-free status.

## Integration handoff

Parent must provide the selected provider/frontend boundary, identity/error/memory schema, concrete test project/command, exact build artifacts and standard-user Windows 11 access. QA then adds/tests only newly assigned files and independently evaluates these gates. Build/CI remains with the build specialist; status tracking remains parent-owned. At this baseline, M1 tests and new-app GUI/performance/release acceptance are **not run**, not completed.
