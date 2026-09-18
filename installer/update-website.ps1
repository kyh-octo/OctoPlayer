# octo-brain.com 배포 섹션 갱신 스크립트 (OctoCapture / OctoPlayer / OctoConverter 공통)
# 1) 웹사이트 저장소(kyh-octo/octobrain-website)에 릴리스 <app>-v<버전> 생성/갱신 + 설치 파일 업로드
# 2) store.html의 해당 앱 카드(버전/용량/다운로드/릴리스 노트 링크) 갱신 후 커밋·푸시 → GitHub Pages 자동 배포
# 사용법: powershell -ExecutionPolicy Bypass -File installer\update-website.ps1 -AppName OctoPlayer -Version 1.2.0 -InstallerPath <exe>
# (release.ps1 / build-installer.ps1 이 설치 파일 빌드 후 자동으로 호출한다)

param(
    [Parameter(Mandatory = $true)] [string]$AppName,
    [Parameter(Mandatory = $true)] [string]$Version,
    [Parameter(Mandatory = $true)] [string]$InstallerPath,
    [string]$NotesFile = "",
    [string]$SiteRepoDir = (Join-Path $env:LOCALAPPDATA "OctoBrain\octobrain-website"),
    [string]$CoAuthor = ""
)

$ErrorActionPreference = "Stop"
$SiteRepo = "kyh-octo/octobrain-website"
$SiteRepoUrl = "https://github.com/$SiteRepo.git"
$TagPrefix = $AppName.ToLowerInvariant()
$Tag = "$TagPrefix-v$Version"

if (-not (Test-Path $InstallerPath)) { throw "설치 파일을 찾을 수 없습니다: $InstallerPath" }
$installerName = Split-Path $InstallerPath -Leaf
$sizeMB = [math]::Round((Get-Item $InstallerPath).Length / 1MB)

$gh = @("$env:ProgramFiles\GitHub CLI\gh.exe", "${env:ProgramFiles(x86)}\GitHub CLI\gh.exe", "$env:LOCALAPPDATA\Programs\GitHub CLI\gh.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $gh) { $cmd = Get-Command gh -ErrorAction SilentlyContinue; if ($cmd) { $gh = $cmd.Source } }
if (-not $gh) { throw "GitHub CLI(gh)를 찾을 수 없습니다." }

Write-Host "== octo-brain.com 배포 갱신: $AppName v$Version ==" -ForegroundColor Cyan

# ---------- 1) 웹사이트 저장소 릴리스 생성/갱신 ----------
if (-not $NotesFile) {
    $NotesFile = Join-Path $env:TEMP "$TagPrefix-site-notes-$Version.md"
    $notes = "$AppName v$Version 설치 파일입니다. ``$installerName`` 을 내려받아 실행하세요 (.NET 설치 불필요, Windows 10/11 x64).`n`n" +
             "자세한 변경 내역: https://github.com/kyh-octo/$AppName/releases/tag/v$Version`n"
    [System.IO.File]::WriteAllText($NotesFile, $notes, (New-Object System.Text.UTF8Encoding($false)))
}

# 주의: PowerShell 5.1에서는 네이티브 명령의 stderr를 리다이렉션하면 오류로 승격되므로
#       (gh release view의 "release not found") 리다이렉션 없이 목록으로 존재 여부를 확인한다.
$existingTags = @(& $gh release list -R $SiteRepo --limit 200 --json tagName --jq '.[].tagName')
if ($LASTEXITCODE -ne 0) { throw "웹사이트 저장소 릴리스 목록 조회 실패" }
if ($existingTags -contains $Tag) {
    Write-Host "[1/3] 릴리스 $Tag 존재 → 설치 파일 교체 업로드" -ForegroundColor Yellow
    & $gh release upload $Tag $InstallerPath -R $SiteRepo --clobber
    if ($LASTEXITCODE -ne 0) { throw "릴리스 자산 업로드 실패" }
    & $gh release edit $Tag -R $SiteRepo --notes-file $NotesFile --latest | Out-Null
} else {
    Write-Host "[1/3] 릴리스 $Tag 생성 + 설치 파일 업로드" -ForegroundColor Yellow
    & $gh release create $Tag $InstallerPath -R $SiteRepo --title "$AppName $Version" --notes-file $NotesFile --latest
    if ($LASTEXITCODE -ne 0) { throw "릴리스 생성 실패" }
}

# ---------- 2) 자동화 전용 클론을 origin/main에 동기화 ----------
Write-Host "[2/3] 웹사이트 저장소 동기화 ($SiteRepoDir)" -ForegroundColor Yellow
if (-not (Test-Path (Join-Path $SiteRepoDir ".git"))) {
    New-Item -ItemType Directory -Force (Split-Path $SiteRepoDir -Parent) | Out-Null
    git clone --quiet $SiteRepoUrl $SiteRepoDir
    if ($LASTEXITCODE -ne 0) { throw "웹사이트 저장소 클론 실패" }
} else {
    git -C $SiteRepoDir fetch --quiet origin
    git -C $SiteRepoDir reset --quiet --hard origin/main
    if ($LASTEXITCODE -ne 0) { throw "웹사이트 저장소 동기화 실패" }
}

# ---------- 3) store.html의 앱 카드 갱신 ----------
$storePath = Join-Path $SiteRepoDir "store.html"
if (-not (Test-Path $storePath)) { throw "store.html을 찾을 수 없습니다: $storePath" }

$bytes = [System.IO.File]::ReadAllBytes($storePath)
$hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
$html = [System.Text.Encoding]::UTF8.GetString($bytes)
if ($hasBom) { $html = $html.TrimStart([char]0xFEFF) }

$titleIdx = $html.IndexOf("<h3 class=""dl-title"">$AppName</h3>")
if ($titleIdx -lt 0) { throw "store.html에서 $AppName 카드를 찾을 수 없습니다." }
$start = $html.LastIndexOf('<article', $titleIdx)
$end = $html.IndexOf('</article>', $titleIdx)
if ($start -lt 0 -or $end -lt 0) { throw "$AppName 카드의 <article> 범위를 찾을 수 없습니다." }
$end += '</article>'.Length

$card = $html.Substring($start, $end - $start)
$new = $card
$new = [regex]::Replace($new, '(<span class="dl-version">)v[^<]+(</span>)', "`${1}v$Version`${2}")
$new = [regex]::Replace($new, '(<p class="dl-meta">Windows 10/11 · 64bit · )\d+MB(</p>)', "`${1}${sizeMB}MB`${2}")
$new = [regex]::Replace($new, "releases/download/$TagPrefix-v[^/""]+/[^""]+\.exe", "releases/download/$Tag/$installerName")
$new = [regex]::Replace($new, "releases/tag/$TagPrefix-v[^""]+", "releases/tag/$Tag")

if ($new -eq $card) {
    Write-Host "store.html 변경 없음 (이미 v$Version)" -ForegroundColor DarkGray
} else {
    $html = $html.Substring(0, $start) + $new + $html.Substring($end)
    $enc = New-Object System.Text.UTF8Encoding($hasBom)
    [System.IO.File]::WriteAllText($storePath, $html, $enc)

    $msgFile = Join-Path $env:TEMP "$TagPrefix-site-commit-$Version.txt"
    $msg = "$AppName v$Version 배포 갱신"
    if ($CoAuthor) { $msg += "`n`nCo-Authored-By: $CoAuthor" }
    [System.IO.File]::WriteAllText($msgFile, $msg, (New-Object System.Text.UTF8Encoding($false)))

    Write-Host "[3/3] store.html 갱신 → 커밋/푸시 (GitHub Pages 자동 배포)" -ForegroundColor Yellow
    git -C $SiteRepoDir add store.html
    git -C $SiteRepoDir commit --quiet -F $msgFile
    if ($LASTEXITCODE -ne 0) { throw "웹사이트 커밋 실패" }
    git -C $SiteRepoDir push --quiet origin main
    if ($LASTEXITCODE -ne 0) { throw "웹사이트 푸시 실패" }
}

Write-Host "완료: https://www.octo-brain.com/store.html  (다운로드: https://github.com/$SiteRepo/releases/download/$Tag/$installerName)" -ForegroundColor Green
