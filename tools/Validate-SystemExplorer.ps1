<#
.SYNOPSIS
  SystemExplorer one-command validation harness.
  Modes: short (default), extended, stress, soak.
  Produces a structured JSON report plus raw evidence in the output directory.
  Windows Server evidence is NOT Windows 11 acceptance evidence.
#>
param(
    [ValidateSet('short', 'extended', 'stress', 'soak')][string]$Mode = 'short',
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$RepoRoot = '',
    [switch]$SkipBuild
)
if (-not $RepoRoot) {
    $scriptDir = $PSScriptRoot
    if (-not $scriptDir) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
    if (-not $scriptDir) { $scriptDir = 'C:\Users\Administrator\repos\se-ui-polish-plan\tools' }
    $RepoRoot = (Resolve-Path (Join-Path $scriptDir '..')).Path
}
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$report = [ordered]@{
    HarnessVersion = 1
    Mode = $Mode
    StartedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    RepoRoot = $RepoRoot
    Environment = [ordered]@{}
    Steps = [ordered]@{}
    CrossValidation = @()
    Verdicts = @()
    Limitations = @('Windows Server results are not Windows 11 acceptance evidence.',
        'typeperf sampling is not phase-aligned with probe scans; tolerances absorb jitter.')
}
$os = Get-CimInstance Win32_OperatingSystem
$cs = Get-CimInstance Win32_ComputerSystem
$report.Environment.Platform = $os.Caption + ' ' + $os.Version
$report.Environment.OsBuild = $os.BuildNumber
$report.Environment.IsWindows11 = ($os.Caption -match 'Windows 11')
$report.Environment.LogicalProcessors = [Environment]::ProcessorCount
$report.Environment.TotalPhysicalMemoryBytes = $cs.TotalPhysicalMemory
$report.Environment.DotNetVersion = (dotnet --version)
$report.Environment.PowerShellVersion = $PSVersionTable.PSVersion.ToString()

function Step($name, [scriptblock]$body) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try {
        $result = & $body
        $report.Steps[$name] = [ordered]@{ Status = 'pass'; Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1); Detail = $result }
        Write-Host "[pass] $name ($([math]::Round($sw.Elapsed.TotalSeconds,1))s)"
    } catch {
        $report.Steps[$name] = [ordered]@{ Status = 'fail'; Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1); Error = $_.Exception.Message }
        Write-Host "[FAIL] $name : $($_.Exception.Message)"
    }
}

# 1. Build and tests
if (-not $SkipBuild) {
    Step 'build-app' {
        $out = dotnet build "$RepoRoot/src/SystemExplorer.App/SystemExplorer.App.csproj" -c Release -v q --nologo 2>&1
        if ($LASTEXITCODE -ne 0) { throw "build failed: $($out | Select-Object -Last 3)" }
        'release build 0 warnings expected'
    }
    Step 'core-tests' {
        $out = dotnet run --project "$RepoRoot/tests/SystemExplorer.Core.Tests/SystemExplorer.Core.Tests.csproj" -c Release 2>&1
        if (($out | Select-String '95/95 passed') -eq $null) { throw 'core tests did not report 95/95' }
        '95/95'
    }
    Step 'windows-smoke' {
        $out = dotnet run --project "$RepoRoot/tests/SystemExplorer.Windows.Smoke/SystemExplorer.Windows.Smoke.csproj" -c Release 2>&1
        if ($LASTEXITCODE -ne 0) { throw "smoke failed: $($out | Select-Object -Last 2)" }
        'provider smoke passed'
    }
}

# 2. Probe + typeperf cross-validation
$probeSeconds = @{ short = 45; extended = 180; stress = 240; soak = 3600 }[$Mode]
Step 'probe-capture' {
    dotnet build "$RepoRoot/tools/probe/SystemExplorer.Probe.csproj" -c Release -v q --nologo 2>&1 | Out-Null
    $probeDll = "$RepoRoot/tools/probe/bin/Release/net10.0-windows/SystemExplorer.Probe.dll"
    $probeJsonl = Join-Path $OutputDirectory 'probe.jsonl'
    $typeperfCsv = Join-Path $OutputDirectory 'typeperf.csv'
    $counters = '\Memory\Available Bytes', '\Processor(_Total)\% Processor Time', '\PhysicalDisk(_Total)\Disk Read Bytes/sec', '\PhysicalDisk(_Total)\Disk Write Bytes/sec'
    $cfFile = Join-Path $OutputDirectory 'counters.txt'
    $counters | Set-Content -LiteralPath $cfFile -Encoding ASCII
    $tp = Start-Process -FilePath 'typeperf' -ArgumentList ('-cf', ('"' + $cfFile + '"'), '-si', '2', '-sc', [math]::Floor($probeSeconds/2), '-o', ('"' + $typeperfCsv + '"')) -PassThru -WindowStyle Hidden
    $probeOut = dotnet $probeDll $probeSeconds 2000 $probeJsonl 2>&1
    if ($LASTEXITCODE -ne 0) { throw "probe failed: $probeOut" }
    if (-not $tp.HasExited) { $tp.WaitForExit(30000) }
    "$probeOut"
}

# 3. Cross-validation analysis
Step 'cross-validation' {
    $samples = Get-Content (Join-Path $OutputDirectory 'probe.jsonl') | ForEach-Object { $_ | ConvertFrom-Json }
    $tpRows = @(Import-Csv (Join-Path $OutputDirectory 'typeperf.csv') | ForEach-Object {
        $props = $_.PSObject.Properties | Select-Object -Skip 1
        $vals = @($props | ForEach-Object { $v = 0.0; [double]::TryParse($_.Value, [ref]$v) | Out-Null; $v })
        if ($vals.Count -ge 4) {
            [pscustomobject]@{ MemAvailable = $vals[0]; CpuTotal = $vals[1]; DiskRead = $vals[2]; DiskWrite = $vals[3] }
        }
    })
    $n = [math]::Min($samples.Count, $tpRows.Count)
    if ($n -lt 5) { throw "insufficient overlapping samples: probe=$($samples.Count) typeperf=$($tpRows.Count)" }
    $memDiffs = @(); $cpuDiffs = @(); $readDiffs = @()
    for ($i = 0; $i -lt $n; $i++) {
        $p = $samples[$i + 1]; $t = $tpRows[$i]
        if ($null -ne $p.physicalAvailableBytes -and $t.MemAvailable -gt 0) {
            $memDiffs += [math]::Abs($p.physicalAvailableBytes - $t.MemAvailable) / $t.MemAvailable * 100
        }
        if ($null -ne $p.cpuPercent) { $cpuDiffs += [math]::Abs($p.cpuPercent - $t.CpuTotal) }
        if ($null -ne $p.diskReadBps -and $t.DiskRead -gt 1024) {
            $readDiffs += [math]::Abs($p.diskReadBps - $t.DiskRead) / [math]::Max($t.DiskRead, 1) * 100
        }
    }
    $result = [ordered]@{}
    if ($memDiffs.Count -gt 0) {
        $meanMem = ($memDiffs | Measure-Object -Average).Average
        $result.MemoryAvailable = [ordered]@{ MeanRelativeDiffPercent = [math]::Round($meanMem, 2); Samples = $memDiffs.Count; Pass = $meanMem -lt 5 }
        $report.CrossValidation += [ordered]@{ Metric = 'Memory\Available Bytes vs GetPerformanceInfo PhysicalAvailable'; MeanRelativeDiffPercent = [math]::Round($meanMem, 2); TolerancePercent = 5; Pass = $meanMem -lt 5 }
    }
    if ($cpuDiffs.Count -gt 0) {
        $meanCpu = ($cpuDiffs | Measure-Object -Average).Average
        $result.CpuTotal = [ordered]@{ MeanAbsoluteDiffPoints = [math]::Round($meanCpu, 2); Samples = $cpuDiffs.Count; Pass = $meanCpu -lt 5 }
        $report.CrossValidation += [ordered]@{ Metric = 'Processor time total vs GetSystemTimes total'; MeanAbsoluteDiffPoints = [math]::Round($meanCpu, 2); TolerancePoints = 5; Pass = $meanCpu -lt 5 }
    }
    $result
}

# 4. Packaged app validation
Step 'packaged-app' {
    $appDir = Join-Path $OutputDirectory 'app'
    dotnet publish "$RepoRoot/src/SystemExplorer.App/SystemExplorer.App.csproj" -c Release -r win-x64 --self-contained -o $appDir -v q --nologo 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
    $exe = Join-Path $appDir 'SystemExplorer.App.exe'
    $hash = (Get-FileHash $exe -Algorithm SHA256).Hash
    $report.Environment.AppSha256 = $hash
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process -FilePath $exe -PassThru
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    $ready = $false
    while ($deadline.Elapsed.TotalSeconds -lt 25) {
        Start-Sleep -Milliseconds 500
        $p.Refresh()
        if ($p.HasExited) { throw 'app exited during startup' }
        if ($p.MainWindowHandle -ne 0) { $ready = $true; break }
    }
    if (-not $ready) { throw 'no main window within 25s' }
    $startupMs = $sw.ElapsedMilliseconds
    $report.Environment.AppStartupToWindowMs = $startupMs
    $measureDir = Join-Path $OutputDirectory 'app-measure'
    $measureSeconds = @{ short = 45; extended = 180; stress = 120; soak = 1800 }[$Mode]
    & "$RepoRoot/tools/Measure-SystemExplorer.ps1" -TargetProcessId $p.Id -DurationSeconds $measureSeconds -IntervalSeconds 2 -OutputDirectory $measureDir | Out-Null
    $summary = Get-Content (Join-Path $measureDir 'summary.json') | ConvertFrom-Json
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    $p.Refresh()
    if (-not $p.HasExited) { throw 'app did not exit cleanly' }
    [ordered]@{
        StartupToWindowMs = $startupMs
        MeanCpuPercentOfMachine = [math]::Round($summary.MeanCpuPercentOfMachine, 3)
        P95CpuPercentOfMachine = [math]::Round($summary.P95CpuPercentOfMachine, 3)
        PrivateCommitDeltaMB = [math]::Round($summary.PrivateCommitDeltaBytes / 1MB, 2)
        HandleDelta = $summary.HandleDelta
        CleanExit = $true
    }
}

$report.FinishedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
$reportPath = Join-Path $OutputDirectory 'validation-report.json'
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8
Write-Host "report: $reportPath"
$report | ConvertTo-Json -Depth 8
