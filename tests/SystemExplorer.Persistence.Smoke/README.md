# Read-only persistence and driver service smoke

On Windows with the .NET 10 SDK:

```powershell
dotnet run --project tests/SystemExplorer.Persistence.Smoke -c Release
dotnet run --project tests/SystemExplorer.ServiceInspection.Smoke -c Release
```

Fixed registry byte buffers exercise raw UTF-16, MULTI_SZ (including empty items),
DWORD formatting, unsupported types and byte bounds. Injectable registry/WMI/SCM
backends exercise missing and inaccessible sources, source isolation, caps,
configuration failure, cooperative timeout, cancellation and resource disposal.
Driver enumeration buffers test malformed pointers and bounded rows.

Live checks read local registry views, local `root\subscription` COM metadata and
driver service configuration with the current token. They make no changes and do
not request credentials, elevation, privileges or remote connections. ScriptText
is never requested. Paths and strings remain raw and unexpanded; no target files
are opened or executed. Only counts and statuses are printed.

HKLM Software sources use both registry views. HKCU Winlogon and Active Setup keys
are shared keys, so use CurrentUser64 once. SYSTEM sources use LocalMachine64 once.
KnownDLLs rows are references, not suspiciousness findings. Missing keys/values
produce no rows; missing keys explicitly report `key not present`.

Bounds: 8192 bytes/value; 2048 subkeys/source; 4096 total persistence rows;
1024 WMI objects/class and 4096 characters/WMI string; separate cooperative
five-second registry/WMI budgets. Driver enumeration uses 256 KiB, 16 calls and
2048 rows, with three 8192-byte configuration calls per row under a five-second
cooperative budget. Blocking native/COM calls cannot be interrupted.

Windows Server/current-token evidence is not Windows 11 standard-user acceptance.
