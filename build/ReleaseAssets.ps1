[CmdletBinding()]
param([Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][string]$ArtifactDirectory)
$ErrorActionPreference = 'Stop'
# Both distribution builders normalize public filenames, including prerelease labels.
$Version = $Version.ToLowerInvariant()
$root = (Resolve-Path -LiteralPath $ArtifactDirectory).Path
$rids = @('win-x64','win-arm64','linux-x64','linux-musl-x64','osx-x64','osx-arm64')
$expected = @(
    foreach ($tfm in @('net8.0','net9.0','net10.0')) {
        foreach ($rid in $rids) {
            $extension = if ($rid.StartsWith('win-')) { 'zip' } else { 'tar.gz' }
            "duburl-$Version-$tfm-$rid.$extension"
        }
    }
    foreach ($rid in @('win-x64','win-arm64')) { "duburl-$Version-net10.0-$rid-setup.exe" }
    foreach ($rid in $rids) {
        $extension = if ($rid.StartsWith('win-')) { 'zip' } else { 'tar.gz' }
        "duburl-worker-$Version-net10.0-$rid.$extension"
    }
)
$files = @(Get-ChildItem -LiteralPath $root -Recurse -File)
if ($files.Count -ne $expected.Count -or @($files.Name | Sort-Object -Unique).Count -ne $expected.Count) {
    throw "Expected exactly $($expected.Count) unique public tool distributions."
}
$difference = @(Compare-Object $expected $files.Name -CaseSensitive)
if ($difference.Count) {
    throw ('Public tool distribution names do not match the supported version/framework/runtime matrix: ' + ($difference | Out-String))
}
$files | Sort-Object Name
