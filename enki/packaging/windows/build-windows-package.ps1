<#
.SYNOPSIS
    Empaqueta Enki para Windows: publica el build self-contained y lo envuelve con installer.iss
    (Inno Setup) para producir un instalador .exe con accesos directos y desinstalador.

.DESCRIPTION
    Pensado para ejecutarse tanto en un PC de desarrollo con Inno Setup instalado (descarga en
    https://jrsoftware.org/isinfo.php) como en el job de Windows de la release en CI, que ya trae
    ISCC.exe preinstalado en los runners windows-latest de GitHub Actions.

.PARAMETER Rid
    Runtime Identifier de .NET. Por defecto win-x64.

.PARAMETER Version
    Versión a incrustar en el ejecutable y en el instalador. Por defecto, el valor de <Version> en
    enki/Directory.Build.props.

.PARAMETER OutputDir
    Carpeta de salida del instalador. Por defecto dist/windows en la raíz del repo.

.PARAMETER SkipPublish
    Reusa enki/publish/<Rid> ya existente en vez de volver a compilar.

.EXAMPLE
    enki\packaging\windows\build-windows-package.ps1
    enki\packaging\windows\build-windows-package.ps1 -Version 0.2.0 -Rid win-x64
#>
param(
    [string]$Rid = "win-x64",
    [string]$Version = "",
    [string]$OutputDir = "",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = (Resolve-Path (Join-Path $ScriptDir "..\..\..")).Path

if ([string]::IsNullOrWhiteSpace($Version)) {
    $propsPath = Join-Path $RepoRoot "enki\Directory.Build.props"
    $propsContent = Get-Content -Raw -Path $propsPath
    $match = [regex]::Match($propsContent, "<Version>([^<]+)</Version>")
    if (-not $match.Success) {
        throw "No se pudo determinar la version desde $propsPath -- pasala explicitamente con -Version X.Y.Z"
    }
    $Version = $match.Groups[1].Value
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $RepoRoot "dist\windows"
}

Write-Host "==> Enki $Version ($Rid)"

$PublishDir = Join-Path $RepoRoot "publish\$Rid"

if (-not $SkipPublish) {
    Write-Host "==> dotnet publish ($Rid, self-contained, single-file)"
    $csproj = Join-Path $RepoRoot "enki\src\enki\enki.csproj"
    dotnet publish $csproj `
        --configuration Release `
        --runtime $Rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:PublishTrimmed=false `
        -p:Version=$Version `
        --output $PublishDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish fallo con codigo $LASTEXITCODE"
    }
} else {
    Write-Host "==> -SkipPublish: reusando $PublishDir"
}

$exePath = Join-Path $PublishDir "enki.exe"
if (-not (Test-Path $exePath)) {
    throw "No se encuentra $exePath -- fallo el publish?"
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$iscc = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
if ($iscc) {
    $isccPath = $iscc.Source
} else {
    $candidates = @(
        "${Env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${Env:ProgramFiles}\Inno Setup 6\ISCC.exe"
    )
    $isccPath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $isccPath) {
        throw "No se encuentra ISCC.exe (Inno Setup). Instalalo desde https://jrsoftware.org/isinfo.php o anadelo al PATH."
    }
}

Write-Host "==> ISCC ($isccPath)"
$issPath = Join-Path $ScriptDir "installer.iss"
& $isccPath $issPath "/DMyAppVersion=$Version" "/DSourceDir=$PublishDir" "/DOutputDir=$OutputDir"
if ($LASTEXITCODE -ne 0) {
    throw "ISCC fallo con codigo $LASTEXITCODE"
}

$installerPath = Join-Path $OutputDir "enki-setup-$Version-win-x64.exe"
Write-Host "==> $installerPath"
Write-Host "==> listo"
