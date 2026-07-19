using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OctoPlayer.Services
{
    /// <summary>
    /// 미디어 확장자를 OctoPlayer와 연결(등록/해제)합니다.
    /// HKCU(사용자별) 영역만 사용하므로 관리자 권한이 필요 없습니다.
    ///
    /// 등록하면:
    /// - 파일 우클릭 → "연결 프로그램" 목록에 OctoPlayer가 나타나고,
    /// - Windows 설정 → 앱 → 기본 앱에서 OctoPlayer를 기본 플레이어로 선택할 수 있습니다.
    /// (Windows 10/11은 기본 앱 지정 자체는 사용자가 직접 선택해야 합니다.)
    /// </summary>
    public static class FileAssociations
    {
        private const string ProgId = "OctoPlayer.MediaFile";
        private const string AppName = "OctoPlayer";

        /// <summary>현재 사용자에게 등록되어 있는지 여부입니다.</summary>
        public static bool IsRegistered
        {
            get
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}");
                return key != null;
            }
        }

        /// <summary>지원하는 모든 미디어 확장자를 현재 사용자에게 등록합니다.</summary>
        public static void Register()
        {
            string exe = Environment.ProcessPath
                ?? throw new InvalidOperationException("실행 파일 경로를 확인할 수 없습니다.");

            // 1) ProgID (파일 형식 정의: 아이콘 + 열기 명령)
            // 문자열은 리소스가 아닌 Loc.Current 분기를 씁니다(백그라운드 스레드에서도 호출되므로
            // WPF 리소스 사전 접근 대신 스레드 안전한 값만 사용).
            bool en = Loc.Current == "en";
            using (RegistryKey prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
            {
                prog.SetValue(null, en ? "OctoPlayer media file" : "OctoPlayer 미디어 파일");
                using RegistryKey icon = prog.CreateSubKey("DefaultIcon");
                icon.SetValue(null, $"\"{exe}\",0");
                using RegistryKey open = prog.CreateSubKey(@"shell\open");
                open.SetValue(null, en ? "Play with OctoPlayer" : "OctoPlayer로 재생");
                using RegistryKey command = open.CreateSubKey("command");
                command.SetValue(null, $"\"{exe}\" \"%1\"");
            }

            // 2) 각 확장자의 '연결 프로그램' 후보 목록에 추가
            foreach (string ext in SupportedFormats.AllExtensions)
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(
                    $@"Software\Classes\{ext}\OpenWithProgids");
                key.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            }

            // 3) Windows 설정 > 기본 앱 목록 등록 (Default Programs / Capabilities)
            using (RegistryKey cap = Registry.CurrentUser.CreateSubKey($@"Software\{AppName}\Capabilities"))
            {
                cap.SetValue("ApplicationName", AppName);
                cap.SetValue("ApplicationDescription",
                    en ? "OctoBrain Softworks media player" : "OctoBrain Softworks 동영상 플레이어");
                using RegistryKey fa = cap.CreateSubKey("FileAssociations");
                foreach (string ext in SupportedFormats.AllExtensions)
                {
                    fa.SetValue(ext, ProgId);
                }
            }

            using (RegistryKey reg = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            {
                reg.SetValue(AppName, $@"Software\{AppName}\Capabilities");
            }

            NotifyShell();
        }

        /// <summary>
        /// 레지스트리에 등록된 실행 파일이 더 이상 존재하지 않으면(이전 버전 삭제/이동 후)
        /// 현재 실행 파일 경로로 바로잡습니다. 시작 시 호출해 "죽은 등록" 문제를 자동 복구합니다.
        /// - 앱 정식 등록(ProgId)이 죽은 경로면 → 현재 경로로 재등록
        /// - 탐색기의 "다른 앱 선택 → 찾아보기"가 만든 Applications 항목이 죽은 경로면 → 교정
        /// 살아 있는 경로(예: 설치본과 개발 빌드가 공존)는 건드리지 않습니다.
        /// </summary>
        public static void RepairIfStale()
        {
            try
            {
                string? exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe))
                {
                    return;
                }

                string? registered = ReadCommandExe($@"Software\Classes\{ProgId}\shell\open\command");
                if (registered != null
                    && !string.Equals(registered, exe, StringComparison.OrdinalIgnoreCase)
                    && !File.Exists(registered))
                {
                    Register();
                }

                const string appCommandKey = @"Software\Classes\Applications\OctoPlayer.exe\shell\open\command";
                string? appExe = ReadCommandExe(appCommandKey);
                if (appExe != null
                    && !string.Equals(appExe, exe, StringComparison.OrdinalIgnoreCase)
                    && !File.Exists(appExe))
                {
                    using RegistryKey key = Registry.CurrentUser.CreateSubKey(appCommandKey);
                    key.SetValue(null, $"\"{exe}\" \"%1\"");
                    NotifyShell();
                }
            }
            catch
            {
                // 연결 복구 실패는 앱 동작에 영향을 주지 않습니다.
            }
        }

        /// <summary>레지스트리 open command("경로" "%1" 형식)에서 실행 파일 경로만 꺼냅니다.</summary>
        private static string? ReadCommandExe(string subKey)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(subKey);
            if (key?.GetValue(null) is not string command || command.Length == 0)
            {
                return null;
            }

            if (command.StartsWith('"'))
            {
                int end = command.IndexOf('"', 1);
                return end > 1 ? command[1..end] : null;
            }

            int space = command.IndexOf(' ');
            return space > 0 ? command[..space] : command;
        }

        /// <summary>등록을 해제합니다(현재 사용자).</summary>
        public static void Unregister()
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);

            foreach (string ext in SupportedFormats.AllExtensions)
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                    $@"Software\Classes\{ext}\OpenWithProgids", writable: true);
                key?.DeleteValue(ProgId, throwOnMissingValue: false);
            }

            // 앱 설정은 %AppData%\OctoPlayer\settings.json에 있으므로 이 키에는 Capabilities만 들어 있습니다.
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\{AppName}", throwOnMissingSubKey: false);

            using (RegistryKey? reg = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true))
            {
                reg?.DeleteValue(AppName, throwOnMissingValue: false);
            }

            NotifyShell();
        }

        /// <summary>탐색기에 연결 변경을 알려 아이콘/목록을 새로고침합니다.</summary>
        private static void NotifyShell()
        {
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }

        private const int SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_IDLIST = 0x0000;

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
    }
}
