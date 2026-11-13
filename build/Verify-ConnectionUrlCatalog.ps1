param([switch]$Refresh)

$ErrorActionPreference = 'Stop'
$catalogDirectory = Join-Path $PSScriptRoot '../DubUrl.Cli/ConnectionUrls'
[xml]$pin = Get-Content -LiteralPath (Join-Path $catalogDirectory 'ConnectionUrlCatalog.props') -Raw
$version = $pin.Project.PropertyGroup.ConnectionUrlCatalogVersion
$sha256 = $pin.Project.PropertyGroup.ConnectionUrlCatalogSha256
if ($version -notmatch '^\d+\.\d+\.\d+$' -or $sha256 -notmatch '^[0-9a-fA-F]{64}$') {
    throw 'The connection URL catalog pin is invalid.'
}

$archive = 'connection-url-catalog.zip'
$download = New-TemporaryFile
try {
    Invoke-WebRequest "https://github.com/Seddryck/DubUrl.Providers/releases/download/connection-url-catalog-v$version/$archive" -OutFile $download.FullName
    if ((Get-FileHash -LiteralPath $download.FullName).Hash -ne $sha256) {
        throw "The published connection URL catalog $version does not match the pinned SHA-256."
    }
    $zip = [IO.Compression.ZipFile]::OpenRead($download.FullName)
    try {
        $reader = [IO.StreamReader]::new($zip.GetEntry('manifest.json').Open())
        try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json }
        finally { $reader.Dispose() }
        if ($manifest.catalogVersion -ne $version -or $manifest.bundleFormatVersion -ne 1 -or $manifest.schemaVersion -ne 2) {
            throw 'The downloaded connection URL catalog has an unexpected manifest.'
        }
    }
    finally { $zip.Dispose() }

    $bundled = Join-Path $catalogDirectory $archive
    if ($Refresh) { Copy-Item -LiteralPath $download.FullName -Destination $bundled -Force }
    if ((Get-FileHash -LiteralPath $bundled).Hash -ne $sha256) {
        throw 'The bundled connection URL catalog does not match the verified publication.'
    }
    Write-Host "Verified the bundled Providers connection URL catalog $version."
}
finally { Remove-Item -LiteralPath $download.FullName }
