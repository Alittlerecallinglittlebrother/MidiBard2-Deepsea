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
    dotnet run --project (Join-Path $PSScriptRoot 'Stage/BardStage.AutoAssignmentTests/BardStage.AutoAssignmentTests.csproj') @runOptions
    if ($LASTEXITCODE -ne 0) { throw 'Automatic assignment checks failed.' }
    dotnet run --project (Join-Path $PSScriptRoot 'Stage/BardStage.PartyPlaybackTests/BardStage.PartyPlaybackTests.csproj') @runOptions
    if ($LASTEXITCODE -ne 0) { throw 'Party playback checks failed.' }
    dotnet run --project (Join-Path $PSScriptRoot 'Stage/BardStage.LiveSwitchTests/BardStage.LiveSwitchTests.csproj') @runOptions
    if ($LASTEXITCODE -ne 0) { throw 'Live instrument switch checks failed.' }
}
dotnet build (Join-Path $PSScriptRoot 'Midibard/MidiBard2.csproj') @buildOptions
if ($LASTEXITCODE -ne 0) { throw 'Integrated plugin build failed.' }
Write-Output 'Build completed. In-game verification is still required.'
