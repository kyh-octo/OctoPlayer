using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OctoPlayer.Services
{
    /// <summary>
    /// 플레이어가 지원하는 미디어 파일 형식을 정의하고 판별합니다.
    /// libVLC가 디코딩하는 일반적인 컨테이너 형식을 폭넓게 포함합니다.
    /// </summary>
    public static class SupportedFormats
    {
        /// <summary>지원하는 동영상 확장자(소문자, 선행 점 포함)입니다.</summary>
        public static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".m4v", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm",
            ".ts", ".m2ts", ".mts", ".mpg", ".mpeg", ".mpe", ".m2v", ".vob",
            ".3gp", ".3g2", ".ogv", ".ogm", ".rm", ".rmvb", ".asf", ".divx",
            ".f4v", ".mxf", ".dav"
        };

        /// <summary>지원하는 오디오 확장자(소문자, 선행 점 포함)입니다.</summary>
        public static readonly IReadOnlySet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".flac", ".aac", ".m4a", ".wav", ".wma", ".ogg", ".oga",
            ".opus", ".ac3", ".dts", ".ape", ".alac", ".aiff", ".mka"
        };

        /// <summary>지원하는 모든 확장자(동영상 + 오디오)입니다.</summary>
        public static readonly IReadOnlySet<string> AllExtensions =
            new HashSet<string>(VideoExtensions.Concat(AudioExtensions), StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 지정한 경로의 확장자가 지원되는 미디어인지 여부를 반환합니다.
        /// </summary>
        public static bool IsSupported(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            string ext = Path.GetExtension(path);
            return !string.IsNullOrEmpty(ext) && AllExtensions.Contains(ext);
        }

        /// <summary>
        /// 파일 열기 대화상자에 사용할 필터 문자열을 생성합니다.
        /// </summary>
        public static string BuildOpenFileFilter()
        {
            string videoMask = string.Join(";", VideoExtensions.Select(e => "*" + e));
            string audioMask = string.Join(";", AudioExtensions.Select(e => "*" + e));
            string allMask = string.Join(";", AllExtensions.Select(e => "*" + e));

            return
                $"미디어 파일|{allMask}|" +
                $"동영상 파일|{videoMask}|" +
                $"오디오 파일|{audioMask}|" +
                "모든 파일|*.*";
        }
    }
}
