using System.IO;
using System.Text.Json;
using OctoPlayer.Models;

namespace OctoPlayer.Services
{
    /// <summary>
    /// 사용자 설정(반복 모드, 반복 횟수, 셔플, 볼륨 등)을 PC에 저장하고 불러옵니다.
    /// 설정 파일은 %AppData%\OctoPlayer\settings.json 에 저장됩니다.
    /// </summary>
    public class AppSettings
    {
        public RepeatMode RepeatMode { get; set; } = RepeatMode.None;

        public int RepeatCount { get; set; } = 1;

        public bool IsShuffled { get; set; }

        public int Volume { get; set; } = 100;

        /// <summary>
        /// 마우스 뒤로/앞으로 버튼으로 한 번에 건너뛸 시간(초). 기본 10초.
        /// </summary>
        public int SkipSeconds { get; set; } = 10;

        /// <summary>재생목록 패널의 너비(픽셀)입니다.</summary>
        public int PlaylistWidth { get; set; } = 240;

        /// <summary>재생목록 보기 방식(제목 목록/미리보기)입니다.</summary>
        public PlaylistViewMode PlaylistView { get; set; } = PlaylistViewMode.Titles;

        /// <summary>방향키(←/→)로 한 번에 이동할 시간(초)입니다.</summary>
        public int ArrowSkipSeconds { get; set; } = 5;

        /// <summary>마우스 휠/방향키(↑/↓)로 한 번에 바꿀 볼륨 단계입니다.</summary>
        public int WheelVolumeStep { get; set; } = 5;

        /// <summary>영상과 같은 이름의 자막 파일을 자동으로 불러올지 여부입니다.</summary>
        public bool AutoLoadSubtitles { get; set; } = true;

        /// <summary>마지막으로 보던 위치에서 이어서 재생할지 여부입니다.</summary>
        public bool ResumePlayback { get; set; } = true;

        /// <summary>마지막 재생 파일 경로(이어보기용)입니다.</summary>
        public string? LastFilePath { get; set; }

        /// <summary>마지막 재생 위치(밀리초, 이어보기용)입니다.</summary>
        public long LastPositionMs { get; set; }

        /// <summary>종료 시 재생 속도를 기억했다가 다음 실행에 적용할지 여부입니다.</summary>
        public bool RememberRate { get; set; }

        /// <summary>마지막 재생 속도(재생 속도 기억용)입니다.</summary>
        public float LastRate { get; set; } = 1f;

        /// <summary>종료 시 창 크기를 기억했다가 다음 실행에 적용할지 여부입니다.</summary>
        public bool RememberWindowSize { get; set; } = true;

        /// <summary>기억된 창 너비/높이(DIP)입니다.</summary>
        public double WindowWidth { get; set; }
        public double WindowHeight { get; set; }

        /// <summary>창을 항상 위에 표시할지 여부입니다.</summary>
        public bool AlwaysOnTop { get; set; }

        /// <summary>UI 언어("ko"/"en"). null 또는 빈 값이면 시스템 언어를 따릅니다.</summary>
        public string? Language { get; set; }

        /// <summary>영상 캡처 저장 폴더입니다(비어 있으면 사진\OctoPlayer).</summary>
        public string? CaptureFolder { get; set; }

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true
        };

        private static string SettingsFilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "OctoPlayer");
                return Path.Combine(dir, "settings.json");
            }
        }

        /// <summary>
        /// 저장된 설정을 불러옵니다. 파일이 없거나 손상된 경우 기본값을 반환합니다.
        /// </summary>
        public static AppSettings Load()
        {
            try
            {
                string path = SettingsFilePath;
                if (!File.Exists(path))
                {
                    return new AppSettings();
                }

                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            catch
            {
                // 설정을 읽지 못하면 기본값으로 진행합니다.
                return new AppSettings();
            }
        }

        /// <summary>
        /// 현재 설정을 디스크에 저장합니다. 실패해도 앱 동작에는 영향을 주지 않습니다.
        /// </summary>
        public void Save()
        {
            try
            {
                string path = SettingsFilePath;
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string json = JsonSerializer.Serialize(this, SerializerOptions);
                File.WriteAllText(path, json);
            }
            catch
            {
                // 저장 실패는 무시합니다.
            }
        }
    }
}
