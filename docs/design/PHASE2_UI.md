# Phase 2 native Processes design

## Scope and reference

The approved Processes screenshot is the visual reference, not a telemetry specification. This implementation keeps SystemExplorer branding, the native WinUI 3/.NET architecture, local providers and upstream notices. It does not reproduce the reference's unsupported suspended counts, green Running states, startup impact, process network throughput, or a misleading Total RAM column. No End Task or Suspend action is added.

## Reusable presentation system

- `Themes/DesignTokens.xaml` supplies shared page, header and group surfaces, theme-aware cards, secondary copy, section labels, numeric/data cells, headers and read-only actions. Light, dark/default and high-contrast resource dictionaries use native foreground, focus, selection and border resources. Diagnostic cards use the same resources as Processes.
- `Controls/SummaryCard` composes a compact icon, label and value. The four cards remain observed process count, inferred application groups, physical memory used and system CPU.
- Segoe typography follows native control defaults: 28px page headings, 20px inspector identities, 24px summary values, 13px data and 14px body copy. Data rows retain a 32px minimum height and use semibold group entries, subtle separators and indented children. Executable icons still come from the existing local icon loader.
- A 192px expanded navigation pane switches to compact navigation at narrower widths. The 320px inspector is replaced by the existing details dialog when the Processes content area is below 1000px. The visible Details action supplements row Enter/double-click discovery. The dialog follows the requested theme and includes identity, provenance, executable, command line, ownership, parent, memory and relationships.
- Inspector cards separate identity/provenance, executable, ownership, memory, relationships and read-only actions. Group selection explicitly describes the selected representative member; aggregates remain in the table.

## Column resizing and interaction

`Controls/ProcessColumns` is the single width model shared by column headers and every realized process row. Each column has a finite minimum and maximum. `Controls/ColumnResizeHandle` supports pointer drag plus keyboard Left/Right (16 logical pixels), Home (minimum) and End (maximum). Handles have individual UI Automation names and keyboard help. Width changes update the existing rows without rebuilding the monitoring model. Widths are session-local, not persisted.

The table scrolls horizontally as a single header-and-rows surface rather than dropping essential fields. Vertical scrolling remains in the ListView. Numeric values align to the right. Existing row container automation names, stable process identity, Ctrl+K, grouping, sorting, arrow/Enter expansion, pause/resume and empty-state behavior are preserved.

Neutral Observed badges mean presence in the sample, not a verified running/suspended state. Missing fields remain unavailable rather than becoming zero. Group provenance and private-commit caveats remain accessible in row tooltips, the table footer and inspector.

## Native validation

The complete unsigned production publish was exercised with real local provider data, not a mock. Both executable and sibling PRI were verified before launch. The final `SystemExplorer-ui-build02` contains the same application source as this change; build01 supplied full regression coverage and build02 rechecked the final sort-label/mapping change plus refreshed wide/narrow evidence. The final app was left running for follow-up inspection.

### Build and source checks

SDK 10.0.403 was installed into the user's `.dotnet` directory because it was not initially on PATH. Commands ran from the repository root with that directory on PATH:

```powershell
dotnet format src/SystemExplorer.App/SystemExplorer.App.csproj --verify-no-changes --no-restore --include src/SystemExplorer.App/MainWindow.xaml.cs src/SystemExplorer.App/ShellViewModel.cs src/SystemExplorer.App/Controls/SummaryCard.xaml.cs src/SystemExplorer.App/Controls/ProcessColumns.cs src/SystemExplorer.App/Controls/ColumnResizeHandle.cs
.\tools\Build-SystemExplorer.ps1 -OutputDirectory C:\Users\Administrator\SystemExplorer-ui-build02
git diff --check
```

All three exited **0**. Format emitted a workspace-loading warning but no formatting violations. The build script performed locked restore and warnings-as-errors Release publish, including executable, PRI and dependency notices. Initial iterations exposed an incomplete XAML transformation; that was repaired before successful build01/build02. No project, package lock, provider or Core file changed, and no CI bypass was used.

### Rendered comparison and evidence

| Approved reference intent | Observed native result |
| --- | --- |
| Restrained light surfaces and readable hierarchy | Shared bordered cards, 28px heading, compact 13px aligned data and an expanded 192px navigation pane. Native window chrome is retained. |
| Differentiated groups with indented member rows | Real Chrome and Docker groups render semibold with subtle theme-aware shading; child icons/names indent consistently. Selected member identity remains in the inspector across refresh. |
| Summary cards above the table | Four real values render in reusable icon/label/value cards. Unsupported reference statistics are intentionally absent. |
| Right-side identity and details | 320px inspector separates provenance, executable, owner/parent, memory and relationships. It is more text-heavy and scrolls vertically; it has no large identity icon. |
| Legible table and interaction states | Numeric values align right, neutral badges do not imply Running, native hover/focus states remain. Selection is subtler gray than the reference's blue. |
| Responsive display | At 1000×900, compact navigation and a Details/Enter dialog replace the side inspector. Horizontal scrolling reaches PID rather than hiding columns. |
| Dark-theme consistency | Processes, summary cards, inspector and Memory diagnostic cards use the shared native foreground/surface resources. The OS titlebar remains white on this Server VM. |

| Final wide light | Final wide dark |
| --- | --- |
| ![Native expanded group and selected inspector, light](https://app.devin.ai/attachments/d8839364-5157-4cde-93e8-9dc185074c39/build02-wide-light.png) | ![Native expanded group and selected inspector, dark](https://app.devin.ai/attachments/8b89ed83-36c5-4aee-a2b0-a4411c0395c4/build02-wide-dark.png) |

| Final narrow window | Final narrow details |
| --- | --- |
| ![Native narrow process view](https://app.devin.ai/attachments/419c17a8-0d6e-4c60-a8c5-ef9c774083f2/build02-narrow.png) | ![Native narrow details dialog](https://app.devin.ai/attachments/ed4cbaee-f873-44ad-97af-1f02666e5e7f/build02-narrow-details.png) |

[Native test report and comparison](https://app.devin.ai/attachments/fe5d8f30-8ab9-4293-a090-454c74966efe/SystemExplorer-native-design-validation.md) · [Final-build annotated recording](https://app.devin.ai/sessions/756c458b159142f49bf36baf39e75ab7?testRecording=e3456e6e-8392-4265-936b-d472398cca19)

### Behavioral checks

- Passed on build01: chevron and row Left/Right/Enter expansion; stable selected process identity/path/UIA container name across refresh; Ctrl+K and synchronized search; no-match empty state and clearing; grouping and Name sort/reverse; pause/resume; pointer column drag; keyboard Right/Left, Home minimum 180px and End maximum 600px for Name; shared header/row alignment; horizontal PID reachability and vertical scrolling; light/dark Processes and dark Memory cards; narrow Enter/Details dialog.
- Passed on final build02: initial Private commit order; Name and Private commit header/dropdown synchronization; CPU and Private commit dropdown order; expanded selected group in light/dark; narrow inspector collapse and matching member-details dialog. Prior full regression was not repeated for this label/index-mapping-only delta.
- Build02 exposed a native Ctrl+K tooltip away from the search box after restoration. The follow-up disables the root accelerator's automatic tooltip while retaining its shortcut, visible search hint and UIA accelerator key; focused combined-v2 verification passed without the floating tooltip. Group/sort selectors still have accessibility names but no visible Group by/Sort by captions.
- The follow-up adds Copy PID for the observed member in the wide inspector and the PID captured by the narrow Details dialog. No selection disables copying; grouped rows do not imply copying every member. Clipboard writes use the shared error-reporting helper; focused combined-v2 Copy PID verification passed.

Focused combined-v2 checks at `7d45e7fb412a6ad576f767275b4ba2c66b82e4a4` (Windows Server 2022, high-integrity Administrator, 96 DPI) confirmed copying stayed disabled with no selection and copied individual, group-representative, child and protected-process PIDs even when the executable path was unavailable; the narrow dialog remained open after copy, and Escape/Close worked. Ctrl+K/Tab/search, theme/resize and tray regressions passed without a floating tooltip. Forced clipboard-error and PID-reuse/lifetime faults remain unverified; this was focused follow-up coverage, not a full UI-page/suite rerun. See [PR #5](https://github.com/EgerDev/SystemExplorer/pull/5) for the native evidence.

The available VM is Windows Server 2022 build 20348 with an inherited high-integrity Administrator token. It is not Windows 11 standard-user acceptance. Windows 11, non-elevated operation, Narrator, clean-machine portability and 100/125/150/200% DPI remain independent acceptance gates unless explicitly verified elsewhere.

## Intentional differences from the reference

- SystemExplorer branding and truthful local measurements replace Pulse Monitor sample labels and invented values.
- Native window chrome and WinUI navigation remain, rather than a custom titlebar/window implementation.
- Private commit replaces Total RAM; process I/O is not labeled disk-only or network bandwidth.
- The real inspector contains permission, lifetime and unsupported-provider explanations and may need vertical scrolling.
- Horizontal scrolling protects readable numeric fields at smaller widths; no essential columns are silently hidden.
- There are no destructive actions. Publisher verification, copying a PID or local path and opening a local file location remain read-only actions.

## Documentation consulted

- [Microsoft WinUI Gallery: XAML resource examples](https://github.com/microsoft/WinUI-Gallery/tree/main/WinUIGallery/Samples/XamlResources), retrieved via Context7: theme resources re-evaluate when the requested theme changes.
- [WinUI resource/property research via DeepWiki](https://deepwiki.com/search/for-winui-3-what-are-the-suppo_56e57231-a5e6-4179-acdb-4e17ef736ecf). Native rendering and scrolling tests, rather than the generated research answer, determine acceptance.
- No actual Figma design source was supplied; the attached screenshot is authoritative. No cloud benchmark infrastructure or UI dependencies were added.
