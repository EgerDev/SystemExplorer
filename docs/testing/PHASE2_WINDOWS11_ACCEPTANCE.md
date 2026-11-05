# Phase 2: external Windows 11 acceptance kit

## Checkpoint: BLOCKED, not Windows 11 acceptance

On 2026-11-05 this independent QA VM reported **Microsoft Windows Server 2022
Standard**, version **10.0.20348**, build **20348**, **ProductType 3**. `whoami /all`
reported `BUILTIN\Administrators` (`S-1-5-32-544`) enabled and High mandatory
integrity (`S-1-16-12288`). It is neither Windows 11 nor a standard-user token.
No qualifying Windows 11 host is available to this workstream. **Every manual
case below is NOT RUN.** No portable build was launched, no Windows 11 screenshot
was captured, and no timing or soak result is asserted by this document.

Source inspected: `9101b5580d10531f49ec566e0837c7f28339a3dd`. This kit changes
neither production code nor the existing architecture/notices. The parent owns
the tested integration revision, artifact production, performance protocol,
visual acceptance and release decisions. Retest the final integrated portable
artifact, not this documentation-only branch or an old Server build.

## Safety and result vocabulary

- Use an existing authorized Windows 11 x64 workstation and interactive account.
  Do not install an OS, create accounts, enable a driver, elevate, or change
  Defender, SmartScreen, execution policy, Secure Boot or code-integrity policy.
  If policy blocks this unsigned build or script, preserve the error and mark
  BLOCKED; do not unblock files or bypass policy to obtain a pass.
- Observation is read-only. Never terminate/suspend arbitrary processes, control
  services, change startup entries, or close network connections. Only launch
  bounded current-user test workers that exit themselves. Close test apps via
  their own ordinary UI; do not force-kill them.
- The optional preflight observes OS and token only. It never changes display,
  network, account or security settings, installs anything, or launches the app.
  `Add-Type` compiles its token-query helper; a restricted language/compiler
  failure is BLOCKED, not permission to loosen policy.
- `PASS`: actual stated behavior observed, with linked evidence. `FAIL`: observed
  mismatch. `BLOCKED`: missing prerequisite or inaccessible observation surface.
  `NOT RUN`: no execution. Never turn unsupported hardware or an absent feature
  into PASS. A preflight exit 0 means only host/token eligibility, not acceptance.
- An `asInvoker` manifest inherits the caller's token: it is not proof of a
  standard user. A filtered administrator token can satisfy the non-elevated,
  non-admin-enabled token check but is not evidence of a non-admin account.
  Prefer an existing account that is not an Administrators member and record
  account category separately; never describe a filtered admin as such an account.

## 1. Handoff bundle and provenance (parent/build owner)

Provide a private test handoff, not a public release:

1. Complete unsigned portable **directory** or its intact archive, produced using
   [Build-SystemExplorer.ps1](../../tools/Build-SystemExplorer.ps1) and SDK
   `10.0.403`. Preserve all native/managed DLLs, .NET runtime, WinUI resources,
   `SystemExplorer.App.pri`, localization/resources, notices and licenses. Do not
   cherry-pick files or trim dependencies for this test. No target SDK install.
2. Record source commit, build command and exit code, SDK version, builder OS,
   artifact name, archive SHA-256, executable SHA-256, and independently supplied
   SHA-256 of `SHA256SUMS.txt`. Include `BUILD-METADATA.json`,
   `dependency-versions.json`, `README-TEST-BUILD.txt`, `LICENSE.txt`,
   `COPYRIGHT.txt`, `licenses/` and the complete `SHA256SUMS.txt`.
3. Inspect metadata: `SourceRevision` must equal the parent-approved revision,
   `DotnetSdk` must match the recorded SDK, and `UnsignedTestBuild` must be true.
   `SourceDirty=true` is not clean provenance: require a parent-reviewed exact
   diff (including docs-only edits) and its hash, or a clean rebuild. Do not
   silently replace metadata or assume a known local edit is the only change.
4. Before transfer, use the installed Windows SDK `mt.exe` to extract the
   **built executable's** embedded manifest into a sidecar evidence directory:

   ```powershell
   & $mtExe "-inputresource:$portable\SystemExplorer.App.exe;#1" "-out:$evidence\embedded.manifest"
   if ($LASTEXITCODE -ne 0) { throw 'Embedded manifest extraction failed' }
   ```

   `$mtExe` is the installed SDK tool path; `$portable` and `$evidence` are
   absolute paths. Evidence goes outside the portable directory. Require
   `requestedExecutionLevel level="asInvoker"`, `uiAccess="false"` and
   `dpiAwareness` `PerMonitorV2`. A source manifest alone is insufficient. Also
   record x64 PE headers with the existing developer tools and executable
   version metadata. Do not install developer tools on the clean target for this.
5. Record `Get-AuthenticodeSignature` for the app executable and shipped PE files.
   The application should be explicitly recorded as `NotSigned` for this unsigned
   handoff; dependencies may be signed. Distinguish invalid signatures from unsigned
   files. Do not sign anything or equate a hash manifest with publisher authenticity.
6. Include this checklist and the optional preflight script as separate kit files,
   plus their hashes. Keep raw evidence outside the payload. The hash list does
   not hash itself, so authenticate its hash via the independent parent handoff.

### Target-side integrity verification

Before launch, compare archive and hash-list SHA-256 with the independent handoff
using `Get-FileHash -Algorithm SHA256 -LiteralPath <path>`. Extract into a new,
empty, current-user-writable directory, never over an older build. Keep the full
tree, including hidden files. Do not launch from the ZIP viewer. If extracted
through a GUI, record the extraction tool and destination.

For **every** line in `SHA256SUMS.txt`, compare the SHA-256 of the corresponding
relative path in the extracted root. Use literal paths, reject rooted/parent
traversal paths, and reject duplicate entries. Inventory the whole extracted tree
with `Get-ChildItem -LiteralPath $portable -Recurse -Force -File`; require exact
path-set equality with the hash list plus `SHA256SUMS.txt` itself. Missing,
additional or changed files block launch. Record the comparison log and counts,
not just a screenshot of the EXE hash. This is a manual verification requirement,
not a check performed by the host/token preflight.

## 2. Qualify the actual target and launch token

Run in a normal, non-elevated Windows PowerShell console in the same interactive
session that will launch the app. Do not use Run as administrator, alternate
credentials or an elevated scheduled task. Use already approved script execution
policy; none of the commands below changes it. Set `$kit` and `$evidence` to
existing absolute kit/evidence directories outside the portable payload.

```powershell
Get-CimInstance Win32_OperatingSystem |
    Select-Object Caption,Version,BuildNumber,ProductType,OSArchitecture |
    ConvertTo-Json | Set-Content -LiteralPath "$evidence\os.json"
whoami /all | Out-File -LiteralPath "$evidence\whoami-all.txt"
$whoamiExit = $LASTEXITCODE
& "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -File "$kit\Windows11-Acceptance-Preflight.ps1" > "$evidence\preflight.json"
$preflightExit = $LASTEXITCODE
"whoami=$whoamiExit; preflight=$preflightExit" | Set-Content -LiteralPath "$evidence\preflight-exit.txt"
```

Preflight exits: **0** eligible host/token only; **2** observed disqualifying
host/token; **3** observation failed. A policy/host failure may return another
nonzero code before the script starts; preserve stderr too. Missing/invalid JSON
or a nonzero code is BLOCKED. Do not run subsequent acceptance to fill green cells.

Required OS: workstation **ProductType=1 AND BuildNumber>=22000**, native x64
(processor Architecture code 9), confirmed Windows 11 edition/build in `winver`
and Settings > System > About. Server is rejected even if its build is above
22000; a build number or Windows-like screenshot alone is insufficient.

Required actual token: `TokenIsElevated=false`, `AdministratorsEnabled=false`,
and not full elevation (`TokenElevationType=2`). Record numeric elevation type
(1 default, 2 full, 3 limited), user SID, `whoami /all` group attributes and
integrity. The Administrators SID must be absent or deny-only, not enabled;
expected interactive integrity is Medium (`S-1-16-8192`), not High/System.
Unexpected Low/restricted/service/impersonated context is not the intended
standard desktop test: report it for review rather than substituting it.
The preflight also requires enabled `INTERACTIVE` (`S-1-5-4`) or
`REMOTE_INTERACTIVE` (`S-1-5-14`) membership, `TokenSessionId>0`, and
`UserInteractive=true` (an interactive desktop/window station). Positive logon
membership uses `WindowsPrincipal.IsInRole(SecurityIdentifier)` so a deny-only
group cannot qualify. Any `SERVICE` (`S-1-5-6`) or `BATCH` (`S-1-5-3`) group
presence blocks eligibility, even for an ordinary user SID or when interactive
membership is also present. Built-in service identities remain excluded.
Record `InteractiveEnabled`, `RemoteInteractiveEnabled`, `ServiceLogonPresent`,
`BatchLogonPresent`, `TokenSessionId` and `UserInteractive` from the JSON alongside
the existing elevation fields. These checks are only a pre-screen, not proof of
an actual standard-user Windows 11 application run.

The dependency-free `tools/tests/Windows11-Acceptance-Preflight.Tests.ps1` script
extracts and exercises the actual logon predicate with **synthetic** desktop,
remote-interactive, service, batch, session-zero and noninteractive fixtures.
It does not create accounts/services/tasks or change live tokens, and is not
evidence of live regular-user service execution or positive Windows 11 acceptance.
Record the actual running app's Elevated status as **No** in Task Manager Details
(add Elevated column). Keep token, session, launch path, PID and start time tied
to the recording; recheck after any relaunch from a different context.

No-driver evidence: record clean-image provenance and read-only inventory from
`Get-CimInstance Win32_SystemDriver | Select-Object Name,DisplayName,State,PathName`
before and after launch. Review it for System Informer/KSystemInformer/KPH or
other diagnostic helper drivers; names alone cannot prove absence. Confirm no
helper was installed/loaded by the workflow and no privileged broker was used.
If inventory access is denied or the image's history is unknown, driverless
clean-machine acceptance is BLOCKED. Do not unload or disable existing drivers.

## 3. Clean-machine offline portable run (manual)

Use a separately provisioned Windows 11 x64 image with documented history, no
SystemExplorer/System Informer installation, no developer tools, and no manually
installed .NET/Windows App SDK/VC++ prerequisites for this app. Inventory existing
dependencies rather than removing OS components. A developer workstation is not
clean-machine evidence. No OS/account provisioning is part of this kit.

The owner must provide an already authorized offline environment. Record the
network-isolation method and visible network state (including other adapters,
VPN/remote-management context); do not change firewall/security or disconnect a
managed session to manufacture offline conditions. "Did not use the browser"
does not establish offline operation. If offline state cannot be demonstrated,
mark this case BLOCKED and keep online diagnostics testing separate.

After hashes and preflight, launch the EXE from its complete extracted folder
using the same normal-user context. Record first launch through usable live
Processes/Memory/Services/Network views, then ordinary close and relaunch. Require
no install, runtime download, UAC prompt or missing PRI/DLL error. Capture any
policy warning without bypassing it. Verify live refresh and theme/resource
loading while offline. Repeat from a current-user path containing spaces. This
tests extraction-and-run portability, not an installer or uninstall procedure.
If a dependency is missing, capture the exact error and leave the clean image
unchanged: installing it would invalidate the claim being tested.

## 4. Manual matrix (all NOT RUN at this checkpoint)

For each row use the result record below. The final integrated build may expose
different controls; an absent promised feature is FAIL or a parent-approved
unsupported limitation, not an invented successful interaction.

| ID | Steps / expected behavior | Required evidence |
| --- | --- | --- |
| W11-01 | Complete provenance, target/token, driverless and offline gates above. Launch, close normally and relaunch the complete portable output. | Hash/inventory comparison, metadata, extracted manifest, signature results, OS/token/driver evidence and uninterrupted launch recording. |
| W11-02 | At **100%, 125%, 150%, 200%**, manually use authorized Windows Display settings; reopen app if requested by Windows. At every scale: initial size, minimum supported resize, maximize/restore, expanded group and open inspector; scroll long content horizontally/vertically where supported. No overlapping/clipped essential controls; inspector reachable and selection visible. | Separate full-window screenshots at every scale with resolution, physical/logical window size, theme and Settings scale evidence. No screenshot zoom or browser emulation. Record actual minimum; parent resolves unsupported minimum-size contract. |
| W11-03 | With real mixed-DPI monitors, move live app between 100% and 150%/200% displays in both directions, then resize/maximize/restore with inspector open. Text, hit targets and focus remain aligned; no blank content or stale size. | Display topology, both scales, recording across monitor boundary. One monitor or remote scaling alone means BLOCKED. |
| W11-04 | Repeat representative Processes/inspector, Memory, Network, Services, Settings and empty/search states in explicit light and dark themes, plus System theme following an authorized OS theme change. Check selection, hover, focus, disabled/unavailable text and graph labels. | Matched screenshots; no essential text lost against backgrounds. Parent compares actual screenshots with the approved reference; mockup is not evidence of execution. |
| W11-05 | Without mouse: Tab/Shift+Tab through navigation, search, table/group expanders, inspector, settings and tray menu; use Ctrl+K, arrows, Enter/Space, supported Esc dismissal, scroll and return focus. Sort and select rows; clear search. No trap, hidden focus or essential pointer-only action. | Keyboard-only recording and exact sequence, including visible focus after navigation, filtering and refresh. Record unsupported key actions explicitly. |
| W11-06 | With Narrator, discover navigation, search, row name/PID, selected state, group expanded/collapsed state, metric labels/units, inspector headings and close affordance. Confirm useful reading order and no per-refresh speech flood or lost focus. Inspect UIA tree/properties with already approved Accessibility Insights/Inspect at matching privilege: Name, ControlType, IsEnabled, IsKeyboardFocusable, HasKeyboardFocus, Selection/ExpandCollapse/Invoke/Value patterns as appropriate. | Narrator audio/video or verbatim observations, UIA property captures for controls and live row identity, issues and reproduction steps. AutomationId absence alone is not a failure; usable names/state/patterns are required. Automated scan does not replace Narrator. Missing UIA tool means that part BLOCKED, not permission to install onto the clean image. |
| W11-07 | Search name/PID, sort CPU/private commit/name/PID, expand/collapse a real multi-process group, select child and compare inspector. Pause/resume. Run the bounded self-exiting worker recipe in [quality gates](QUALITY_GATES.md#non-destructive-churn-recipe): 50 sequential, then at most 20 concurrent. Observe creation/exit with PID AND creation time while sorting/filtering. No duplicate identity, stale inspector attached to replacement, stuck refresh or phantom accumulating rows. | Worker PID/start/exit log and screen recording. Group totals must cover each verified member once and identify partial coverage. Workers shorter than cadence may be missed; natural churn is not proof of PID-reuse handling. Synthetic reuse tests stay separate. |
| W11-08 | Compare Services table and selected process services with `Get-CimInstance Win32_Service` fields Name/DisplayName/State/ProcessId near the same timestamp; compare shared-host services by exact service name and owner identity, not process name alone. Read dependencies where the app exposes them, without starting/stopping services. | Saved timestamped reference rows and app screenshots for stopped, running and shared-host services. Exit/restart races are re-observed, not forced. Unsupported dependency details remain explicit. |
| W11-09 | Compare TCP/UDP against `Get-NetTCPConnection` and `Get-NetUDPEndpoint` (or read-only `netstat -ano` if unavailable), recording collection time, address family, local/remote address/port, state and OwningProcess. Match a stable current-user loopback connection and UDP listener from a parent-approved self-closing local fixture; also inspect an ordinary existing connection. Include IPv4/IPv6 if available. | Reference output, fixture owner PID/creation time and app rows. UDP endpoints are not connections; network counts are not throughput. A missing fixture/family or denied reference is BLOCKED for that case. Offline local-loopback evidence does not imply internet coverage. Never expose listeners outside loopback. |
| W11-10 | Read own process details and an actually access-denied/protected target without changing permissions. Visit all advertised navigation sections. Distinguish unsupported, denied, exited, partial and initial-unsampled fields from measured zero; compare like-for-like memory/CPU units. | Raw error/status where observable plus UI. A dash only proves unavailable: never label it Access denied without evidence. If no denied query can be established, live-denial case BLOCKED; deterministic fixtures cannot replace it. |
| W11-11 | Use actual tray icon and overflow area if needed: accessible name/tooltip/summary, activate/open, minimize/restore behavior, keyboard menu and normal Exit. Follow shipped close-to-tray behavior rather than assuming it; verify closing/restoring keeps selected identity sane. | Recording of every advertised tray action and process lifecycle. Ordinary Exit removes app/tray icon with no leftover app worker; do not force termination. Missing tray functionality must not be silently skipped. |
| W11-12 | Perform parent-approved extended stability protocol on this exact host/artifact, with live refresh and a recorded mix of stable idle, search/sort, inspector navigation, bounded churn, pause/resume and tray restore. Check recovery and responsiveness, stale rows, resource and error trends. | UTC start/end, actual elapsed duration, workload timeline, interruptions, raw samples and error/crash evidence. Parent owns collection/analysis and budgets; shortened/interrupted runs are incomplete, never a full soak PASS. |

For DPI cases manual display changes require owner approval and should restore
the owner's original scale/layout after testing. The script never makes these
changes. Do not ask for elevation to complete a case.

### Accuracy and unavailable readings

- Compare the same PID **and creation time**, metric definition and nearby
  sampling window. Task Manager's default Memory is not private commit. Record
  machine CPU normalization, interval, logical processors and app pause state.
- At the inspected source baseline, GPU utilization, private working set,
  standby memory, command lines, service start types and catalog signatures are
  documented as unavailable in [src/README.md](../../src/README.md). Recheck the
  final integrated artifact's capability list rather than assuming Phase 2 added
  them. Missing support must remain explicit, not fabricated zero measurements.
- Protected/exited processes and fast-changing endpoints can explain gaps only
  when evidence supports that explanation. Preserve error codes and timestamps;
  do not guess access denied from an empty cell or accept mismatched owners.
- Signature/publisher checks offline use cached local trust data, not evidence
  of a fresh online revocation check. Do not alter trust policy.

## 5. Evidence record and retest handoff

Use one record per case/configuration (DPI/theme/monitor variants are separate):

```text
Case ID / configuration:
Status: NOT RUN | BLOCKED | FAIL | PASS
Tester / UTC start and end / actual elapsed duration:
Source commit / source dirty state + diff hash if any:
Archive SHA256 / EXE SHA256 / SHA256SUMS.txt SHA256:
Windows caption / version / build / ProductType / architecture:
Token elevation / admin-enabled / integrity / account category:
Driverless and clean-image provenance / offline evidence:
Display topology / scale / resolution / window size / theme:
Refresh interval / workload / PID + creation time:
Exact command or interaction / command exit code:
Expected result:
Actual result (no inferred PASS):
Evidence paths or private links:
Limitation / issue / next retest step:
```

Keep raw logs, full-window images and native-screen recordings with capture
timestamps. Use only authorized benign data; process paths, service details,
network addresses and identity logs may be sensitive. Preserve raw evidence in
approved private storage; redact shared copies and document redactions. No
secrets, machine dumps or binary releases belong in git. Upload evidence and
return private links alongside the immutable commit/artifact hashes.

Rerun affected cases after fixes with a new artifact hash; retain previous FAIL
or BLOCKED evidence rather than overwriting it. Parent decides regression scope
and signs off visual acceptance. A checklist marked by automation without
observations is not acceptance. This kit authorizes no merge, signing or release.

### Existing gates and ownership

- [Quality gates](QUALITY_GATES.md), especially the required acceptance matrix,
  artifact/release gate and **proposed** performance protocol. Earlier scoped
  baseline descriptions there are not current Phase 2 test results.
- [Windows acceptance checklist](WINDOWS-ACCEPTANCE-CHECKLIST.md) and
  [native build/check commands](../../src/README.md). Parent runs the applicable
  build/provider suites; this doc/script-only workstream does not rebuild the app.
- [Existing measurement collector](../../tools/Measure-SystemExplorer.ps1) is
  parent-owned. Agree duration/workload/reference hardware and analysis with the
  parent; do not reinterpret a ten-minute Server observation as Windows 11 soak
  acceptance or invent performance numbers. No CodSpeed/cloud infrastructure.

### Documentation consulted

- [Microsoft: self-contained Windows App SDK deployment](https://learn.microsoft.com/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)
  and [unpackaged WinUI apps](https://learn.microsoft.com/windows/apps/package-and-deploy/unpackage-winui-app), retrieved through Context7: keep the complete runtime/resource payload, with both .NET and Windows App SDK self-contained.
- [Microsoft: accessibility testing](https://learn.microsoft.com/windows/apps/design/accessibility/accessibility-testing), retrieved through Context7: UIA inspection and Narrator/manual interaction complement one another.
- [DeepWiki WindowsAppSDK deployment research](https://deepwiki.com/search/for-unpackaged-selfcontained-w_c3f37382-12f1-4a3e-a2b1-641028a80563), checked against the repository's actual publish configuration. Research does not establish runtime success.
- API reference for preflight interpretation: [Win32_OperatingSystem](https://learn.microsoft.com/windows/win32/cimwin32prov/win32-operatingsystem), [TOKEN_ELEVATION](https://learn.microsoft.com/windows/win32/api/winnt/ns-winnt-token_elevation), [TOKEN_ELEVATION_TYPE](https://learn.microsoft.com/windows/win32/api/winnt/ne-winnt-token_elevation_type), [GetTokenInformation](https://learn.microsoft.com/windows/win32/api/securitybaseapi/nf-securitybaseapi-gettokeninformation).
