param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Path $outputPath -Force
$binaryPath = Join-Path $PSScriptRoot 'Midibard/bin/Release'
$manifest = Get-Content -LiteralPath (Join-Path $binaryPath 'MidiBard2.json') -Raw | ConvertFrom-Json
$version = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $binaryPath 'MidiBard2.dll')).Version.ToString()
if ($version -ne '3.2.5.24' -or $manifest.AssemblyVersion -ne $version -or $manifest.DalamudApiLevel -ne 15) {
    throw 'Build the 3.2.5.24 / API 15 Release first.'
}
if ($manifest.Name -cne 'midibard2-深海回响特供版' -or $manifest.Author -cne 'akira0245, Ori, Kalle, Zune, 断水剑, SevenCat') {
    throw 'Unexpected branding or author order.'
}
$pluginZip = Join-Path $outputPath 'MidiBard2-Deepsea-3.2.5.24-local.zip'
$sourceZip = Join-Path $outputPath 'MidiBard2-Deepsea-3.2.5.24-local-source.zip'
foreach ($path in @($pluginZip, $sourceZip)) { if (Test-Path -LiteralPath $path) { throw "Output already exists: $path" } }
function Add-ZipFile($archive, [string]$path, [string]$entry) {
    $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $path, $entry.Replace('\', '/'), [IO.Compression.CompressionLevel]::Optimal)
}
$archive = [IO.Compression.ZipFile]::Open($pluginZip, [IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem -LiteralPath $binaryPath -Recurse -File | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($binaryPath, $_.FullName).Replace('\', '/')
        if ($relative -notmatch '^MidiBard2/' -and $_.Extension -notin @('.pdb', '.zip')) {
            Add-ZipFile $archive $_.FullName $relative
        }
    }
    foreach ($name in @('LICENSE', 'LOCAL-SONG-SYNC.md', 'STAGE-README.md', 'STAGE-ROOM.md', 'STAGE-IPC.md', 'LOCAL-VERIFICATION.md', 'LOCAL-MANUAL-ASSIGNMENT.md', 'LOCAL-MOVEMENT.md')) {
        Add-ZipFile $archive (Join-Path $PSScriptRoot $name) $name
    }
    Add-ZipFile $archive (Join-Path $PSScriptRoot 'Stage/THIRD-PARTY-NOTICES.txt') 'STAGE-THIRD-PARTY-NOTICES.txt'
}
finally { $archive.Dispose() }
$archive = [IO.Compression.ZipFile]::Open($sourceZip, [IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem -LiteralPath $PSScriptRoot -Recurse -File -Force | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($PSScriptRoot, $_.FullName).Replace('\', '/')
        if ($relative -notmatch '(^|/)(\.git|bin|obj|verification|artifacts)(/|$)' -and $_.Extension -notin @('.log', '.zip')) {
            Add-ZipFile $archive $_.FullName ('MidiBard2-Deepsea-3.2.5.24-local/' + $relative)
        }
    }
}
finally { $archive.Dispose() }
foreach ($path in @($pluginZip, $sourceZip)) {
    $zip = [IO.Compression.ZipFile]::OpenRead($path)
    try {
        foreach ($entry in $zip.Entries) {
            $stream = $entry.Open()
            try { $stream.CopyTo([IO.Stream]::Null) } finally { $stream.Dispose() }
        }
        if ($zip.Entries.Count -eq 0) { throw "Empty ZIP: $path" }
    }
    finally { $zip.Dispose() }
}
$hashes = foreach ($path in @($pluginZip, $sourceZip)) {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash, [IO.Path]::GetFileName($path)
}
$hashes | Set-Content -LiteralPath (Join-Path $outputPath 'SHA256SUMS.txt') -Encoding utf8
$hashes
