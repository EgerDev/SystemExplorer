# Read-only service inspection smoke

Use the .NET **10.0.403** SDK from `src/README.md` on Windows:

```powershell
dotnet run --project tests/SystemExplorer.ServiceInspection.Smoke -c Release
dotnet build src/SystemExplorer.Relationships -c Release -warnaserror
```

This zero-package executable covers synthetic bounded UTF-16 and MULTI_SZ buffers,
malformed pointers/terminators, dependency truncation, native failure classification,
resize caps, cooperative timeout, cancellation and handle cleanup. It verifies that
only case-insensitive `desktop.ini` folder entries are excluded: registry names and
unknown folder entries remain unchanged. Folder entries remain unresolved inventory,
not guaranteed executable startup commands.

The real local smoke enumerates up to 32 local service names and reads one available
configuration without requiring a particular service to be installed or configured.
Enumeration uses the existing read-only enumeration API; the new provider itself
requests only `SC_MANAGER_CONNECT` and `SERVICE_QUERY_CONFIG`. No services/processes
are created, changed, started or stopped. Output contains counts/status only.

`WindowsServiceInspectionProvider` is on demand, serialized per instance and bounded
to 8192 bytes, three total query calls (including the sizing probe), 256 returned
dependencies and a cooperative three-second budget. Cancellation/time checks cannot
interrupt a blocking native call and are not hard deadlines. `CapturedAt` is capture
start, not an atomic snapshot. Invalid fields produce `Partial`; oversized/changing
configuration or excess dependencies produce `Truncated`. Missing/deleted services
use Core `InspectionStatus.Exited` with explicit missing/deleted detail; this is not
a process-lifetime assertion. No PID association or ownership inference is made.

All strings remain unexpanded observed local values. A dependency beginning with `+`
names a load-order group, not a service link. Base start type is not delayed/trigger
behavior. For driver services, the SCM account field may instead be a driver object
name. Binary path and account can be sensitive local metadata; no credentials are
collected or logged. No target binary/shortcut is opened, resolved or executed.

Official API contracts:
- [OpenSCManagerW](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-openscmanagerw)
- [OpenServiceW](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-openservicew)
- [QueryServiceConfigW](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-queryserviceconfigw)
- [QUERY_SERVICE_CONFIGW](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-query_service_configw)

Windows Server evidence does not establish Windows 11 standard-user acceptance.
