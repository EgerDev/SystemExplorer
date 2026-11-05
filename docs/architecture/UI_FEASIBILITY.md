# SystemExplorer native UI feasibility

Scope: specialist C, M0/M1 only. Research date: 2026-11-05. Base: `0a4975a47`.
Branch: `feature/winui-feasibility`.
Session: https://app.devin.ai/sessions/ff9121fdee614303855f6ab81817d312

## Decision boundary

**Recommendation, not an architecture decision:** continue evaluating C# / XAML / WinUI 3
for the new frontend. Keep the original System Informer solution intact. The parent must
approve the native monitoring boundary after its integration proof; a window that launches
does not demonstrate provider extraction, process correctness, performance, or safety.

The isolated prototype is explicitly labeled **Static feasibility screen**. It displays no
process rows, sample counters, or fabricated telemetry. The only implemented interaction
is a native button updating a text label. Search and future navigation destinations are
disabled. It is neither the production Processes screen nor pixel-perfect design acceptance.
No WebView control, browser frontend, driver, telemetry, elevated operation, or cloud service is used.
The umbrella App SDK package nevertheless brings transitive WebView2 and AI/ML packages;
their presence in the lock file does not mean those features are initialized. Revisit the
dependency/footprint choice before release rather than claiming this is a minimal package set.

## Official package and support research

Observed from official documentation and NuGet indexes on the research date:

| Component | Selected recipe | Evidence / scope |
| --- | --- | --- |
| .NET | SDK `10.0.403`, runtime `10.0.14`, C# defaults from .NET 10 | Official release metadata; .NET 10 LTS policy lists support through 2028-11-14. |
| Windows App SDK | `Microsoft.WindowsAppSDK` `2.6.5` | Stable downloads and NuGet index agree. Official channel page lists 2.0 family current servicing through 2027-04-29; latest patches remain required. |
| SDK build tools | `Microsoft.Windows.SDK.BuildTools` `10.0.28000.2957` | Stable version exists in official NuGet index; selected explicitly, no preview dependency. |
| Target | `net10.0-windows10.0.26100.0`, `win-x64` | Product target remains Windows 11 x64; manifest is per-monitor DPI aware, `asInvoker`. |
| Deployment | Unpackaged; .NET and Windows App SDK both self-contained | `WindowsPackageType=None`, `SelfContained=true`, `WindowsAppSDKSelfContained=true`. Full output directory required. |

This is a version-pinned feasibility baseline, not permission to stop servicing dependencies.
Do not start a new production baseline on .NET 8/9 or Windows App SDK 1.8 merely because
older examples use them: the researched support windows are ending or have ended.

Official sources:

- [Windows App SDK downloads](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads)
- [Channels and servicing](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/stable-channel)
- [Supported Windows releases](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/support)
- [Self-contained deployment](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)
- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
- [.NET 10 release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)
- [Windows App SDK NuGet versions](https://api.nuget.org/v3-flatcontainer/microsoft.windowsappsdk/index.json)
- [Windows SDK build tools NuGet versions](https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/index.json)
- [Official current blank app template](https://github.com/microsoft/WindowsAppSDK/blob/main/dev/Templates/Dotnet/templates/blank-app/ProjectTemplate.csproj)

Context7 was queried using `/microsoft/windowsappsdk` for the unpackaged project template
and `/websites/learn_microsoft_en-us_windows_apps` for collection virtualization. Versions
were checked against official feeds, not inferred from examples. No third-party grid or
MVVM package is required for this bounded launch proof.

## Reproduction and evidence

The prototype's local `Directory.Build.props` and `Directory.Build.targets` deliberately stop
MSBuild's upward search. The root files import native WDK/SDK properties and define a
multi-platform native build; they are unchanged. Invoke commands **from the prototype
directory** so its SDK pin is found. Do not add it to the original solution at this stage.

Initial environment observation: Windows Server 2022 Standard `10.0.20348`, x64; no `dotnet` or `msbuild` on PATH,
no standard dotnet SDK, Visual Studio, or Windows Kits installation directories found.
The minimal setup is a user-local official SDK ZIP plus project NuGet dependencies, rather
than a full Visual Studio installation. Server 2022 appears in the App SDK support matrix;
it is **not** evidence of the Windows 11 user experience.

`Get-AppxPackage '*WindowsAppRuntime*'` returned no registered runtime packages for the
current user before launch. The VM already had `System32\vcruntime140.dll` version
`14.00.24215.1`; do not infer a pristine clean-machine CRT test from this environment.

Official SDK archive:
`https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.403/dotnet-sdk-10.0.403-win-x64.zip`

Expected SHA-512 from the release metadata:
`7a114c318fa7f1d9822991b534b2814d0e78386ceda816b371f5141acefe741d87ccb606ede06cadac8f9ac5070e32b708a67bbd1619ff5e5a7858a27874ef48`

Setup used PowerShell `Invoke-WebRequest -Uri <official archive URL> -OutFile <local zip>`,
`Get-FileHash -Algorithm SHA512`, an equality check against the hash above, then
`Expand-Archive -LiteralPath <local zip> -DestinationPath C:\Users\Administrator\tools\dotnet`.
The archive hash matched before extraction. Wait for extraction to complete before invoking
the SDK; partially extracted reference packs are not a valid installation.

Initial restore failed with `NU1100` because the machine's NuGet configuration had no package
sources, not because official packages were unavailable. A prototype-local `NuGet.Config`
now clears inherited sources and adds only `https://api.nuget.org/v3/index.json`.
No network restriction or source bypass was involved.

Build recipe (PowerShell; SDK executable may also be on PATH):

```powershell
Set-Location C:\Users\Administrator\repos\SystemExplorer\prototypes\winui-feasibility
$dotnet = 'C:\Users\Administrator\tools\dotnet\dotnet.exe'
& $dotnet --info
& $dotnet restore SystemExplorer.WinUI.Feasibility.csproj --locked-mode
& $dotnet publish SystemExplorer.WinUI.Feasibility.csproj -c Release -r win-x64 --self-contained true --no-restore -o artifacts\verified-publish
& $dotnet format SystemExplorer.WinUI.Feasibility.csproj --verify-no-changes --no-restore --verbosity diagnostic
Start-Process .\artifacts\verified-publish\SystemExplorer.WinUI.Feasibility.exe
```

Do not copy just the EXE. Self-contained Windows App SDK deploys native libraries and
registration-free activation support beside the executable. .NET self-contained deployment
is a separate setting; setting only one is insufficient. No runtime installer/MSIX identity
is intended for this proof. Production installation, redistributables, signing, and clean-machine
validation remain separate gates. Self-contained deployment also makes runtime security
updates the application's responsibility. Trimming/single-file publishing are not enabled.

**Observed packaging failure and repair:** the initial publish succeeded but omitted the
application's generated `SystemExplorer.WinUI.Feasibility.pri`. Its executable exited with
`0xC000027B`; the XAML unhandled-exception message was `Cannot locate resource from
'ms-appx:///MainWindow.xaml'`. The build output contained that PRI and rendered the native
window successfully. `IncludeWinUIResourcesInPublish` now adds the SDK's `ProjectPriFullPath`
to `ResolvedFileToPublish` with `ProjectPriFileName` and errors if it is missing. This is a
project-level publish recipe fix, not a manual copy into one artifact directory.

Verification logs live in the ignored prototype `artifacts/` directory. SDK installation,
restore and locked restore succeeded. Format diagnostic verification reported zero files
changed and exit 0, with workspace warnings about duplicate generated XAML source entries
under `obj/Debug`; do not interpret this as warning-free analyzer coverage. There are no unit
tests in this launch proof. Runtime/rendering evidence and final publish results are recorded
below; the build alone is not the launch gate.

### Final observed result

- Release publish into a **new** `artifacts/verified-publish` directory succeeded, including
  the EXE and application PRI (2,368,536 bytes). No manual artifact copy was used.
- Launched that published EXE on Server 2022. `Get-Process` confirmed its path, title
  `SystemExplorer — WinUI feasibility`, and `Responding=True` (PID 976 in this run).
- Visually inspected the actual desktop window: native title controls, navigation, center
  empty state, explicit static-feasibility notice, and inspector all rendered.
- Tab moved focus to navigation and then the native button; Enter changed the visible
  label to `Native WinUI event received (1). No monitoring data collected.`
- `Get-AppxPackage '*WindowsAppRuntime*'` remained empty for the current user after launch.
- Full-desktop screenshot: `ss_40229aec.png`, delivered with the session report. No Windows 11,
  Narrator, DPI matrix, live telemetry, table virtualization, or provider-integration acceptance
  is claimed. This satisfies only the compiled/launched native frontend feasibility gate.

## Reference-derived design specification (proposal)

These are **proposed DIPs and semantic tokens**, estimated from the approved screenshot,
not extracted Figma values or measured color-contrast results. The reference product name
becomes SystemExplorer. Keep native title-bar affordances and Windows snapping behavior.

| Token / element | Proposed baseline |
| --- | --- |
| Typography | Segoe UI Variable with Segoe UI fallback; body 13–14, table 12–13, section title 26–28, inspector title 18–20; semibold sparingly. Respect text scaling. |
| Spacing | 4 DIP base; 8 intra-control, 12 related groups, 16 panel padding, 20–24 major sections. |
| Surfaces | Window near `#F7F8FC`, sidebar near `#EDF1F8`, cards near white; use theme resources, not literal colors, in production. |
| Strokes and corners | Neutral low-contrast 1 DIP separators; panel radius 6–8, control radius 4–6. No heavy shadows, gradients, glow, or blur. |
| Selection | Subtle blue fill around `#D5E5FC`, blue indicator around `#0067C0`; preserve readable text and a separate keyboard focus ring. |
| Layout | Left navigation 176–200; inspector 288–320; center consumes remaining width. Title/search band about 56. |
| Table | Column header 32–36; normal rows 28–32, group rows 32–36; optional comfortable density. Numeric columns right-aligned. |
| Status | Text plus restrained semantic badge; never color alone. Unknown is not Running. |
| Icons | Fluent/system symbols for UI, cached executable icons from provider metadata; placeholder for unavailable icons; never load full-size icons on every sample. |

Navigation order: Overview, Processes, Memory, CPU, GPU, Disks, Network, Services, Insights;
Settings and Help at the bottom. Prefer native `NavigationView` for the production shell;
the launch proof uses a minimal native list, not the final shell. Future destinations must
not pretend to be implemented. Global Ctrl+K search and local process search have distinct scopes.

At generous widths keep the reference's three panels. At narrower widths collapse the
navigation to an icon rail; move inspector to an explicit details pane rather than crush
columns. Keep identity and primary memory/CPU columns visible; allow user-chosen optional
columns and horizontal scrolling for the remainder. Validate actual breakpoints with
200% text/DPI scaling before adopting fixed minimums. Native window controls must not
overlap search hit targets. The feasibility screen has a fixed three-column layout only.

## Dense process hierarchy: implementation plan, not yet proven

No production table control is selected by this proof. WinUI `ListView` has useful selection,
focus, and recycling, but is **not a data grid**: Microsoft recommends a DataGrid for true
multi-column tables. A templated list cannot be called accessible table support for free.
Avoid silently importing the archived UWP Community Toolkit DataGrid into WinUI 3.

Recommended next spike: a flattened, visible-row projection backed by a virtualizing native
control. Each row has stable identity, hierarchy depth, expansion state, and cell view models;
expansion changes the projection, not nested scrollable lists. Evaluate a maintained WinUI 3
grid/tree-grid against a custom native control before committing to one. Review license,
maintenance, keyboard semantics and UI Automation support before adding any dependency.
`TreeView` provides hierarchy semantics, but column resizing/sorting and large-scale behavior
still need proof. `ItemsRepeater` offers flexible virtualization but requires selection,
keyboard behavior and automation to be designed; it is not a low-cost shortcut.

Keep a finite-height `Grid` star row as viewport, with `ItemsStackPanel` or another verified
virtualizing layout. Do not wrap the list in a vertical outer `ScrollViewer` or a height-auto
stack that measures infinite space. Share column widths between header and recycled rows;
provide accessible resize/sort controls. Sorting/grouping/filtering operate on the same snapshot
projection. Preserve stable selection, expansion and scroll position across updates and PID reuse.
Group totals require explicit metric semantics: never label summed shared working sets as
physical RAM. The screenshot's generic “Total RAM” is not a sufficient contract.

Collect on a worker path, cancel obsolete work, publish bounded batches through DispatcherQueue,
and coalesce stale snapshots rather than queue unlimited UI updates. Do not rebuild every
row on every sample. Lazy-load expensive details; selection changes cancel prior inspector
requests and reject results whose identity no longer matches. Unknown, denied, exited, stale,
partial and valid zero must be distinguishable. CPU requires an explicit sample interval and
normalization; unavailable per-process network throughput must not be invented.

Before acceptance: profile a clearly labeled synthetic high-row-count fixture separately
from real monitoring; measure realized row/container counts, allocation growth, frame/refresh
latency, startup, and idle CPU. Then exercise real process churn. No performance target or
virtualization result is claimed by this empty-state launch proof.

## Inspector and keyboard / accessibility plan

- Selection updates the inspector by stable process identity (PID plus creation-time identity),
  not by current list position. Show group vs individual-process context explicitly.
- Inspector: executable path/copy, parent identity, supported services, connection counts,
  permissions and observation freshness. Do not infer service associations or startup impact.
- Read-only inspection is primary. Future termination/suspension requires identity revalidation,
  capability checks, confirmation and protected-process safeguards; it is outside this proof.
- Tab order: global search, navigation, local search/group/sort, table, inspector. Shift+Tab
  reverses it; no focus trap. Ctrl+K global search; Ctrl+F local search; Esc clears/dismisses
  the current layer without silently discarding selection.
- Table: Up/Down, Home/End, PageUp/PageDown; Left/Right collapse/expand (or move to parent/child),
  Enter opens details; sort headers expose direction. Provide Shift+F10 context menu, accessible
  keyboard column resize, and a deliberate focus path from row to inspector. Never bind Delete
  directly to terminating a process.
- Name/icon is not the whole accessible row: expose column header associations, values/units,
  expanded/collapsed state, level, selected state and availability. A custom table must implement
  the appropriate UI Automation peers/patterns; a pile of TextBlocks does not satisfy this.
- Preserve focus and announce exits/selection changes politely; do not announce every 1-second
  metric update. Retain visible focus indicators. Respect high contrast, reduced motion and
  system text size. Test with Narrator and keyboard-only users; these are not yet acceptance-tested.

Relevant official control guidance:

- [ListView / GridView and table caveat](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/listview-and-gridview)
- [Collection virtualization](https://learn.microsoft.com/en-us/windows/apps/develop/performance/optimize-gridview-and-listview)
- [TreeView](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/tree-view)
- [Keyboard accessibility](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/keyboard-accessibility)

## Integration requirements / gates still open

1. Parent-approved provider contract and native integration proof; no production frontend edits
   should derive an ABI from this document. Require stable identity, monotonic sequence/timestamp,
   units/semantics, field availability/error state, cancellation, capabilities and ownership rules.
2. Validate real Windows 11 x64 launch, non-admin account, clean-machine deployment and runtime
   dependencies, then high-DPI/text-scale, high-contrast and Narrator behavior.
3. Prove the chosen dense hierarchy/table's virtualization and keyboard/automation semantics.
4. Validate live search, sorting, grouping, inspector races and process churn with real data.
5. Measure runtime footprint and performance before choosing framework-dependent vs self-contained
   production packaging; no release-readiness or lightweight-runtime claim follows from launch.

The prototype remains isolated and can be removed without affecting the original project.
