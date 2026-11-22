# Builds the minimap into .\dist: GroundedMinimap.exe + extractor\
# Requires the .NET 10 SDK (winget install Microsoft.DotNet.SDK.10)
# -Version stamps the exe (used by the release workflow), e.g. .\build.ps1 -Version 1.0.42
param([string]$Version = "")
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Get-Process GroundedMinimap -ErrorAction SilentlyContinue | Stop-Process -Force

$props = @()
if ($Version) { $props += "-p:Version=$Version" }

dotnet publish .\Overlay\GroundedMinimap.csproj -c Release -o .\dist @props
if ($LASTEXITCODE -ne 0) { throw 'Minimap build failed' }
Remove-Item .\dist\*.pdb -ErrorAction SilentlyContinue

# Map/icon extractor (reads textures from the game's .pak files when a level or patch needs it)
dotnet publish .\Extractor\Extractor.csproj -c Release -o .\dist\extractor @props
if ($LASTEXITCODE -ne 0) { throw 'Extractor build failed' }
Remove-Item .\dist\extractor\*.pdb -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Done: $PSScriptRoot\dist\GroundedMinimap.exe" -ForegroundColor Green
