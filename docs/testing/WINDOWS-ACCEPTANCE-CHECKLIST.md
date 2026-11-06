# Windows acceptance checklist

Run against the published production executable, not the empty feasibility proof.
Record revision, binary SHA256, OS build, token integrity, logical processors,
screen scale and workload. The target is Windows 11 x64 under a standard user.
Windows Server 2022 measurements are supporting evidence only.

## Live UI

- Confirm a real process started after app launch appears with its PID and path.
- Filter by name and PID, sort private commit/CPU/name/PID, and clear filters.
- Expand a multi-process group; compare its exact private-commit sum to members.
- Select a member and check identity, parent, memory, services and endpoint count.
- Pause and resume; verify frozen measurements and resumed live changes.
- Visit Overview, Memory, CPU, GPU, Disks, Network, Services, Startup and Insights.
- Distinguish unsupported GPU/private-working-set/standby data from zero usage.
- Use Ctrl+K, Tab, arrows and Enter; verify visible focus and readable labels.
- Resize at 100% and high DPI; inspect light/dark/system themes and hidden inspector access.
- Verify icons, tray summary/activation, safe file-location opening and shutdown.
- Capture full-window evidence and compare density/layout with the supplied reference.

## Provider correctness

Run all three executable check projects in Release; failure is a nonzero exit.
Loopback endpoint fixtures must match both exact local port and creation identity.
Synthetic malformed-buffer cases must be labeled separately from live observations.
Process creation/exit churn must not retain history or ownership under a reused PID.
Unknown/denied fields must remain unavailable. Do not seek elevation to hide them.

## Resource observation

After a warmup, leave a stable live Processes view and run:

```powershell
./tools/Measure-SystemExplorer.ps1 -TargetProcessId <PID> -DurationSeconds 600 -IntervalSeconds 5 -OutputDirectory <outside-repo-directory>
```

The script records monotonic-time, machine-normalized process CPU intervals,
private commit, working set (including shared pages), handle count and thread count.
It pins creation time and fails if the target exits. Initial CPU is unavailable.
Report actual duration, workload and data rather than calling a short run a soak.
Memory/handle deltas alone are not evidence of a leak; repeat across steady phases
and include profiler/handle attribution before diagnosing one. Longer Windows 11
soak and high-process-count coverage remain separate gates from this bounded run.

## Distribution

Test the copied portable directory with all PRI/runtime resources intact. Do not
claim that an executable copied alone is portable. Record archive and executable
hashes, dependency versions and included notices. No signing or public-release
claim is permitted without separate evidence and approval.
