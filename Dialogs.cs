using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OctoPlayer
{
    internal static class DialogTheme
    {
        public static Brush Bg => (Brush)Application.Current.FindResource("BarBrush");
        public static Brush Surface => (Brush)Application.Current.FindResource("SurfaceBrush");
        public static Brush Text => (Brush)Application.Current.FindResource("TextBrush");
        public static Brush Accent => (Brush)Application.Current.FindResource("AccentSoftBrush");

        public static Button MakeButton(string text, bool accent = false)
        {
            var button = new Button
            {
                Content = text,
                MinWidth = 84,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(6, 0, 0, 0),
                Style = (Style)Application.Current.FindResource("IconButton")
            };
            if (accent)
            {
                button.Background = Accent;
            }
            return button;
        }

        public static void Setup(Window window, double width, double height)
        {
            window.Width = width;
            window.Height = height;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            window.ResizeMode = ResizeMode.NoResize;
            window.Background = Bg;
            window.Foreground = Text;
            window.FontFamily = new FontFamily("Segoe UI");
            window.FontSize = 13;
            window.ShowInTaskbar = false;
        }
    }

    /// <summary>
    /// 환경설정 대화상자.
    /// 탐색/소리/재생/자막/화면/캡처 항목을 편집하고, 확인 시 전달받은 AppSettings에 반영합니다.
    /// (저장과 런타임 적용은 호출 측(MainWindow.Menu_Settings)에서 수행합니다.)
    /// </summary>
    public sealed class SettingsWindow : Window
    {
        private readonly Services.AppSettings _settings;
        private readonly TextBox _sideSkipBox;
        private readonly TextBox _arrowSkipBox;
        private readonly TextBox _volumeStepBox;
        private readonly CheckBox _resumeBox;
        private readonly CheckBox _rememberRateBox;
        private readonly CheckBox _autoSubBox;
        private readonly CheckBox _rememberSizeBox;
        private readonly CheckBox _alwaysOnTopBox;
        private readonly TextBox _captureFolderBox;

        public SettingsWindow(Services.AppSettings settings)
        {
            _settings = settings;
            Title = "환경 설정";
            DialogTheme.Setup(this, 480, 680);

            var root = new StackPanel { Margin = new Thickness(20, 12, 20, 0) };

            TextBlock Section(string text) => new()
            {
                Text = text,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.FindResource("AccentBrush"),
                Margin = new Thickness(0, 14, 0, 6)
            };

            TextBox NumberRow(string label, int value, string suffix)
            {
                var box = new TextBox
                {
                    Text = value.ToString(),
                    Width = 64,
                    TextAlignment = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                var right = new StackPanel { Orientation = Orientation.Horizontal };
                right.Children.Add(box);
                right.Children.Add(new TextBlock
                {
                    Text = suffix,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 0, 0),
                    Foreground = (Brush)Application.Current.FindResource("TextDimBrush")
                });
                DockPanel.SetDock(right, Dock.Right);
                row.Children.Add(right);
                row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
                root.Children.Add(row);
                return box;
            }

            CheckBox CheckRow(string label, bool value)
            {
                var box = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 4, 0, 4) };
                root.Children.Add(box);
                return box;
            }

            // ----- 탐색 -----
            root.Children.Add(Section("탐색"));
            _sideSkipBox = NumberRow("마우스 뒤로/앞으로 버튼 이동 시간", settings.SkipSeconds, "초");
            _arrowSkipBox = NumberRow("방향키(←/→) 이동 시간", settings.ArrowSkipSeconds, "초");

            // ----- 소리 -----
            root.Children.Add(Section("소리"));
            _volumeStepBox = NumberRow("휠/방향키(↑/↓) 볼륨 조절량", settings.WheelVolumeStep, "단계");

            // ----- 재생 -----
            root.Children.Add(Section("재생"));
            _resumeBox = CheckRow("마지막으로 본 위치에서 이어서 재생", settings.ResumePlayback);
            _rememberRateBox = CheckRow("종료 시 재생 속도 기억", settings.RememberRate);

            // ----- 자막 -----
            root.Children.Add(Section("자막"));
            _autoSubBox = CheckRow("같은 이름의 자막 파일 자동 불러오기", settings.AutoLoadSubtitles);

            // ----- 화면 -----
            root.Children.Add(Section("화면"));
            _rememberSizeBox = CheckRow("종료 시 창 크기 기억", settings.RememberWindowSize);
            _alwaysOnTopBox = CheckRow("항상 위에 표시", settings.AlwaysOnTop);

            // ----- 캡처 -----
            root.Children.Add(Section("캡처"));
            _captureFolderBox = new TextBox
            {
                Text = settings.CaptureFolder ?? string.Empty,
                VerticalAlignment = VerticalAlignment.Center
            };
            var browse = DialogTheme.MakeButton("찾아보기...");
            browse.Click += (_, _) =>
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "캡처 저장 폴더 선택" };
                if (dialog.ShowDialog(this) == true)
                {
                    _captureFolderBox.Text = dialog.FolderName;
                }
            };
            var folderRow = new DockPanel { Margin = new Thickness(0, 3, 0, 0) };
            DockPanel.SetDock(browse, Dock.Right);
            folderRow.Children.Add(browse);
            folderRow.Children.Add(_captureFolderBox);
            root.Children.Add(new TextBlock
            {
                Text = "저장 폴더 (비워 두면 사진\\OctoPlayer)",
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = (Brush)Application.Current.FindResource("TextDimBrush")
            });
            root.Children.Add(folderRow);

            // ----- 파일 연결 (즉시 적용) -----
            root.Children.Add(Section("파일 연결"));
            var assocStatus = new TextBlock
            {
                Foreground = (Brush)Application.Current.FindResource("TextDimBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            };
            void RefreshAssocStatus()
            {
                assocStatus.Text = Services.FileAssociations.IsRegistered
                    ? "등록됨 — 파일 우클릭 > 연결 프로그램, 또는 Windows 설정 > 기본 앱에서 OctoPlayer를 선택할 수 있습니다."
                    : "등록되지 않음 — 등록하면 동영상/음악 파일을 OctoPlayer로 열 수 있게 됩니다.";
            }
            RefreshAssocStatus();

            var assocRegister = DialogTheme.MakeButton("확장자 연결 등록", accent: true);
            assocRegister.Margin = new Thickness(0, 0, 6, 0);
            assocRegister.Click += (_, _) =>
            {
                try
                {
                    Services.FileAssociations.Register();
                    MessageBox.Show(this,
                        "등록되었습니다.\n\n기본 플레이어로 쓰려면 동영상 파일 우클릭 → 연결 프로그램 → " +
                        "다른 앱 선택 → OctoPlayer → '항상 사용'을 선택하세요.",
                        "파일 연결", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"등록에 실패했습니다.\n{ex.Message}",
                        "파일 연결", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                RefreshAssocStatus();
            };

            var assocRemove = DialogTheme.MakeButton("연결 해제");
            assocRemove.Click += (_, _) =>
            {
                try
                {
                    Services.FileAssociations.Unregister();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"해제에 실패했습니다.\n{ex.Message}",
                        "파일 연결", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                RefreshAssocStatus();
            };

            var assocRow = new StackPanel { Orientation = Orientation.Horizontal };
            assocRow.Children.Add(assocRegister);
            assocRow.Children.Add(assocRemove);
            root.Children.Add(assocStatus);
            root.Children.Add(assocRow);

            // ----- 확인/취소 -----
            Button ok = DialogTheme.MakeButton("확인", accent: true);
            ok.IsDefault = true;
            ok.Click += (_, _) => { Apply(); DialogResult = true; };
            Button cancel = DialogTheme.MakeButton("취소");
            cancel.IsCancel = true;
            cancel.Click += (_, _) => { DialogResult = false; };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 22, 0, 16)
            };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);

            Content = new ScrollViewer
            {
                Content = root,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
        }

        private void Apply()
        {
            static int ParseClamped(TextBox box, int fallback, int min, int max) =>
                int.TryParse(box.Text, out int v) ? Math.Clamp(v, min, max) : fallback;

            _settings.SkipSeconds = ParseClamped(_sideSkipBox, _settings.SkipSeconds, 1, 600);
            _settings.ArrowSkipSeconds = ParseClamped(_arrowSkipBox, _settings.ArrowSkipSeconds, 1, 600);
            _settings.WheelVolumeStep = ParseClamped(_volumeStepBox, _settings.WheelVolumeStep, 1, 50);
            _settings.ResumePlayback = _resumeBox.IsChecked == true;
            _settings.RememberRate = _rememberRateBox.IsChecked == true;
            _settings.AutoLoadSubtitles = _autoSubBox.IsChecked == true;
            _settings.RememberWindowSize = _rememberSizeBox.IsChecked == true;
            _settings.AlwaysOnTop = _alwaysOnTopBox.IsChecked == true;
            _settings.CaptureFolder = string.IsNullOrWhiteSpace(_captureFolderBox.Text)
                ? null
                : _captureFolderBox.Text.Trim();
        }
    }

    /// <summary>간단한 텍스트 정보 창 (재생 정보/프로그램 정보 공용).</summary>
    public sealed class InfoWindow : Window
    {
        private InfoWindow(string title, string text)
        {
            Title = title;
            DialogTheme.Setup(this, 520, 380);
            ResizeMode = ResizeMode.CanResize;

            var textBlock = new TextBox
            {
                Text = text,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(14)
            };

            Button close = DialogTheme.MakeButton("닫기", accent: true);
            close.Click += (_, _) => Close();
            close.IsCancel = true;
            close.HorizontalAlignment = HorizontalAlignment.Right;
            close.Margin = new Thickness(0, 0, 14, 12);

            var root = new DockPanel();
            DockPanel.SetDock(close, Dock.Bottom);
            root.Children.Add(close);
            root.Children.Add(textBlock);
            Content = root;
        }

        public static void Show(Window owner, string title, string text)
        {
            new InfoWindow(title, text) { Owner = owner }.ShowDialog();
        }
    }

    /// <summary>한 줄 입력 대화상자 (주소 열기 등).</summary>
    public sealed class PromptWindow : Window
    {
        private readonly TextBox _input;
        private bool _accepted;

        private PromptWindow(string title, string label)
        {
            Title = title;
            DialogTheme.Setup(this, 460, 170);

            _input = new TextBox { Margin = new Thickness(18, 8, 18, 0) };

            Button ok = DialogTheme.MakeButton("확인", accent: true);
            ok.Click += (_, _) => { _accepted = true; Close(); };
            ok.IsDefault = true;
            Button cancel = DialogTheme.MakeButton("취소");
            cancel.Click += (_, _) => Close();
            cancel.IsCancel = true;

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(18, 18, 18, 0)
            };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            var root = new StackPanel();
            root.Children.Add(new TextBlock { Text = label, Margin = new Thickness(18, 18, 18, 0) });
            root.Children.Add(_input);
            root.Children.Add(buttons);
            Content = root;

            Loaded += (_, _) => _input.Focus();
        }

        public static string? Show(Window owner, string title, string label)
        {
            var dialog = new PromptWindow(title, label) { Owner = owner };
            dialog.ShowDialog();
            return dialog._accepted ? dialog._input.Text : null;
        }
    }

    /// <summary>작고 항상 위에 떠 있는 제어창 (F7).</summary>
    public sealed class ControllerWindow : Window
    {
        public ControllerWindow(MainWindow player)
        {
            Title = "제어창";
            DialogTheme.Setup(this, 320, 96);
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = SystemParameters.WorkArea.Right - 340;
            Top = SystemParameters.WorkArea.Bottom - 130;

            Button Make(string glyph, Action action, double size = 15)
            {
                Button b = DialogTheme.MakeButton(glyph);
                b.FontSize = size;
                b.MinWidth = 48;
                b.Click += (_, _) => action();
                return b;
            }

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(Make("⏮", player.PlayPreviousManual));
            row.Children.Add(Make("−10s", () => player.Skip(-10_000), 12));
            row.Children.Add(Make("⏯", player.TogglePlayPause, 17));
            row.Children.Add(Make("+10s", () => player.Skip(+10_000), 12));
            row.Children.Add(Make("⏭", player.PlayNextManual));
            row.Children.Add(Make("🔇", player.ToggleMute, 13));

            Content = row;
        }
    }
}
