using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using OctoPlayer.Models;

namespace OctoPlayer.Services
{
    /// <summary>
    /// 재생목록과 재생 순서(셔플/반복)를 관리합니다.
    /// </summary>
    public sealed class PlaylistManager
    {
        private readonly List<PlaylistItem> _items = new();
        private readonly List<int> _order = new();          // _items에 대한 인덱스(재생 순서)
        private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
        private readonly Random _random = new();

        private int _position = -1;                          // _order 내 현재 위치

        /// <summary>재생목록 항목(추가된 순서)입니다.</summary>
        public IReadOnlyList<PlaylistItem> Items => _items;

        /// <summary>항목 개수입니다.</summary>
        public int Count => _items.Count;

        /// <summary>반복 모드입니다.</summary>
        public RepeatMode RepeatMode { get; set; } = RepeatMode.None;

        /// <summary>셔플 사용 여부입니다.</summary>
        public bool IsShuffled { get; private set; }

        /// <summary>현재 항목입니다. 없으면 null입니다.</summary>
        public PlaylistItem? Current =>
            (_position >= 0 && _position < _order.Count) ? _items[_order[_position]] : null;

        /// <summary>현재 항목의 <see cref="Items"/> 기준 인덱스입니다. 없으면 -1입니다.</summary>
        public int CurrentItemIndex =>
            (_position >= 0 && _position < _order.Count) ? _order[_position] : -1;

        /// <summary>
        /// 지원되는 파일을 재생목록에 추가합니다. 이미 존재하거나 미지원이면 무시합니다.
        /// </summary>
        public PlaylistItem? AddFile(string path)
        {
            if (!SupportedFormats.IsSupported(path) || _paths.Contains(path))
            {
                return null;
            }

            var item = new PlaylistItem(path);
            _items.Add(item);
            _order.Add(_items.Count - 1);
            _paths.Add(path);
            return item;
        }

        /// <summary>
        /// 여러 파일/폴더를 추가합니다. 폴더 경로는 그 안의 지원 파일을 스캔합니다.
        /// </summary>
        public int AddPaths(IEnumerable<string> paths)
        {
            int added = 0;
            foreach (string path in paths)
            {
                if (Directory.Exists(path))
                {
                    added += AddFromFolder(path);
                }
                else if (AddFile(path) != null)
                {
                    added++;
                }
            }
            return added;
        }

        /// <summary>
        /// 폴더 내(최상위)의 지원 파일을 자연 정렬 순서로 추가합니다.
        /// </summary>
        public int AddFromFolder(string folderPath)
        {
            int added = 0;
            foreach (string file in ScanFolderFiles(folderPath))
            {
                if (AddFile(file) != null)
                {
                    added++;
                }
            }
            return added;
        }

        /// <summary>
        /// 폴더 내(최상위)의 지원 파일을 자연 정렬 순서로 나열합니다.
        /// 디스크 IO만 하므로 어느 스레드에서든 호출할 수 있습니다.
        /// </summary>
        public static List<string> ScanFolderFiles(string folderPath)
        {
            if (!Directory.Exists(folderPath))
            {
                return new List<string>();
            }

            try
            {
                return Directory.EnumerateFiles(folderPath)
                    .Where(SupportedFormats.IsSupported)
                    .OrderBy(Path.GetFileName, NaturalComparer.Instance)
                    .ToList();
            }
            catch (IOException)
            {
                return new List<string>();
            }
            catch (UnauthorizedAccessException)
            {
                return new List<string>();
            }
        }

        /// <summary>
        /// 다음 항목으로 이동합니다. 끝에 도달하면 반복 모드에 따라 처리합니다.
        /// 더 이상 진행할 수 없으면 null을 반환합니다.
        /// </summary>
        public PlaylistItem? Next()
        {
            if (_order.Count == 0)
            {
                return null;
            }

            if (_position < _order.Count - 1)
            {
                _position++;
                return Current;
            }

            if (RepeatMode == RepeatMode.All)
            {
                if (IsShuffled)
                {
                    ShuffleOrderFresh();
                }
                _position = 0;
                return Current;
            }

            return null;
        }

        /// <summary>
        /// 이전 항목으로 이동합니다. 처음에서 반복(전체)이면 끝으로 순환합니다.
        /// </summary>
        public PlaylistItem? Previous()
        {
            if (_order.Count == 0)
            {
                return null;
            }

            if (_position > 0)
            {
                _position--;
                return Current;
            }

            if (RepeatMode == RepeatMode.All)
            {
                _position = _order.Count - 1;
                return Current;
            }

            // 처음에서 이전을 누르면 현재(첫) 항목을 다시 반환합니다.
            return Current;
        }

        /// <summary>
        /// <see cref="Items"/> 기준 인덱스로 현재 항목을 설정합니다(목록에서 직접 선택).
        /// </summary>
        public PlaylistItem? SelectByItemIndex(int itemIndex)
        {
            if (itemIndex < 0 || itemIndex >= _items.Count)
            {
                return null;
            }

            int pos = _order.IndexOf(itemIndex);
            if (pos < 0)
            {
                return null;
            }

            _position = pos;
            return Current;
        }

        /// <summary>
        /// 경로로 현재 항목을 설정합니다.
        /// </summary>
        public PlaylistItem? SelectByPath(string path)
        {
            int idx = _items.FindIndex(i => string.Equals(i.FilePath, path, StringComparison.OrdinalIgnoreCase));
            return idx >= 0 ? SelectByItemIndex(idx) : null;
        }

        /// <summary>
        /// 셔플 사용 여부를 설정합니다. 현재 재생 중인 항목은 유지됩니다.
        /// </summary>
        public void SetShuffle(bool shuffle)
        {
            if (IsShuffled == shuffle)
            {
                return;
            }

            IsShuffled = shuffle;
            RebuildOrderPreservingCurrent();
        }

        /// <summary>
        /// 셔플이 켜져 있으면 재생 순서를 다시 섞습니다. 현재 재생 중인 항목 위치는 보존됩니다.
        /// 시작 시 저장된 셔플 설정을 적용한 뒤 파일을 추가한 경우처럼,
        /// 항목 추가 이후 순서를 새로 섞어야 할 때 호출합니다.
        /// </summary>
        public void ReshuffleIfNeeded()
        {
            if (IsShuffled)
            {
                RebuildOrderPreservingCurrent();
            }
        }

        /// <summary>
        /// <see cref="Items"/> 기준 인덱스의 항목을 재생목록에서 제거합니다.
        /// 제거한 항목이 현재 재생 중인 항목이었으면 true를 반환합니다.
        /// 현재 위치(_position)는 보존/보정되어, 현재 항목을 지운 경우 같은 자리(다음 항목)를 가리킵니다.
        /// </summary>
        public bool RemoveAt(int itemIndex)
        {
            if (itemIndex < 0 || itemIndex >= _items.Count)
            {
                return false;
            }

            bool removedCurrent = CurrentItemIndex == itemIndex;
            int orderPos = _order.IndexOf(itemIndex);

            _paths.Remove(_items[itemIndex].FilePath);
            _items.RemoveAt(itemIndex);

            // 순서 목록에서 해당 항목을 제거하고, itemIndex보다 큰 인덱스는 한 칸씩 당깁니다.
            _order.RemoveAt(orderPos);
            for (int i = 0; i < _order.Count; i++)
            {
                if (_order[i] > itemIndex)
                {
                    _order[i]--;
                }
            }

            // 현재 위치 보정
            if (_order.Count == 0)
            {
                _position = -1;
            }
            else if (removedCurrent)
            {
                // 지운 자리에는 다음 항목이 들어오므로 _position은 그대로 두되, 범위를 벗어나면 마지막으로 맞춥니다.
                if (_position >= _order.Count)
                {
                    _position = _order.Count - 1;
                }
            }
            else if (orderPos < _position)
            {
                // 현재보다 앞 순서의 항목을 지웠으면 위치를 한 칸 당깁니다.
                _position--;
            }

            return removedCurrent;
        }

        /// <summary>
        /// 재생목록을 모두 비웁니다.
        /// </summary>
        public void Clear()
        {
            _items.Clear();
            _order.Clear();
            _paths.Clear();
            _position = -1;
        }

        /// <summary>
        /// 셔플/일반 순서를 다시 만들고, 현재 재생 중인 항목 위치를 보존합니다.
        /// </summary>
        private void RebuildOrderPreservingCurrent()
        {
            int currentItem = CurrentItemIndex;

            _order.Clear();
            for (int i = 0; i < _items.Count; i++)
            {
                _order.Add(i);
            }

            if (IsShuffled)
            {
                FisherYates(_order);
            }

            _position = currentItem >= 0 ? _order.IndexOf(currentItem) : (_order.Count > 0 ? 0 : -1);
        }

        /// <summary>
        /// 순서를 새로 섞습니다(현재 위치 보존하지 않음, 전체 반복 순환 시 사용).
        /// </summary>
        private void ShuffleOrderFresh()
        {
            FisherYates(_order);
        }

        private void FisherYates(List<int> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        /// <summary>
        /// Windows 탐색기와 동일한 자연 정렬(파일2 &lt; 파일10) 비교자입니다.
        /// </summary>
        private sealed class NaturalComparer : IComparer<string?>
        {
            public static readonly NaturalComparer Instance = new();

            [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
            private static extern int StrCmpLogicalW(string psz1, string psz2);

            public int Compare(string? x, string? y) =>
                StrCmpLogicalW(x ?? string.Empty, y ?? string.Empty);
        }
    }
}
