[CmdletBinding()]
param([Parameter(Mandatory)][string]$Version,[Parameter(Mandatory)][string]$ArtifactDirectory)
$ErrorActionPreference = 'Stop'
$assets = @(& "$PSScriptRoot/ReleaseAssets.ps1" -Version $Version -ArtifactDirectory $ArtifactDirectory)
$staging = Join-Path (Split-Path ([IO.Path]::GetFullPath($ArtifactDirectory)) -Parent) ('release-staging-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $staging | Out-Null
$manifest = Join-Path $staging 'sha256sums.txt'
$lines = @($assets | ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name })
[IO.File]::WriteAllText([IO.Path]::GetFullPath($manifest), ($lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
$assets += Get-Item -LiteralPath $manifest
$tag = "v$Version"
$existing = gh release view $tag --json assets,isDraft 2>$null | ConvertFrom-Json
if ($LASTEXITCODE) {
    gh release create $tag --verify-tag --draft --title "DubUrl.Tools $Version" --generate-notes
    if ($LASTEXITCODE) { throw 'Creating the draft release failed.' }
    $existing = gh release view $tag --json assets,isDraft | ConvertFrom-Json
    if ($LASTEXITCODE) { throw 'Reading the draft release failed.' }
}
if (@($existing.assets | Where-Object name -NotIn $assets.Name).Count) { throw 'The existing release contains unexpected assets.' }
$retry = Join-Path $staging 'retry'
New-Item -ItemType Directory $retry | Out-Null
foreach ($asset in $assets) {
    if ($asset.Name -in $existing.assets.name) {
        gh release download $tag --pattern $asset.Name --dir $retry
        if ($LASTEXITCODE) { throw 'Downloading the existing release asset failed.' }
        if ((Get-FileHash (Join-Path $retry $asset.Name)).Hash -ne (Get-FileHash $asset.FullName).Hash) {
            throw "Existing release asset '$($asset.Name)' differs; refusing to replace it."
        }
    } else {
        gh release upload $tag $asset.FullName
        if ($LASTEXITCODE) { throw "Uploading '$($asset.Name)' failed." }
    }
}
gh release edit $tag --draft=false
if ($LASTEXITCODE) { throw 'Publishing the completed release failed.' }
