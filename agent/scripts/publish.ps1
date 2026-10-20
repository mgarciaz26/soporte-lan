param(
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$project = Split-Path $PSScriptRoot -Parent

Write-Host "Publicando agente ($Runtime)..." -ForegroundColor Cyan
dotnet publish $project -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:PublishTrimmed=true --nologo

$output = Join-Path $project "bin\Release\net9.0-windows\$Runtime\publish\soporte-agent.exe"

if (Test-Path $output) {
    $size = [math]::Round((Get-Item $output).Length / 1MB, 2)
    Write-Host ""
    Write-Host "Listo: $output ($size MB)" -ForegroundColor Green
    Write-Host "Instala el autostart con: .\scripts\install-autostart.ps1" -ForegroundColor DarkGray
}
else {
    throw "No se generó el ejecutable."
}
