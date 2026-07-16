using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace OctoPlayer.Services
{
    /// <summary>
    /// Windows 탐색기와 같은 방식(셸 IShellItemImageFactory)으로 동영상 파일의
    /// 미리보기 이미지를 백그라운드에서 추출해 캐시합니다.
    /// 반환하는 BitmapSource는 Freeze되어 어떤 스레드에서도 사용할 수 있습니다.
    /// 추출 실패(미지원 코덱 등)한 파일은 null로 캐시되어 다시 시도하지 않습니다.
    /// </summary>
    public sealed class ThumbnailCache : IDisposable
    {
        private readonly ConcurrentDictionary<string, BitmapSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<string> _pending = new();
        private readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _queueLock = new();
        private readonly int _width;
        private readonly int _height;
        private readonly Action _onThumbnailReady;
        private int _working;
        private volatile bool _disposed;

        /// <param name="width">추출할 썸네일 최대 너비.</param>
        /// <param name="height">추출할 썸네일 최대 높이(비율은 유지됩니다).</param>
        /// <param name="onThumbnailReady">썸네일이 준비될 때마다 호출되는 콜백(백그라운드 스레드에서 호출됨).</param>
        public ThumbnailCache(int width, int height, Action onThumbnailReady)
        {
            _width = width;
            _height = height;
            _onThumbnailReady = onThumbnailReady;
        }

        /// <summary>
        /// 캐시된 썸네일을 반환합니다. 아직 없으면 백그라운드 생성을 예약하고 null을 반환합니다.
        /// </summary>
        public BitmapSource? Get(string path)
        {
            if (_disposed)
            {
                return null;
            }

            if (_cache.TryGetValue(path, out BitmapSource? cached))
            {
                return cached;
            }

            lock (_queueLock)
            {
                if (_queued.Add(path))
                {
                    _pending.Enqueue(path);
                }
            }

            if (Interlocked.CompareExchange(ref _working, 1, 0) == 0)
            {
                StartWorker();
            }

            return null;
        }

        /// <summary>
        /// 추출 작업 스레드를 시작합니다. 셸 썸네일 추출은 영상 프레임 디코딩까지 하므로
        /// 재생(디코딩)과 CPU/디스크를 다투지 않도록 낮은 우선순위 전용 스레드를 씁니다.
        /// </summary>
        private void StartWorker()
        {
            var worker = new Thread(ProcessQueue)
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = "OctoPlayer.Thumbnails"
            };
            worker.Start();
        }

        /// <summary>이미 추출된 썸네일만 반환합니다(생성 예약 없음).</summary>
        public BitmapSource? TryGetCached(string path) =>
            _cache.TryGetValue(path, out BitmapSource? bmp) ? bmp : null;

        private void ProcessQueue()
        {
            try
            {
                while (!_disposed && _pending.TryDequeue(out string? path))
                {
                    BitmapSource? bmp = null;
                    try
                    {
                        bmp = ExtractShellThumbnail(path, _width, _height);
                    }
                    catch
                    {
                        // 실패는 null로 캐시해 무한 재시도를 막습니다.
                    }

                    _cache[path] = bmp;

                    try
                    {
                        _onThumbnailReady();
                    }
                    catch
                    {
                        // UI 갱신 콜백 실패는 무시합니다.
                    }
                }
            }
            finally
            {
                Volatile.Write(ref _working, 0);

                if (!_disposed && !_pending.IsEmpty && Interlocked.CompareExchange(ref _working, 1, 0) == 0)
                {
                    StartWorker();
                }
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _cache.Clear();
        }

        // -------------------------------------------------------------------
        // Windows 셸 썸네일 추출 (탐색기가 보여주는 것과 동일한 이미지)
        // -------------------------------------------------------------------

        private static BitmapSource? ExtractShellThumbnail(string path, int width, int height)
        {
            Guid iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out IShellItemImageFactory factory);
            try
            {
                factory.GetImage(new NativeSize(width, height), SIIGBF_RESIZETOFIT, out IntPtr hBitmap);
                try
                {
                    BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze(); // 백그라운드 스레드에서 만들어도 UI 스레드에서 쓸 수 있게 고정
                    return source;
                }
                finally
                {
                    DeleteObject(hBitmap);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }

        private const int SIIGBF_RESIZETOFIT = 0x00;

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct NativeSize
        {
            public readonly int Width;
            public readonly int Height;

            public NativeSize(int width, int height)
            {
                Width = width;
                Height = height;
            }
        }

        [ComImport]
        [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            void GetImage(NativeSize size, int flags, out IntPtr phbm);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            string pszPath, IntPtr pbc, ref Guid riid, out IShellItemImageFactory ppv);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);
    }
}
