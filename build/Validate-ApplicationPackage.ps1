[CmdletBinding()]
param([Parameter(Mandatory)][string]$Version,[Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference = 'Stop'
$package = Join-Path $Directory "DubUrl.Application.$Version.nupkg"
$archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path $package))
try {
    foreach ($tfm in @('net8.0','net9.0','net10.0')) {
        if (-not $archive.GetEntry("lib/$tfm/DubUrl.Application.dll")) { throw "Application package is missing $tfm." }
    }
    $reader = [IO.StreamReader]::new($archive.GetEntry('DubUrl.Application.nuspec').Open())
    try { [xml]$spec = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if ($spec.package.metadata.version -ne $Version) { throw 'Application package version differs from the tested version.' }
    $deps = @($spec.SelectNodes("//*[local-name()='dependency']") | ForEach-Object id)
    foreach ($expected in @('DubUrl','DubUrl.Schema','DubUrl.Schema.Documents')) {
        if ($expected -notin $deps) { throw "Missing dependency $expected." }
    }
    if (@($deps | Where-Object { $_ -in @('DubUrl.Cli','DubUrl-cli','DubUrl.Worker','ConnectorCatalog.Build') }).Count) { throw 'Application depends on a host or build tool.' }
} finally { $archive.Dispose() }
