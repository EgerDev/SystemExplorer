# Synthetic predicate tests only; no live token or application acceptance.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$source = Join-Path $PSScriptRoot 'Windows11-Acceptance-Preflight.ps1'
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw "Preflight parse errors: $parseErrors" }
$functions = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Get-AcceptanceLogonEligibility'
}, $false))
if ($functions.Count -ne 1) { throw 'Expected exactly one production logon eligibility predicate.' }
# Load only the actual function, not the host observations or process-exit paths.
. ([scriptblock]::Create($functions[0].Extent.Text))

$desktop = @{
    InteractiveEnabled = $true
    RemoteInteractiveEnabled = $false
    ServiceLogonPresent = $false
    BatchLogonPresent = $false
    TokenSessionId = 1
    UserInteractive = $true
}
$cases = @(
    @{ Name = 'normal desktop'; Changes = @{}; Eligible = $true; Reason = $null },
    @{ Name = 'remote desktop'; Changes = @{ InteractiveEnabled = $false; RemoteInteractiveEnabled = $true }; Eligible = $true; Reason = $null },
    @{ Name = 'regular-user service group'; Changes = @{ ServiceLogonPresent = $true }; Eligible = $false; Reason = 'Token contains the SERVICE logon group.' },
    @{ Name = 'batch group'; Changes = @{ BatchLogonPresent = $true }; Eligible = $false; Reason = 'Token contains the BATCH logon group.' },
    @{ Name = 'session zero'; Changes = @{ TokenSessionId = 0 }; Eligible = $false; Reason = 'Token session must be greater than zero.' },
    @{ Name = 'no interactive desktop'; Changes = @{ UserInteractive = $false }; Eligible = $false; Reason = 'Process has no interactive desktop/window station.' },
    @{ Name = 'no enabled interactive membership'; Changes = @{ InteractiveEnabled = $false }; Eligible = $false; Reason = 'Token lacks enabled INTERACTIVE or REMOTE_INTERACTIVE membership.' }
)

foreach ($case in $cases) {
    $arguments = $desktop.Clone()
    foreach ($key in $case.Changes.Keys) { $arguments[$key] = $case.Changes[$key] }
    $actual = Get-AcceptanceLogonEligibility @arguments
    if ($actual.Eligible -ne $case.Eligible) { throw "Synthetic case '$($case.Name)' eligibility mismatch." }
    if ($case.Eligible -and $actual.Reasons.Count -ne 0) { throw "Synthetic case '$($case.Name)' has unexpected reasons." }
    if (-not $case.Eligible -and $actual.Reasons -notcontains $case.Reason) {
        throw "Synthetic case '$($case.Name)' lacks its explicit rejection reason."
    }
    Write-Output "SYNTHETIC PASS: $($case.Name)"
}
Write-Output 'All 7 synthetic logon predicate cases passed. Live Windows 11/service-account acceptance NOT RUN.'
