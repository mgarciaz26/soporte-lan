param(
    [string]$Exe = "",
    [switch]$Start
)

$ErrorActionPreference = "Stop"

if (-not $Exe) {
    $candidate = Join-Path $PSScriptRoot "..\bin\Release\net9.0-windows\win-x64\publish\soporte-agent.exe"
    if (-not (Test-Path $candidate)) {
        throw "No encuentro el ejecutable. Ejecuta primero .\scripts\publish.ps1 o pasa -Exe <ruta>."
    }
    $Exe = (Resolve-Path $candidate).Path
}

$targetDirectory = Join-Path $env:LOCALAPPDATA "SoporteLAN"
New-Item -ItemType Directory -Force -Path $targetDirectory | Out-Null

# Si el agente está corriendo, el .exe está bloqueado: hay que cerrarlo antes.
Get-Process soporte-agent -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

$target = Join-Path $targetDirectory "soporte-agent.exe"
Copy-Item $Exe $target -Force

$startup = [Environment]::GetFolderPath("Startup")
$link = Join-Path $startup "Soporte LAN.lnk"

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($link)
$shortcut.TargetPath = $target
$shortcut.Arguments = "--quiet"   # al iniciar sesión, sin ventana emergente
$shortcut.WorkingDirectory = $targetDirectory
$shortcut.WindowStyle = 1   # normal (el agente no muestra ventana)
$shortcut.Description = "Soporte LAN - agente"
$shortcut.Save()

Write-Host "Agente copiado a: $target" -ForegroundColor Green
Write-Host "Arranca al iniciar sesión mediante: $link" -ForegroundColor Green
Write-Host ""
Write-Host "El agente NO se ejecuta antes del login (modo asistido)." -ForegroundColor DarkGray

if ($Start) {
    Start-Process $target
}
