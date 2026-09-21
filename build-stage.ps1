param([string]$DalamudLibPath = '', [switch]$SkipChecks)
$ErrorActionPreference = 'Stop'
if ($DalamudLibPath) {
    $DalamudLibPath = [IO.Path]::GetFullPath($DalamudLibPath).Replace('\', '/').TrimEnd('/') + '/'
    $env:DALAMUD_HOME = $DalamudLibPath
}
$buildOptions = @('-c', 'Release', '--nologo')
if ($DalamudLibPath) { $buildOptions += "-p:DalamudLibPath=$DalamudLibPath" }
if (-not $SkipChecks) {
    dotnet test (Join-Path $PSScriptRoot 'Stage/BardStage.Core.Tests/BardStage.Core.Tests.csproj') @buildOptions
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
    $runOptions = @($buildOptions | Where-Object { $_ -ne '--nologo' })
    dotnet run --project (Join-Path $PSScriptRoot 'Stage/BardStage.RuntimeTests/BardStage.RuntimeTests.csproj') @runOptions -- (Join-Path $PSScriptRoot 'verification')
    if ($LASTEXITCODE -ne 0) { throw 'Runtime checks failed.' }
    dotnet run --project (Join-Path $PSScriptRoot 'Stage/BardStage.RuntimeTests/BardStage.RuntimeTests.csproj') @runOptions -- --song-sync-check (Join-Path $PSScriptRoot 'verification/song-sync')
    if ($LASTEXITCODE -ne 0) { throw 'Song transfer runtime checks failed.' }
    dotnet run --project (Join-Path $PSScriptRoot 'Stage/BardStage.RuntimeTests/BardStage.RuntimeTests.csproj') @runOptions -- --song-sync-ui (Join-Path $PSScriptRoot 'verification/song-sync-ui')
    if ($LASTEXITCODE -ne 0) { throw 'Song transfer UI checks failed.' }
    dotnet run --project (Join-Path $PSScriptRoot 'Stage/BardStage.RuntimeTests/BardStage.RuntimeTests.csproj') @runOptions -- --manual-assignment-check (Join-Path $PSScriptRoot 'verification/manual-assignment')
    if ($LASTEXITCODE -ne 0) { throw 'Manual assignment transport checks failed.' }
    dotnet run --project (Join-Path $PSScriptRoot 'Stage/BardStage.RuntimeTests/BardStage.RuntimeTests.csproj') @runOptions -- --manual-assignment-ui (Join-Path $PSScriptRoot 'verification/manual-ui')
    if ($LASTEXITCODE -ne 0) { throw 'Manual assignment UI checks failed.' }
    dotnet run --project (Join-Path $PSScriptRoot 'Stage/BardStage.RuntimeTests/BardStage.RuntimeTests.csproj') @runOptions -- --movement-check (Join-Path $PSScriptRoot 'verification/movement')
    if ($LASTEXITCODE -ne 0) { throw 'Movement transport checks failed.' }
    dotnet run --project (Join-Path $PSScriptRoot 'Stage/BardStage.RuntimeTests/BardStage.RuntimeTests.csproj') @runOptions -- --movement-ui (Join-Path $PSScriptRoot 'verification/movement-ui')
    if ($LASTEXITCODE -ne 0) { throw 'Movement UI checks failed.' }
    dotnet run --project (Join-Path $PSScriptRoot 'Stage/BardStage.AutoAssignmentTests/BardStage.AutoAssignmentTests.csproj') @runOptions
    if ($LASTEXITCODE -ne 0) { throw 'Automatic assignment checks failed.' }
    dotnet run --project (Join-Path $PSScriptRoot 'Stage/BardStage.PartyPlaybackTests/BardStage.PartyPlaybackTests.csproj') @runOptions
    if ($LASTEXITCODE -ne 0) { throw 'Party playback checks failed.' }
}
dotnet build (Join-Path $PSScriptRoot 'Midibard/MidiBard2.csproj') @buildOptions
if ($LASTEXITCODE -ne 0) { throw 'Integrated plugin build failed.' }
Write-Output 'Build completed. In-game verification is still required.'
