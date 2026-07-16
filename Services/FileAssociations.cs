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
            using (RegistryKey prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
            {
                prog.SetValue(null, "OctoPlayer 미디어 파일");
                using RegistryKey icon = prog.CreateSubKey("DefaultIcon");
                icon.SetValue(null, $"\"{exe}\",0");
                using RegistryKey open = prog.CreateSubKey(@"shell\open");
                open.SetValue(null, "OctoPlayer로 재생");
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
                cap.SetValue("ApplicationDescription", "OctoBrain Softworks 동영상 플레이어");
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
