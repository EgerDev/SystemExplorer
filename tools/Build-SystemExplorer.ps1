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
$assets = Get-Content -LiteralPath (Join-Path $root 'src\SystemExplorer.App\obj\project.assets.json') -Raw | ConvertFrom-Json
$packages = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($library in $assets.libraries.PSObject.Properties) {
    if ($library.Value.type -eq 'package') { [void]$packages.Add($library.Value.path) }
}
foreach ($framework in $assets.project.frameworks.PSObject.Properties) {
    foreach ($dependency in $framework.Value.downloadDependencies) {
        if ($dependency.version -notmatch '^\[([^,\]]+),\s*([^\]]+)\]$' -or $Matches[1] -ne $Matches[2]) {
            throw "Expected an exact restored runtime-pack version: $($dependency.name)"
        }
        [void]$packages.Add(($dependency.name + '/' + $Matches[1]).ToLowerInvariant())
    }
}
foreach ($package in ($packages | Sort-Object)) {
    $packagePath = $null
    foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
        $candidate = Join-Path $folder $package
        if (Test-Path -LiteralPath $candidate -PathType Container) { $packagePath = $candidate; break }
    }
    if (!$packagePath) { throw "Restored package not found for notice collection: $package" }
    $noticeFiles = @(Get-ChildItem -LiteralPath $packagePath -Recurse -File | Where-Object {
        $_.Name -match '(?i)(license|notice|copyright)' -or $_.Extension -eq '.nuspec'
    })
    if (!$noticeFiles.Count) { throw "No license metadata found for $package" }
    foreach ($file in $noticeFiles) {
        $relative = $file.FullName.Substring($packagePath.TrimEnd('\', '/').Length + 1)
        $destination = Join-Path (Join-Path (Join-Path $output 'licenses') $package) $relative
        [void](New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent))
        Copy-Item -LiteralPath $file.FullName -Destination $destination
    }
}
$revision = & git -C $root rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify source revision.' }
$dirty = & git -C $root status --porcelain --untracked-files=normal
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify source worktree state.' }
[ordered]@{
    SourceRevision = $revision
    SourceDirty = [bool]$dirty
    DotnetSdk = (& $dotnet --version)
    Runtime = (Get-Content -LiteralPath (Join-Path $output 'SystemExplorer.App.runtimeconfig.json') -Raw | ConvertFrom-Json).runtimeOptions
    UnsignedTestBuild = $true
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'BUILD-METADATA.json') -Encoding UTF8
@'
SystemExplorer unsigned test build

Run SystemExplorer.App.exe from this complete extracted directory. Do not copy
only the executable: .NET, Windows App SDK and PRI resources are required.
Read-only local monitoring; no driver or automatic elevation. This is not a signed
public release or a claim of completed Windows 11 standard-user acceptance.

The original System Informer source tree is preserved separately. Its advanced
features are not all exposed here. LICENSE.txt/COPYRIGHT.txt retain upstream notices;
they do not imply that every original-tree component is linked into this application.
dependency-versions.json records managed package versions. licenses/ preserves
package license/notice files and NuGet metadata, including build-only dependencies
and downloaded runtime packs; this inventory does not assert every package is linked.
BUILD-METADATA.json identifies the source revision and whether it was dirty.
See src/README.md and docs/testing/QUALITY_GATES.md in the source repository for
supported features, missing measurements and remaining license review gates.
'@ | Set-Content -LiteralPath (Join-Path $output 'README-TEST-BUILD.txt') -Encoding UTF8
$manifest = Get-ChildItem -LiteralPath $output -Recurse -File | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($output.TrimEnd('\').Length + 1)
    "{0}  {1}" -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash, $relative
}
$manifest | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding UTF8
Write-Output "Unsigned portable directory: $output"
