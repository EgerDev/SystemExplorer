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
        'Probe and typeperf rows are paired by nearest timestamp within one second; their sampling intervals remain approximate, not phase-aligned.')
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

function Get-MatchedSamples($ProbeSamples, $CounterRows, [timespan]$Tolerance = [timespan]::FromSeconds(1)) {
    $pairs = New-Object 'System.Collections.Generic.List[object]'
    $counterIndex = 0
    foreach ($sample in $ProbeSamples) {
        $probeAt = [DateTimeOffset]::Parse([string]$sample.atUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal)
        while ($counterIndex -lt $CounterRows.Count -and ($probeAt - $CounterRows[$counterIndex].CapturedAt) -gt $Tolerance) { $counterIndex++ }
        $bestIndex = -1
        $bestMilliseconds = [double]::PositiveInfinity
        for ($i = $counterIndex; $i -lt $CounterRows.Count; $i++) {
            $delta = $CounterRows[$i].CapturedAt - $probeAt
            if ($delta -gt $Tolerance) { break }
            $distance = [math]::Abs($delta.TotalMilliseconds)
            if ($distance -lt $bestMilliseconds) { $bestIndex = $i; $bestMilliseconds = $distance }
        }
        if ($bestIndex -ge 0) {
            $pairs.Add([pscustomobject]@{ Probe = $sample; Counter = $CounterRows[$bestIndex]; OffsetMilliseconds = $bestMilliseconds })
            $counterIndex = $bestIndex + 1
        }
    }
    return $pairs.ToArray()
}

function Convert-TypeperfTimestamp($Value, [int]$BiasMinutes) {
    $localTimestamp = [DateTime]::Parse([string]$Value, [Globalization.CultureInfo]::CurrentCulture, [Globalization.DateTimeStyles]::AllowWhiteSpaces)
    $localTimestamp = [DateTime]::SpecifyKind($localTimestamp, [DateTimeKind]::Unspecified)
    return [DateTimeOffset]::new($localTimestamp, [TimeSpan]::FromMinutes(-$BiasMinutes)).ToUniversalTime()
}

function Convert-TypeperfRows($Path) {
    $rows = New-Object 'System.Collections.Generic.List[object]'
    $biasMinutes = $null
    foreach ($row in Import-Csv -LiteralPath $Path) {
        $properties = @($row.PSObject.Properties)
        if ($properties.Count -lt 5) { continue }
        if ($null -eq $biasMinutes) {
            $header = [string]$properties[0].Name
            if ($header -notmatch '\((-?\d+)\)$') { throw "Typeperf CSV header has no time-zone bias: $header" }
            $biasMinutes = [int]$Matches[1]
        }
        $capturedAt = Convert-TypeperfTimestamp $properties[0].Value $biasMinutes
        $values = New-Object 'System.Collections.Generic.List[object]'
        for ($i = 1; $i -le 4; $i++) {
            $raw = [string]$properties[$i].Value
            $value = 0.0
            if ([string]::IsNullOrWhiteSpace($raw) -or
                -not [double]::TryParse($raw, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$value) -or
                [double]::IsNaN($value) -or [double]::IsInfinity($value)) {
                $values.Add($null)
            } else {
                $values.Add($value)
            }
        }
        $rows.Add([pscustomobject]@{
            CapturedAt = $capturedAt
            MemAvailable = $values[0]
            CpuTotal = $values[1]
            DiskRead = $values[2]
            DiskWrite = $values[3]
        })
    }
    return $rows.ToArray()
}

function Get-CrossValidationResult($Pairs) {
    $memoryDiffs = New-Object 'System.Collections.Generic.List[double]'
    $cpuDiffs = New-Object 'System.Collections.Generic.List[double]'
    foreach ($pair in $Pairs) {
        $probe = $pair.Probe
        $counter = $pair.Counter
        if ($null -ne $probe.physicalAvailableBytes -and $null -ne $counter.MemAvailable -and $counter.MemAvailable -gt 0) {
            $memoryDiffs.Add([math]::Abs([double]$probe.physicalAvailableBytes - [double]$counter.MemAvailable) / [double]$counter.MemAvailable * 100)
        }
        if ($null -ne $probe.cpuPercent -and $null -ne $counter.CpuTotal) {
            $cpuDiffs.Add([math]::Abs([double]$probe.cpuPercent - [double]$counter.CpuTotal))
        }
    }
    $meanMemory = if ($memoryDiffs.Count -gt 0) { ($memoryDiffs | Measure-Object -Average).Average } else { $null }
    $meanCpu = if ($cpuDiffs.Count -gt 0) { ($cpuDiffs | Measure-Object -Average).Average } else { $null }
    $roundedMemory = if ($null -eq $meanMemory) { $null } else { [math]::Round($meanMemory, 2) }
    $roundedCpu = if ($null -eq $meanCpu) { $null } else { [math]::Round($meanCpu, 2) }
    [pscustomobject]@{
        MemoryAvailable = [pscustomobject]@{
            MeanRelativeDiffPercent = $roundedMemory
            Samples = $memoryDiffs.Count
            Pass = $memoryDiffs.Count -ge 5 -and $null -ne $meanMemory -and $meanMemory -lt 5
        }
        CpuTotal = [pscustomobject]@{
            MeanAbsoluteDiffPoints = $roundedCpu
            Samples = $cpuDiffs.Count
            Pass = $cpuDiffs.Count -ge 5 -and $null -ne $meanCpu -and $meanCpu -lt 5
        }
        MatchedSamples = $Pairs.Count
    }
}

function Assert-CrossValidation($Result) {
    $failures = @()
    if (-not $Result.MemoryAvailable.Pass) { $failures += "memory samples=$($Result.MemoryAvailable.Samples), mean=$($Result.MemoryAvailable.MeanRelativeDiffPercent), tolerance=5%" }
    if (-not $Result.CpuTotal.Pass) { $failures += "CPU samples=$($Result.CpuTotal.Samples), mean=$($Result.CpuTotal.MeanAbsoluteDiffPoints), tolerance=5 points" }
    if ($failures.Count -gt 0) { throw "Cross-validation failed: $($failures -join '; ')" }
}

function Test-CleanExitVerdict($CloseRequested, $Exited, $ExitCode) {
    return ($CloseRequested -eq $true -and $Exited -eq $true -and $null -ne $ExitCode -and [int]$ExitCode -eq 0)
}

Step 'cross-validation-regression-tests' {
    $fixturePath = Join-Path $OutputDirectory ('typeperf-offset-' + [guid]::NewGuid().ToString('N') + '.csv')
    try {
        $fixtureLocal = [DateTime]::new(2026, 1, 1, 4, 0, 0)
        $fixtureHeader = '"(PDH-CSV 4.0) (Pacific Standard Time)(480)","\\FIXTURE\Memory\Available Bytes","\\FIXTURE\Processor(_Total)\% Processor Time","\\FIXTURE\PhysicalDisk(_Total)\Disk Read Bytes/sec","\\FIXTURE\PhysicalDisk(_Total)\Disk Write Bytes/sec"'
        $fixtureRow = '"{0}","1024","10","0","0"' -f $fixtureLocal.ToString('G', [Globalization.CultureInfo]::CurrentCulture)
        [IO.File]::WriteAllLines($fixturePath, @($fixtureHeader, $fixtureRow), [Text.Encoding]::ASCII)
        $fixtureRows = @(Convert-TypeperfRows $fixturePath)
        $expectedUtc = [DateTimeOffset]::new($fixtureLocal, [TimeSpan]::FromHours(-8)).ToUniversalTime()
        if ($fixtureRows.Count -ne 1 -or $fixtureRows[0].CapturedAt -ne $expectedUtc) {
            throw 'Typeperf local timestamp was not converted using the CSV header time-zone bias'
        }
    } finally {
        Remove-Item -LiteralPath $fixturePath -Force -ErrorAction SilentlyContinue
    }

    $base = [DateTimeOffset]::Parse('2026-01-01T00:00:00Z')
    $counterRows = @([pscustomobject]@{ CapturedAt = $base; MemAvailable = 1000.0; CpuTotal = $null; DiskRead = $null; DiskWrite = $null })
    $probeRows = @()
    for ($i = 0; $i -le 6; $i++) {
        $time = $base.AddSeconds($i * 2)
        if ($i -ne 0 -and $i -ne 3) {
            $counterRows += [pscustomobject]@{ CapturedAt = $time; MemAvailable = 1000.0 + $i; CpuTotal = 10.0 + $i; DiskRead = 0.0; DiskWrite = 0.0 }
        }
        $cpuValue = if ($i -eq 0) { $null } else { 10.0 + $i }
        $probeRows += [pscustomobject]@{
            atUtc = $time.AddMilliseconds(100).ToString('o')
            physicalAvailableBytes = 1000.0 + $i
            cpuPercent = $cpuValue
        }
    }
    $pairs = @(Get-MatchedSamples $probeRows $counterRows)
    if ($pairs.Count -ne 6 -or $pairs[0].Probe.atUtc -ne $probeRows[0].atUtc -or $pairs[-1].Counter.CapturedAt -ne $base.AddSeconds(12)) {
        throw 'timestamp matching did not retain the warmup probe or recover after a missing counter row'
    }
    $valid = Get-CrossValidationResult $pairs
    Assert-CrossValidation $valid
    if ($valid.MemoryAvailable.Samples -ne 6 -or $valid.CpuTotal.Samples -ne 5) { throw 'blank warmup counters were not excluded metric-by-metric' }

    $badProbes = @($probeRows | ForEach-Object {
        $cpu = if ($null -eq $_.cpuPercent) { $null } else { 100.0 }
        [pscustomobject]@{ atUtc = $_.atUtc; physicalAvailableBytes = $_.physicalAvailableBytes; cpuPercent = $cpu }
    })
    $failed = $false
    try { Assert-CrossValidation (Get-CrossValidationResult (Get-MatchedSamples $badProbes $counterRows)) } catch { $failed = $true }
    if (-not $failed) { throw 'out-of-tolerance comparisons did not fail' }

    $emptyProbes = @($probeRows | ForEach-Object { [pscustomobject]@{ atUtc = $_.atUtc; physicalAvailableBytes = $null; cpuPercent = $null } })
    $failed = $false
    try { Assert-CrossValidation (Get-CrossValidationResult (Get-MatchedSamples $emptyProbes $counterRows)) } catch { $failed = $true }
    if (-not $failed) { throw 'zero comparable metrics did not fail' }
    'timestamp pairing and comparison gates passed'
}

Step 'clean-exit-regression-tests' {
    if (Test-CleanExitVerdict -CloseRequested $false -Exited $true -ExitCode 0) { throw 'forced termination was classified as a clean exit' }
    if (-not (Test-CleanExitVerdict -CloseRequested $true -Exited $true -ExitCode 0)) { throw 'natural successful exit was not classified as clean' }
    'clean-exit classification passed'
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
        if ($LASTEXITCODE -ne 0) { throw "core tests failed: $($out | Select-Object -Last 5)" }
        if (($out -join "`n") -notmatch '\d+/\d+ passed') { throw 'core tests did not report a passing total' }
        ($out | Select-String '\d+/\d+ passed' | Select-Object -Last 1).Line
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
    $tpRows = @(Convert-TypeperfRows (Join-Path $OutputDirectory 'typeperf.csv'))
    $pairs = @(Get-MatchedSamples $samples $tpRows)
    if ($pairs.Count -lt 5) { throw "insufficient timestamp-matched samples: probe=$($samples.Count) typeperf=$($tpRows.Count) matched=$($pairs.Count)" }
    $result = Get-CrossValidationResult $pairs
    $report.CrossValidation = @(
        [ordered]@{ Metric = 'Memory\Available Bytes vs GetPerformanceInfo PhysicalAvailable'; MeanRelativeDiffPercent = $result.MemoryAvailable.MeanRelativeDiffPercent; Samples = $result.MemoryAvailable.Samples; MatchedPairs = $pairs.Count; TolerancePercent = 5; Pass = $result.MemoryAvailable.Pass },
        [ordered]@{ Metric = 'Processor time total vs GetSystemTimes total'; MeanAbsoluteDiffPoints = $result.CpuTotal.MeanAbsoluteDiffPoints; Samples = $result.CpuTotal.Samples; MatchedPairs = $pairs.Count; TolerancePoints = 5; Pass = $result.CpuTotal.Pass }
    )
    Assert-CrossValidation $result
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
    $p = $null
    try {
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
        $closeRequested = $p.CloseMainWindow()
        $exited = $closeRequested -and $p.WaitForExit(10000)
        $exitCode = $null
        if ($exited) {
            $p.Refresh()
            $exitCode = $p.ExitCode
        } else {
            if (-not $p.HasExited) { $p.Kill(); [void]$p.WaitForExit(5000) }
            $p.Refresh()
        }
        $cleanExit = Test-CleanExitVerdict $closeRequested $exited $exitCode
        $report.Environment.AppCleanExit = $cleanExit
        if (-not $cleanExit) { throw "app did not exit naturally with code 0 (CleanExit=false, CloseRequested=$closeRequested, Exited=$exited, ExitCode=$exitCode)" }
        [ordered]@{
            StartupToWindowMs = $startupMs
            MeanCpuPercentOfMachine = [math]::Round($summary.MeanCpuPercentOfMachine, 3)
            P95CpuPercentOfMachine = [math]::Round($summary.P95CpuPercentOfMachine, 3)
            PrivateCommitDeltaMB = [math]::Round($summary.PrivateCommitDeltaBytes / 1MB, 2)
            HandleDelta = $summary.HandleDelta
            CleanExit = $cleanExit
            ExitCode = $exitCode
        }
    } finally {
        if ($null -ne $p) {
            $p.Refresh()
            if (-not $p.HasExited) { $p.Kill(); [void]$p.WaitForExit(5000) }
        }
    }
}

$report.FinishedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
$reportPath = Join-Path $OutputDirectory 'validation-report.json'
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8
Write-Host "report: $reportPath"
$report | ConvertTo-Json -Depth 8
$failedSteps = @($report.Steps.Keys | Where-Object { $report.Steps[$_].Status -ne 'pass' })
if ($failedSteps.Count -gt 0) { exit 1 }
