param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\SystemExplorer-win-x64'))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $output) -and @(Get-ChildItem -LiteralPath $output -Force).Count -gt 0) {
    throw 'Choose an empty output directory to avoid mixing old and new portable binaries.'
}
$dotnet = if (Get-Command dotnet -ErrorAction SilentlyContinue) { 'dotnet' } else { Join-Path $env:USERPROFILE '.dotnet\dotnet.exe' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$project = Join-Path $root 'src\SystemExplorer.App\SystemExplorer.App.csproj'
& $dotnet restore $project --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
& $dotnet publish $project -c Release --no-restore -o $output -warnaserror
if ($LASTEXITCODE -ne 0) { throw 'Production publish failed.' }
if (!(Test-Path -LiteralPath (Join-Path $output 'SystemExplorer.App.exe')) -or
    !(Test-Path -LiteralPath (Join-Path $output 'SystemExplorer.App.pri'))) {
    throw 'Portable output is incomplete: executable or application PRI is missing.'
}
Copy-Item -LiteralPath (Join-Path $root 'LICENSE.txt') -Destination $output
Copy-Item -LiteralPath (Join-Path $root 'COPYRIGHT.txt') -Destination $output
Copy-Item -LiteralPath (Join-Path $root 'src\SystemExplorer.App\packages.lock.json') -Destination (Join-Path $output 'dependency-versions.json')
@'
SystemExplorer unsigned test build

Run SystemExplorer.App.exe from this complete extracted directory. Do not copy
only the executable: .NET, Windows App SDK and PRI resources are required.
Read-only local monitoring; no driver or automatic elevation. This is not a signed
public release or a claim of completed Windows 11 standard-user acceptance.

The original System Informer source tree is preserved separately. Its advanced
features are not all exposed here. LICENSE.txt/COPYRIGHT.txt retain upstream notices;
they do not imply that every original-tree component is linked into this application.
dependency-versions.json records managed package versions. Runtime notices shipped
by the SDK remain in this directory. See the repository release-readiness report
for supported features, missing measurements and remaining license review gates.
'@ | Set-Content -LiteralPath (Join-Path $output 'README-TEST-BUILD.txt') -Encoding UTF8
$manifest = Get-ChildItem -LiteralPath $output -Recurse -File | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($output.TrimEnd('\').Length + 1)
    "{0}  {1}" -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash, $relative
}
$manifest | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding UTF8
Write-Output "Unsigned portable directory: $output"
