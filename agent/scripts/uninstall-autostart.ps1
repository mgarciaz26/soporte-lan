$ErrorActionPreference = "SilentlyContinue"

Stop-Process -Name "soporte-agent" -Force
Start-Sleep -Milliseconds 300

$link = Join-Path ([Environment]::GetFolderPath("Startup")) "Soporte LAN.lnk"
Remove-Item $link -Force

$targetDirectory = Join-Path $env:LOCALAPPDATA "SoporteLAN"
Remove-Item $targetDirectory -Recurse -Force

Write-Host "Autostart eliminado y agente desinstalado." -ForegroundColor Green
