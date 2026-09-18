<p align="center">
  <img src="Assets/Logo.png" alt="OctoPlayer" width="128" />
</p>

<h1 align="center">OctoPlayer</h1>

<p align="center">
  Windows용 무료 오픈소스 미디어 플레이어<br/>
  A free, open-source media player for Windows — built with WPF and libVLC.
</p>

---

## 다운로드 (Download)

**[최신 버전 다운로드 (Releases)](../../releases/latest)** — `OctoPlayer-Setup-<버전>.exe`를 받아 실행하면 됩니다.

- Windows 10/11 (64비트)
- .NET 설치 불필요 (self-contained 배포)
- 관리자 권한 없이 사용자 단위 설치

## 주요 기능 (Features)

- **재생**: libVLC 기반으로 대부분의 동영상/오디오 형식 재생 (MP4, MKV, AVI, WebM, MP3, FLAC 등)
- **재생목록**: 제목 목록/썸네일 미리보기 전환, 셔플, 반복 재생(전체/한 곡/횟수 지정)
- **자막**: 같은 이름의 자막 파일 자동 불러오기, 자막 트랙 선택, 싱크 조절(±0.5초)
- **구간 반복 (A-B)**: 시작/끝 지점을 지정해 구간 반복
- **재생 속도**: 0.1배 단위 조절, 종료 시 속도 기억 옵션
- **영상 조절**: 화면 회전, 명도/대비/채도/색상 조절, 화면 비율(4:3, 16:9, 2.35:1 등), 팬 & 스캔(확대/이동)
- **오디오**: 오디오 트랙 선택, 이퀄라이저, 휠로 볼륨 조절
- **영상 캡처**: 현재 화면을 이미지로 저장
- **편의 기능**: 이어보기(마지막 위치 기억), 창 크기 기억, 항상 위, 미디어 파일 연결(파일 연결 등록), 드래그 앤 드롭
- **주소 열기**: 네트워크 스트림 URL 재생

## 소스에서 빌드 (Build from source)

요구 사항: [.NET 10 SDK](https://dotnet.microsoft.com/download) 이상

```powershell
git clone <this-repo>
cd OctoPlayer
dotnet build
dotnet run
```

### 설치 파일 만들기

[Inno Setup 6](https://jrsoftware.org/isinfo.php)이 필요합니다 (`winget install -e --id JRSoftware.InnoSetup`).

```powershell
powershell -ExecutionPolicy Bypass -File Setup\build.ps1
```

결과물: `installer\output\OctoPlayer-Setup-<버전>.exe` (버전은 csproj의 `<Version>`)

### 릴리즈 (원클릭)

`release.bat`을 실행하면 Git 최신 커밋 기준으로 설치 파일 빌드 → GitHub 릴리스(태그 `v<버전>`) 생성 → octo-brain.com 배포 갱신까지 자동으로 진행됩니다. 커밋되지 않은 로컬 변경은 릴리즈에 포함되지 않습니다. 옵션은 `installerelease.ps1` 머리말 참고.

## 라이선스 (License)

OctoPlayer의 소스 코드는 [MIT 라이선스](LICENSE)로 배포됩니다.

이 프로그램은 다음 오픈소스 라이브러리를 사용합니다:

- [LibVLCSharp](https://github.com/videolan/libvlcsharp) — LGPL-2.1
- [libVLC](https://www.videolan.org/vlc/libvlc.html) (VideoLAN.LibVLC.Windows) — LGPL-2.1

설치 파일에는 libVLC 바이너리가 포함되며, 해당 바이너리는 LGPL-2.1 조건을 따릅니다.
