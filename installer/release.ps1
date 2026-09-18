# OctoPlayer 원클릭 릴리즈 스크립트
# 실행: 프로젝트 루트의 release.bat 더블클릭 (또는 powershell -ExecutionPolicy Bypass -File installer\release.ps1)
#
# 동작 (Git 최신 커밋 기준으로 릴리즈):
#   1) origin/main 동기화 - fetch → ff-only pull, 로컬이 앞서 있으면 push
#   2) 최신 커밋을 임시 작업트리(git worktree)에 받아 빌드 → 커밋되지 않은 로컬 변경은 릴리즈에 섞이지 않는다
#   3) csproj <Version> 을 읽어 installer\output\OctoPlayer-Setup-<버전>.exe 생성
#   4) 프로젝트 저장소(kyh-octo/OctoPlayer)에 태그 v<버전> + GitHub 릴리스 생성 (이미 있으면 설치 파일만 교체)
#   5) octo-brain.com 배포 갱신 (update-website.ps1)
#
# 옵션:
#   -SkipWebsite   홈페이지 갱신 생략
#   -SkipRelease   프로젝트 저장소 GitHub 릴리스 생략
#   -InPlace       임시 작업트리 대신 현재 폴더(로컬 변경 포함)를 그대로 빌드
#   -NoSync        origin 동기화(pull/push) 생략
#   -NotesFile     릴리스 노트 md 파일. 없으면 저장소 루트의 RELEASE_NOTES.md → 없으면 커밋 로그로 자동 생성
#   -CoAuthor      홈페이지 커밋에 붙일 Co-Authored-By

param(
    [switch]$SkipWebsite,
    [switch]$SkipRelease,
    [switch]$InPlace,
    [switch]$NoSync,
    [string]$NotesFile = "",
    [string]$CoAuthor = ""
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

# ===== 프로젝트 설정 =====
$AppName = "OctoPlayer"
$Repo    = "kyh-octo/OctoPlayer"
$Branch  = "main"
$Root    = Split-Path $PSScriptRoot -Parent
# ========================

$outputDir = Join-Path $PSScriptRoot "output"
$sw = [System.Diagnostics.Stopwatch]::StartNew()

function Step($msg) { Write-Host ""; Write-Host "== $msg" -ForegroundColor Cyan }
function Run-Git { param([string[]]$GitArgs) & git @GitArgs; if ($LASTEXITCODE -ne 0) { throw "git $($GitArgs -join ' ') 실패 (exit $LASTEXITCODE)" } }

$gh = @("$env:ProgramFiles\GitHub CLI\gh.exe", "${env:ProgramFiles(x86)}\GitHub CLI\gh.exe", "$env:LOCALAPPDATA\Programs\GitHub CLI\gh.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $gh) { $cmd = Get-Command gh -ErrorAction SilentlyContinue; if ($cmd) { $gh = $cmd.Source } }
if (-not $gh) { throw "GitHub CLI(gh)를 찾을 수 없습니다. winget install GitHub.cli 후 gh auth login 하세요." }
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw "git을 찾을 수 없습니다." }

Write-Host "########## $AppName 릴리즈 ##########" -ForegroundColor Green
Set-Location $Root

# ---------- 1) Git 동기화 ----------
Step "[1/5] Git 동기화 ($Repo, $Branch)"
$dirty = @(git status --porcelain)
if ($dirty.Count -gt 0) {
    if ($InPlace) {
        Write-Host "경고: 커밋되지 않은 변경 $($dirty.Count)건이 그대로 빌드에 포함됩니다 (-InPlace)." -ForegroundColor Red
    } else {
        Write-Host "참고: 커밋되지 않은 변경 $($dirty.Count)건은 릴리즈에 포함되지 않습니다 (최신 커밋만 빌드)." -ForegroundColor DarkYellow
    }
}
if (-not $NoSync) {
    Run-Git @("fetch", "--quiet", "--tags", "origin")
    $cur = (git rev-parse --abbrev-ref HEAD).Trim()
    if ($cur -ne $Branch) {
        if ($dirty.Count -gt 0) { throw "현재 브랜치가 '$cur' 입니다. $Branch 로 전환하려면 먼저 변경 사항을 커밋/스태시하세요." }
        Run-Git @("checkout", "--quiet", $Branch)
    }
    Run-Git @("pull", "--quiet", "--ff-only", "origin", $Branch)
    $ahead = [int](git rev-list --count "origin/$Branch..HEAD")
    if ($ahead -gt 0) {
        Write-Host "로컬이 origin보다 $ahead 커밋 앞서 있어 push 합니다." -ForegroundColor Yellow
        Run-Git @("push", "--quiet", "origin", $Branch)
    }
}
$sha = (git rev-parse --short HEAD).Trim()
$shaFull = (git rev-parse HEAD).Trim()
Write-Host "릴리즈 커밋: $sha  $(git log -1 --pretty=%s)"

# ---------- 2) 버전 ----------
$csproj = Join-Path $Root "$AppName.csproj"
$version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "$AppName.csproj 에서 <Version> 을 찾을 수 없습니다." }
$version = "$version".Trim()
$tag = "v$version"
Write-Host "버전: $version (태그 $tag)"

$tagExists = ((git tag --list $tag) | Measure-Object).Count -gt 0
if ($tagExists) {
    $tagSha = (git rev-parse "$tag^{commit}").Trim()
    if ($tagSha -ne $shaFull) {
        throw "태그 $tag 가 이미 다른 커밋($($tagSha.Substring(0,7)))을 가리킵니다. $AppName.csproj 의 <Version> 을 올리고 커밋한 뒤 다시 실행하세요."
    }
    Write-Host "태그 $tag 가 이미 현재 커밋에 있습니다 → 같은 버전 재릴리즈 (설치 파일 교체)" -ForegroundColor DarkYellow
}

# ---------- 3) 빌드 ----------
$installerName = "$AppName-Setup-$version.exe"
$installer = Join-Path $outputDir $installerName
$worktree = $null
try {
    if ($InPlace) {
        Step "[2/5] 설치 파일 빌드 (현재 폴더)"
        & (Join-Path $PSScriptRoot "build-installer.ps1") -SkipWebsite
        if ($LASTEXITCODE) { throw "빌드 실패 (exit $LASTEXITCODE)" }
    } else {
        $worktree = Join-Path $env:TEMP "octo-release\$AppName"
        Step "[2/5] 설치 파일 빌드 (최신 커밋을 임시 작업트리에 체크아웃: $worktree)"
        if (Test-Path $worktree) {
            git worktree remove --force $worktree | Out-Null
            if (Test-Path $worktree) { Remove-Item -Recurse -Force $worktree }
        }
        git worktree prune
        Run-Git @("worktree", "add", "--quiet", "--detach", $worktree, $shaFull)
        & (Join-Path $worktree "installer\build-installer.ps1") -SkipWebsite
        if ($LASTEXITCODE) { throw "빌드 실패 (exit $LASTEXITCODE)" }
        $built = Join-Path $worktree "installer\output\$installerName"
        if (-not (Test-Path $built)) { throw "설치 파일이 생성되지 않았습니다: $built" }
        New-Item -ItemType Directory -Force $outputDir | Out-Null
        Copy-Item $built $installer -Force
    }
} finally {
    if ($worktree -and (Test-Path $worktree)) {
        git worktree remove --force $worktree | Out-Null
        git worktree prune
    }
}
if (-not (Test-Path $installer)) { throw "설치 파일을 찾을 수 없습니다: $installer" }
$mb = [math]::Round((Get-Item $installer).Length / 1MB, 1)
Write-Host "설치 파일: $installer ($mb MB)" -ForegroundColor Green

# ---------- 4) 프로젝트 저장소 GitHub 릴리스 ----------
if ($SkipRelease) {
    Step "[3/5] GitHub 릴리스 생략 (-SkipRelease)"
} else {
    Step "[3/5] GitHub 릴리스 ($Repo $tag)"
    $existing = @(& $gh release list -R $Repo --limit 200 --json tagName --jq '.[].tagName')
    if ($LASTEXITCODE -ne 0) { throw "릴리스 목록 조회 실패" }
    if ($existing -contains $tag) {
        Write-Host "릴리스 $tag 존재 → 설치 파일 교체 업로드" -ForegroundColor Yellow
        & $gh release upload $tag $installer -R $Repo --clobber
        if ($LASTEXITCODE -ne 0) { throw "릴리스 자산 업로드 실패" }
    } else {
        if (-not $tagExists) {
            Run-Git @("tag", "-a", $tag, "-m", "$AppName $tag")
            Run-Git @("push", "--quiet", "origin", $tag)
        }
        if (-not $NotesFile) {
            $cand = Join-Path $Root "RELEASE_NOTES.md"
            if (Test-Path $cand) { $NotesFile = $cand; Write-Host "릴리스 노트: RELEASE_NOTES.md" }
        }
        if (-not $NotesFile) {
            $prev = @(git tag --sort=-creatordate --merged HEAD) | Where-Object { $_ -ne $tag } | Select-Object -First 1
            if ($prev) { $log = @(git log --pretty=format:"- %s" "$prev..HEAD"); $range = "$prev 이후" }
            else       { $log = @(git log --pretty=format:"- %s" -n 30);          $range = "최근 커밋" }
            $log = @($log | Where-Object { $_ -and ($_ -notmatch '^- (Bump version|v?\d+\.\d+\.\d+:?\s*$)') })
            if ($log.Count -eq 0) { $log = @("- 유지보수 업데이트") }
            $NotesFile = Join-Path $env:TEMP "$AppName-notes-$version.md"
            $body = "## 설치`n" +
                    "``$installerName`` 을 내려받아 실행하세요. 이전 버전 위에 그대로 업그레이드됩니다. (.NET 설치 불필요, Windows 10/11 x64)`n`n" +
                    "## 변경 내역 ($range)`n" +
                    ($log -join "`n") + "`n"
            [System.IO.File]::WriteAllText($NotesFile, $body, (New-Object System.Text.UTF8Encoding($false)))
            Write-Host "릴리스 노트: 커밋 로그에서 자동 생성 ($($log.Count)줄)"
        }
        Write-Host "릴리스 $tag 생성 + 설치 파일 업로드" -ForegroundColor Yellow
        & $gh release create $tag $installer -R $Repo --title "$AppName v$version" --notes-file $NotesFile --latest
        if ($LASTEXITCODE -ne 0) { throw "릴리스 생성 실패" }
    }
    Write-Host "https://github.com/$Repo/releases/tag/$tag" -ForegroundColor Green
}

# ---------- 5) 홈페이지 ----------
if ($SkipWebsite) {
    Step "[4/5] 홈페이지 갱신 생략 (-SkipWebsite)"
} else {
    Step "[4/5] octo-brain.com 배포 갱신"
    & (Join-Path $PSScriptRoot "update-website.ps1") -AppName $AppName -Version $version -InstallerPath $installer -CoAuthor $CoAuthor
}

Step "[5/5] 완료: $AppName v$version ($sha) - $([math]::Round($sw.Elapsed.TotalSeconds))초"
