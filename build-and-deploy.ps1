<#
.SYNOPSIS
  Compila o AcadMcp e instala o bundle em %APPDATA%\Autodesk\ApplicationPlugins\AcadMcp.bundle.

.PARAMETER Configuration
  Release (padrão) ou Debug.

.PARAMETER NoDeploy
  Só compila, não copia o bundle.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\build-and-deploy.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$NoDeploy
)

$ErrorActionPreference = 'Stop'
$root    = $PSScriptRoot
$project = Join-Path $root 'src\AcadMcp\AcadMcp.csproj'
$outDir  = Join-Path $root "src\AcadMcp\bin\$Configuration\net10.0-windows"
$bundle  = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\AcadMcp.bundle'
$contents = Join-Path $bundle 'Contents'

Write-Host "==> dotnet build ($Configuration)" -ForegroundColor Cyan
dotnet build $project -c $Configuration -nologo
if ($LASTEXITCODE -ne 0) { throw "Falha na compilação (exit $LASTEXITCODE)." }

if ($NoDeploy) { Write-Host 'Compilado. Deploy ignorado (-NoDeploy).'; return }

# A DLL carregada fica travada pelo acad.exe; detecta isso antes de copiar pela metade.
$installedDll = Join-Path $contents 'AcadMcp.dll'
if (Test-Path $installedDll) {
    try {
        $fs = [IO.File]::Open($installedDll, 'Open', 'ReadWrite', 'None'); $fs.Close()
    } catch {
        throw "O AutoCAD está com $installedDll carregado. Feche o AutoCAD e rode o script de novo."
    }
}

Write-Host "==> Instalando em $bundle" -ForegroundColor Cyan
if (Test-Path $contents) { Remove-Item $contents -Recurse -Force }
New-Item -ItemType Directory -Force $contents | Out-Null

Copy-Item (Join-Path $root 'bundle\PackageContents.xml') $bundle -Force
Get-ChildItem $outDir -File |
    Where-Object { $_.Extension -in '.dll', '.pdb', '.json' } |
    Copy-Item -Destination $contents -Force

Get-ChildItem $contents | Select-Object Name, Length | Format-Table -AutoSize

if (Get-Process acad -ErrorAction SilentlyContinue) {
    Write-Host 'AutoCAD está aberto: reinicie-o para carregar o plugin.' -ForegroundColor Yellow
} else {
    Write-Host 'Pronto. Abra o AutoCAD 2027; o servidor sobe sozinho (MCPSTATUS para conferir).' -ForegroundColor Green
}
