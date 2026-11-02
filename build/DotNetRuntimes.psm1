Set-StrictMode -Version Latest

function Get-MissingDotNetRuntimes {
    param(
        [Parameter(Mandatory)][ValidatePattern('^[1-9]\d*\.\d+$')][string[]]$Channels,
        [AllowEmptyCollection()][string[]]$RuntimeLines = @()
    )
    $installed = @($RuntimeLines | ForEach-Object {
        if ($_ -match '^Microsoft\.NETCore\.App\s+(\d+\.\d+)\.\d+\s+\[') { $Matches[1] }
    })
    $Channels | Select-Object -Unique | Where-Object { $_ -notin $installed }
}

function Resolve-DotNetRuntimes {
    param(
        [Parameter(Mandatory)][ValidatePattern('^[1-9]\d*\.\d+$')][string[]]$Channels,
        [Parameter(Mandatory)][scriptblock]$Inspect,
        [Parameter(Mandatory)][scriptblock]$InstallRuntime
    )
    $missing = @(Get-MissingDotNetRuntimes -Channels $Channels -RuntimeLines @(& $Inspect))
    foreach ($channel in $missing) {
        & $InstallRuntime $channel
        if (@(Get-MissingDotNetRuntimes -Channels @($channel) -RuntimeLines @(& $Inspect)).Count) {
            throw "The runtime installer did not provide Microsoft.NETCore.App $channel."
        }
    }
}

Export-ModuleMember -Function Get-MissingDotNetRuntimes, Resolve-DotNetRuntimes
