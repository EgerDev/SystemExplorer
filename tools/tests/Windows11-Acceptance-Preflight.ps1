# Host/token eligibility only. This does not run or pass application acceptance.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-AcceptanceLogonEligibility {
    param(
        [Parameter(Mandatory = $true)][bool]$InteractiveEnabled,
        [Parameter(Mandatory = $true)][bool]$RemoteInteractiveEnabled,
        [Parameter(Mandatory = $true)][bool]$ServiceLogonPresent,
        [Parameter(Mandatory = $true)][bool]$BatchLogonPresent,
        [Parameter(Mandatory = $true)][int]$TokenSessionId,
        [Parameter(Mandatory = $true)][bool]$UserInteractive
    )

    $reasons = [Collections.Generic.List[string]]::new()
    if (-not ($InteractiveEnabled -or $RemoteInteractiveEnabled)) {
        $reasons.Add('Token lacks enabled INTERACTIVE or REMOTE_INTERACTIVE membership.')
    }
    if ($ServiceLogonPresent) { $reasons.Add('Token contains the SERVICE logon group.') }
    if ($BatchLogonPresent) { $reasons.Add('Token contains the BATCH logon group.') }
    if ($TokenSessionId -le 0) { $reasons.Add('Token session must be greater than zero.') }
    if (-not $UserInteractive) { $reasons.Add('Process has no interactive desktop/window station.') }

    [pscustomobject]@{
        Eligible = $reasons.Count -eq 0
        Reasons = @($reasons.ToArray())
    }
}

try {
    $os = Get-CimInstance Win32_OperatingSystem
    $cpu = @(Get-CimInstance Win32_Processor)
    if (-not ('SystemExplorer.Acceptance.TokenReader' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace SystemExplorer.Acceptance
{
    public static class TokenReader
    {
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(IntPtr token, int informationClass,
            out int information, int informationLength, out int returnLength);

        public static int ReadInt32(IntPtr token, int informationClass)
        {
            int value, length;
            if (!GetTokenInformation(token, informationClass, out value, sizeof(int), out length))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (length != sizeof(int))
                throw new InvalidOperationException("Unexpected token information length.");
            return value;
        }
    }
}
'@
    }

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        $adminEnabled = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        $interactiveEnabled = $principal.IsInRole([Security.Principal.SecurityIdentifier]::new('S-1-5-4'))
        $remoteInteractiveEnabled = $principal.IsInRole([Security.Principal.SecurityIdentifier]::new('S-1-5-14'))
        $groupSids = @($identity.Groups | ForEach-Object { $_.Value })
        $serviceLogonPresent = $groupSids -contains 'S-1-5-6'
        $batchLogonPresent = $groupSids -contains 'S-1-5-3'
        # TOKEN_INFORMATION_CLASS: TokenSessionId = 12, TokenElevationType = 18, TokenElevation = 20.
        $tokenSessionId = [SystemExplorer.Acceptance.TokenReader]::ReadInt32($identity.Token, 12)
        $elevationType = [SystemExplorer.Acceptance.TokenReader]::ReadInt32($identity.Token, 18)
        $elevated = [SystemExplorer.Acceptance.TokenReader]::ReadInt32($identity.Token, 20) -ne 0
        $userSid = $identity.User.Value
    } finally {
        $identity.Dispose()
    }

    $userInteractive = [Environment]::UserInteractive
    $logonEligibility = Get-AcceptanceLogonEligibility -InteractiveEnabled $interactiveEnabled `
        -RemoteInteractiveEnabled $remoteInteractiveEnabled -ServiceLogonPresent $serviceLogonPresent `
        -BatchLogonPresent $batchLogonPresent -TokenSessionId $tokenSessionId -UserInteractive $userInteractive
    $reasons = [Collections.Generic.List[string]]::new()
    foreach ($reason in $logonEligibility.Reasons) { $reasons.Add($reason) }
    if ([int]$os.ProductType -ne 1) { $reasons.Add('OS is not a workstation (ProductType must be 1).') }
    if ([int]$os.BuildNumber -lt 22000) { $reasons.Add('OS build is below 22000.') }
    if (-not [Environment]::Is64BitOperatingSystem -or $cpu.Count -eq 0 -or
        @($cpu | Where-Object { [int]$_.Architecture -ne 9 }).Count -gt 0) {
        $reasons.Add('Native x64 host required; ARM64 emulation is not x64 acceptance.')
    }
    if ($elevated -or $adminEnabled -or $elevationType -eq 2) {
        $reasons.Add('Token is elevated or has Administrators enabled.')
    }
    if ($elevationType -notin @(1, 2, 3)) { $reasons.Add('Unknown token elevation type.') }
    if ($userSid -in @('S-1-5-18', 'S-1-5-19', 'S-1-5-20')) {
        $reasons.Add('Service identity is not an interactive standard-user test account.')
    }

    [ordered]@{
        CapturedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        Caption = $os.Caption
        Version = $os.Version
        BuildNumber = [int]$os.BuildNumber
        ProductType = [int]$os.ProductType
        OSArchitecture = $os.OSArchitecture
        ProcessorArchitectureCodes = @($cpu | ForEach-Object { [int]$_.Architecture })
        UserSid = $userSid
        TokenIsElevated = $elevated
        TokenElevationType = $elevationType
        AdministratorsEnabled = $adminEnabled
        InteractiveEnabled = $interactiveEnabled
        RemoteInteractiveEnabled = $remoteInteractiveEnabled
        ServiceLogonPresent = $serviceLogonPresent
        BatchLogonPresent = $batchLogonPresent
        TokenSessionId = $tokenSessionId
        UserInteractive = $userInteractive
        Eligibility = $(if ($reasons.Count -eq 0) { 'ELIGIBLE_HOST_TOKEN_ONLY' } else { 'BLOCKED' })
        Reasons = @($reasons.ToArray())
        ManualAcceptance = 'NOT_RUN'
        Note = 'No artifact, driver, offline, display, UIA, Narrator or stability checks were performed. Capture whoami /all separately.'
    } | ConvertTo-Json -Depth 4
    if ($reasons.Count -gt 0) { exit 2 }
    exit 0
} catch {
    [ordered]@{
        Eligibility = 'BLOCKED_OBSERVATION_FAILED'
        Error = $_.Exception.Message
        ManualAcceptance = 'NOT_RUN'
    } | ConvertTo-Json
    exit 3
}
