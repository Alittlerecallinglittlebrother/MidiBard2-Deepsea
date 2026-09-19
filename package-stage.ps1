param([string]$OutputDirectory = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$binaryPath = Join-Path $PSScriptRoot 'Midibard/bin/Release'
$pluginZip = Join-Path $OutputDirectory 'MidiBard2-Deepsea-3.2.5.19.zip'
$sourceZip = Join-Path $OutputDirectory 'MidiBard2-Deepsea-3.2.5.19-source.zip'
$screenshotsZip = Join-Path $OutputDirectory 'MidiBard2-Deepsea-3.2.5.19-screenshots.zip'
foreach ($path in @($pluginZip, $sourceZip, $screenshotsZip)) {
    if (Test-Path -LiteralPath $path) { throw "Output already exists: $path" }
}
$manifest = Get-Content -LiteralPath (Join-Path $binaryPath 'MidiBard2.json') -Raw | ConvertFrom-Json
$version = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $binaryPath 'MidiBard2.dll')).Version.ToString()
if ($manifest.InternalName -ne 'MidiBard2' -or $version -ne '3.2.5.19' -or $manifest.AssemblyVersion -ne $version -or $manifest.DalamudApiLevel -ne 15) { throw 'Unexpected plugin manifest.' }
if ($manifest.Name -cne 'midibard2-深海回响特供版' -or $manifest.Author -cne 'akira0245, Ori, Kalle, Zune, 断水剑, SevenCat') { throw 'Unexpected plugin branding.' }
$required = @('MidiBard2.dll', 'MidiBard.Stage.dll', 'BardStage.Core.dll', 'BardMusicPlayer.XIVMIDI.dll', 'Melanchall.DryWetMidi.dll', 'Melanchall_DryWetMidi_Native64.dll')
foreach ($name in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $binaryPath $name))) { throw "Missing dependency: $name" }
}
function Add-ZipFile($archive, [string]$path, [string]$entry) {
    $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $path, $entry.Replace('\', '/'), [IO.Compression.CompressionLevel]::Optimal)
}
$archive = [IO.Compression.ZipFile]::Open($pluginZip, [IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem -LiteralPath $binaryPath -Recurse -File | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($binaryPath, $_.FullName).Replace('\', '/')
        if ($relative -notmatch '^MidiBard2/' -and $_.Extension -notin @('.pdb', '.zip')) { Add-ZipFile $archive $_.FullName $relative }
    }
    foreach ($name in @('LICENSE', 'STAGE-README.md', 'STAGE-ROOM.md', 'STAGE-IPC.md', 'STAGE-VERIFICATION.md')) { Add-ZipFile $archive (Join-Path $PSScriptRoot $name) $name }
    Add-ZipFile $archive (Join-Path $PSScriptRoot 'Stage/THIRD-PARTY-NOTICES.txt') 'STAGE-THIRD-PARTY-NOTICES.txt'
}
finally { $archive.Dispose() }
$archive = [IO.Compression.ZipFile]::Open($sourceZip, [IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem -LiteralPath $PSScriptRoot -Recurse -File -Force | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($PSScriptRoot, $_.FullName).Replace('\', '/')
        if ($relative -notmatch '(^|/)(\.git|bin|obj|verification|artifacts)(/|$)' -and $_.Extension -ne '.log') { Add-ZipFile $archive $_.FullName ('MidiBard2-Deepsea-3.2.5.19/' + $relative) }
    }
}
finally { $archive.Dispose() }
$archive = [IO.Compression.ZipFile]::Open($screenshotsZip, [IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'verification') -File -Filter '*.png' | ForEach-Object { Add-ZipFile $archive $_.FullName $_.Name }
}
finally { $archive.Dispose() }
$hashes = foreach ($path in @($pluginZip, $sourceZip, $screenshotsZip) + ($required | ForEach-Object { Join-Path $binaryPath $_ })) {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash, [IO.Path]::GetRelativePath($OutputDirectory, $path).Replace('\', '/')
}
$hashes | Set-Content -LiteralPath (Join-Path $OutputDirectory 'MidiBard2-Deepsea-3.2.5.19-SHA256SUMS.txt') -Encoding utf8
$hashes
