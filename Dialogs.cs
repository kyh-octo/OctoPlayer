using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OctoPlayer.Services;

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
        private readonly CheckBox _openScanBox;
        private readonly CheckBox _rememberRateBox;
        private readonly CheckBox _autoSubBox;
        private readonly CheckBox _rememberSizeBox;
        private readonly CheckBox _alwaysOnTopBox;
        private readonly TextBox _captureFolderBox;
        private readonly RadioButton _langAuto;
        private readonly RadioButton _langKo;
        private readonly RadioButton _langEn;

        public SettingsWindow(Services.AppSettings settings)
        {
            _settings = settings;
            Title = Loc.T("S_SettingsTitle");
            DialogTheme.Setup(this, 480, 790);

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
            root.Children.Add(Section(Loc.T("S_SecNav")));
            _sideSkipBox = NumberRow(Loc.T("S_SideSkip"), settings.SkipSeconds, Loc.T("S_Seconds"));
            _arrowSkipBox = NumberRow(Loc.T("S_ArrowSkip"), settings.ArrowSkipSeconds, Loc.T("S_Seconds"));

            // ----- 소리 -----
            root.Children.Add(Section(Loc.T("S_SecAudio")));
            _volumeStepBox = NumberRow(Loc.T("S_VolumeStep"), settings.WheelVolumeStep, Loc.T("S_Steps"));

            // ----- 재생 -----
            root.Children.Add(Section(Loc.T("S_SecPlayback")));
            _resumeBox = CheckRow(Loc.T("S_ResumeOpt"), settings.ResumePlayback);
            _openScanBox = CheckRow(Loc.T("S_OpenScanOpt"), settings.OpenFolderScan);
            _rememberRateBox = CheckRow(Loc.T("S_RememberRateOpt"), settings.RememberRate);

            // ----- 자막 -----
            root.Children.Add(Section(Loc.T("S_SecSubtitles")));
            _autoSubBox = CheckRow(Loc.T("S_AutoSubOpt"), settings.AutoLoadSubtitles);

            // ----- 화면 -----
            root.Children.Add(Section(Loc.T("S_SecDisplay")));
            _rememberSizeBox = CheckRow(Loc.T("S_RememberSizeOpt"), settings.RememberWindowSize);
            _alwaysOnTopBox = CheckRow(Loc.T("S_AlwaysOnTopOpt"), settings.AlwaysOnTop);

            // ----- 언어 -----
            root.Children.Add(Section(Loc.T("S_SecLanguage")));
            RadioButton LangRadio(string label, bool isChecked)
            {
                var radio = new RadioButton
                {
                    Content = label,
                    GroupName = "UiLanguage",
                    IsChecked = isChecked,
                    Margin = new Thickness(0, 2, 18, 2),
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                return radio;
            }
            _langAuto = LangRadio(Loc.T("S_LangAuto"), string.IsNullOrEmpty(settings.Language));
            _langKo = LangRadio("한국어", settings.Language == "ko");
            _langEn = LangRadio("English", settings.Language == "en");
            var langRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            langRow.Children.Add(_langAuto);
            langRow.Children.Add(_langKo);
            langRow.Children.Add(_langEn);
            root.Children.Add(langRow);

            // ----- 캡처 -----
            root.Children.Add(Section(Loc.T("S_SecCapture")));
            _captureFolderBox = new TextBox
            {
                Text = settings.CaptureFolder ?? string.Empty,
                VerticalAlignment = VerticalAlignment.Center
            };
            var browse = DialogTheme.MakeButton(Loc.T("S_Browse"));
            browse.Click += (_, _) =>
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("S_SelectCaptureFolder") };
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
                Text = Loc.T("S_CaptureFolderHint"),
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = (Brush)Application.Current.FindResource("TextDimBrush")
            });
            root.Children.Add(folderRow);

            // ----- 파일 연결 (즉시 적용) -----
            root.Children.Add(Section(Loc.T("S_SecAssoc")));
            var assocStatus = new TextBlock
            {
                Foreground = (Brush)Application.Current.FindResource("TextDimBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            };
            void RefreshAssocStatus()
            {
                assocStatus.Text = Services.FileAssociations.IsRegistered
                    ? Loc.T("S_AssocRegistered")
                    : Loc.T("S_AssocNotRegistered");
            }
            RefreshAssocStatus();

            var assocRegister = DialogTheme.MakeButton(Loc.T("S_AssocRegister"), accent: true);
            assocRegister.Margin = new Thickness(0, 0, 6, 0);
            assocRegister.Click += (_, _) =>
            {
                try
                {
                    Services.FileAssociations.Register();
                    MessageBox.Show(this, Loc.T("S_AssocDoneMsg"),
                        Loc.T("S_SecAssoc"), MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, Loc.F("S_AssocFailMsg", ex.Message),
                        Loc.T("S_SecAssoc"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
                RefreshAssocStatus();
            };

            var assocRemove = DialogTheme.MakeButton(Loc.T("S_AssocUnregister"));
            assocRemove.Click += (_, _) =>
            {
                try
                {
                    Services.FileAssociations.Unregister();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, Loc.F("S_AssocUnregFailMsg", ex.Message),
                        Loc.T("S_SecAssoc"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
                RefreshAssocStatus();
            };

            var assocRow = new StackPanel { Orientation = Orientation.Horizontal };
            assocRow.Children.Add(assocRegister);
            assocRow.Children.Add(assocRemove);
            root.Children.Add(assocStatus);
            root.Children.Add(assocRow);

            // ----- 확인/취소 -----
            Button ok = DialogTheme.MakeButton(Loc.T("S_OK"), accent: true);
            ok.IsDefault = true;
            ok.Click += (_, _) => { Apply(); DialogResult = true; };
            Button cancel = DialogTheme.MakeButton(Loc.T("S_Cancel"));
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
            _settings.OpenFolderScan = _openScanBox.IsChecked == true;
            _settings.RememberRate = _rememberRateBox.IsChecked == true;
            _settings.AutoLoadSubtitles = _autoSubBox.IsChecked == true;
            _settings.RememberWindowSize = _rememberSizeBox.IsChecked == true;
            _settings.AlwaysOnTop = _alwaysOnTopBox.IsChecked == true;
            _settings.CaptureFolder = string.IsNullOrWhiteSpace(_captureFolderBox.Text)
                ? null
                : _captureFolderBox.Text.Trim();
            _settings.Language = _langKo.IsChecked == true ? "ko"
                : _langEn.IsChecked == true ? "en"
                : null;
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

            Button close = DialogTheme.MakeButton(Loc.T("S_Close"), accent: true);
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

            Button ok = DialogTheme.MakeButton(Loc.T("S_OK"), accent: true);
            ok.Click += (_, _) => { _accepted = true; Close(); };
            ok.IsDefault = true;
            Button cancel = DialogTheme.MakeButton(Loc.T("S_Cancel"));
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
            Title = Loc.T("S_ControllerTitle");
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
