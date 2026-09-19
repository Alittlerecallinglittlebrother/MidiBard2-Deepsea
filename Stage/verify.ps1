param([string]$ScreenshotDirectory = (Join-Path $PSScriptRoot 'verification'))
$ErrorActionPreference = 'Stop'
dotnet test (Join-Path $PSScriptRoot 'BardStage.Core.Tests/BardStage.Core.Tests.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Core verification failed.' }
dotnet run --project (Join-Path $PSScriptRoot 'BardStage.RuntimeTests/BardStage.RuntimeTests.csproj') -c Release -- $ScreenshotDirectory
if ($LASTEXITCODE -ne 0) { throw 'ImGui runtime verification failed.' }
