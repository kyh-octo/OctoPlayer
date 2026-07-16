# ============================================================================
# OctoPlayer 설치 파일 빌드 스크립트
#   1) self-contained 게시 (대상 PC에 .NET 설치 불필요)
#   2) 불필요한 아키텍처(libvlc x86/arm64) 제거
#   3) Inno Setup으로 설치 파일 컴파일
# 실행: powershell -ExecutionPolicy Bypass -File build.ps1
# 결과: Setup\Output\OctoPlayer-Setup-x64.exe
# ============================================================================
param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$setupDir = $PSScriptRoot
$projectDir = Split-Path $setupDir -Parent
$publishDir = Join-Path $setupDir "publish"

Write-Host "[1/3] dotnet publish ($Configuration, win-x64, self-contained, ReadyToRun)..."
# PublishReadyToRun: IL을 미리 네이티브로 컴파일해 콜드 스타트(JIT) 시간을 크게 줄입니다.
dotnet publish (Join-Path $projectDir "OctoPlayer.csproj") `
    -c $Configuration -r win-x64 --self-contained true -p:PublishReadyToRun=true -o $publishDir -nologo
if ($LASTEXITCODE -ne 0) { Write-Error "게시 실패"; exit 1 }

Write-Host "[2/3] 불필요한 libvlc 아키텍처 제거..."
foreach ($arch in @("win-x86", "win-arm64")) {
    $dir = Join-Path $publishDir "libvlc\$arch"
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
}

Write-Host "[3/3] Inno Setup 컴파일..."
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Error "Inno Setup(ISCC.exe)을 찾을 수 없습니다. 설치: winget install -e --id JRSoftware.InnoSetup"
    exit 1
}

& $iscc (Join-Path $setupDir "OctoPlayer.iss")
if ($LASTEXITCODE -ne 0) { Write-Error "설치 파일 컴파일 실패"; exit 1 }

$output = Join-Path $setupDir "Output\OctoPlayer-Setup-x64.exe"
Write-Host ""
Write-Host "완료: $output ($([math]::Round((Get-Item $output).Length / 1MB)) MB)"
