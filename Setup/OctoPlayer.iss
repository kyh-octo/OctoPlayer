; ============================================================================
; OctoPlayer 설치 파일 스크립트 (Inno Setup 6)
;
; 빌드 방법: 같은 폴더의 build.ps1 실행 (게시 → 설치 파일 생성까지 자동)
;   powershell -ExecutionPolicy Bypass -File build.ps1
; 결과물: Setup\Output\OctoPlayer-Setup-x64.exe
; ============================================================================

#define MyAppName "OctoPlayer"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "OctoBrain Softworks"
#define MyAppExeName "OctoPlayer.exe"
#define MyGroupName "OctoBrain"

[Setup]
; AppId는 업그레이드/제거 식별자이므로 바꾸지 마세요.
AppId={{B7E5D6C4-3F2A-4A81-9C5D-1E8F0A2B7C64}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyGroupName}\{#MyAppName}
; 시작 메뉴 그룹: OctoBrain (사용자가 바꾸지 않도록 그룹 선택 페이지는 생략)
DefaultGroupName={#MyGroupName}
DisableProgramGroupPage=yes
; 관리자 권한 없이 사용자 단위로 설치(기본). 필요 시 대화상자에서 전체 사용자 설치 선택 가능.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=Output
OutputBaseFilename=OctoPlayer-Setup-x64
SetupIconFile=..\Properties\OctoPlayer.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; 바탕화면 바로가기 생성 여부(체크박스, 기본 체크됨)
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; self-contained 게시 결과 전체 (x86/arm64 libvlc는 제외)
Source: "publish\*"; DestDir: "{app}"; \
    Flags: ignoreversion recursesubdirs createallsubdirs; \
    Excludes: "libvlc\win-x86\*,libvlc\win-arm64\*"

[Icons]
; 시작 메뉴 (OctoBrain 그룹으로 정리)
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{#MyAppName} 제거"; Filename: "{uninstallexe}"
; 바탕화면 (Tasks에서 선택 시)
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; \
    Flags: nowait postinstall skipifsilent
