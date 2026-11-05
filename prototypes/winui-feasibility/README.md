# SystemExplorer WinUI feasibility

An isolated, **static** native window proof. No monitoring data or production process UI.
See [UI_FEASIBILITY](../../docs/architecture/UI_FEASIBILITY.md) for verified setup, source links,
design proposal, results and remaining gates.

From this directory with .NET SDK 10.0.403 installed:

```powershell
dotnet restore SystemExplorer.WinUI.Feasibility.csproj --locked-mode
dotnet publish SystemExplorer.WinUI.Feasibility.csproj -c Release -r win-x64 --self-contained true --no-restore -o artifacts\verified-publish
dotnet format SystemExplorer.WinUI.Feasibility.csproj --verify-no-changes --no-restore --verbosity diagnostic
Start-Process .\artifacts\verified-publish\SystemExplorer.WinUI.Feasibility.exe
```

Distribute the whole publish directory, not only the EXE. The project explicitly publishes
the generated PRI resource file; omitting it can compile successfully but crash at runtime.
No Visual Studio or machine-wide App SDK runtime installation was needed for this proof.
The manifest requests no elevation. Production support remains Windows 11 x64; this proof
was compiled and exercised on Windows Server 2022.
