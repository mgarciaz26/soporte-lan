param(
    [switch]$Zip
)

$ErrorActionPreference = "Stop"

$viewer = Split-Path $PSScriptRoot -Parent
Set-Location $viewer

if (-not (Test-Path (Join-Path $viewer "node_modules"))) {
    Write-Host "Instalando dependencias..." -ForegroundColor Cyan
    npm install
}

Write-Host "Compilando bridge e interfaz..." -ForegroundColor Cyan
npm run build
if ($LASTEXITCODE -ne 0) { throw "Falló la compilación." }

$out = Join-Path $viewer "dist-visor"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out | Out-Null

# 1. Interfaz web compilada (la sirve el propio bridge).
Copy-Item (Join-Path $viewer "ui\dist") (Join-Path $out "ui") -Recurse

# 2. Bridge en un único archivo, sin node_modules.
Write-Host "Empaquetando el bridge..." -ForegroundColor Cyan
$esbuild = Join-Path $viewer "node_modules\.bin\esbuild.cmd"
& $esbuild "bridge\src\index.ts" --bundle --platform=node --format=cjs --target=node20 `
    "--outfile=$out\visor.cjs" --external:bufferutil --external:utf-8-validate --log-level=warning
if ($LASTEXITCODE -ne 0) { throw "Falló el empaquetado del bridge." }

# 3. Node portátil: el técnico no necesita instalar nada.
$node = (Get-Command node).Source
if (-not $node) { throw "No encuentro node.exe en el PATH." }
Copy-Item $node (Join-Path $out "node.exe")

# 4. Lanzador de doble clic.
Copy-Item (Join-Path $PSScriptRoot "Iniciar visor.vbs") (Join-Path $out "Iniciar visor.vbs")

# 5. Instrucciones.
@"
Soporte LAN - Visor
===================

1. Doble clic en "Iniciar visor.vbs".
2. Se abre el navegador en http://127.0.0.1:7788
3. Elige el equipo de la lista (o escribe su IP), teclea el PIN y pulsa Conectar.

No hace falta instalar nada: esta carpeta ya incluye Node.
El visor se queda en segundo plano, sin ventana de consola. Cuando cierras la
pestana del navegador se apaga solo a los 2 minutos.

Si vuelves a hacer doble clic con el visor ya abierto, solo abre la pestana.

Registro: %LOCALAPPDATA%\SoporteLAN\viewer.log
"@ | Set-Content (Join-Path $out "LEEME.txt") -Encoding UTF8

if ($Zip) {
    # Ojo: $zip chocaría con el parámetro -Zip (PowerShell no distingue mayúsculas).
    $zipFile = Join-Path $viewer "SoporteLAN-Visor.zip"
    if (Test-Path $zipFile) { Remove-Item $zipFile -Force }
    Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zipFile
    Write-Host "ZIP: $zipFile" -ForegroundColor Green
}

$size = [math]::Round(((Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host ""
Write-Host "Listo: $out ($size MB)" -ForegroundColor Green
Write-Host "Doble clic en: $out\Iniciar visor.vbs" -ForegroundColor Green
