[CmdletBinding()]
param([Parameter(Mandatory)][string]$Channels, [switch]$RuntimeOnly)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DotNetRuntimes.psm1') -Force
$requested = @($Channels.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($requested.Count -eq 0) { throw 'At least one runtime channel is required.' }
foreach ($channel in $requested) {
    if ($channel -notmatch '^[1-9]\d*\.\d+$') { throw "Invalid runtime channel '$channel'." }
}
$temporaryRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$command = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if ($command) {
    $hostFile = Get-Item -LiteralPath $command.Source
    $resolved = $hostFile.ResolveLinkTarget($true)
    if ($resolved) { $hostFile = $resolved }
    $root = Split-Path -Parent $hostFile.FullName
    $hostPath = $hostFile.FullName
} else {
    $root = Join-Path $temporaryRoot 'duburl-dotnet-runtimes'
    $hostPath = $null
}
$context = @{ HostPath = $hostPath; Installer = $null }
$inspect = {
    if ($context.HostPath) {
        $lines = @(& $context.HostPath --list-runtimes)
        if ($LASTEXITCODE) { throw 'Listing installed runtimes failed.' }
        $lines
    }
}
# Archive-only jobs can use a private runtime root without changing the installed SDKs.
# Build jobs keep additional runtimes alongside the SDK selected by setup-dotnet.
if ($RuntimeOnly -and @(Get-MissingDotNetRuntimes -Channels $requested -RuntimeLines @(& $inspect)).Count) {
    $root = Join-Path $temporaryRoot 'duburl-dotnet-runtimes'
    $candidate = Join-Path $root $(if ($IsWindows) { 'dotnet.exe' } else { 'dotnet' })
    $context.HostPath = if (Test-Path -LiteralPath $candidate) { $candidate } else { $null }
}
$install = {
    param($channel)
    if (-not $context.Installer) {
        $extension = if ($IsWindows) { 'ps1' } else { 'sh' }
        $context.Installer = Join-Path $temporaryRoot "duburl-runtime-install.$extension"
        Invoke-WebRequest "https://dot.net/v1/dotnet-install.$extension" -OutFile $context.Installer -MaximumRetryCount 3
    }
    Write-Host "Installing missing Microsoft.NETCore.App $channel runtime only."
    if ($IsWindows) {
        & $context.Installer -Runtime dotnet -Channel $channel -InstallDir $root -NoPath -SkipNonVersionedFiles
    } else {
        & bash $context.Installer --runtime dotnet --channel $channel --install-dir $root --no-path --skip-non-versioned-files
    }
    if ($LASTEXITCODE) { throw "Installing .NET $channel runtime failed." }
    $context.HostPath = Join-Path $root $(if ($IsWindows) { 'dotnet.exe' } else { 'dotnet' })
}
Resolve-DotNetRuntimes -Channels $requested -Inspect $inspect -InstallRuntime $install
$architectureVariable = 'DOTNET_ROOT_' + [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToUpperInvariant()
$env:DOTNET_ROOT = $root
[Environment]::SetEnvironmentVariable($architectureVariable, $root, 'Process')
$env:PATH = $root + [IO.Path]::PathSeparator + $env:PATH
if ($env:GITHUB_PATH) { $root | Out-File -LiteralPath $env:GITHUB_PATH -Append -Encoding utf8NoBOM }
if ($env:GITHUB_ENV) {
    "DOTNET_ROOT=$root" | Out-File -LiteralPath $env:GITHUB_ENV -Append -Encoding utf8NoBOM
    "$architectureVariable=$root" | Out-File -LiteralPath $env:GITHUB_ENV -Append -Encoding utf8NoBOM
}
Write-Host "Required runtimes available from $root`: $($requested -join ', ')."
