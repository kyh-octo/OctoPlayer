namespace OctoPlayer.Models
{
    /// <summary>
    /// 재생목록 반복 동작 모드입니다.
    /// </summary>
    public enum RepeatMode
    {
        /// <summary>반복하지 않습니다. 마지막 곡이 끝나면 정지합니다.</summary>
        None = 0,

        /// <summary>현재 곡을 무한 반복합니다.</summary>
        One = 1,

        /// <summary>재생목록 전체를 반복합니다.</summary>
        All = 2
    }
}
