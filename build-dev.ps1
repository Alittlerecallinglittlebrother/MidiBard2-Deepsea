param(
    [string]$Destination = (Join-Path $env:APPDATA 'XIVLauncherCN/devPlugins/MidiBard2-Deepsea-3.2.5.21'),
    [string]$DalamudLibPath = (Join-Path $env:APPDATA 'XIVLauncherCN/addon/Hooks/dev')
)
$ErrorActionPreference = 'Stop'
$destinationPath = [IO.Path]::GetFullPath($Destination)
if ($destinationPath -match '(^|[\\/])installedPlugins([\\/]|$)') { throw 'Choose a dedicated developer-plugin directory.' }
if (-not (Test-Path -LiteralPath (Join-Path $DalamudLibPath 'Dalamud.dll'))) { throw 'Dalamud development files were not found.' }
& (Join-Path $PSScriptRoot 'build-stage.ps1') -DalamudLibPath $DalamudLibPath -SkipChecks
$binaryPath = Join-Path $PSScriptRoot 'Midibard/bin/Release'
$manifest = Get-Content -LiteralPath (Join-Path $binaryPath 'MidiBard2.json') -Raw | ConvertFrom-Json
$version = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $binaryPath 'MidiBard2.dll')).Version.ToString()
if ($manifest.InternalName -ne 'MidiBard2' -or $manifest.DalamudApiLevel -ne 15 -or $manifest.AssemblyVersion -ne $version) {
    throw 'The plugin manifest does not match the compiled assembly.'
}
foreach ($required in @('MidiBard2.dll', 'MidiBard.Stage.dll', 'BardStage.Core.dll', 'BardMusicPlayer.XIVMIDI.dll', 'Melanchall.DryWetMidi.dll', 'Melanchall_DryWetMidi_Native64.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $binaryPath $required))) { throw "Missing runtime dependency: $required" }
}
if (Test-Path -LiteralPath $destinationPath) {
    if (-not (Test-Path -LiteralPath (Join-Path $destinationPath 'MidiBard2.json'))) { throw 'Existing destination is not a recognized MidiBard developer-plugin directory.' }
    if (Get-Process ffxiv_dx11 -ErrorAction SilentlyContinue) { throw 'Stop the game before replacing an existing developer-plugin build.' }
    $backupPath = $destinationPath + '.backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff')
    Copy-Item -LiteralPath $destinationPath -Destination $backupPath -Recurse
    Write-Output "Backup: $backupPath"
}
$null = New-Item -ItemType Directory -Path $destinationPath -Force
$copied = @()
Get-ChildItem -LiteralPath $binaryPath -Recurse -File | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($binaryPath, $_.FullName).Replace('\', '/')
    if ($relative -notmatch '^MidiBard2/' -and $_.Extension -ne '.zip') {
        $targetPath = Join-Path $destinationPath $relative
        $null = New-Item -ItemType Directory -Path (Split-Path $targetPath -Parent) -Force
        Copy-Item -LiteralPath $_.FullName -Destination $targetPath
        $sourceHash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        $targetHash = (Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash
        if ($sourceHash -ne $targetHash) { throw "Copied file did not verify: $relative" }
        $copied += [pscustomobject]@{ Path = $relative; Sha256 = $targetHash; Bytes = $_.Length }
    }
}
foreach ($name in @('LICENSE', 'STAGE-README.md', 'STAGE-ROOM.md', 'STAGE-IPC.md', 'STAGE-VERIFICATION.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $destinationPath
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Stage/THIRD-PARTY-NOTICES.txt') -Destination (Join-Path $destinationPath 'STAGE-THIRD-PARTY-NOTICES.txt')
[pscustomobject]@{
    BuiltAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    AssemblyVersion = $version
    DalamudApiLevel = $manifest.DalamudApiLevel
    EntryPoint = Join-Path $destinationPath 'MidiBard2.dll'
    ConfigurationChanged = $false
    GameLoadVerified = $false
    Files = $copied
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $destinationPath 'DEV-BUILD-VERIFICATION.json') -Encoding utf8
Write-Output "Developer plugin ready: $(Join-Path $destinationPath 'MidiBard2.dll')"
Write-Output "Version: $version; API: $($manifest.DalamudApiLevel); verified files: $($copied.Count)"
