$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DotNetRuntimes.psm1') -Force
function Assert-Equal($actual, $expected, [string]$message) {
    if (@(Compare-Object @($actual) @($expected)).Count) { throw $message }
}
function Assert-Rejected([scriptblock]$operation, [string]$message) {
    $rejected = $false
    try { & $operation } catch { $rejected = $true }
    if (-not $rejected) { throw $message }
}
$lines = @('Microsoft.NETCore.App 8.0.32 [/dotnet/shared/Microsoft.NETCore.App]',
           'Microsoft.NETCore.App 9.0.21 [/dotnet/shared/Microsoft.NETCore.App]')
if (@(Get-MissingDotNetRuntimes -Channels @('8.0','9.0') -RuntimeLines $lines).Count) {
    throw 'Installed stable runtime patches must be reused.'
}
Assert-Equal @(Get-MissingDotNetRuntimes -Channels @('8.0') -RuntimeLines @('8.0.416 [/dotnet/sdk]', 'Microsoft.AspNetCore.App 8.0.32 [/dotnet/shared/Microsoft.AspNetCore.App]')) @('8.0') 'An SDK or ASP.NET runtime cannot replace Microsoft.NETCore.App.'
Assert-Equal @(Get-MissingDotNetRuntimes -Channels @('8.0','9.0') -RuntimeLines @('Microsoft.NETCore.App 8.0.0-preview.1 [/dotnet/shared/Microsoft.NETCore.App]', $lines[1])) @('8.0') 'A preview runtime cannot satisfy a stable runtime channel.'
$context = @{ Lines = @($lines[0]); Installs = [Collections.Generic.List[string]]::new() }
Resolve-DotNetRuntimes -Channels @('8.0','9.0','9.0') -Inspect { $context.Lines } -InstallRuntime {
    param($channel)
    $context.Installs.Add($channel)
    $context.Lines += "Microsoft.NETCore.App $channel.21 [/dotnet/shared/Microsoft.NETCore.App]"
}
Assert-Equal @($context.Installs) @('9.0') 'Install only the missing channel, once.'
$context.Installs.Clear()
Resolve-DotNetRuntimes -Channels @('8.0','9.0') -Inspect { $context.Lines } -InstallRuntime { throw 'Nothing should be installed.' }
Assert-Rejected { Resolve-DotNetRuntimes -Channels @('8.0') -Inspect { @() } -InstallRuntime { throw 'Installer failed.' } } 'Runtime installation failure must propagate.'
Assert-Rejected { Resolve-DotNetRuntimes -Channels @('8.0') -Inspect { @() } -InstallRuntime { } } 'A successful installer exit without a runtime must fail verification.'
$context.InvalidInspected = $false
Assert-Rejected { Resolve-DotNetRuntimes -Channels @('8.0-preview') -Inspect { $context.InvalidInspected = $true; @() } -InstallRuntime { } } 'Invalid channels must be rejected.'
if ($context.InvalidInspected) { throw 'Invalid channels reached runtime inspection.' }
Write-Host 'Passed 8 runtime-setup scenarios: reuse, runtime identity, stable versions, missing-only installation, idempotence, installer failure, post-install verification, and invalid input.'
