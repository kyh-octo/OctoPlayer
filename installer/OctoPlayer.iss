; OctoPlayer 설치 스크립트 (Inno Setup 6)
; 빌드 방법: installer\build-installer.ps1 실행 (게시 → 설치파일 생성까지 자동)
; 결과물: installer\output\OctoPlayer-Setup-<버전>.exe

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName "OctoPlayer"
#define AppPublisher "OctoBrain Softworks"
#define AppExeName "OctoPlayer.exe"
#define PublishDir "..\bin\Release\Publish"

[Setup]
; AppId는 업그레이드 인식용 고유 값 - 절대 변경하지 말 것
AppId={{B7E5D6C4-3F2A-4A81-9C5D-1E8F0A2B7C64}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
OutputDir=output
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
SetupIconFile=..\Properties\OctoPlayer.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 관리자 권한 없이 사용자 단위 설치 (프로그램 파일 대신 LocalAppData)
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
; 실행 중인 OctoPlayer를 감지해 종료 안내
CloseApplications=yes
RestartApplications=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; self-contained 게시 결과 전체 (x86/arm64 libvlc는 제외)
Source: "{#PublishDir}\*"; Excludes: "*.pdb,*.xml,libvlc\win-x86\*,libvlc\win-arm64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; libVLC 플러그인 캐시(plugins.dat)를 미리 생성해 첫 실행부터 빠르게 시작되도록 합니다.
; (캐시가 없으면 libVLC가 실행마다 수백 개 플러그인을 전체 스캔해 시작이 수 초 느려집니다.)
Filename: "{app}\{#AppExeName}"; Parameters: "--gen-plugins-cache"; StatusMsg: "미디어 엔진을 준비하는 중..."; Flags: runhidden waituntilterminated
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 제거 전에 실행 중인 앱 종료
Filename: "{cmd}"; Parameters: "/C taskkill /F /IM {#AppExeName}"; Flags: runhidden; RunOnceId: "KillApp"

[UninstallDelete]
; 앱이 만든 설정/이어보기 기록 정리
Type: filesandordirs; Name: "{userappdata}\OctoPlayer"

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
  Exe := ExpandConstant('{app}\{#AppExeName}');

  RegWriteStringValue(HKA, 'Software\Classes\' + ProgId, '', 'OctoPlayer 미디어 파일');
  RegWriteStringValue(HKA, 'Software\Classes\' + ProgId + '\DefaultIcon', '', '"' + Exe + '",0');
  RegWriteStringValue(HKA, 'Software\Classes\' + ProgId + '\shell\open', '', 'OctoPlayer로 재생');
  RegWriteStringValue(HKA, 'Software\Classes\' + ProgId + '\shell\open\command', '', '"' + Exe + '" "%1"');

  RegWriteStringValue(HKA, 'Software\{#AppName}\Capabilities', 'ApplicationName', '{#AppName}');
  RegWriteStringValue(HKA, 'Software\{#AppName}\Capabilities', 'ApplicationDescription',
    'OctoBrain Softworks 동영상 플레이어');
  RegWriteStringValue(HKA, 'Software\RegisteredApplications', '{#AppName}',
    'Software\{#AppName}\Capabilities');

  Rest := ExtList;
  while Rest <> '' do
  begin
    Ext := NextExt(Rest);
    RegWriteStringValue(HKA, 'Software\Classes\' + Ext + '\OpenWithProgids', ProgId, '');
    RegWriteStringValue(HKA, 'Software\{#AppName}\Capabilities\FileAssociations', Ext, ProgId);
  end;

  SHChangeNotify(SHCNE_ASSOCCHANGED, 0, 0, 0);
end;

{ 지정한 루트(HKA 또는 HKCU)에서 등록 흔적을 지웁니다. }
procedure CleanFileAssociations(Root: Integer);
var
  Ext, Rest: String;
begin
  RegDeleteKeyIncludingSubkeys(Root, 'Software\Classes\' + ProgId);
  RegDeleteKeyIncludingSubkeys(Root, 'Software\{#AppName}');
  RegDeleteValue(Root, 'Software\RegisteredApplications', '{#AppName}');

  Rest := ExtList;
  while Rest <> '' do
  begin
    Ext := NextExt(Rest);
    RegDeleteValue(Root, 'Software\Classes\' + Ext + '\OpenWithProgids', ProgId);
  end;

  { 탐색기의 "다른 앱 선택 → 찾아보기"가 만드는 항목: 앱이 제거되므로 함께 정리 }
  RegDeleteKeyIncludingSubkeys(Root, 'Software\Classes\Applications\{#AppExeName}');
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
