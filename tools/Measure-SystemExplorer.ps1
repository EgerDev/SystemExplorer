param(
    [Parameter(Mandatory = $true)][int]$TargetProcessId,
    [ValidateRange(30, 86400)][int]$DurationSeconds = 600,
    [ValidateRange(1, 60)][int]$IntervalSeconds = 5,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$process = Get-Process -Id $TargetProcessId
$creation = $process.StartTime.ToUniversalTime().ToFileTimeUtc()
$processors = [Environment]::ProcessorCount
$watch = [Diagnostics.Stopwatch]::StartNew()
$samples = [Collections.Generic.List[object]]::new()
$previousCpu = $null
$previousAt = $null
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

try {
    while ($true) {
        $process.Refresh()
        if ($process.HasExited -or $process.StartTime.ToUniversalTime().ToFileTimeUtc() -ne $creation) {
            throw 'Target exited or process identity changed; the measurement is incomplete.'
        }
        $at = $watch.Elapsed.TotalSeconds
        $cpu = $process.TotalProcessorTime.TotalSeconds
        $normalizedCpu = if ($null -ne $previousAt -and $at -gt $previousAt) {
            ($cpu - $previousCpu) / ($at - $previousAt) / $processors * 100
        } else { $null }
        $samples.Add([pscustomobject]@{
            CapturedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
            ElapsedSeconds = $at
            ProcessId = $TargetProcessId
            CreationFileTime = $creation
            CpuPercentOfMachine = $normalizedCpu
            TotalCpuSeconds = $cpu
            PrivateCommitBytes = $process.PrivateMemorySize64
            WorkingSetIncludingSharedBytes = $process.WorkingSet64
            Handles = $process.HandleCount
            Threads = $process.Threads.Count
        })
        $previousCpu = $cpu
        $previousAt = $at
        if ($at -ge $DurationSeconds) { break }
        Start-Sleep -Seconds $IntervalSeconds
    }
} finally {
    $samples | Export-Csv -LiteralPath (Join-Path $OutputDirectory 'samples.csv') -NoTypeInformation -Encoding UTF8
    $process.Dispose()
}

$cpuSamples = @($samples | Where-Object { $null -ne $_.CpuPercentOfMachine } | ForEach-Object { $_.CpuPercentOfMachine } | Sort-Object)
if ($cpuSamples.Count -eq 0) { throw 'No valid CPU intervals were collected.' }
$first = $samples[0]
$last = $samples[$samples.Count - 1]
$summary = [ordered]@{
    Platform = [Environment]::OSVersion.VersionString
    LogicalProcessors = $processors
    ProcessId = $TargetProcessId
    CreationFileTime = $creation
    Samples = $samples.Count
    RequestedIntervalSeconds = $IntervalSeconds
    ObservedSeconds = $last.ElapsedSeconds - $first.ElapsedSeconds
    MeanCpuPercentOfMachine = ($last.TotalCpuSeconds - $first.TotalCpuSeconds) / ($last.ElapsedSeconds - $first.ElapsedSeconds) / $processors * 100
    P95CpuPercentOfMachine = $cpuSamples[[Math]::Ceiling($cpuSamples.Count * 0.95) - 1]
    InitialPrivateCommitBytes = $first.PrivateCommitBytes
    FinalPrivateCommitBytes = $last.PrivateCommitBytes
    PrivateCommitDeltaBytes = $last.PrivateCommitBytes - $first.PrivateCommitBytes
    InitialHandles = $first.Handles
    FinalHandles = $last.Handles
    HandleDelta = $last.Handles - $first.Handles
    Note = 'Observed resource changes are not a leak diagnosis. Record workload, privilege, DPI and application revision separately. Server evidence is not Windows 11 acceptance.'
}
$summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'summary.json') -Encoding UTF8
$summary | ConvertTo-Json
