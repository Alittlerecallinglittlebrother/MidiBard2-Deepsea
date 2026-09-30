param([string]$DalamudLibPath = '', [switch]$SkipChecks, [string]$VerificationDirectory = (Join-Path $PSScriptRoot 'verification'))
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
    dotnet run --project (Join-Path $PSScriptRoot 'Stage/BardStage.RuntimeTests/BardStage.RuntimeTests.csproj') @runOptions -- $VerificationDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Runtime checks failed.' }
    dotnet run --project (Join-Path $PSScriptRoot 'Stage/BardStage.RuntimeTests/BardStage.RuntimeTests.csproj') @runOptions -- --workspace-ui (Join-Path $VerificationDirectory 'workspace-ui')
    if ($LASTEXITCODE -ne 0) { throw 'Onboarding and workspace UI checks failed.' }
    dotnet run --project (Join-Path $PSScriptRoot 'Stage/BardStage.OutputTests/BardStage.OutputTests.csproj') @runOptions
    if ($LASTEXITCODE -ne 0) { throw 'Compensated output tail checks failed.' }
    dotnet run --project (Join-Path $PSScriptRoot 'Stage/BardStage.RuntimeTests/BardStage.RuntimeTests.csproj') @runOptions -- --song-sync-check (Join-Path $VerificationDirectory 'song-sync')
    if ($LASTEXITCODE -ne 0) { throw 'Song transfer runtime checks failed.' }
    dotnet run --project (Join-Path $PSScriptRoot 'Stage/BardStage.RuntimeTests/BardStage.RuntimeTests.csproj') @runOptions -- --song-sync-ui (Join-Path $VerificationDirectory 'song-sync-ui')
    if ($LASTEXITCODE -ne 0) { throw 'Song transfer UI checks failed.' }
    dotnet run --project (Join-Path $PSScriptRoot 'Stage/BardStage.RuntimeTests/BardStage.RuntimeTests.csproj') @runOptions -- --manual-assignment-check (Join-Path $VerificationDirectory 'manual-assignment')
    if ($LASTEXITCODE -ne 0) { throw 'Manual assignment transport checks failed.' }
    dotnet run --project (Join-Path $PSScriptRoot 'Stage/BardStage.RuntimeTests/BardStage.RuntimeTests.csproj') @runOptions -- --manual-assignment-ui (Join-Path $VerificationDirectory 'manual-ui')
    if ($LASTEXITCODE -ne 0) { throw 'Manual assignment UI checks failed.' }
    dotnet run --project (Join-Path $PSScriptRoot 'Stage/BardStage.RuntimeTests/BardStage.RuntimeTests.csproj') @runOptions -- --movement-check (Join-Path $VerificationDirectory 'movement')
    if ($LASTEXITCODE -ne 0) { throw 'Movement transport checks failed.' }
    dotnet run --project (Join-Path $PSScriptRoot 'Stage/BardStage.RuntimeTests/BardStage.RuntimeTests.csproj') @runOptions -- --movement-ui (Join-Path $VerificationDirectory 'movement-ui')
    if ($LASTEXITCODE -ne 0) { throw 'Movement UI checks failed.' }
    dotnet run --project (Join-Path $PSScriptRoot 'Stage/BardStage.RuntimeTests/BardStage.RuntimeTests.csproj') @runOptions -- --large-ensemble-check (Join-Path $VerificationDirectory 'large-ensemble')
    if ($LASTEXITCODE -ne 0) { throw 'Eight-performer transport checks failed.' }
    dotnet run --project (Join-Path $PSScriptRoot 'Stage/BardStage.RuntimeTests/BardStage.RuntimeTests.csproj') @runOptions -- --network-timing-check (Join-Path $VerificationDirectory 'network-timing')
    if ($LASTEXITCODE -ne 0) { throw 'Eight-process network timing checks failed.' }
    dotnet run --project (Join-Path $PSScriptRoot 'Stage/BardStage.RuntimeTests/BardStage.RuntimeTests.csproj') @runOptions -- --large-ensemble-ui (Join-Path $VerificationDirectory 'large-ensemble-ui')
    if ($LASTEXITCODE -ne 0) { throw 'Eight-performer UI checks failed.' }
    dotnet run --project (Join-Path $PSScriptRoot 'Stage/BardStage.RuntimeTests/BardStage.RuntimeTests.csproj') @runOptions -- --local-ensemble-check (Join-Path $VerificationDirectory 'local-ensemble')
    if ($LASTEXITCODE -ne 0) { throw 'Same-computer ensemble process checks failed.' }
    dotnet run --project (Join-Path $PSScriptRoot 'Stage/BardStage.RuntimeTests/BardStage.RuntimeTests.csproj') @runOptions -- --local-ensemble-ui (Join-Path $VerificationDirectory 'local-ensemble-ui')
    if ($LASTEXITCODE -ne 0) { throw 'Same-computer ensemble UI checks failed.' }
    dotnet run --project (Join-Path $PSScriptRoot 'Stage/BardStage.RuntimeTests/BardStage.RuntimeTests.csproj') @runOptions -- --public-capacity-check
    if ($LASTEXITCODE -ne 0) { throw 'Public eight-performer capacity checks failed.' }
    dotnet run --project (Join-Path $PSScriptRoot 'Stage/BardStage.AutoAssignmentTests/BardStage.AutoAssignmentTests.csproj') @runOptions
    if ($LASTEXITCODE -ne 0) { throw 'Automatic assignment checks failed.' }
    dotnet run --project (Join-Path $PSScriptRoot 'Stage/BardStage.PartyPlaybackTests/BardStage.PartyPlaybackTests.csproj') @runOptions
    if ($LASTEXITCODE -ne 0) { throw 'Party playback checks failed.' }
}
dotnet build (Join-Path $PSScriptRoot 'Midibard/MidiBard2.csproj') @buildOptions
if ($LASTEXITCODE -ne 0) { throw 'Integrated plugin build failed.' }
Write-Output 'Build completed. In-game verification is still required.'
