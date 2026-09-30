param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
# Both packaging entry points create only the current eight-player build.
& (Join-Path $PSScriptRoot 'package-local-song-sync.ps1') -OutputDirectory $OutputDirectory
