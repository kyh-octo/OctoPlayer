# OctoPlayer 설치 파일 빌드 스크립트
# 사용법: powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 [-SkipWebsite]
# 결과물: installer\output\OctoPlayer-Setup-<버전>.exe
# 빌드가 끝나면 update-website.ps1을 호출해 octo-brain.com 배포 섹션(웹사이트 릴리스 + store.html)을 자동 갱신한다.
# -SkipWebsite 를 주면 웹사이트 갱신을 건너뛴다 (로컬 테스트 빌드용).
# Git 최신 커밋 기준 원클릭 릴리즈(프로젝트 GitHub 릴리스 포함)는 release.bat / installer\release.ps1 을 사용한다.

param(
    [switch]$SkipWebsite,
    [switch]$RequireSignedRelease,
    [string]$CoAuthor = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$csproj = Join-Path $root "OctoPlayer.csproj"
$publishDir = Join-Path $root "bin\Release\Publish"
# Credentials and signing metadata stay external to the repository.
$signingEnabled = $env:OCTO_CODESIGN -eq '1'
if ($RequireSignedRelease -and -not $signingEnabled) { throw '-RequireSignedRelease requires OCTO_CODESIGN=1.' }
if ($signingEnabled) {
    if ($env:OCTO_SIGN_PROVIDER -ne 'ArtifactSigning') { throw 'Set OCTO_SIGN_PROVIDER=ArtifactSigning.' }
    foreach ($key in @('OCTO_SIGNTOOL','OCTO_SIGN_DLIB','OCTO_SIGN_METADATA')) {
        $value = [Environment]::GetEnvironmentVariable($key)
        if (-not $value -or $value -notmatch '^(?:[A-Za-z]:\\|\\\\[^\\]+\\[^\\]+\\)' -or -not (Test-Path -LiteralPath $value -PathType Leaf)) { throw "$key must point to an existing absolute file path." }
    }
    $SkipWebsite = $true
}


# 1) csproj에서 버전 읽기
$version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { $version = "1.0.0" }
$version = "$version".Trim()
Write-Host "== OctoPlayer v$version 설치 파일 빌드 ==" -ForegroundColor Cyan

# 2) 게시 (자체 포함 - 대상 PC에 .NET 설치 불필요)
Write-Host "[1/2] dotnet publish..." -ForegroundColor Yellow
$expectedPublish = [IO.Path]::GetFullPath((Join-Path $root 'bin\Release\Publish'))
if ([IO.Path]::GetFullPath($publishDir) -ne $expectedPublish) { throw 'Unexpected publish removal path.' }
if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
dotnet publish $csproj -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:DebugType=none -o $publishDir -v q -nologo
if ($LASTEXITCODE -ne 0) { throw "게시 실패 (exit $LASTEXITCODE)" }

foreach ($requiredNotice in @('LICENSE','THIRD-PARTY-NOTICES.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDir $requiredNotice) -PathType Leaf)) { throw "Required distribution notice missing: $requiredNotice" }
}
if (-not (Test-Path -LiteralPath (Join-Path $publishDir 'licenses') -PathType Container)) { throw 'Bundled license directory missing.' }

# 2-1) 불필요한 libvlc 아키텍처 제거 (x64만 배포)
foreach ($arch in @("win-x86", "win-arm64")) {
    $dir = Join-Path $publishDir "libvlc\$arch"
    if (-not ([IO.Path]::GetFullPath($dir).StartsWith($expectedPublish + '\', [StringComparison]::OrdinalIgnoreCase))) { throw 'Unexpected architecture removal path.' }
    if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
}

if ($signingEnabled) {
    & (Join-Path $PSScriptRoot 'sign-file.ps1') -Path (Join-Path $publishDir 'OctoPlayer.exe') -Provider ArtifactSigning -SignTool $env:OCTO_SIGNTOOL -Dlib $env:OCTO_SIGN_DLIB -Metadata $env:OCTO_SIGN_METADATA
}

# 3) Inno Setup 컴파일
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6을 찾을 수 없습니다. winget install -e --id JRSoftware.InnoSetup 으로 설치하세요." }

Write-Host "[2/2] Inno Setup 컴파일..." -ForegroundColor Yellow
$isccArgs = @("/DAppVersion=$version")
if ($signingEnabled) {
    $signFile = Join-Path $PSScriptRoot 'sign-file.ps1'
    $signingShell = Join-Path $PSHOME $(if ($PSVersionTable.PSEdition -eq 'Core') { 'pwsh.exe' } else { 'powershell.exe' })
    $signCommand = '$q' + $signingShell + '$q -NoProfile -ExecutionPolicy Bypass -File $q' + $signFile + '$q -Path $f -Provider ArtifactSigning -SignTool $q' + $env:OCTO_SIGNTOOL + '$q -Dlib $q' + $env:OCTO_SIGN_DLIB + '$q -Metadata $q' + $env:OCTO_SIGN_METADATA + '$q'
    $isccArgs += '/DSignedBuild'
    $isccArgs += "/SOctoSign=$signCommand"
}
$isccArgs += (Join-Path $PSScriptRoot 'OctoPlayer.iss')
& $iscc @isccArgs
if ($LASTEXITCODE -ne 0) { throw "설치 파일 컴파일 실패 (exit $LASTEXITCODE)" }

$setup = Join-Path $PSScriptRoot "output\OctoPlayer-Setup-$version.exe"
if (Test-Path $setup) {
    $mb = [math]::Round((Get-Item $setup).Length / 1MB, 1)
    Write-Host "완료: $setup ($mb MB)" -ForegroundColor Green
} else {
    throw "설치 파일이 생성되지 않았습니다."
}

if ($signingEnabled) {
    & (Join-Path $PSScriptRoot 'verify-signature.ps1') -Path $setup -SignTool $env:OCTO_SIGNTOOL
    $checksum = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($setup)
    [IO.File]::WriteAllText((Join-Path (Split-Path $setup) 'SHA256SUMS.txt'), $checksum + "`n", [Text.UTF8Encoding]::new($false))
}

# 4) octo-brain.com 배포 섹션 갱신 (실패해도 설치 파일 빌드 자체는 성공으로 둔다)
if (-not $SkipWebsite) {
    Write-Host "[3/3] octo-brain.com 배포 갱신..." -ForegroundColor Yellow
    try {
        & (Join-Path $PSScriptRoot "update-website.ps1") -AppName "OctoPlayer" -Version $version -InstallerPath $setup -CoAuthor $CoAuthor
    } catch {
        Write-Host "경고: 웹사이트 갱신 실패 - $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "      수동 실행: powershell -ExecutionPolicy Bypass -File installer\update-website.ps1 -AppName OctoPlayer -Version $version -InstallerPath `"$setup`"" -ForegroundColor Red
    }
}
