# Read-only scheduled-task inventory smoke

Use .NET **10.0.403** on Windows (see `src/README.md`):

```powershell
dotnet run --project tests/SystemExplorer.ScheduledTasks.Smoke -c Release
dotnet build src/SystemExplorer.Relationships -c Release -warnaserror
```

The zero-package executable covers recursive inventory, hidden-task flags, local
connection, denied/failed reads, empty inventories, limits, cancellation, cooperative
timeout and disposal using a narrow allowlisted backend seam. The native check only
enumerates existing local tasks under the current token. Empty, denied, partial,
truncated, timed-out and unavailable outcomes are explicit, not invented success.
It creates, starts, stops, edits and deletes no tasks and logs only counts/status.

`WindowsScheduledTaskProvider` calls `Schedule.Service.Connect()` with no arguments,
opens `GetFolder("\\")`, enumerates `GetTasks(1)` (including hidden tasks) and
recurses through `GetFolders(0)`. Reads are restricted to name, path, state, enabled,
definition action types/ExecAction.Path and trigger types. COM handlers are labeled
only, never activated. Unknown types retain their numeric codes. No arguments,
dates, XML, security descriptors, credentials, remote lookup, file resolution,
environment expansion or target execution is performed. Paths may be sensitive
local metadata. This is not a list exclusively of logon/startup tasks.

Limits: 1000 task rows, 256 attempted folders including root, depth 16 below root,
32 actions/triggers each and 4096 UTF-16 characters per output field. Root denial
is `AccessDenied`; inaccessible child folders/items make the inventory incomplete.
Other bounds yield `Truncated`; time budget exhaustion yields `TimedOut`. The
three-second budget is checked between COM operations; it cannot interrupt a
blocking native call. Native collection/BSTR allocation itself is controlled by
Windows, not a hard memory bound supplied to COM. Enumeration is not atomic and
Windows may omit entries inaccessible to the current token.

Calls are serialized per provider, run off the caller context, and every acquired
COM wrapper has a scoped release. There is no COM foreach enumerator or chained
property access retaining hidden RCWs. Cancellation propagates only after cleanup.
The dynamic COM adapter relies on the application's existing `PublishTrimmed=false`;
it introduces no packages or changes to application publishing.

Official references:
- [TaskService.Connect](https://learn.microsoft.com/en-us/windows/win32/taskschd/taskservice-connect)
- [RegisteredTask](https://learn.microsoft.com/en-us/windows/win32/taskschd/registeredtask)
- [ExecAction](https://learn.microsoft.com/en-us/windows/win32/taskschd/execaction)
- [GetTasks](https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-itaskfolder-gettasks)
- [GetFolders](https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-itaskfolder-getfolders)

Windows Server evidence is not Windows 11 standard-user acceptance. No UI or full
application publish is part of this focused check.
