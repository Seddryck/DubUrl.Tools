$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('duburl-tools-assets-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $root | Out-Null
try {
    foreach ($version in @('1.2.0', '1.2.0-PullRequest1.6')) {
        $assetVersion = $version.ToLowerInvariant()
        $rids = @('win-x64','win-arm64','linux-x64','linux-musl-x64','osx-x64','osx-arm64')
        foreach ($tfm in @('net8.0','net9.0','net10.0')) {
            foreach ($rid in $rids) {
                $ext = if ($rid.StartsWith('win-')) { 'zip' } else { 'tar.gz' }
                New-Item -ItemType File (Join-Path $root "duburl-$assetVersion-$tfm-$rid.$ext") | Out-Null
            }
        }
        foreach ($rid in @('win-x64','win-arm64')) { New-Item -ItemType File (Join-Path $root "duburl-$assetVersion-net10.0-$rid-setup.exe") | Out-Null }
        foreach ($rid in $rids) {
            $ext = if ($rid.StartsWith('win-')) { 'zip' } else { 'tar.gz' }
            New-Item -ItemType File (Join-Path $root "duburl-worker-$assetVersion-net10.0-$rid.$ext") | Out-Null
        }
        $assets = @(& "$PSScriptRoot/ReleaseAssets.ps1" -Version $version -ArtifactDirectory $root)
        if ($assets.Count -ne 26) { throw 'Incorrect public distribution count.' }
        function Reject([scriptblock]$action) {
            $failed = $false
            try { & $action | Out-Null } catch { $failed = $true }
            if (-not $failed) { throw 'Invalid public distribution set was accepted.' }
        }
        $extra = Join-Path $root 'internal-build-inventory.txt'
        New-Item -ItemType File $extra | Out-Null
        Reject { & "$PSScriptRoot/ReleaseAssets.ps1" -Version $version -ArtifactDirectory $root }
        Remove-Item -LiteralPath $extra
        $worker = Join-Path $root "duburl-worker-$assetVersion-net10.0-linux-x64.tar.gz"
        Remove-Item -LiteralPath $worker
        Reject { & "$PSScriptRoot/ReleaseAssets.ps1" -Version $version -ArtifactDirectory $root }
        New-Item -ItemType File (Join-Path $root "duburl-worker-$assetVersion-net9.0-linux-x64.tar.gz") | Out-Null
        Reject { & "$PSScriptRoot/ReleaseAssets.ps1" -Version $version -ArtifactDirectory $root }
        Get-ChildItem -LiteralPath $root -File | Remove-Item
    }
    Write-Host 'Validated stable and mixed-case prerelease CLI/worker inventories, missing assets, wrong frameworks, and private metadata exclusion.'
} finally {
    $resolved = [IO.Path]::GetFullPath($root)
    if ((Split-Path $resolved -Parent) -ne [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath())) { throw 'Unexpected fixture path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
