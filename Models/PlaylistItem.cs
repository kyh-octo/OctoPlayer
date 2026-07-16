using System.IO;

namespace OctoPlayer.Models
{
    /// <summary>
    /// 재생목록의 단일 미디어 항목을 나타냅니다.
    /// </summary>
    public sealed class PlaylistItem
    {
        public PlaylistItem(string filePath)
        {
            FilePath = filePath;
            DisplayName = Path.GetFileName(filePath);
        }

        /// <summary>미디어 파일의 전체 경로입니다.</summary>
        public string FilePath { get; }

        /// <summary>재생목록에 표시할 이름(파일명)입니다.</summary>
        public string DisplayName { get; }

        /// <summary>재생 길이입니다. 아직 알 수 없으면 <see cref="TimeSpan.Zero"/>입니다.</summary>
        public TimeSpan Duration { get; set; } = TimeSpan.Zero;

        /// <summary>ListBox 등에서 표시될 텍스트입니다.</summary>
        public override string ToString() => DisplayName;
    }
}
