# SystemExplorer native application

This isolated .NET 10 / WinUI 3 application uses documented Windows APIs for local,
read-only diagnostics. The original System Informer solution remains separate and
unchanged; its driver and online plugins are not loaded by this frontend.

## Build and checks (Windows x64)

Install official .NET SDK **10.0.403**. The build restores pinned NuGet packages;
the application does not require those network endpoints at runtime.

From the repository root, with `dotnet` on PATH:

```powershell
dotnet run --project tests/SystemExplorer.Core.Tests -c Release
dotnet run --project tests/SystemExplorer.Windows.Smoke -c Release
dotnet run --project tests/SystemExplorer.Relationships.Smoke -c Release
./tools/Build-SystemExplorer.ps1 -OutputDirectory C:\builds\SystemExplorer-test
C:\builds\SystemExplorer-test\SystemExplorer.App.exe
```

Choose an empty output directory. Keep the **whole** publish directory, including
the application PRI, .NET runtime and Windows App SDK libraries. This is an unsigned
portable test build, not an installer or public release. The manifest requests
`asInvoker`; do not elevate simply to make unavailable data disappear.

## Using the app

- Processes: search, application grouping, expansion, sortable metrics and inspector.
- Memory: physical totals, commit, kernel/cache categories and private attribution.
- CPU/Disks: measured rates; process I/O is separate from physical disk throughput.
- Network/Services: observed tables with creation-time-validated owner associations.
- Startup: Run/RunOnce keys and startup folders; no disable action or impact guess.
- Overview/Insights: bounded history and deterministic explanations, not cloud AI.
- Settings: light, dark and system theme; pause freezes live refresh.

Use Ctrl+K for search; values shown as `—` are unavailable, never zero. Private
commit is not physical RAM; working sets include shared resident pages. Growth
is a trend, not a memory-leak diagnosis. Publisher checks use cached local trust
data and do not claim a fresh online revocation check.

## Current limits

Private working set is independently nullable and reported only when the documented
PSAPI EX2 API returns complete supported data. It is distinct from private commit
and total working set. Service base start types are best-effort configuration
observations; denied, unavailable or budget-skipped values remain unknown. The
enrichment budget is checked between synchronous calls, not a hard timeout.

GPU utilization, standby memory, command lines and catalog signatures are not
implemented. Delayed/trigger service startup behavior is not inferred.
There are no termination, suspension, startup-modification or driver-install actions.
Windows App SDK dependencies follow the pinned app lockfile; WebView features are
not initialized by this application. Dependency footprint and
exact redistributed notices remain release-review gates, not blanket MIT claims.

See [project status](../docs/development/PROJECT_STATUS.md),
[quality gates](../docs/testing/QUALITY_GATES.md) and
[Windows acceptance checklist](../docs/testing/WINDOWS-ACCEPTANCE-CHECKLIST.md).
Windows Server 2022 evidence must not be represented as Windows 11 standard-user
acceptance. No upstream merge or public release is implied by a successful build.
