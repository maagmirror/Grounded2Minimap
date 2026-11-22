# Compiles the minimap and leaves the exe in .\dist\GroundedMinimap.exe
# Requires the .NET 10 SDK (winget install Microsoft.DotNet.SDK.10)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Get-Process GroundedMinimap -ErrorAction SilentlyContinue | Stop-Process -Force

dotnet publish .\Overlay\GroundedMinimap.csproj -c Release -o .\dist
if ($LASTEXITCODE -ne 0) { throw 'Fallo la compilacion del minimapa' }
Remove-Item .\dist\*.pdb -ErrorAction SilentlyContinue

# Map extractor (reads the map texture from the game's .pak files when a level or patch needs it)
dotnet publish .\Extractor\Extractor.csproj -c Release -o .\dist\extractor
if ($LASTEXITCODE -ne 0) { throw 'Fallo la compilacion del extractor' }
Remove-Item .\dist\extractor\*.pdb -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Listo: $PSScriptRoot\dist\GroundedMinimap.exe" -ForegroundColor Green
