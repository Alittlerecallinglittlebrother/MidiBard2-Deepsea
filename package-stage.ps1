param([string]$OutputDirectory = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$binaryPath = Join-Path $PSScriptRoot 'Midibard/bin/Release'
$manifestPath = Join-Path $binaryPath 'MidiBard2.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$version = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $binaryPath 'MidiBard2.dll')).Version.ToString()
$feed = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'repo.json') -Raw | ConvertFrom-Json)
if ($manifest.InternalName -ne 'MidiBard2' -or $manifest.AssemblyVersion -ne $version -or $manifest.DalamudApiLevel -ne 15) { throw 'Unexpected plugin manifest.' }
if ($manifest.Name -cne 'midibard2-深海回响特供版' -or $manifest.Author -cne 'akira0245, Ori, Kalle, Zune, 断水剑, SevenCat') { throw 'Unexpected plugin branding.' }
$download = "https://github.com/Alittlerecallinglittlebrother/MidiBard2-Deepsea/releases/download/v$version/MidiBard2.zip"
if ($feed.Count -ne 1 -or $feed[0].AssemblyVersion -ne $version -or $feed[0].DalamudApiLevel -ne 15 -or $feed[0].DownloadLinkInstall -ne $download -or $feed[0].DownloadLinkUpdate -ne $download) { throw 'The online feed does not match the release.' }
foreach ($field in @('InternalName', 'Name', 'Author', 'Description', 'Punchline', 'Changelog')) {
    if ($feed[0].$field -cne $manifest.$field) { throw "Feed and manifest differ: $field" }
}
$required = @('MidiBard2.dll', 'MidiBard.Stage.dll', 'BardStage.Core.dll', 'BardMusicPlayer.XIVMIDI.dll', 'Melanchall.DryWetMidi.dll', 'Melanchall_DryWetMidi_Native64.dll')
foreach ($name in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $binaryPath $name) -PathType Leaf)) { throw "Missing dependency: $name" }
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$pluginZip = Join-Path $OutputDirectory 'MidiBard2.zip'
$sourceZip = Join-Path $OutputDirectory 'MidiBard2-source.zip'
$outputManifest = Join-Path $OutputDirectory 'MidiBard2.json'
$checksums = Join-Path $OutputDirectory 'SHA256SUMS.txt'
foreach ($path in @($pluginZip, $sourceZip, $outputManifest, $checksums)) {
    if (Test-Path -LiteralPath $path) { throw "Output already exists: $path" }
}
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
# Include tracked source plus new nonignored source, including a dirty development tree.
$sourceFiles = @(git -C $PSScriptRoot -c core.quotepath=false ls-files --cached --others --exclude-standard | Sort-Object -Unique)
if ($LASTEXITCODE -ne 0 -or $sourceFiles.Count -eq 0) { throw 'Unable to enumerate repository source.' }
foreach ($name in @('Midibard/MidiBard2.csproj', 'Stage/BardStage/RoomMovementCoordinator.cs', 'LICENSE', 'repo.json')) {
    if ($name -notin $sourceFiles) { throw "Missing source: $name" }
}
function Add-ZipFile($archive, [string]$path, [string]$entry) {
    $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $path, $entry.Replace('\', '/'), [IO.Compression.CompressionLevel]::Optimal)
}
$archive = [IO.Compression.ZipFile]::Open($pluginZip, [IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem -LiteralPath $binaryPath -Recurse -File | Sort-Object FullName | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($binaryPath, $_.FullName).Replace('\', '/')
        if ($relative -notmatch '^MidiBard2/' -and $_.Extension -notin @('.pdb', '.zip')) { Add-ZipFile $archive $_.FullName $relative }
    }
    foreach ($name in @('LICENSE', 'README.md', 'STAGE-README.md', 'STAGE-ROOM.md', 'STAGE-IPC.md', 'STAGE-VERIFICATION.md', 'LOCAL-SONG-SYNC.md', 'LOCAL-MANUAL-ASSIGNMENT.md', 'LOCAL-MOVEMENT.md', 'LOCAL-VERIFICATION.md')) {
        Add-ZipFile $archive (Join-Path $PSScriptRoot $name) $name
    }
    Add-ZipFile $archive (Join-Path $PSScriptRoot 'Stage/THIRD-PARTY-NOTICES.txt') 'STAGE-THIRD-PARTY-NOTICES.txt'
}
finally { $archive.Dispose() }
$archive = [IO.Compression.ZipFile]::Open($sourceZip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($relative in $sourceFiles) {
        $path = Join-Path $PSScriptRoot $relative
        if (Test-Path -LiteralPath $path -PathType Leaf) { Add-ZipFile $archive $path ("MidiBard2-Deepsea-$version/" + $relative) }
    }
}
finally { $archive.Dispose() }
Copy-Item -LiteralPath $manifestPath -Destination $outputManifest
$hashes = foreach ($path in @($pluginZip, $sourceZip, $outputManifest)) {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash, [IO.Path]::GetFileName($path)
}
$hashes | Set-Content -LiteralPath $checksums -Encoding utf8NoBOM
$hashes
