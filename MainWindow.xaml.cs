using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using OctoPlayer.Models;
using OctoPlayer.Services;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace OctoPlayer
{
    /// <summary>
    /// 메인 플레이어 창.
    /// - 메뉴 트리(UI)는 MainWindow.xaml 의 ContextMenu 에 정의되어 있습니다.
    /// - 이 파일은 이벤트 핸들러(Logic)만 담습니다. 단축키는 Window_PreviewKeyDown 에서
    ///   메뉴와 같은 핸들러를 호출하므로 메뉴를 열지 않아도 동일하게 동작합니다.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly string[] _startupArgs;
        private readonly PlaylistManager _playlist = new();
        private readonly AppSettings _settings = AppSettings.Load();
        private readonly System.Collections.ObjectModel.ObservableCollection<PlaylistEntry> _entries = new();
        private ThumbnailCache _thumbnails = null!;

        private LibVLC _libVlc = null!;
        private MediaPlayer _mediaPlayer = null!;
        private Media? _currentMedia;
        private int _repeatCount = 1;
        private static readonly int[] RepeatCountCycle = { 1, 2, 3, 5, 10 };

        private bool _loaded;
        private bool _isSeeking;
        private bool _updatingUi;
        private bool _isFullscreen;

        // 트랙 전환(다음 파일/반복 재시작) 중에는 대기 화면(로고)을 잠시 억제해 깜빡임을 막습니다.
        private bool _transitioning;
        private DateTime _transitionStartUtc;

        // 현재 미디어가 VLC 내부 반복(:input-repeat)으로 재생 중인지 여부.
        // 내부 반복은 디코더/비디오 출력을 유지한 채 이어지므로 반복 시 검은 화면이 없습니다.
        private bool _mediaRepeatsForever;
        private long _lastTimerTimeMs;

        // libVLC 상태 캐시. Time/Length 같은 네이티브 getter/setter는 시크·버퍼링 중
        // 입력 스레드 잠금을 기다리며 UI 스레드를 수백 ms 이상 막을 수 있어(끊김/응답 없음),
        // 이벤트(TimeChanged 등)로 받은 값을 캐시해 두고 UI에서는 캐시만 읽습니다.
        private long _cachedTimeMs;
        private long _cachedLengthMs;
        private bool _cachedSeekable;
        private volatile VLCState _cachedState = VLCState.NothingSpecial;

        // 진행 중인 시크 요청. libVLC 시크는 비동기라 바쁜 순간(버퍼링/반복 랩 등)에는
        // 무시될 수 있어, 제한된 횟수만 재적용해 클릭이 확실히 반영되게 합니다.
        private long _pendingSeekMs = -1;
        private DateTime _pendingSeekUtc;
        private int _seekRetryCount;
        private Rect _restoreBounds;
        private int _rotation;
        private int _playerRotation;
        private bool _switchingPlayer;
        private bool _applyingSettings;
        private long _resumeAtMs;

        private float _playbackRate = 1f;
        private bool _isMuted;
        private int _lastSpu = -1;

        // 구간 반복(A-B)
        private long _abStartMs = -1;
        private long _abEndMs = -1;

        // 영상 속성(명도/대비/채도/색상)
        private float _brightness = 1f, _contrast = 1f, _saturation = 1f, _hue;

        // 팬 & 스캔
        private float _zoom = 1f;
        private int _panX, _panY;

        // 화면 비율 ("keep"/"orig"/"4:3"/"16:9"/"16:10"/"2.35:1")
        private string _aspectMode = "keep";

        // 이퀄라이저
        private int _eqPreset = -1;
        private int _eqLastPreset;

        private PlaylistViewMode _playlistView = PlaylistViewMode.Titles;
        private readonly HashSet<string> _attachedSubtitles = new(StringComparer.OrdinalIgnoreCase);

        // 챕터(체크포인트) 마커: 재생 시작 시 백그라운드에서 읽어 시크바 위에 ▼로 표시합니다.
        private ChapterDescription[] _chapters = Array.Empty<ChapterDescription>();

        private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
        private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(1.8) };
        private readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
        // 설정 저장 디바운스: 휠 볼륨처럼 연타되는 조작마다 파일을 쓰지 않도록 모아서 저장합니다.
        private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
        private DateTime _lastSideButtonSkipUtc = DateTime.MinValue;
        private ControllerWindow? _controller;

        public MainWindow() : this(Array.Empty<string>())
        {
        }

        // libVLC 초기화(플러그인 스캔)는 수 초가 걸릴 수 있어 백그라운드에서 수행합니다.
        // 준비 전에는 컨트롤을 비활성화하고, 열기 요청은 큐에 담았다가 준비되면 재생합니다.
        private bool _playerReady;
        private (string[] Paths, bool AutoScan)? _pendingOpen;

        public MainWindow(string[] args)
        {
            _startupArgs = args ?? Array.Empty<string>();

            // 언어 적용은 XAML 로드(DynamicResource 평가) 전에 수행합니다.
            Loc.Apply(_settings.Language);

            InitializeComponent();

            _thumbnails = new ThumbnailCache(96, 54, OnThumbnailReady);

            // 창을 즉시 표시하기 위해 libVLC 생성은 Loaded 이후 백그라운드로 미룹니다.
            // 준비 전 상호작용은 컨트롤 비활성화로 차단합니다(타이틀바 창 제어는 유지).
            ContentArea.IsEnabled = false;

            PlaylistList.ItemsSource = _entries;

            // 창 모드 기본 배치: 재생목록/컨트롤바를 영상과 겹치지 않는 별도 칸으로 이동합니다.
            // (XAML에서는 전체화면 오버레이 위치(OverlayRoot)에 선언되어 있습니다.)
            MoveOverlaysToWindowed();

            _uiTimer.Tick += UiTimer_Tick;
            _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };
            _idleTimer.Tick += (_, _) => HideControlsWhenIdle();
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSettingsNow(); };

            // 창 표시를 기다리지 않고 곧바로 백그라운드 초기화를 시작해 시작 시간을 줄입니다.
            // (기존에는 Loaded 이후에 시작해 첫 렌더링 시간만큼 준비가 늦어졌습니다.)
            InitializePlayerAsync();
        }

        // =====================================================================
        // 초기화 / 종료
        // =====================================================================

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            ApplySettings();
            UpdatePlayPauseButton();
            UpdateShuffleButton();
            UpdateRepeatButton();
            UpdateVolumeText();
            UpdateRateText();
            _idleTimer.Start();
            _loaded = true;

            // 이전 버전이 삭제된 뒤에도 레지스트리에 남은 "죽은 연결 등록"을 현재 경로로 복구합니다.
            _ = Task.Run(FileAssociations.RepairIfStale);

            if (_startupArgs.Length > 0)
            {
                OpenPaths(_startupArgs, _settings.OpenFolderScan); // 준비 전이므로 큐에 저장됨
            }
        }

        /// <summary>
        /// libVLC/MediaPlayer를 백그라운드에서 생성합니다(플러그인 스캔 때문에 콜드 스타트 시 수 초 소요).
        /// 창은 즉시 표시되고, 준비가 끝나면 컨트롤을 활성화하고 대기 중인 열기 요청을 처리합니다.
        /// </summary>
        private async void InitializePlayerAsync()
        {
            try
            {
                string[] options = BuildLibVlcOptions(_rotation);
                (LibVLC lib, MediaPlayer mp) = await Task.Run(() =>
                {
                    Core.Initialize();
                    var l = new LibVLC(options);
                    var m = new MediaPlayer(l);
                    return (l, m);
                });

                _libVlc = lib;
                _mediaPlayer = mp;
                ConfigurePlayer(mp);
                SubscribePlayerEvents(mp);
                _playerRotation = _rotation;
                VideoView.MediaPlayer = mp;
                // Loaded(ApplySettings)보다 먼저 끝날 수 있으므로 슬라이더 대신 설정값을 씁니다.
                mp.Volume = Math.Clamp(_settings.Volume, 0, 100);

                _playerReady = true;
                ContentArea.IsEnabled = true;
                _uiTimer.Start();
                UpdatePlayPauseButton();

                if (_pendingOpen is { } pending)
                {
                    _pendingOpen = null;
                    OpenPaths(pending.Paths, pending.AutoScan);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, Loc.F("S_InitFail", ex.Message),
                    "OctoPlayer", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
        }

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            // 이어보기용 마지막 위치 저장 (5초 이상 재생했고 거의 끝이 아닐 때만)
            if (_settings.ResumePlayback && _playlist.Current != null)
            {
                long time = Volatile.Read(ref _cachedTimeMs);
                long length = Volatile.Read(ref _cachedLengthMs);
                if (time > 5000 && (length <= 0 || length - time > 10000))
                {
                    _settings.LastFilePath = _playlist.Current.FilePath;
                    _settings.LastPositionMs = time;
                }
                else
                {
                    _settings.LastFilePath = null;
                    _settings.LastPositionMs = 0;
                }
            }

            _saveTimer.Stop();
            SaveSettingsNow();
            _uiTimer.Stop();
            _controller?.Close();
            _thumbnails.Dispose();

            // 백그라운드 초기화가 끝나기 전에 닫는 경우를 대비해 null 가드
            if (_mediaPlayer != null)
            {
                UnsubscribePlayerEvents(_mediaPlayer);
                VideoView.MediaPlayer = null;
                try { _mediaPlayer.Stop(); } catch { }
                _currentMedia?.Dispose();
                _mediaPlayer.Dispose();
            }
            _libVlc?.Dispose();
        }

        private static string[] BuildLibVlcOptions(int rotation)
        {
            var options = new List<string>
            {
                // 자막은 앱이 인코딩 변환까지 해서 직접 붙이므로 VLC 자체 탐지는 끕니다(중복 트랙 방지).
                "--no-sub-autodetect-file",
                // 한글 자막 글꼴 깨짐 방지
                "--freetype-font=Malgun Gothic"
            };

            // libVLC는 플러그인 캐시(plugins.dat)를 읽기만 하고 스스로 만들지는 않아,
            // 캐시가 없으면 매 실행마다 수백 개 플러그인 DLL을 전체 스캔합니다(시작 지연의 주원인).
            // 캐시가 없을 때 이 옵션을 주면 이번 스캔 결과를 캐시로 기록해 다음 실행부터 빨라집니다.
            // (공식 vlc-cache-gen 도구가 쓰는 것과 같은 방식입니다.)
            if (!PluginsCacheExists())
            {
                options.Add("--reset-plugins-cache");
            }

            if (rotation != 0)
            {
                options.Add("--video-filter=transform");
                options.Add($"--transform-type={rotation}");
            }

            return options.ToArray();
        }

        private static bool PluginsCacheExists()
        {
            try
            {
                string arch = RuntimeInformation.ProcessArchitecture switch
                {
                    Architecture.X86 => "win-x86",
                    Architecture.Arm64 => "win-arm64",
                    _ => "win-x64"
                };
                string plugins = Path.Combine(AppContext.BaseDirectory, "libvlc", arch, "plugins");
                // 앱과 함께 배포된 libvlc가 아니면(시스템 VLC 등) 캐시 관리에 관여하지 않습니다.
                return !Directory.Exists(plugins) || File.Exists(Path.Combine(plugins, "plugins.dat"));
            }
            catch
            {
                return true; // 확인 실패 시 기본 동작 유지
            }
        }

        private void ConfigurePlayer(MediaPlayer mp)
        {
            // libVLC 비디오 창이 입력을 가로채지 않아야 WPF 오버레이가 마우스/키보드를 받습니다.
            mp.EnableKeyInput = false;
            mp.EnableMouseInput = false;
        }

        private void SubscribePlayerEvents(MediaPlayer mp)
        {
            mp.EndReached += Player_EndReached;
            mp.Playing += Player_Playing;
            mp.Paused += Player_Paused;
            mp.Stopped += Player_Stopped;
            mp.Opening += Player_Opening;
            mp.EncounteredError += Player_EncounteredError;
            mp.TimeChanged += Player_TimeChanged;
            mp.LengthChanged += Player_LengthChanged;
            mp.SeekableChanged += Player_SeekableChanged;
        }

        private void UnsubscribePlayerEvents(MediaPlayer mp)
        {
            mp.EndReached -= Player_EndReached;
            mp.Playing -= Player_Playing;
            mp.Paused -= Player_Paused;
            mp.Stopped -= Player_Stopped;
            mp.Opening -= Player_Opening;
            mp.EncounteredError -= Player_EncounteredError;
            mp.TimeChanged -= Player_TimeChanged;
            mp.LengthChanged -= Player_LengthChanged;
            mp.SeekableChanged -= Player_SeekableChanged;
        }

        // ----- 상태 캐시 갱신 (libVLC 스레드에서 호출되므로 필드 기록만 합니다) -----

        private void Player_TimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e) =>
            Volatile.Write(ref _cachedTimeMs, e.Time);

        private void Player_LengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
        {
            Volatile.Write(ref _cachedLengthMs, e.Length);
            RunOnUi(RenderChapterMarkers); // 길이를 알아야 챕터 위치를 배치할 수 있음
        }

        private void Player_SeekableChanged(object? sender, MediaPlayerSeekableChangedEventArgs e) =>
            _cachedSeekable = e.Seekable != 0;

        private void Player_Opening(object? sender, EventArgs e) => _cachedState = VLCState.Opening;

        private void Player_Paused(object? sender, EventArgs e)
        {
            _cachedState = VLCState.Paused;
            RunOnUi(UpdatePlayPauseButton);
        }

        private void Player_Stopped(object? sender, EventArgs e)
        {
            _cachedState = VLCState.Stopped;
            Volatile.Write(ref _cachedTimeMs, 0);
            RunOnUi(UpdatePlayPauseButton);
        }

        private void Player_EncounteredError(object? sender, EventArgs e)
        {
            _cachedState = VLCState.Error;
            RunOnUi(UpdatePlayPauseButton);
        }

        private void RunOnUi(Action action) => Dispatcher.BeginInvoke(action);

        // =====================================================================
        // 설정
        // =====================================================================

        private void ApplySettings()
        {
            _applyingSettings = true;
            try
            {
                _playlist.RepeatMode = _settings.RepeatMode;
                _playlist.SetShuffle(_settings.IsShuffled);
                _repeatCount = Math.Clamp(_settings.RepeatCount, 1, 99);
                RepeatCountButton.Content = $"×{_repeatCount}";

                int volume = Math.Clamp(_settings.Volume, 0, 100);
                VolumeSlider.Value = volume;
                if (_mediaPlayer != null)
                {
                    _mediaPlayer.Volume = volume;
                }

                PlaylistPanel.Width = Math.Clamp(_settings.PlaylistWidth, 180, 700);
                SetPlaylistViewMode(_settings.PlaylistView);

                // 창 크기 기억
                if (_settings.RememberWindowSize && _settings.WindowWidth >= MinWidth && _settings.WindowHeight >= MinHeight)
                {
                    Width = _settings.WindowWidth;
                    Height = _settings.WindowHeight;
                }

                // 재생 속도 기억
                if (_settings.RememberRate && _settings.LastRate is >= 0.25f and <= 2f)
                {
                    _playbackRate = _settings.LastRate;
                    UpdateRateText();
                }

                Topmost = _settings.AlwaysOnTop;
            }
            finally
            {
                _applyingSettings = false;
            }
        }

        /// <summary>환경설정 변경 직후 런타임에 바로 반영해야 하는 항목을 적용합니다.</summary>
        private void ApplyRuntimeSettings()
        {
            if (!_isFullscreen)
            {
                Topmost = _settings.AlwaysOnTop;
            }
        }

        /// <summary>
        /// 설정 저장을 예약합니다. 휠 볼륨처럼 연타되는 조작마다 디스크에 쓰면 UI가 미세하게
        /// 끊길 수 있어, 잠시 모았다가(0.8초) 한 번만 저장합니다.
        /// </summary>
        private void SaveSettings()
        {
            if (_applyingSettings || !_loaded)
            {
                return;
            }

            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void SaveSettingsNow()
        {
            // 아직 초기 로드가 끝나지 않았거나 설정을 적용하는 중이면 저장하지 않습니다.
            if (_applyingSettings || !_loaded)
            {
                return;
            }

            _settings.RepeatMode = _playlist.RepeatMode;
            _settings.IsShuffled = _playlist.IsShuffled;
            _settings.RepeatCount = _repeatCount;
            _settings.Volume = (int)VolumeSlider.Value;
            _settings.PlaylistWidth = (int)PlaylistPanel.Width;
            _settings.PlaylistView = _playlistView;
            _settings.LastRate = _playbackRate;

            // 창 크기(일반 상태일 때만; 전체화면/최대화 크기는 저장하지 않음)
            if (!_isFullscreen && WindowState == WindowState.Normal
                && ActualWidth >= MinWidth && ActualHeight >= MinHeight)
            {
                _settings.WindowWidth = ActualWidth;
                _settings.WindowHeight = ActualHeight;
            }

            _settings.Save();
        }

        // =====================================================================
        // 파일/폴더/주소 열기
        // =====================================================================

        private void Menu_OpenFile(object? sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = Loc.T("S_OpenMediaTitle"),
                Multiselect = true,
                Filter = SupportedFormats.BuildOpenFileFilter()
            };
            if (dialog.ShowDialog(this) == true)
            {
                // 폴더의 다른 파일도 함께 열지는 환경설정에서 선택합니다.
                OpenPaths(dialog.FileNames, _settings.OpenFolderScan);
            }
        }

        private void Menu_OpenFolder(object? sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("S_SelectFolderTitle") };
            if (dialog.ShowDialog(this) == true)
            {
                OpenPaths(new[] { dialog.FolderName }, autoScanFolder: false);
            }
        }

        private void Menu_OpenUrl(object? sender, RoutedEventArgs e)
        {
            string? url = PromptWindow.Show(this, Loc.T("S_OpenUrlTitle"), Loc.T("S_OpenUrlLabel"));
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            var media = new Media(_libVlc, url, FromType.FromLocation);
            _mediaPlayer.Play(media);
            _currentMedia?.Dispose();
            _currentMedia = media;
            SetTitle(url);
            UpdatePlayPauseButton();
        }

        private void Menu_CloseMedia(object? sender, RoutedEventArgs e)
        {
            StopPlaybackInternal();
            _currentMedia?.Dispose();
            _currentMedia = null;
            _chapters = Array.Empty<ChapterDescription>();
            ChapterMarkerCanvas.Children.Clear();
            SetTitle(null);
            ShowToast(Loc.T("S_Close"));
        }

        private async void OpenPaths(IEnumerable<string> paths, bool autoScanFolder)
        {
            var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            if (list.Count == 0)
            {
                return;
            }

            // 미디어 엔진 준비 전(시작 직후)에는 큐에 담아 두고 준비되면 처리합니다.
            if (!_playerReady)
            {
                _pendingOpen = (list.ToArray(), autoScanFolder);
                return;
            }

            // 폴더 열람 + 자연 정렬은 느린 디스크에서 수 초가 걸릴 수 있어 백그라운드에서 수행합니다.
            // (기존에는 시작 직후 UI 스레드에서 실행되어 창이 하얗게(응답 없음) 되는 원인이었습니다.)
            (List<string> files, string? firstPlayable) =
                await Task.Run(() => CollectPlayableFiles(list, autoScanFolder));

            foreach (string file in files)
            {
                _playlist.AddFile(file); // 중복은 내부에서 무시됨
            }

            firstPlayable ??= _playlist.Items.Count > 0 ? _playlist.Items[0].FilePath : null;
            _playlist.ReshuffleIfNeeded();
            RefreshPlaylist();

            if (firstPlayable != null && _playlist.SelectByPath(firstPlayable) != null)
            {
                PlayCurrent();
            }
            else if (_playlist.Current == null && _playlist.Count > 0)
            {
                _playlist.SelectByItemIndex(0);
                PlayCurrent();
            }
        }

        /// <summary>
        /// 열기 요청 경로들을 실제 재생 파일 목록으로 펼칩니다(폴더 스캔 포함).
        /// 디스크 IO만 하므로 백그라운드 스레드에서 호출됩니다.
        /// </summary>
        private static (List<string> Files, string? FirstPlayable) CollectPlayableFiles(
            List<string> paths, bool autoScanFolder)
        {
            var files = new List<string>();
            string? firstPlayable = null;

            foreach (string path in paths)
            {
                if (Directory.Exists(path))
                {
                    files.AddRange(PlaylistManager.ScanFolderFiles(path));
                }
                else if (SupportedFormats.IsSupported(path) && File.Exists(path))
                {
                    firstPlayable ??= path;

                    string? dir = autoScanFolder ? Path.GetDirectoryName(path) : null;
                    if (!string.IsNullOrEmpty(dir))
                    {
                        files.AddRange(PlaylistManager.ScanFolderFiles(dir));
                    }
                    else
                    {
                        files.Add(path);
                    }
                }
            }

            return (files, firstPlayable);
        }

        // =====================================================================
        // 재생 핵심 (회전 시 플레이어 스택 재생성 포함)
        // =====================================================================

        private void PlayCurrent()
        {
            _rotation = 0;
            StartPlayback();
        }

        // 연속 파일 전환 보호: libVLC의 Play/미디어 교체는 동시 호출하면 안 되므로
        // 게이트로 직렬화하고, 밀린 요청 중 "마지막 것만" 실제로 재생합니다(이전 요청 무효화).
        private int _playbackVersion;
        private readonly SemaphoreSlim _playbackGate = new(1, 1);

        private async void StartPlayback(long startTimeMs = 0)
        {
            if (_switchingPlayer || !_playerReady)
            {
                return;
            }

            PlaylistItem? item = _playlist.Current;
            if (item == null)
            {
                return;
            }

            int version = Interlocked.Increment(ref _playbackVersion);

            // 정지→재생 사이의 순간에도 로고가 깜빡이지 않도록 전환 상태로 표시합니다(Playing에서 해제).
            _transitioning = true;
            _transitionStartUtc = DateTime.UtcNow;

            if (_playerRotation != _rotation)
            {
                await RecreatePlayerAsync();
                if (version != _playbackVersion)
                {
                    return; // 회전 재구성 중 더 새 요청이 들어옴
                }
            }

            // 이어보기: 마지막으로 종료한 파일을 다시 열면 그 위치(2초 앞)에서 시작합니다. 1회만 적용.
            if (startTimeMs == 0
                && _settings.ResumePlayback
                && !string.IsNullOrEmpty(_settings.LastFilePath)
                && string.Equals(item.FilePath, _settings.LastFilePath, StringComparison.OrdinalIgnoreCase)
                && _settings.LastPositionMs > 5000)
            {
                startTimeMs = Math.Max(0, _settings.LastPositionMs - 2000);
                _settings.LastFilePath = null;
                ShowToast(Loc.F("S_ResumeToast", FormatTime(startTimeMs)));
            }

            // 새 미디어 기준으로 상태 캐시를 초기화합니다(이전 트랙 값이 남지 않도록).
            _mediaRepeatsForever = WantsInfiniteLoop();
            _lastTimerTimeMs = 0;
            Volatile.Write(ref _cachedTimeMs, 0);
            Volatile.Write(ref _cachedLengthMs, 0);
            _cachedSeekable = false;
            _pendingSeekMs = -1;
            _chapters = Array.Empty<ChapterDescription>();
            ChapterMarkerCanvas.Children.Clear();

            // 백그라운드 작업으로 넘길 값들을 UI 스레드에서 미리 복사합니다.
            bool loopForever = _mediaRepeatsForever;
            int repeatCount = _repeatCount;
            int rotation = _rotation;
            bool autoSubs = _settings.AutoLoadSubtitles;
            string filePath = item.FilePath;
            long startMs = startTimeMs;
            _resumeAtMs = startTimeMs;

            // 명시적 Stop() 없이 바로 새 미디어로 전환합니다. Stop을 먼저 호출하면 비디오 출력이
            // 완전히 파괴됐다가 재생성되어 트랙 전환 시 검은 화면이 길게 보입니다.
            // Play(media)는 내부에서 전환을 처리하며 가능하면 기존 비디오 출력을 재활용합니다.
            //
            // 게이트로 직렬화: Play를 동시에 두 번 호출하거나, 이전 미디어를 아직 사용 중일 때
            // Dispose하면 네이티브 크래시가 날 수 있습니다. 밀린 요청은 최신 것만 실행합니다.
            await _playbackGate.WaitAsync();
            try
            {
                if (version != _playbackVersion)
                {
                    return; // 더 새 요청이 이미 접수됨 → 이 요청은 폐기
                }

                // 미디어 생성/옵션/자막 탐색은 파일·폴더 IO를 포함하므로 전부 백그라운드에서 수행합니다.
                // (기존에는 UI 스레드에서 실행되어 느린 디스크에서 트랙 전환마다 UI가 멈췄습니다.)
                var attached = new List<string>();
                Media media = await Task.Run(() =>
                {
                    var m = new Media(_libVlc, filePath, FromType.FromPath);

                    // 반복 계획을 미디어에 미리 심습니다. VLC가 내부적으로 입력만 되감아 반복하므로
                    // 정지→재생 없이(비디오 출력 유지) 이어져 반복 시 빈 화면이 생기지 않습니다.
                    if (loopForever)
                    {
                        m.AddOption(":input-repeat=65535");
                    }
                    else if (repeatCount > 1)
                    {
                        // 반복 횟수 N: VLC가 N회 재생한 뒤 EndReached를 발생 → 다음 파일로 진행
                        m.AddOption($":input-repeat={repeatCount - 1}");
                    }

                    if (startMs > 0)
                    {
                        double seconds = startMs / 1000.0;
                        m.AddOption($":start-time={seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                    }

                    if (Volatile.Read(ref _playbackVersion) == version)
                    {
                        AttachLocalSubtitles(m, filePath, rotation, autoSubs, attached);
                    }

                    return m;
                });

                if (version != _playbackVersion)
                {
                    _ = Task.Run(() => { try { media.Dispose(); } catch { } });
                    return;
                }

                _attachedSubtitles.Clear();
                foreach (string sub in attached)
                {
                    _attachedSubtitles.Add(sub);
                }

                await Task.Run(() => _mediaPlayer.Play(media));

                // 이전 미디어는 플레이어가 새 미디어로 전환된 뒤에만 정리합니다.
                Media? oldMedia = _currentMedia;
                _currentMedia = media;
                if (oldMedia != null)
                {
                    _ = Task.Run(() => { try { oldMedia.Dispose(); } catch { } });
                }
            }
            finally
            {
                _playbackGate.Release();
            }

            if (version != _playbackVersion)
            {
                return; // UI 갱신도 최신 요청만 수행
            }

            _mediaPlayer.Volume = (int)VolumeSlider.Value;
            SetTitle(item.DisplayName);
            UpdatePlayingHighlight();
            UpdatePlayPauseButton();
        }

        private async Task RecreatePlayerAsync()
        {
            _switchingPlayer = true;
            try
            {
                MediaPlayer oldPlayer = _mediaPlayer;
                LibVLC oldLib = _libVlc;
                Media? oldMedia = _currentMedia;

                VideoView.MediaPlayer = null;
                UnsubscribePlayerEvents(oldPlayer);
                await Task.Run(() => { try { oldPlayer.Stop(); } catch { } });

                LibVLC newLib = new(BuildLibVlcOptions(_rotation));
                MediaPlayer newPlayer = new(newLib);
                ConfigurePlayer(newPlayer);
                _libVlc = newLib;
                _mediaPlayer = newPlayer;
                _currentMedia = null;
                SubscribePlayerEvents(newPlayer);
                VideoView.MediaPlayer = newPlayer;
                newPlayer.Volume = (int)VolumeSlider.Value;
                newPlayer.Mute = _isMuted;
                _playerRotation = _rotation;

                // 새 플레이어 기준으로 상태 캐시 초기화
                _cachedState = VLCState.NothingSpecial;
                Volatile.Write(ref _cachedTimeMs, 0);
                Volatile.Write(ref _cachedLengthMs, 0);
                _cachedSeekable = false;

                _ = Task.Run(() =>
                {
                    try { oldPlayer.Dispose(); } catch { }
                    try { oldMedia?.Dispose(); } catch { }
                    try { oldLib.Dispose(); } catch { }
                });
            }
            finally
            {
                _switchingPlayer = false;
            }
        }

        private void Player_EndReached(object? sender, EventArgs e)
        {
            _cachedState = VLCState.Ended;

            // 다음 곡/반복 여부가 결정되기 전(Ended 상태 구간)에도 로고가 깜빡이지 않도록
            // 이벤트 시점에 즉시 전환 상태로 표시합니다. 계속 재생하지 않으면 HandleTrackEnded에서 해제합니다.
            _transitioning = true;
            _transitionStartUtc = DateTime.UtcNow;
            RunOnUi(HandleTrackEnded);
        }

        private void Player_Playing(object? sender, EventArgs e)
        {
            _cachedState = VLCState.Playing;

            long resume = _resumeAtMs;
            _resumeAtMs = 0;

            RunOnUi(() =>
            {
                _transitioning = false; // 재생이 시작됐으므로 대기 화면 억제 해제

                if (resume > 0 && _pendingSeekMs < 0)
                {
                    // 시작 위치는 :start-time 옵션이 이미 처리하므로 여기서는 추적만 등록합니다.
                    // start-time을 무시하는 포맷이면 UiTimer가 1초 뒤 실제 시크로 보정하고,
                    // 사용자가 이미 다른 위치로 시크했다면(_pendingSeekMs 존재) 덮어쓰지 않습니다.
                    _pendingSeekMs = resume;
                    _pendingSeekUtc = DateTime.UtcNow;
                    _seekRetryCount = 0;
                    _lastTimerTimeMs = resume;
                }

                // 오디오 출력이 준비된 시점에 볼륨/음소거/속도/영상속성/팬스캔을 재적용합니다.
                _mediaPlayer.Volume = (int)VolumeSlider.Value;
                _mediaPlayer.Mute = _isMuted;
                if (Math.Abs(_playbackRate - 1f) > 0.001f)
                {
                    _mediaPlayer.SetRate(_playbackRate);
                }
                ReapplyVideoAdjust();
                ApplyPanScan();
                ApplyAspect();

                if (_mediaPlayer.Spu == -1 && _mediaPlayer.SpuCount > 0)
                {
                    SelectFirstSubtitleTrack();
                }

                UpdatePlayPauseButton();
                UpdateSubtitleButton();
                LoadChaptersAsync();
            });
        }

        /// <summary>
        /// 현재 미디어의 챕터 목록을 백그라운드에서 읽어 시크바 위 마커로 표시합니다.
        /// 재생 시작 직후에는 챕터 정보가 아직 준비되지 않았을 수 있어 잠시 후 한 번 더 시도합니다.
        /// </summary>
        private void LoadChaptersAsync()
        {
            int version = _playbackVersion;
            MediaPlayer mp = _mediaPlayer;
            _ = Task.Run(async () =>
            {
                ChapterDescription[] chapters = Array.Empty<ChapterDescription>();
                try
                {
                    chapters = mp.FullChapterDescriptions(-1) ?? Array.Empty<ChapterDescription>();
                    if (chapters.Length == 0)
                    {
                        await Task.Delay(1500);
                        if (Volatile.Read(ref _playbackVersion) != version)
                        {
                            return;
                        }
                        chapters = mp.FullChapterDescriptions(-1) ?? Array.Empty<ChapterDescription>();
                    }
                }
                catch
                {
                    // 챕터 조회 실패 시 마커만 표시되지 않습니다.
                }

                ChapterDescription[] result = chapters;
                RunOnUi(() =>
                {
                    if (version != _playbackVersion)
                    {
                        return;
                    }
                    _chapters = result;
                    RenderChapterMarkers();
                });
            });
        }

        /// <summary>시크바 위 캔버스에 챕터 위치마다 ▼ 화살표 마커를 그립니다(클릭 시 해당 지점으로 이동).</summary>
        private void RenderChapterMarkers()
        {
            ChapterMarkerCanvas.Children.Clear();

            long len = Volatile.Read(ref _cachedLengthMs);
            double width = ChapterMarkerCanvas.ActualWidth;
            if (len <= 0 || width <= 0 || _chapters.Length == 0)
            {
                return;
            }

            for (int i = 0; i < _chapters.Length; i++)
            {
                ChapterDescription chapter = _chapters[i];
                long timeMs = chapter.TimeOffset;
                if (timeMs <= 500)
                {
                    continue; // 0초 시작 챕터는 표시할 의미가 없습니다.
                }

                double x = Math.Clamp(timeMs / (double)len, 0, 1) * width;
                string name = string.IsNullOrWhiteSpace(chapter.Name) ? Loc.F("S_Chapter", i + 1) : chapter.Name!;

                var marker = new System.Windows.Shapes.Polygon
                {
                    Points = new PointCollection { new Point(0, 0), new Point(8, 0), new Point(4, 6) },
                    Fill = (Brush)FindResource("AccentBrush"),
                    Cursor = Cursors.Hand,
                    ToolTip = $"{name}  ({FormatTime(timeMs)})"
                };
                long targetMs = timeMs;
                marker.MouseLeftButtonDown += (_, args) =>
                {
                    ApplySeek(targetMs);
                    args.Handled = true;
                };
                Canvas.SetLeft(marker, x - 4);
                Canvas.SetTop(marker, 2);
                ChapterMarkerCanvas.Children.Add(marker);
            }
        }

        private void ChapterMarkerCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RenderChapterMarkers();

        /// <summary>한 곡 반복 또는 (전체 반복 + 단일 항목)처럼 같은 파일을 무한 반복해야 하는 상태인지.</summary>
        private bool WantsInfiniteLoop() =>
            _playlist.RepeatMode == RepeatMode.One
            || (_playlist.RepeatMode == RepeatMode.All && _playlist.Count == 1);

        private void HandleTrackEnded()
        {
            _abStartMs = _abEndMs = -1;

            // 무한 반복이어야 하는데 자연 종료가 온 경우(내부 반복 미적용 미디어 등)의 안전망: 재시작.
            // 반복 횟수는 :input-repeat 옵션으로 VLC가 이미 N회 재생을 마쳤으므로 여기서는 진행만 합니다.
            if (WantsInfiniteLoop())
            {
                StartPlayback();
                return;
            }

            if (_playlist.Next() != null)
            {
                PlayCurrent();
            }
            else
            {
                // 더 재생할 항목이 없으므로 대기 화면(로고)을 표시합니다.
                _transitioning = false;
                UpdatePlayPauseButton();
            }
        }

        private async void StopPlaybackInternal()
        {
            _transitioning = false; // 의도적 정지: 대기 화면 표시
            _pendingSeekMs = -1;

            if (_cachedState is VLCState.Playing or VLCState.Paused or VLCState.Opening)
            {
                await Task.Run(() => _mediaPlayer.Stop());
            }
            _updatingUi = true;
            SeekSlider.Value = 0;
            _updatingUi = false;
            CurrentTimeText.Text = "00:00:00";
            TotalTimeText.Text = "00:00:00";
            _abStartMs = _abEndMs = -1;
            UpdatePlayPauseButton();
        }

        // =====================================================================
        // 재생 메뉴 핸들러
        // =====================================================================

        private void Menu_PlayPause(object? sender, RoutedEventArgs e) => TogglePlayPause();

        internal void TogglePlayPause()
        {
            if (_playlist.Current == null && _currentMedia == null)
            {
                // 재생목록에 항목이 있으면(추가만 해 둔 상태) 첫 항목부터 재생합니다.
                if (_playlist.Count > 0)
                {
                    _playlist.SelectByItemIndex(0);
                    PlayCurrent();
                    return;
                }

                Menu_OpenFile(null, new RoutedEventArgs());
                return;
            }

            if (_cachedState == VLCState.Playing)
            {
                _mediaPlayer.Pause();
            }
            else if (_cachedState == VLCState.Ended || _currentMedia == null)
            {
                PlayCurrent();
            }
            else
            {
                _mediaPlayer.Play();
            }

            UpdatePlayPauseButton();
        }

        private void Stop_Click(object? sender, RoutedEventArgs e) => StopPlaybackInternal();

        private void Menu_PrevFile(object? sender, RoutedEventArgs e) => PlayPreviousManual();

        private void Menu_NextFile(object? sender, RoutedEventArgs e) => PlayNextManual();

        internal void PlayNextManual()
        {
            if (_playlist.Next() != null)
            {
                PlayCurrent();
            }
        }

        internal void PlayPreviousManual()
        {
            if (Volatile.Read(ref _cachedTimeMs) > 3000)
            {
                ApplySeek(0);
                return;
            }

            if (_playlist.Previous() != null)
            {
                PlayCurrent();
            }
        }

        private void Menu_SpeedDown(object? sender, RoutedEventArgs e) => ChangePlaybackRate(-0.1f);
        private void Menu_SpeedUp(object? sender, RoutedEventArgs e) => ChangePlaybackRate(+0.1f);
        private void Menu_SpeedReset(object? sender, RoutedEventArgs e) => SetPlaybackRate(1f);

        private void SetPlaybackRate(float rate)
        {
            float clamped = Math.Clamp(rate, 0.25f, 2f);
            _playbackRate = MathF.Round(clamped * 100f) / 100f;
            _mediaPlayer.SetRate(_playbackRate);
            UpdateRateText();
            ShowToast(Loc.F("S_SpeedToast", $"{_playbackRate:0.##}"));
        }

        private void ChangePlaybackRate(float delta) => SetPlaybackRate(_playbackRate + delta);

        private void UpdateRateText()
        {
            RateText.Text = $"{_playbackRate:0.##}x";
            RateText.Foreground = Math.Abs(_playbackRate - 1f) < 0.001f
                ? (Brush)FindResource("TextDimBrush")
                : (Brush)FindResource("AccentBrush");
        }

        // ----- 구간 반복 (A-B) -----

        private void Menu_AbSetA(object? sender, RoutedEventArgs e)
        {
            _abStartMs = Volatile.Read(ref _cachedTimeMs);
            if (_abEndMs <= _abStartMs)
            {
                _abEndMs = -1;
            }
            ShowToast(Loc.F("S_AbAToast", FormatTime(_abStartMs)));
        }

        private void Menu_AbSetB(object? sender, RoutedEventArgs e)
        {
            long now = Volatile.Read(ref _cachedTimeMs);
            if (_abStartMs < 0 || now <= _abStartMs)
            {
                ShowToast(Loc.T("S_AbNeedA"));
                return;
            }
            _abEndMs = now;
            ShowToast(Loc.F("S_AbRangeToast", FormatTime(_abStartMs), FormatTime(_abEndMs)));
        }

        private void Menu_AbClear(object? sender, RoutedEventArgs e)
        {
            _abStartMs = _abEndMs = -1;
            ShowToast(Loc.T("S_AbClear"));
        }

        private void Menu_CycleRepeat(object? sender, RoutedEventArgs e)
        {
            _playlist.RepeatMode = _playlist.RepeatMode switch
            {
                RepeatMode.None => RepeatMode.All,
                RepeatMode.All => RepeatMode.One,
                _ => RepeatMode.None
            };
            UpdateRepeatButton();
            SaveSettings();
            ShowToast(RepeatButton.Content.ToString() ?? "");
        }

        private void Menu_ToggleShuffle(object? sender, RoutedEventArgs e)
        {
            _playlist.SetShuffle(!_playlist.IsShuffled);
            UpdateShuffleButton();
            SaveSettings();
            ShowToast(_playlist.IsShuffled ? Loc.T("S_ShuffleOn") : Loc.T("S_ShuffleOff"));
        }

        private void RepeatCount_Click(object? sender, RoutedEventArgs e)
        {
            int idx = Array.IndexOf(RepeatCountCycle, _repeatCount);
            _repeatCount = RepeatCountCycle[(idx + 1) % RepeatCountCycle.Length];
            RepeatCountButton.Content = $"×{_repeatCount}";
            SaveSettings();
            ShowToast(Loc.F("S_RepeatCountToast", _repeatCount));
        }

        // =====================================================================
        // 자막
        // =====================================================================

        /// <summary>
        /// 영상과 같은 폴더의 짝 자막을 찾아 미디어에 붙입니다. 붙인 원본 경로는 attached에 담습니다.
        /// 폴더 열람·파일 읽기·인코딩 변환(IO)이 있으므로 백그라운드 스레드에서 호출됩니다.
        /// </summary>
        private static void AttachLocalSubtitles(Media media, string videoPath, int rotation, bool autoLoad, List<string> attached)
        {
            if (!autoLoad)
            {
                return;
            }

            try
            {
                string? dir = Path.GetDirectoryName(videoPath);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    return;
                }

                string baseName = Path.GetFileNameWithoutExtension(videoPath);
                foreach (string file in Directory.EnumerateFiles(dir))
                {
                    if (!SubtitleFiles.IsSubtitleFile(file) || !SubtitleFiles.MatchesVideo(file, baseName))
                    {
                        continue;
                    }

                    // 현재 회전값을 반영해(90/180/270이면 회전 ASS로) 변환합니다.
                    string prepared = SubtitleFiles.PrepareForLibVlc(file, rotation);
                    if (media.AddSlave(MediaSlaveType.Subtitle, 4, new Uri(prepared).AbsoluteUri))
                    {
                        attached.Add(file);
                    }
                }
            }
            catch
            {
                // 자막 탐색 실패는 재생에 영향 주지 않음
            }
        }

        private void Menu_OpenSubtitle(object? sender, RoutedEventArgs e)
        {
            if (_playlist.Current == null && _currentMedia == null)
            {
                return;
            }

            string mask = string.Join(";", SubtitleFiles.Extensions.Select(x => "*" + x));
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = Loc.T("S_OpenSubTitle"),
                Filter = $"{Loc.T("S_SubFilter")}|{mask}|{Loc.T("S_AllFiles")}|*.*"
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            if (_attachedSubtitles.Contains(dialog.FileName))
            {
                if (_mediaPlayer.Spu == -1)
                {
                    SelectFirstSubtitleTrack();
                }
                return;
            }

            string prepared = SubtitleFiles.PrepareForLibVlc(dialog.FileName, _rotation);
            if (_mediaPlayer.AddSlave(MediaSlaveType.Subtitle, new Uri(prepared).AbsoluteUri, true))
            {
                _attachedSubtitles.Add(dialog.FileName);
            }
        }

        private void SubtitleButton_Click(object? sender, RoutedEventArgs e) =>
            Menu_ToggleSubtitles(null, new RoutedEventArgs());

        /// <summary>하단 컨트롤바 자막 버튼 강조: 자막 트랙이 켜져 있으면 강조 배경.</summary>
        private void UpdateSubtitleButton()
        {
            bool on = _mediaPlayer != null && _mediaPlayer.Spu != -1;
            SubtitleButton.Background = on
                ? (Brush)FindResource("AccentSoftBrush")
                : Brushes.Transparent;
        }

        private void Menu_ToggleSubtitles(object? sender, RoutedEventArgs e)
        {
            if (_mediaPlayer.Spu != -1)
            {
                _lastSpu = _mediaPlayer.Spu;
                _mediaPlayer.SetSpu(-1);
                ShowToast(Loc.T("S_SubHidden"));
            }
            else
            {
                bool restored = false;
                foreach (TrackDescription t in _mediaPlayer.SpuDescription)
                {
                    if (t.Id == _lastSpu && t.Id != -1)
                    {
                        _mediaPlayer.SetSpu(t.Id);
                        restored = true;
                        break;
                    }
                }
                if (!restored)
                {
                    SelectFirstSubtitleTrack();
                }
                ShowToast(_mediaPlayer.Spu != -1 ? Loc.T("S_SubShown") : Loc.T("S_SubNone"));
            }

            UpdateSubtitleButton();
        }

        private void SelectFirstSubtitleTrack()
        {
            foreach (TrackDescription track in _mediaPlayer.SpuDescription)
            {
                if (track.Id != -1)
                {
                    _mediaPlayer.SetSpu(track.Id);
                    return;
                }
            }
        }

        private void Menu_SubDelayMinus(object? sender, RoutedEventArgs e) => ChangeSubDelay(-500_000);
        private void Menu_SubDelayPlus(object? sender, RoutedEventArgs e) => ChangeSubDelay(+500_000);

        private void Menu_SubDelayReset(object? sender, RoutedEventArgs e)
        {
            _mediaPlayer.SetSpuDelay(0);
            ShowToast(Loc.T("S_SubSyncResetToast"));
        }

        private void ChangeSubDelay(long deltaMicroseconds)
        {
            long delay = _mediaPlayer.SpuDelay + deltaMicroseconds;
            _mediaPlayer.SetSpuDelay(delay);
            ShowToast(Loc.F("S_SubDelayToast", $"{delay / 1_000_000.0:+0.0;-0.0;0}"));
        }

        // =====================================================================
        // 영상 (캡처 / 회전 / 속성)
        // =====================================================================

        private void Menu_Capture(object? sender, RoutedEventArgs e)
        {
            if (_currentMedia == null)
            {
                ShowToast(Loc.T("S_NoVideo"));
                return;
            }

            string dir = string.IsNullOrWhiteSpace(_settings.CaptureFolder)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "OctoPlayer")
                : _settings.CaptureFolder;
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch
            {
                // 설정된 폴더를 만들 수 없으면 기본 폴더로 대체합니다.
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "OctoPlayer");
                Directory.CreateDirectory(dir);
            }
            string prefix = Loc.Current == "en" ? "Capture" : "캡처";
            string path = Path.Combine(dir, $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}.png");

            bool ok = _mediaPlayer.TakeSnapshot(0, path, 0, 0);
            ShowToast(ok ? Loc.F("S_CaptureSaved", path) : Loc.T("S_CaptureFail"));
        }

        private void Menu_Rotate(object? sender, RoutedEventArgs e) => RotateVideo();

        private void RotateVideo()
        {
            if (_switchingPlayer || _playlist.Current == null)
            {
                return;
            }

            long resumeAt = Volatile.Read(ref _cachedTimeMs);
            _rotation = (_rotation + 90) % 360;
            StartPlayback(resumeAt);
            ShowToast(Loc.F("S_RotateToast", _rotation));
        }

        private void Menu_BrightUp(object? sender, RoutedEventArgs e) => AdjustVideo(ref _brightness, +0.1f, 0f, 2f, Loc.T("S_Brightness"));
        private void Menu_BrightDown(object? sender, RoutedEventArgs e) => AdjustVideo(ref _brightness, -0.1f, 0f, 2f, Loc.T("S_Brightness"));
        private void Menu_ContrastUp(object? sender, RoutedEventArgs e) => AdjustVideo(ref _contrast, +0.1f, 0f, 2f, Loc.T("S_Contrast"));
        private void Menu_ContrastDown(object? sender, RoutedEventArgs e) => AdjustVideo(ref _contrast, -0.1f, 0f, 2f, Loc.T("S_Contrast"));
        private void Menu_SaturationUp(object? sender, RoutedEventArgs e) => AdjustVideo(ref _saturation, +0.1f, 0f, 3f, Loc.T("S_Saturation"));
        private void Menu_SaturationDown(object? sender, RoutedEventArgs e) => AdjustVideo(ref _saturation, -0.1f, 0f, 3f, Loc.T("S_Saturation"));
        private void Menu_HueUp(object? sender, RoutedEventArgs e) => AdjustVideo(ref _hue, +10f, -180f, 180f, Loc.T("S_Hue"));
        private void Menu_HueDown(object? sender, RoutedEventArgs e) => AdjustVideo(ref _hue, -10f, -180f, 180f, Loc.T("S_Hue"));

        private void AdjustVideo(ref float field, float delta, float min, float max, string label)
        {
            field = Math.Clamp(MathF.Round((field + delta) * 100f) / 100f, min, max);
            ReapplyVideoAdjust();
            ShowToast($"{label}: {field:0.##}");
        }

        private void Menu_AdjustReset(object? sender, RoutedEventArgs e)
        {
            _brightness = _contrast = _saturation = 1f;
            _hue = 0f;
            ReapplyVideoAdjust();
            ShowToast(Loc.T("S_AdjustResetToast"));
        }

        private void ReapplyVideoAdjust()
        {
            bool isDefault = Math.Abs(_brightness - 1f) < 0.001f && Math.Abs(_contrast - 1f) < 0.001f
                          && Math.Abs(_saturation - 1f) < 0.001f && Math.Abs(_hue) < 0.001f;

            _mediaPlayer.SetAdjustInt(VideoAdjustOption.Enable, isDefault ? 0 : 1);
            if (!isDefault)
            {
                _mediaPlayer.SetAdjustFloat(VideoAdjustOption.Brightness, _brightness);
                _mediaPlayer.SetAdjustFloat(VideoAdjustOption.Contrast, _contrast);
                _mediaPlayer.SetAdjustFloat(VideoAdjustOption.Saturation, _saturation);
                _mediaPlayer.SetAdjustFloat(VideoAdjustOption.Hue, _hue);
            }
        }

        // =====================================================================
        // 소리 (볼륨 / 음소거 / 이퀄라이저)
        // =====================================================================

        private int VolumeStep => Math.Clamp(_settings.WheelVolumeStep, 1, 50);

        private void Menu_VolumeUp(object? sender, RoutedEventArgs e) => ChangeVolume(+VolumeStep);
        private void Menu_VolumeDown(object? sender, RoutedEventArgs e) => ChangeVolume(-VolumeStep);

        internal void ChangeVolume(int delta)
        {
            double value = Math.Clamp(VolumeSlider.Value + delta, 0, 100);
            VolumeSlider.Value = value; // ValueChanged에서 적용/표시
            SaveSettings();
            ShowToast(Loc.F("S_VolumeToast", (int)value));
        }

        private void Menu_ToggleMute(object? sender, RoutedEventArgs e) => ToggleMute();

        internal void ToggleMute()
        {
            _isMuted = !_isMuted;
            _mediaPlayer.Mute = _isMuted;
            UpdateVolumeText();
            ShowToast(_isMuted ? Loc.T("S_Muted") : Loc.T("S_Unmuted"));
        }

        private void VolumeText_Click(object? sender, MouseButtonEventArgs e) => ToggleMute();

        private void VolumeSlider_ValueChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_mediaPlayer == null)
            {
                return;
            }

            if (_isMuted && !_applyingSettings)
            {
                _isMuted = false;
                _mediaPlayer.Mute = false;
            }

            _mediaPlayer.Volume = (int)e.NewValue;
            UpdateVolumeText();
        }

        private void VolumeSlider_PreviewMouseUp(object? sender, MouseButtonEventArgs e) => SaveSettings();

        private void UpdateVolumeText()
        {
            VolumeText.Text = _isMuted ? Loc.T("S_Muted") : ((int)VolumeSlider.Value).ToString();
            VolumeText.Foreground = _isMuted
                ? (Brush)FindResource("AccentBrush")
                : (Brush)FindResource("TextBrush");
        }

        private void PopulateEqualizerMenu()
        {
            MiEqualizer.Items.Clear();

            var off = new MenuItem { Header = Loc.T("S_EqOff"), IsChecked = _eqPreset < 0 };
            off.Click += (_, _) => SetEqualizerPreset(-1);
            MiEqualizer.Items.Add(off);
            MiEqualizer.Items.Add(new Separator());

            // 프리셋 개수/이름 조회용 임시 인스턴스 (LibVLCSharp 3.x는 인스턴스 멤버)
            using var probe = new Equalizer();
            uint count = probe.PresetCount;
            for (uint i = 0; i < count; i++)
            {
                var item = new MenuItem
                {
                    Header = probe.PresetName(i),
                    IsChecked = _eqPreset == (int)i
                };
                uint captured = i;
                item.Click += (_, _) => SetEqualizerPreset((int)captured);
                MiEqualizer.Items.Add(item);
            }
        }

        private void SetEqualizerPreset(int preset)
        {
            if (preset < 0)
            {
                _mediaPlayer.UnsetEqualizer();
                _eqPreset = -1;
                ShowToast(Loc.T("S_EqOffToast"));
            }
            else
            {
                using var eq = new Equalizer((uint)preset);
                _mediaPlayer.SetEqualizer(eq);
                _eqLastPreset = preset;
                _eqPreset = preset;
                ShowToast(Loc.F("S_EqToast", eq.PresetName((uint)preset) ?? preset.ToString()));
            }
        }

        private void ToggleEqualizer() => SetEqualizerPreset(_eqPreset < 0 ? _eqLastPreset : -1);

        private void PopulateAudioTrackMenu()
        {
            MiAudioTracks.Items.Clear();
            int current = _mediaPlayer.AudioTrack;
            bool any = false;

            foreach (TrackDescription track in _mediaPlayer.AudioTrackDescription)
            {
                if (track.Id == -1)
                {
                    continue;
                }

                any = true;
                var item = new MenuItem { Header = track.Name ?? Loc.F("S_Track", track.Id), IsChecked = track.Id == current };
                int id = track.Id;
                item.Click += (_, _) => _mediaPlayer.SetAudioTrack(id);
                MiAudioTracks.Items.Add(item);
            }

            if (!any)
            {
                MiAudioTracks.Items.Add(new MenuItem { Header = Loc.T("S_NoAudio"), IsEnabled = false });
            }
        }

        private void PopulateSubtitleTrackMenu()
        {
            MiSubTracks.Items.Clear();
            int current = _mediaPlayer.Spu;

            var off = new MenuItem { Header = Loc.T("S_SubOff"), IsChecked = current == -1 };
            off.Click += (_, _) => { _mediaPlayer.SetSpu(-1); UpdateSubtitleButton(); };
            MiSubTracks.Items.Add(off);

            foreach (TrackDescription track in _mediaPlayer.SpuDescription)
            {
                if (track.Id == -1)
                {
                    continue;
                }

                var item = new MenuItem { Header = track.Name ?? Loc.F("S_Track", track.Id), IsChecked = track.Id == current };
                int id = track.Id;
                item.Click += (_, _) => { _mediaPlayer.SetSpu(id); UpdateSubtitleButton(); };
                MiSubTracks.Items.Add(item);
            }
        }

        // =====================================================================
        // 팬 & 스캔 (crop 기반 이동/확대)
        // =====================================================================

        private void Menu_PanLeft(object? sender, RoutedEventArgs e) => Pan(-1, 0);
        private void Menu_PanRight(object? sender, RoutedEventArgs e) => Pan(+1, 0);
        private void Menu_PanUp(object? sender, RoutedEventArgs e) => Pan(0, -1);
        private void Menu_PanDown(object? sender, RoutedEventArgs e) => Pan(0, +1);
        private void Menu_ZoomIn(object? sender, RoutedEventArgs e) => Zoom(+0.1f);
        private void Menu_ZoomOut(object? sender, RoutedEventArgs e) => Zoom(-0.1f);

        private void Menu_PanScanReset(object? sender, RoutedEventArgs e)
        {
            _zoom = 1f;
            _panX = _panY = 0;
            ApplyPanScan();
            ShowToast(Loc.T("S_PanScanResetToast"));
        }

        private void Pan(int dx, int dy)
        {
            if (_zoom <= 1f)
            {
                ShowToast(Loc.T("S_ZoomFirst"));
                return;
            }

            uint w = 0, h = 0;
            if (!_mediaPlayer.Size(0, ref w, ref h) || w == 0)
            {
                return;
            }

            _panX += dx * (int)(w / 20);
            _panY += dy * (int)(h / 20);
            ApplyPanScan();
            ShowToast(Loc.F("S_PanToast", _panX, _panY));
        }

        private void Zoom(float delta)
        {
            _zoom = Math.Clamp(MathF.Round((_zoom + delta) * 100f) / 100f, 1f, 3f);
            ApplyPanScan();
            ShowToast(Loc.F("S_ZoomToast", $"{_zoom:0.0}"));
        }

        private void ApplyPanScan()
        {
            if (_zoom <= 1.001f && _panX == 0 && _panY == 0)
            {
                _mediaPlayer.CropGeometry = null;
                return;
            }

            uint w = 0, h = 0;
            if (!_mediaPlayer.Size(0, ref w, ref h) || w == 0 || h == 0)
            {
                return;
            }

            int cw = (int)(w / _zoom);
            int ch = (int)(h / _zoom);
            int cx = Math.Clamp(((int)w - cw) / 2 + _panX, 0, (int)w - cw);
            int cy = Math.Clamp(((int)h - ch) / 2 + _panY, 0, (int)h - ch);
            _panX = cx - ((int)w - cw) / 2;
            _panY = cy - ((int)h - ch) / 2;
            _mediaPlayer.CropGeometry = $"{cw}x{ch}+{cx}+{cy}";
        }

        // =====================================================================
        // 화면 비율 / 화면 크기
        // =====================================================================

        private void Menu_AspectKeep(object? sender, RoutedEventArgs e) => SetAspect("keep");
        private void Menu_AspectOriginal(object? sender, RoutedEventArgs e) => SetAspect("orig");
        private void Menu_Aspect43(object? sender, RoutedEventArgs e) => SetAspect("4:3");
        private void Menu_Aspect169(object? sender, RoutedEventArgs e) => SetAspect("16:9");
        private void Menu_Aspect1610(object? sender, RoutedEventArgs e) => SetAspect("16:10");
        private void Menu_Aspect235(object? sender, RoutedEventArgs e) => SetAspect("2.35:1");

        private void SetAspect(string mode)
        {
            _aspectMode = mode;
            ApplyAspect();
            ShowToast(Loc.F("S_AspectToast", AspectLabel(mode)));
        }

        private void ApplyAspect()
        {
            switch (_aspectMode)
            {
                case "keep":
                    _mediaPlayer.AspectRatio = null;
                    _mediaPlayer.Scale = 0f; // 창에 맞춤
                    break;
                case "orig":
                    _mediaPlayer.AspectRatio = null;
                    _mediaPlayer.Scale = 1f; // 원본 픽셀 크기
                    break;
                default:
                    _mediaPlayer.Scale = 0f;
                    _mediaPlayer.AspectRatio = _aspectMode;
                    break;
            }
        }

        private static string AspectLabel(string mode) => mode switch
        {
            "keep" => Loc.T("S_AspectKeepLabel"),
            "orig" => Loc.T("S_AspectOrig"),
            _ => mode
        };

        private void Menu_Size05(object? sender, RoutedEventArgs e) => SetWindowSizeFactor(0.5);
        private void Menu_Size10(object? sender, RoutedEventArgs e) => SetWindowSizeFactor(1.0);
        private void Menu_Size15(object? sender, RoutedEventArgs e) => SetWindowSizeFactor(1.5);
        private void Menu_Size20(object? sender, RoutedEventArgs e) => SetWindowSizeFactor(2.0);

        private void SetWindowSizeFactor(double factor)
        {
            uint w = 0, h = 0;
            if (!_mediaPlayer.Size(0, ref w, ref h) || w == 0 || h == 0)
            {
                ShowToast(Loc.T("S_NoVideo"));
                return;
            }

            if (_isFullscreen)
            {
                ToggleFullscreen();
            }

            WindowState = WindowState.Normal;
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            Width = Math.Max(MinWidth, w * factor / dpi.DpiScaleX);
            Height = Math.Max(MinHeight, h * factor / dpi.DpiScaleY + TitleBar.Height);
            ShowToast(Loc.F("S_SizeToast", $"{factor:0.0}"));
        }

        private void Menu_Fullscreen(object? sender, RoutedEventArgs e) => ToggleFullscreen();

        private WindowState _preFullscreenState = WindowState.Normal;
        private Visibility _playlistBeforeFullscreen = Visibility.Visible;

        internal void ToggleFullscreen()
        {
            _isFullscreen = !_isFullscreen;

            if (_isFullscreen)
            {
                _preFullscreenState = WindowState;
                // 최대화 상태에서는 Left/Top/Width DP가 복원용 값이 아니므로 RestoreBounds를 사용하고,
                // 일반 상태에서는 사용자가 마우스로 크기를 바꿨을 때 Width DP가 갱신되지 않을 수 있어
                // 실제 크기(ActualWidth/Height)를 저장합니다.
                _restoreBounds = WindowState == WindowState.Normal
                    ? new Rect(Left, Top, ActualWidth, ActualHeight)
                    : RestoreBounds;

                TitleBar.Visibility = Visibility.Collapsed;
                WindowState = WindowState.Normal;
                Topmost = true;

                Rect screen = GetCurrentScreenBounds();
                Left = screen.Left;
                Top = screen.Top;
                Width = screen.Width;
                Height = screen.Height;
                Activate();

                // 전체화면에서는 재생목록/컨트롤바를 영상 위 오버레이로 옮기고 모두 숨깁니다.
                // 마우스를 상/우/하단 가장자리에 대면 OverlayRoot_MouseMove의 감지가 표시합니다.
                if (ControlBar.ActualHeight > 0)
                {
                    _lastControlBarHeight = ControlBar.ActualHeight;
                }
                _playlistBeforeFullscreen = PlaylistPanel.Visibility;
                MoveOverlaysToFullscreen();
                ControlBar.Visibility = Visibility.Collapsed;
                PlaylistPanel.Visibility = Visibility.Collapsed;
                TopOverlay.Visibility = Visibility.Collapsed;
            }
            else
            {
                Topmost = _settings.AlwaysOnTop;
                TitleBar.Visibility = Visibility.Visible;
                Left = _restoreBounds.Left;
                Top = _restoreBounds.Top;
                Width = _restoreBounds.Width;
                Height = _restoreBounds.Height;
                WindowState = _preFullscreenState;

                // 창 모드 UI 복원 (별도 칸 배치로 되돌림)
                MoveOverlaysToWindowed();
                TopOverlay.Visibility = Visibility.Collapsed;
                ControlBar.Visibility = Visibility.Visible;
                PlaylistPanel.Visibility = _playlistBeforeFullscreen;
                Mouse.OverrideCursor = null;
            }

            NudgeOverlayAlignment();
        }

        /// <summary>
        /// 전체화면: 재생목록/컨트롤바를 영상 위 오버레이(OverlayRoot)로 옮깁니다.
        /// 창 모드에서는 영상과 겹치지 않는 별도 칸(ContentArea)에 배치되기 때문입니다.
        /// </summary>
        private void MoveOverlaysToFullscreen()
        {
            if (OverlayRoot.Children.Contains(PlaylistPanel))
            {
                return; // 이미 오버레이 배치
            }

            ContentArea.Children.Remove(PlaylistPanel);
            ContentArea.Children.Remove(ControlBar);

            Grid.SetRow(PlaylistPanel, 0);
            Grid.SetColumn(PlaylistPanel, 0);
            Grid.SetRow(ControlBar, 1);
            Grid.SetColumn(ControlBar, 0);
            Grid.SetColumnSpan(ControlBar, 1);

            OverlayRoot.Children.Add(PlaylistPanel);
            OverlayRoot.Children.Add(ControlBar);
        }

        /// <summary>창 모드: 재생목록(우측 열)/컨트롤바(하단 행)를 영상과 겹치지 않는 칸으로 옮깁니다.</summary>
        private void MoveOverlaysToWindowed()
        {
            if (ContentArea.Children.Contains(PlaylistPanel))
            {
                return; // 이미 창 모드 배치
            }

            OverlayRoot.Children.Remove(PlaylistPanel);
            OverlayRoot.Children.Remove(ControlBar);

            Grid.SetRow(PlaylistPanel, 0);
            Grid.SetColumn(PlaylistPanel, 1);
            Grid.SetRow(ControlBar, 1);
            Grid.SetColumn(ControlBar, 0);
            Grid.SetColumnSpan(ControlBar, 2);

            ContentArea.Children.Add(PlaylistPanel);
            ContentArea.Children.Add(ControlBar);
        }

        /// <summary>
        /// libVLC 영상 위에 뜨는 오버레이 창은 창 이동/크기 이벤트 시점의 레이아웃을 기준으로 정렬되는데,
        /// 타이틀바 표시/숨김과 창 크기 변경이 동시에 일어나면 이전 레이아웃(타이틀바 36px) 기준으로
        /// 어긋나게 정렬될 수 있습니다. 레이아웃이 끝난 뒤 위치 이벤트를 한 번 더 발생시켜 재정렬합니다.
        /// </summary>
        private void NudgeOverlayAlignment()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                Left += 0.1;
                Left -= 0.1;
            });
        }

        private void Menu_Maximize(object? sender, RoutedEventArgs e)
        {
            if (_isFullscreen)
            {
                ToggleFullscreen();
            }
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private Rect GetCurrentScreenBounds()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            IntPtr monitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
            {
                DpiScale dpi = VisualTreeHelper.GetDpi(this);
                return new Rect(
                    info.rcMonitor.Left / dpi.DpiScaleX,
                    info.rcMonitor.Top / dpi.DpiScaleY,
                    (info.rcMonitor.Right - info.rcMonitor.Left) / dpi.DpiScaleX,
                    (info.rcMonitor.Bottom - info.rcMonitor.Top) / dpi.DpiScaleY);
            }

            return new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public NativeRect rcMonitor;
            public NativeRect rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left, Top, Right, Bottom;
        }

        // =====================================================================
        // 최대화 시 작업표시줄 겹침 방지
        // =====================================================================

        /// <summary>
        /// WindowStyle=None(테두리 없는 창)은 기본적으로 최대화 시 모니터 전체를 덮어
        /// 하단 UI가 작업표시줄에 가려집니다. WM_GETMINMAXINFO를 처리해 최대화 크기를
        /// 해당 모니터의 작업 영역(작업표시줄 제외)으로 제한합니다.
        /// (전체화면은 Maximized가 아니라 수동 좌표 + Topmost 방식이므로 영향이 없습니다.)
        /// </summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            if (PresentationSource.FromVisual(this) is HwndSource source)
            {
                source.AddHook(WindowProc);
            }
        }

        private static IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_GETMINMAXINFO = 0x0024;
            if (msg != WM_GETMINMAXINFO)
            {
                return IntPtr.Zero;
            }

            IntPtr monitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            if (monitor == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info))
            {
                return IntPtr.Zero;
            }

            // 시스템이 채워 둔 기본값에서 최대화 위치/크기만 작업 영역으로 바꿉니다
            // (최소/최대 트래킹 크기 등 나머지 값은 그대로 유지).
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
            mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
            mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
            mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;
            Marshal.StructureToPtr(mmi, lParam, fDeleteOld: false);
            handled = true;
            return IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public NativePoint ptReserved;
            public NativePoint ptMaxSize;
            public NativePoint ptMaxPosition;
            public NativePoint ptMinTrackSize;
            public NativePoint ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X, Y;
        }

        // =====================================================================
        // 대화상자 (환경설정/제어창/정보)
        // =====================================================================

        private void Menu_Settings(object? sender, RoutedEventArgs e)
        {
            var dialog = new SettingsWindow(_settings) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                _settings.Save();
                ApplyRuntimeSettings();

                // 언어 변경은 리소스 사전 교체로 즉시 반영됩니다(DynamicResource).
                // 코드에서 설정하는 텍스트만 다시 그립니다.
                Loc.Apply(_settings.Language);
                RefreshLocalizedTexts();
            }
        }

        /// <summary>언어 변경 직후, 코드에서 직접 설정하는 텍스트를 현재 언어로 다시 그립니다.</summary>
        private void RefreshLocalizedTexts()
        {
            UpdateRepeatButton();
            UpdateVolumeText();
            RenderChapterMarkers(); // 이름 없는 챕터의 "챕터 N" 툴팁
        }

        private void Menu_TogglePlaylist(object? sender, RoutedEventArgs e)
        {
            PlaylistPanel.Visibility = PlaylistPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void Menu_Controller(object? sender, RoutedEventArgs e)
        {
            if (_controller is { IsVisible: true })
            {
                _controller.Close();
                _controller = null;
                return;
            }

            _controller = new ControllerWindow(this) { Owner = this };
            _controller.Show();
        }

        private void Menu_MediaInfo(object? sender, RoutedEventArgs e)
        {
            var sb = new System.Text.StringBuilder();
            PlaylistItem? item = _playlist.Current;
            sb.AppendLine($"{Loc.T("S_InfoFile")}: {item?.FilePath ?? Loc.T("S_InfoNone")}");
            sb.AppendLine($"{Loc.T("S_InfoLength")}: {FormatTime(Volatile.Read(ref _cachedLengthMs))}");
            sb.AppendLine($"{Loc.T("S_InfoPos")}: {FormatTime(Volatile.Read(ref _cachedTimeMs))}");
            sb.AppendLine($"{Loc.T("S_InfoSpeed")}: {_playbackRate:0.##}x");
            sb.AppendLine();

            if (_currentMedia != null)
            {
                foreach (MediaTrack track in _currentMedia.Tracks)
                {
                    switch (track.TrackType)
                    {
                        case TrackType.Video:
                            sb.AppendLine($"{Loc.T("S_InfoVideoTag")} {FourCc(track.Codec)}  {track.Data.Video.Width}x{track.Data.Video.Height}"
                                + (track.Data.Video.FrameRateDen > 0
                                    ? $"  {(double)track.Data.Video.FrameRateNum / track.Data.Video.FrameRateDen:0.###} fps"
                                    : ""));
                            break;
                        case TrackType.Audio:
                            sb.AppendLine($"{Loc.T("S_InfoAudioTag")} {FourCc(track.Codec)}  {track.Data.Audio.Rate} Hz  {track.Data.Audio.Channels}ch");
                            break;
                        case TrackType.Text:
                            sb.AppendLine($"{Loc.T("S_InfoSubTag")} {FourCc(track.Codec)}  {track.Language ?? ""}");
                            break;
                    }
                }
            }

            InfoWindow.Show(this, Loc.T("S_MediaInfoTitle"), sb.ToString());
        }

        private static string FourCc(uint codec)
        {
            var chars = new[]
            {
                (char)(codec & 0xFF), (char)((codec >> 8) & 0xFF),
                (char)((codec >> 16) & 0xFF), (char)((codec >> 24) & 0xFF)
            };
            return new string(chars).Trim();
        }

        private void Menu_About(object? sender, RoutedEventArgs e)
        {
            InfoWindow.Show(this, Loc.T("S_AboutTitle"),
                "OctoPlayer\n\n" +
                $"{Loc.T("S_AboutBody")}\n" +
                "OctoBrain Softworks\n\n" +
                $"LibVLCSharp {typeof(LibVLC).Assembly.GetName().Version}\n" +
                $".NET {Environment.Version}");
        }

        // =====================================================================
        // 메뉴 상태 갱신 (열릴 때 체크/라디오 동기화)
        // =====================================================================

        private void MainMenu_Opened(object? sender, RoutedEventArgs e)
        {
            MiSubVisible.IsChecked = _mediaPlayer.Spu != -1;
            MiMute.IsChecked = _isMuted;
            MiPlaylist.IsChecked = PlaylistPanel.Visibility == Visibility.Visible;
            MiFullscreen.IsChecked = _isFullscreen;
            MiShuffle.IsChecked = _playlist.IsShuffled;

            MiRepeatMode.Header = _playlist.RepeatMode switch
            {
                RepeatMode.All => Loc.T("S_RepeatAll"),
                RepeatMode.One => Loc.T("S_RepeatOne"),
                _ => Loc.T("S_RepeatNone")
            };

            MiAbA.Header = _abStartMs >= 0 ? $"{Loc.T("S_AbSetA")}: {FormatTime(_abStartMs)}" : Loc.T("S_AbSetA");
            MiAbB.Header = _abEndMs >= 0 ? $"{Loc.T("S_AbSetB")}: {FormatTime(_abEndMs)}" : Loc.T("S_AbSetB");

            // 화면 비율 라디오
            MiAspectKeep.IsChecked = _aspectMode == "keep";
            MiAspectOrig.IsChecked = _aspectMode == "orig";
            MiAspect43.IsChecked = _aspectMode == "4:3";
            MiAspect169.IsChecked = _aspectMode == "16:9";
            MiAspect1610.IsChecked = _aspectMode == "16:10";
            MiAspect235.IsChecked = _aspectMode == "2.35:1";

            PopulateSubtitleTrackMenu();
            PopulateAudioTrackMenu();
            PopulateEqualizerMenu();
        }

        // =====================================================================
        // 키보드 (메뉴와 동일한 핸들러 호출)
        // =====================================================================

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // 미디어 엔진 준비 전에는 단축키를 처리하지 않습니다(시작 직후 1~2초).
            if (!_playerReady)
            {
                return;
            }

            // 텍스트 입력 중에는 단축키를 가로채지 않습니다.
            if (Keyboard.FocusedElement is TextBox)
            {
                return;
            }

            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

            bool handled = true;
            var args = new RoutedEventArgs();

            switch (key)
            {
                // ----- 열기/닫기/창 -----
                case Key.F3 when !ctrl && !alt: Menu_OpenFile(null, args); break;
                case Key.O when ctrl: Menu_OpenFile(null, args); break;
                case Key.O when alt: Menu_OpenSubtitle(null, args); break;
                case Key.U when ctrl: Menu_OpenUrl(null, args); break;
                case Key.F2 when !ctrl && !alt: Menu_OpenFolder(null, args); break;
                case Key.F4 when !ctrl && !alt: Menu_CloseMedia(null, args); break;
                case Key.F5 when !ctrl && !alt: Menu_Settings(null, args); break;
                case Key.F6 when !ctrl && !alt: Menu_TogglePlaylist(null, args); break;
                case Key.F7 when !ctrl && !alt: Menu_Controller(null, args); break;
                case Key.F1 when ctrl: Menu_MediaInfo(null, args); break;
                case Key.F1: Menu_About(null, args); break;

                // ----- 재생 -----
                case Key.Space: TogglePlayPause(); break;
                case Key.PageUp: PlayPreviousManual(); break;
                case Key.PageDown: PlayNextManual(); break;
                case Key.P when !ctrl && !alt: PlayPreviousManual(); break;
                case Key.N when !ctrl && !alt: PlayNextManual(); break;
                case Key.Z when !ctrl && !alt: SetPlaybackRate(1f); break;
                case Key.X when !ctrl && !alt: ChangePlaybackRate(-0.1f); break;
                case Key.C when !ctrl && !alt: ChangePlaybackRate(+0.1f); break;
                case Key.OemOpenBrackets: Menu_AbSetA(null, args); break;
                case Key.OemCloseBrackets: Menu_AbSetB(null, args); break;

                // ----- 탐색/볼륨 (이동/조절량은 환경설정에서 변경 가능) -----
                case Key.Left: Skip(-Math.Clamp(_settings.ArrowSkipSeconds, 1, 600) * 1000L); break;
                case Key.Right: Skip(+Math.Clamp(_settings.ArrowSkipSeconds, 1, 600) * 1000L); break;
                case Key.Up: ChangeVolume(+VolumeStep); break;
                case Key.Down: ChangeVolume(-VolumeStep); break;
                case Key.M when !ctrl && !alt: ToggleMute(); break;

                // ----- 자막 -----
                case Key.H when alt: Menu_ToggleSubtitles(null, args); break;
                case Key.OemComma: Menu_SubDelayMinus(null, args); break;
                case Key.OemPeriod: Menu_SubDelayPlus(null, args); break;

                // ----- 영상 -----
                case Key.S when ctrl: Menu_Capture(null, args); break;
                case Key.R when !ctrl && !alt: RotateVideo(); break;
                case Key.Q when !ctrl && !alt: Menu_AdjustReset(null, args); break;
                case Key.E when shift: ToggleEqualizer(); break;

                // ----- 팬 & 스캔 (숫자 패드) -----
                case Key.NumPad4: Pan(-1, 0); break;
                case Key.NumPad6: Pan(+1, 0); break;
                case Key.NumPad8: Pan(0, -1); break;
                case Key.NumPad2: Pan(0, +1); break;
                case Key.NumPad5: Menu_PanScanReset(null, args); break;
                case Key.Add: Zoom(+0.1f); break;
                case Key.Subtract: Zoom(-0.1f); break;

                // ----- 화면 크기/전체화면 -----
                case Key.D1 when !ctrl && !alt: SetWindowSizeFactor(0.5); break;
                case Key.D2 when !ctrl && !alt: SetWindowSizeFactor(1.0); break;
                case Key.D3 when !ctrl && !alt: SetWindowSizeFactor(1.5); break;
                case Key.D4 when !ctrl && !alt: SetWindowSizeFactor(2.0); break;
                case Key.Return when ctrl: Menu_Maximize(null, args); break;
                case Key.Return: ToggleFullscreen(); break; // Enter / Alt+Enter
                case Key.F when !ctrl && !alt: ToggleFullscreen(); break;
                case Key.Escape when _isFullscreen: ToggleFullscreen(); break;

                default:
                    handled = false;
                    break;
            }

            e.Handled = handled;
        }

        internal void Skip(long deltaMs)
        {
            if (Volatile.Read(ref _cachedLengthMs) <= 0)
            {
                return;
            }

            // 진행 중인 시크가 있으면 그 목표를 기준으로 누적(연타 시 정확한 상대 이동)
            long baseMs = _pendingSeekMs >= 0 ? _pendingSeekMs : Volatile.Read(ref _cachedTimeMs);
            ApplySeek(baseMs + deltaMs);
        }

        // =====================================================================
        // 마우스 (휠 볼륨 / 측면 버튼 / 3분할 더블클릭)
        // =====================================================================

        private void OverlayRoot_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (PlaylistPanel.IsMouseOver)
            {
                return; // 재생목록 스크롤 유지
            }

            ChangeVolume(e.Delta > 0 ? +VolumeStep : -VolumeStep);
            e.Handled = true;
        }

        private void OverlayRoot_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // Preview(터널링) 단계에서 처리해 자식 컨트롤이 이벤트를 소비해도 항상 실행됩니다.
            // 오버레이 창 클릭으로 키보드 포커스가 떠나지 않도록 메인 창을 다시 활성화합니다.
            Dispatcher.BeginInvoke(() => Activate());

            if (e.ChangedButton == MouseButton.XButton1)
            {
                TrySideButtonSkip(-1);
            }
            else if (e.ChangedButton == MouseButton.XButton2)
            {
                TrySideButtonSkip(+1);
            }
        }

        private void TrySideButtonSkip(int direction)
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastSideButtonSkipUtc).TotalMilliseconds < 400)
            {
                return;
            }

            _lastSideButtonSkipUtc = now;
            int seconds = Math.Clamp(_settings.SkipSeconds, 1, 600);
            Skip(direction * seconds * 1000L);
            ShowToast(direction < 0 ? $"뒤로 {seconds}초" : $"앞으로 {seconds}초");
        }

        private DateTime _lastVideoClickUtc = DateTime.MinValue;
        private Point _lastVideoClickPos;

        private void VideoSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 오버레이 창(별도 레이어드 창)에서는 활성화 전환 때문에 WPF ClickCount가
            // 2로 올라가지 않는 경우가 있어, 시간/위치 기반으로도 더블클릭을 감지합니다.
            Point pos = e.GetPosition(VideoSurface);
            DateTime now = DateTime.UtcNow;
            bool isDouble = e.ClickCount >= 2
                || ((now - _lastVideoClickUtc).TotalMilliseconds <= 500
                    && Math.Abs(pos.X - _lastVideoClickPos.X) < 24
                    && Math.Abs(pos.Y - _lastVideoClickPos.Y) < 24);

            _lastVideoClickUtc = isDouble ? DateTime.MinValue : now; // 더블 처리 후 리셋(3연타 오동작 방지)
            _lastVideoClickPos = pos;

            if (!isDouble)
            {
                return;
            }

            double width = VideoSurface.ActualWidth;
            int zone = width <= 0 ? 1 : Math.Clamp((int)(pos.X * 3 / width), 0, 2);

            switch (zone)
            {
                case 0: PlayPreviousManual(); break;
                case 2: PlayNextManual(); break;
                default: TogglePlayPause(); break;
            }
        }

        // =====================================================================
        // 전체화면 자동 숨김
        // =====================================================================

        private double _lastControlBarHeight = 92;

        private void OverlayRoot_MouseMove(object sender, MouseEventArgs e)
        {
            Mouse.OverrideCursor = null;
            _idleTimer.Stop();
            _idleTimer.Start();

            if (!_isFullscreen)
            {
                return;
            }

            // 컨트롤바가 숨겨져 있으면 ActualHeight가 0이므로 마지막 표시 높이를 기억해 씁니다.
            if (ControlBar.Visibility == Visibility.Visible && ControlBar.ActualHeight > 0)
            {
                _lastControlBarHeight = ControlBar.ActualHeight;
            }

            Point pos = e.GetPosition(OverlayRoot);
            bool inTopStrip = pos.Y <= TopOverlay.Height;
            bool inRightColumn = pos.X >= OverlayRoot.ActualWidth - PlaylistPanel.Width;
            bool inBottomStrip = pos.Y >= OverlayRoot.ActualHeight - _lastControlBarHeight;

            (bool showPlaylist, bool showTop, bool showBottom) = ComputeFullscreenOverlays(
                inRightColumn, inTopStrip, inBottomStrip,
                PlaylistPanel.Visibility == Visibility.Visible,
                TopOverlay.Visibility == Visibility.Visible,
                ControlBar.Visibility == Visibility.Visible);

            PlaylistPanel.Visibility = showPlaylist ? Visibility.Visible : Visibility.Collapsed;
            TopOverlay.Visibility = showTop ? Visibility.Visible : Visibility.Collapsed;
            ControlBar.Visibility = showBottom ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// 전체화면에서 상단 오버레이·재생목록(우측 열)·컨트롤바(하단 띠) 중 무엇을 보일지 결정합니다.
        /// 세 오버레이는 동시에 표시하지 않으므로 각자 자기 영역 전체(상/하단은 전체 너비,
        /// 재생목록은 전체 높이)를 잘림 없이 사용할 수 있습니다.
        /// 겹침 구역(우측 상단/하단 모서리)은 "이미 표시 중인 오버레이"가 차지해,
        /// 표시된 오버레이 안에서 마우스를 움직이는 동안 다른 오버레이가 튀어나오지 않습니다.
        /// 새로 열 때는 상단 띠 → 우측 열 → 하단 띠 순서로 우선합니다.
        /// (상단 우선: 우상단 모서리는 창 닫기/최소화 버튼 위치이기 때문입니다.)
        /// </summary>
        private static (bool ShowPlaylist, bool ShowTop, bool ShowBottom) ComputeFullscreenOverlays(
            bool inRightColumn, bool inTopStrip, bool inBottomStrip,
            bool playlistVisible, bool topVisible, bool bottomVisible)
        {
            // 이미 떠 있는 오버레이는 자기 영역 안(모서리 포함)에서는 유지됩니다.
            if (playlistVisible && inRightColumn)
            {
                return (true, false, false);
            }

            if (topVisible && inTopStrip)
            {
                return (false, true, false);
            }

            if (bottomVisible && inBottomStrip)
            {
                return (false, false, true);
            }

            // 아무것도 유지되지 않으면(모두 꺼짐 or 이전 오버레이 영역을 벗어남) 새로 엽니다.
            if (inTopStrip)
            {
                return (false, true, false);
            }

            if (inRightColumn)
            {
                return (true, false, false);
            }

            if (inBottomStrip)
            {
                return (false, false, true);
            }

            return (false, false, false);
        }

        private void HideControlsWhenIdle()
        {
            // 전체화면에서 오버레이가 모두 숨겨진 상태로 가만히 있으면 커서도 숨깁니다.
            if (_isFullscreen
                && ControlBar.Visibility != Visibility.Visible
                && PlaylistPanel.Visibility != Visibility.Visible
                && TopOverlay.Visibility != Visibility.Visible
                && !MainMenu.IsOpen)
            {
                Mouse.OverrideCursor = Cursors.None;
            }
        }

        // =====================================================================
        // 시크바 / 타이머
        // =====================================================================

        private void SeekSlider_PreviewMouseDown(object? sender, MouseButtonEventArgs e)
        {
            _isSeeking = true;
            _pendingSeekMs = -1; // 새 조작이 시작되면 이전 시크 추적은 취소
        }

        private void SeekSlider_ValueChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updatingUi || !_isSeeking)
            {
                return;
            }

            long len = Volatile.Read(ref _cachedLengthMs);
            if (len > 0)
            {
                CurrentTimeText.Text = FormatTime((long)(e.NewValue / SeekSlider.Maximum * len));
            }
        }

        private void SeekSlider_PreviewMouseUp(object? sender, MouseButtonEventArgs e)
        {
            long length = Volatile.Read(ref _cachedLengthMs);
            if (length > 0)
            {
                ApplySeek((long)(SeekSlider.Value / SeekSlider.Maximum * length));
            }
            _isSeeking = false;
        }

        /// <summary>
        /// 지정 위치로 시크하고, 반영될 때까지 추적합니다(UiTimer에서 미도착 시 재적용).
        /// </summary>
        private void ApplySeek(long targetMs)
        {
            long length = Volatile.Read(ref _cachedLengthMs);
            targetMs = Math.Clamp(targetMs, 0, length > 500 ? length - 500 : Math.Max(0, length));

            SetPlayerTimeAsync(targetMs);
            _pendingSeekMs = targetMs;
            _pendingSeekUtc = DateTime.UtcNow;
            _seekRetryCount = 0;
            _lastTimerTimeMs = targetMs; // 뒤로 시크가 반복 랩으로 오인되지 않게 기준점 갱신

            // 일시정지 중에는 시크가 반영돼도 TimeChanged가 오지 않는 경우가 많아
            // 캐시를 목표값으로 미리 맞춥니다. 그대로 두면 도착 판정이 4초간 실패하며
            // 재시도를 반복하다가 진행바가 이전 위치로 되돌아가 보였습니다.
            if (_cachedState == VLCState.Paused)
            {
                Volatile.Write(ref _cachedTimeMs, targetMs);
            }
        }

        // 시크 요청 직렬화: 요청마다 Task.Run을 띄우면 스레드풀에서 실행 순서가 뒤집혀
        // 이전 목표가 나중에 적용되어 "원래 위치로 되돌아가는" 현상이 생길 수 있습니다.
        // 최신 목표 하나만 유지하고 단일 워커가 항상 마지막 값을 적용합니다.
        private long _seekRequestMs = -1;
        private int _seekWorkerRunning;

        /// <summary>
        /// libVLC 시간 설정(시크)은 입력 스레드 잠금을 기다리며 UI를 수백 ms 막을 수 있어
        /// 백그라운드 워커에서 수행합니다. 실제 도착 여부는 TimeChanged 캐시로 추적합니다.
        /// </summary>
        private void SetPlayerTimeAsync(long targetMs)
        {
            Interlocked.Exchange(ref _seekRequestMs, targetMs);
            StartSeekWorkerIfIdle();
        }

        private void StartSeekWorkerIfIdle()
        {
            if (Interlocked.CompareExchange(ref _seekWorkerRunning, 1, 0) != 0)
            {
                return; // 이미 워커가 실행 중 → 최신 목표만 갈아끼움
            }

            MediaPlayer mp = _mediaPlayer;
            _ = Task.Run(() =>
            {
                try
                {
                    long target;
                    while ((target = Interlocked.Exchange(ref _seekRequestMs, -1)) >= 0)
                    {
                        try { mp.Time = target; } catch { }
                    }
                }
                finally
                {
                    Volatile.Write(ref _seekWorkerRunning, 0);
                    // 워커 종료 직전에 새 요청이 들어온 경우를 놓치지 않도록 재확인
                    if (Interlocked.Read(ref _seekRequestMs) >= 0)
                    {
                        StartSeekWorkerIfIdle();
                    }
                }
            });
        }

        private void UiTimer_Tick(object? sender, EventArgs e)
        {
            if (_switchingPlayer)
            {
                return;
            }

            // 아래 로직은 전부 이벤트로 갱신되는 캐시만 읽습니다.
            // (이 틱은 0.4초마다 UI 스레드에서 돌므로 네이티브 호출이 있으면 안 됩니다.)
            long time = Volatile.Read(ref _cachedTimeMs);
            long len = Volatile.Read(ref _cachedLengthMs);

            // 구간 반복(A-B)
            if (_abStartMs >= 0 && _abEndMs > _abStartMs && time >= _abEndMs)
            {
                SetPlayerTimeAsync(_abStartMs);
            }

            if (_isSeeking)
            {
                return;
            }

            // 진행 중인 시크 유지: 도착했으면 종료, 무시된 것 같으면 제한 횟수만 재적용.
            // 허용 오차를 3초로 두는 이유: 키프레임 간격이 큰 파일은 목표에서 몇 초 떨어진
            // 지점에 안착하는데, 오차를 좁게 잡으면 "미도착"으로 오인해 시크를 계속 재적용
            // → 매번 키프레임으로 되돌아가 "원래 위치로 돌아가는" 현상이 됐습니다.
            if (_pendingSeekMs >= 0)
            {
                double elapsed = (DateTime.UtcNow - _pendingSeekUtc).TotalMilliseconds;

                if (time >= 0 && Math.Abs(time - _pendingSeekMs) <= 3000)
                {
                    _pendingSeekMs = -1; // 목표(또는 그 근처 키프레임) 도착
                }
                else if (elapsed > 1000)
                {
                    if (_seekRetryCount < 2)
                    {
                        // 바쁜 순간(버퍼링/트랙 전환)에 무시된 요청 재적용.
                        // 1초 간격 최대 2회로 제한해 진행 중인 시크를 계속 재시작하지 않습니다.
                        _seekRetryCount++;
                        _pendingSeekUtc = DateTime.UtcNow;
                        SetPlayerTimeAsync(_pendingSeekMs);
                    }
                    else
                    {
                        // 포기: 일시정지 중에는 이벤트가 오지 않으므로 목표값을 캐시에 반영하고,
                        // 재생 중에는 실제 위치를 그대로 따릅니다(억지로 목표를 표시하면 다시 튀어 보임).
                        if (_cachedState == VLCState.Paused)
                        {
                            Volatile.Write(ref _cachedTimeMs, _pendingSeekMs);
                            _lastTimerTimeMs = _pendingSeekMs;
                        }
                        _pendingSeekMs = -1;
                    }
                }
            }

            // 재생 중 반복 모드가 바뀐 경우를 잇는 다리(반복 자체는 :input-repeat이 담당):
            // 사용자 시크 반영 중에는 오동작(랩 오인/즉시 되감기)을 막기 위해 건너뜁니다.
            if (_abStartMs < 0 && _pendingSeekMs < 0 && _cachedState == VLCState.Playing)
            {
                long cur = time;

                // (a) 반복이 켜졌는데 미디어에 내부 반복이 없으면 → 끝나기 직전에 되감아 이어줍니다.
                if (!_mediaRepeatsForever && WantsInfiniteLoop() && _cachedSeekable)
                {
                    long threshold = (long)(500 * Math.Max(1f, _playbackRate)) + 100;
                    if (len > 2000 && len - cur <= threshold)
                    {
                        SetPlayerTimeAsync(0);
                        cur = 0;
                    }
                }

                // (b) 미디어는 내부 반복 중인데 반복이 꺼졌으면 → 랩(처음으로 되감긴 시점)에서 다음으로 진행합니다.
                if (_mediaRepeatsForever && !WantsInfiniteLoop()
                    && cur >= 0 && cur < 2000 && _lastTimerTimeMs > cur + 2000)
                {
                    if (_playlist.Next() != null)
                    {
                        PlayCurrent();
                    }
                    else
                    {
                        StopPlaybackInternal();
                    }
                }

                _lastTimerTimeMs = cur;
            }

            if (len > 0)
            {
                // 시크 반영 대기 중에는 목표 위치를 표시해 이전 위치로 되돌아가 보이는 현상을 막습니다.
                // 시간 텍스트와 같은 캐시(_cachedTimeMs)를 쓰는 이유: 별도의 PositionChanged 캐시는
                // 갱신 시점이 어긋나 시크 직후 진행바만 이전 위치로 잠깐 되돌아가는 원인이 됐습니다.
                double fraction = _pendingSeekMs >= 0
                    ? _pendingSeekMs / (double)len
                    : Math.Clamp(time / (double)len, 0d, 1d);

                _updatingUi = true;
                SeekSlider.Value = Math.Clamp(fraction * SeekSlider.Maximum, 0, SeekSlider.Maximum);
                _updatingUi = false;
                TotalTimeText.Text = FormatTime(len);
            }
            else
            {
                TotalTimeText.Text = "00:00:00";
            }

            long shownTime = _pendingSeekMs >= 0 ? _pendingSeekMs : time;
            CurrentTimeText.Text = FormatTime(shownTime < 0 ? 0 : shownTime);

            // 상태 이벤트를 놓친 경우를 대비한 대기 화면 동기화
            UpdateIdleLogo();
        }

        // =====================================================================
        // 재생목록
        // =====================================================================

        private void RefreshPlaylist()
        {
            _entries.Clear();
            foreach (PlaylistItem item in _playlist.Items)
            {
                _entries.Add(new PlaylistEntry(item));
            }
            UpdatePlayingHighlight();
            RequestThumbnailsIfNeeded();
        }

        // 사용자가 목록을 스크롤해 둔 위치를 보호하기 위한 플래그.
        // WPF는 선택/포커스된 항목의 컨테이너가 가상화로 재생성될 때마다 자동으로
        // BringIntoView를 요청해 스크롤이 재생 중 항목(예: 맨 위)으로 계속 되돌아갑니다.
        // 트랙 변경 시 우리가 의도적으로 호출하는 ScrollIntoView만 허용합니다.
        private bool _allowBringIntoView;

        private void PlaylistList_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
        {
            if (!_allowBringIntoView)
            {
                e.Handled = true;
            }
        }

        private void UpdatePlayingHighlight()
        {
            int playing = _playlist.CurrentItemIndex;
            for (int i = 0; i < _entries.Count; i++)
            {
                _entries[i].IsPlaying = i == playing;
            }

            if (playing >= 0 && playing < _entries.Count)
            {
                PlaylistList.SelectedIndex = playing;

                _allowBringIntoView = true;
                try
                {
                    PlaylistList.ScrollIntoView(_entries[playing]);
                    PlaylistList.UpdateLayout(); // ScrollIntoView가 이 안에서 완료되도록(플래그 유효 범위 보장)
                }
                finally
                {
                    _allowBringIntoView = false;
                }
            }
        }

        private void PlaylistList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // SelectedIndex 대신 실제 클릭된 항목을 이벤트 소스에서 직접 찾습니다.
            // (선택 상태가 갱신되기 전에 더블클릭이 처리되어도 정확한 항목이 재생되도록)
            int index = GetPlaylistIndexFromEvent(e) ?? PlaylistList.SelectedIndex;
            if (index >= 0 && index < _playlist.Count)
            {
                _playlist.SelectByItemIndex(index);
                PlayCurrent();
            }
        }

        /// <summary>마우스 이벤트가 발생한 재생목록 항목의 인덱스를 찾습니다(항목 밖이면 null).</summary>
        private int? GetPlaylistIndexFromEvent(MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source
                && ItemsControl.ContainerFromElement(PlaylistList, source) is ListBoxItem container)
            {
                int index = PlaylistList.ItemContainerGenerator.IndexFromContainer(container);
                if (index >= 0)
                {
                    return index;
                }
            }

            return null;
        }

        private void PlaylistList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 우클릭한 항목이 선택되도록 하여 컨텍스트 메뉴(제거/삭제)가 해당 항목에 동작하게 합니다.
            int? index = GetPlaylistIndexFromEvent(e);
            if (index.HasValue)
            {
                PlaylistList.SelectedIndex = index.Value;
            }
        }

        private void PlaylistMenu_Opened(object? sender, RoutedEventArgs e)
        {
            bool hasSelection = PlaylistList.SelectedIndex >= 0;
            MiPlRemove.IsEnabled = hasSelection;
            MiPlDelete.IsEnabled = hasSelection;
            MiPlViewTitles.IsChecked = _playlistView == PlaylistViewMode.Titles;
            MiPlViewThumbs.IsChecked = _playlistView == PlaylistViewMode.Thumbnails;
        }

        private void PlaylistMenu_AddFiles(object? sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = Loc.T("S_OpenMediaTitle"),
                Multiselect = true,
                Filter = SupportedFormats.BuildOpenFileFilter()
            };
            if (dialog.ShowDialog(this) == true)
            {
                AddToPlaylist(dialog.FileNames);
            }
        }

        private void PlaylistMenu_AddFolder(object? sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("S_SelectFolderTitle") };
            if (dialog.ShowDialog(this) == true)
            {
                AddToPlaylist(new[] { dialog.FolderName });
            }
        }

        /// <summary>
        /// 재생을 시작하지 않고 파일/폴더를 재생목록에만 추가합니다(폴더는 내용을 스캔).
        /// 폴더 열람은 느린 디스크에서 오래 걸릴 수 있어 백그라운드에서 수행합니다.
        /// </summary>
        private async void AddToPlaylist(IEnumerable<string> paths)
        {
            var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            if (list.Count == 0)
            {
                return;
            }

            (List<string> files, _) = await Task.Run(() => CollectPlayableFiles(list, autoScanFolder: false));

            int added = 0;
            foreach (string file in files)
            {
                if (_playlist.AddFile(file) != null)
                {
                    added++;
                }
            }

            _playlist.ReshuffleIfNeeded();
            RefreshPlaylist();
            ShowToast(Loc.F("S_PlAddedToast", added));
        }

        private void PlaylistMenu_Remove(object? sender, RoutedEventArgs e)
        {
            int index = PlaylistList.SelectedIndex;
            if (index < 0 || index >= _playlist.Count)
            {
                return;
            }

            _playlist.RemoveAt(index);
            RefreshPlaylist();
        }

        private async void PlaylistMenu_Delete(object? sender, RoutedEventArgs e)
        {
            int index = PlaylistList.SelectedIndex;
            if (index < 0 || index >= _playlist.Count)
            {
                return;
            }

            PlaylistItem item = _playlist.Items[index];

            MessageBoxResult result = MessageBox.Show(
                this,
                Loc.F("S_DeleteConfirm", item.FilePath),
                Loc.T("S_DeleteTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            bool removingCurrent = _playlist.CurrentItemIndex == index;
            if (removingCurrent)
            {
                if (_cachedState is VLCState.Playing or VLCState.Paused or VLCState.Stopped or VLCState.Opening)
                {
                    await Task.Run(() => _mediaPlayer.Stop());
                }
                _currentMedia?.Dispose();
                _currentMedia = null;
            }

            if (!RecycleBin.Delete(item.FilePath))
            {
                MessageBox.Show(this, Loc.T("S_DeleteFailMsg"), Loc.T("S_DeleteFailTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
                UpdatePlayPauseButton();
                return;
            }

            _playlist.RemoveAt(index);
            RefreshPlaylist();

            if (removingCurrent)
            {
                StopPlaybackInternal();
                SetTitle(null);
            }
        }

        private void PlaylistMenu_ViewTitles(object? sender, RoutedEventArgs e) => SetPlaylistViewMode(PlaylistViewMode.Titles);
        private void PlaylistMenu_ViewThumbs(object? sender, RoutedEventArgs e) => SetPlaylistViewMode(PlaylistViewMode.Thumbnails);

        private void ViewModeButton_Click(object? sender, RoutedEventArgs e) =>
            SetPlaylistViewMode(_playlistView == PlaylistViewMode.Titles
                ? PlaylistViewMode.Thumbnails
                : PlaylistViewMode.Titles);

        private void SetPlaylistViewMode(PlaylistViewMode mode)
        {
            _playlistView = mode;
            PlaylistList.ItemTemplate = (DataTemplate)FindResource(
                mode == PlaylistViewMode.Thumbnails ? "PlaylistThumbTemplate" : "PlaylistTitleTemplate");
            RequestThumbnailsIfNeeded();
            SaveSettings();
        }

        private void RequestThumbnailsIfNeeded()
        {
            if (_playlistView != PlaylistViewMode.Thumbnails)
            {
                return;
            }

            foreach (PlaylistEntry entry in _entries)
            {
                entry.Thumbnail ??= _thumbnails.Get(entry.Item.FilePath);
            }
        }

        // 썸네일 1장이 완성될 때마다 UI 갱신을 예약하면 큰 목록에서 예약이 목록 크기만큼
        // 쌓입니다(각 예약이 전체 목록을 순회 → O(N²)). 플래그로 한 번의 패스로 모아 반영합니다.
        private int _thumbnailApplyQueued;

        private void OnThumbnailReady()
        {
            if (Interlocked.CompareExchange(ref _thumbnailApplyQueued, 1, 0) == 0)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    _thumbnailApplyQueued = 0;
                    ApplyReadyThumbnails();
                });
            }
        }

        private void ApplyReadyThumbnails()
        {
            foreach (PlaylistEntry entry in _entries)
            {
                entry.Thumbnail ??= _thumbnails.TryGetCached(entry.Item.FilePath);
            }
        }

        private void PlaylistResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            PlaylistPanel.Width = Math.Clamp(PlaylistPanel.Width - e.HorizontalChange, 180, 700);
        }

        private void PlaylistResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e) => SaveSettings();

        // =====================================================================
        // 창 제어 / 드래그 앤 드롭 / 기타 UI
        // =====================================================================

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                Menu_Maximize(null, new RoutedEventArgs());
                return;
            }

            DragMove();
        }

        private void Caption_Minimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void Caption_Maximize(object? sender, RoutedEventArgs e) => Menu_Maximize(null, new RoutedEventArgs());

        private void Caption_Close(object? sender, RoutedEventArgs e) => Close();

        private void Window_DragEnter(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            {
                OpenPaths(paths, _settings.OpenFolderScan);
            }
        }

        private void UpdatePlayPauseButton()
        {
            bool playing = _mediaPlayer != null && _cachedState == VLCState.Playing;
            PlayPauseIcon.Data = (Geometry)FindResource(playing ? "IconPause" : "IconPlay");
            // 재생 삼각형은 시각적 무게중심이 왼쪽이라 살짝 오른쪽으로 보정합니다.
            PlayPauseIcon.Margin = playing ? new Thickness(0) : new Thickness(3, 0, 0, 0);
            UpdateIdleLogo();
        }

        /// <summary>재생 중이 아닐 때(시작 전/정지/종료) 검은 배경 + 회사 로고 대기 화면을 표시합니다.</summary>
        private void UpdateIdleLogo()
        {
            // 전환 상태가 5초 넘게 유지되면(재생 시작 실패 등) 억제를 풀어 대기 화면이 갇히지 않게 합니다.
            if (_transitioning && (DateTime.UtcNow - _transitionStartUtc).TotalSeconds > 5)
            {
                _transitioning = false;
            }

            bool idle = _mediaPlayer == null
                || (!_switchingPlayer && !_transitioning
                    && (_currentMedia == null
                        || _cachedState is VLCState.NothingSpecial or VLCState.Stopped
                                        or VLCState.Ended or VLCState.Error));
            IdleLogo.Visibility = idle ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateShuffleButton() =>
            ShuffleButton.Background = _playlist.IsShuffled
                ? (Brush)FindResource("AccentSoftBrush")
                : Brushes.Transparent;

        private void UpdateRepeatButton()
        {
            RepeatButton.Content = _playlist.RepeatMode switch
            {
                RepeatMode.All => Loc.T("S_RepeatAll"),
                RepeatMode.One => Loc.T("S_RepeatOne"),
                _ => Loc.T("S_RepeatNone")
            };
            RepeatButton.Background = _playlist.RepeatMode != RepeatMode.None
                ? (Brush)FindResource("AccentSoftBrush")
                : Brushes.Transparent;
        }

        private void SetTitle(string? name)
        {
            string title = string.IsNullOrEmpty(name) ? "OctoPlayer" : $"{name} - OctoPlayer";
            Title = title;
            TitleText.Text = title;
            TopOverlayTitle.Text = title;
        }

        internal void ShowToast(string message)
        {
            ToastText.Text = message;
            Toast.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        private static string FormatTime(long milliseconds)
        {
            if (milliseconds < 0)
            {
                milliseconds = 0;
            }

            TimeSpan t = TimeSpan.FromMilliseconds(milliseconds);
            return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
        }
    }

    /// <summary>재생목록 한 항목의 표시용 뷰모델입니다.</summary>
    public sealed class PlaylistEntry : INotifyPropertyChanged
    {
        private ImageSource? _thumbnail;
        private bool _isPlaying;

        public PlaylistEntry(PlaylistItem item)
        {
            Item = item;
        }

        public PlaylistItem Item { get; }

        public string Title => Item.DisplayName;

        public ImageSource? Thumbnail
        {
            get => _thumbnail;
            set
            {
                if (!ReferenceEquals(_thumbnail, value))
                {
                    _thumbnail = value;
                    OnChanged(nameof(Thumbnail));
                }
            }
        }

        public bool IsPlaying
        {
            get => _isPlaying;
            set
            {
                if (_isPlaying != value)
                {
                    _isPlaying = value;
                    OnChanged(nameof(IsPlaying));
                    OnChanged(nameof(PlayingVisibility));
                    OnChanged(nameof(TitleBrush));
                }
            }
        }

        public Visibility PlayingVisibility => IsPlaying ? Visibility.Visible : Visibility.Collapsed;

        public Brush TitleBrush => IsPlaying
            ? (Brush)Application.Current.FindResource("AccentBrush")
            : (Brush)Application.Current.FindResource("TextBrush");

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>휴지통 삭제(SHFileOperation, FOF_ALLOWUNDO)를 수행합니다.</summary>
    internal static class RecycleBin
    {
        public static bool Delete(string path)
        {
            try
            {
                var op = new SHFILEOPSTRUCT
                {
                    wFunc = 3, // FO_DELETE
                    pFrom = path + "\0\0",
                    fFlags = 0x0040 | 0x0010 | 0x0004 // FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT
                };
                return SHFileOperation(ref op) == 0 && !File.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
            public ushort fFlags;
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT fileOp);
    }
}
