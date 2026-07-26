; ============================================================================
; OctoPlayer 설치 파일 스크립트 (Inno Setup 6)
;
; 빌드 방법: 같은 폴더의 build.ps1 실행 (게시 → 설치 파일 생성까지 자동)
;   powershell -ExecutionPolicy Bypass -File build.ps1
; 결과물: Setup\Output\OctoPlayer-Setup-x64.exe
; ============================================================================

#define MyAppName "OctoPlayer"
#define MyAppVersion "1.1.0"
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
; libVLC 플러그인 캐시(plugins.dat)를 미리 생성해 첫 실행부터 빠르게 시작되도록 합니다.
; (캐시가 없으면 libVLC가 실행마다 수백 개 플러그인을 전체 스캔해 시작이 수 초 느려집니다.)
Filename: "{app}\{#MyAppExeName}"; Parameters: "--gen-plugins-cache"; \
    StatusMsg: "미디어 엔진을 준비하는 중..."; Flags: runhidden waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; \
    Flags: nowait postinstall skipifsilent

[Code]
{ ============================================================================
  파일 연결 등록/정리.
  설치 시: 설치 경로 기준으로 ProgID + "연결 프로그램" 후보 + 기본 앱(Capabilities)을
           등록합니다. 예전 실행 파일 경로가 레지스트리에 박제되어 삭제 후에도
           "죽은 OctoPlayer"가 목록에 남는 문제를 막습니다. (기본 앱 지정 자체는
           Windows 정책상 사용자가 직접 선택해야 하며, 여기서는 후보 등록만 합니다.)
  제거 시: 설치 영역(HKA)과 앱이 직접 쓴 사용자 영역(HKCU) 등록을 모두 정리합니다.
  ============================================================================ }
const
  ProgId = 'OctoPlayer.MediaFile';
  ExtList = '.mp4,.m4v,.mkv,.avi,.mov,.wmv,.flv,.webm,.ts,.m2ts,.mts,.mpg,.mpeg,.mpe,.m2v,.vob,' +
            '.3gp,.3g2,.ogv,.ogm,.rm,.rmvb,.asf,.divx,.f4v,.mxf,.dav,' +
            '.mp3,.flac,.aac,.m4a,.wav,.wma,.ogg,.oga,.opus,.ac3,.dts,.ape,.alac,.aiff,.mka';
  SHCNE_ASSOCCHANGED = $08000000;

procedure SHChangeNotify(EventID: Integer; Flags: Cardinal; Item1, Item2: Integer);
  external 'SHChangeNotify@shell32.dll stdcall';

{ 쉼표 목록에서 다음 항목을 꺼냅니다. Rest가 비면 끝. }
function NextExt(var Rest: String): String;
var
  P: Integer;
begin
  P := Pos(',', Rest);
  if P > 0 then
  begin
    Result := Copy(Rest, 1, P - 1);
    Rest := Copy(Rest, P + 1, MaxInt);
  end
  else
  begin
    Result := Rest;
    Rest := '';
  end;
end;

procedure RegisterFileAssociations();
var
  Exe, Ext, Rest: String;
begin
  Exe := ExpandConstant('{app}\{#MyAppExeName}');

  RegWriteStringValue(HKA, 'Software\Classes\' + ProgId, '', 'OctoPlayer 미디어 파일');
  RegWriteStringValue(HKA, 'Software\Classes\' + ProgId + '\DefaultIcon', '', '"' + Exe + '",0');
  RegWriteStringValue(HKA, 'Software\Classes\' + ProgId + '\shell\open', '', 'OctoPlayer로 재생');
  RegWriteStringValue(HKA, 'Software\Classes\' + ProgId + '\shell\open\command', '', '"' + Exe + '" "%1"');

  RegWriteStringValue(HKA, 'Software\{#MyAppName}\Capabilities', 'ApplicationName', '{#MyAppName}');
  RegWriteStringValue(HKA, 'Software\{#MyAppName}\Capabilities', 'ApplicationDescription',
    'OctoBrain Softworks 동영상 플레이어');
  RegWriteStringValue(HKA, 'Software\RegisteredApplications', '{#MyAppName}',
    'Software\{#MyAppName}\Capabilities');

  Rest := ExtList;
  while Rest <> '' do
  begin
    Ext := NextExt(Rest);
    RegWriteStringValue(HKA, 'Software\Classes\' + Ext + '\OpenWithProgids', ProgId, '');
    RegWriteStringValue(HKA, 'Software\{#MyAppName}\Capabilities\FileAssociations', Ext, ProgId);
  end;

  SHChangeNotify(SHCNE_ASSOCCHANGED, 0, 0, 0);
end;

{ 지정한 루트(HKA 또는 HKCU)에서 등록 흔적을 지웁니다. }
procedure CleanFileAssociations(Root: Integer);
var
  Ext, Rest: String;
begin
  RegDeleteKeyIncludingSubkeys(Root, 'Software\Classes\' + ProgId);
  RegDeleteKeyIncludingSubkeys(Root, 'Software\{#MyAppName}');
  RegDeleteValue(Root, 'Software\RegisteredApplications', '{#MyAppName}');

  Rest := ExtList;
  while Rest <> '' do
  begin
    Ext := NextExt(Rest);
    RegDeleteValue(Root, 'Software\Classes\' + Ext + '\OpenWithProgids', ProgId);
  end;

  { 탐색기의 "다른 앱 선택 → 찾아보기"가 만드는 항목: 앱이 제거되므로 함께 정리 }
  RegDeleteKeyIncludingSubkeys(Root, 'Software\Classes\Applications\{#MyAppExeName}');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    RegisterFileAssociations();
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    CleanFileAssociations(HKA);
    CleanFileAssociations(HKCU);
    SHChangeNotify(SHCNE_ASSOCCHANGED, 0, 0, 0);
  end;
end;
