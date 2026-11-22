# Compiles the minimap and leaves the exe in .\dist\GroundedMinimap.exe
# Requires the .NET 10 SDK (winget install Microsoft.DotNet.SDK.10)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Get-Process GroundedMinimap -ErrorAction SilentlyContinue | Stop-Process -Force

dotnet publish .\Overlay\GroundedMinimap.csproj -c Release -o .\dist
Remove-Item .\dist\*.pdb -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Listo: $PSScriptRoot\dist\GroundedMinimap.exe" -ForegroundColor Green
