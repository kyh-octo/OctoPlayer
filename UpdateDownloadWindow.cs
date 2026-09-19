using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OctoPlayer.Services;

namespace OctoPlayer
{
    /// <summary>
    /// 업데이트 설치 파일을 내려받는 진행 창. 다운로드가 끝나면 설치 프로그램을 실행하고 앱을 종료한다.
    /// </summary>
    public sealed class UpdateDownloadWindow : Window
    {
        private readonly UpdateInfo _info;
        private readonly CancellationTokenSource _cts = new();
        private readonly TextBlock _status;
        private readonly TextBlock _detail;
        private readonly ProgressBar _bar;
        private bool _finished;

        private UpdateDownloadWindow(UpdateInfo info)
        {
            _info = info;
            Title = UpdateText.T("UpdDownloadTitle");
            DialogTheme.Setup(this, 460, 190);
            _status = new TextBlock
            {
                Text = UpdateText.F("UpdDownloading", info.Version),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            };
            _bar = new ProgressBar { Maximum = 100, Height = 10 };
            _detail = new TextBlock { Margin = new Thickness(0, 6, 0, 14), FontSize = 11, Opacity = 0.75 };

            var cancel = DialogTheme.MakeButton(UpdateText.T("Cancel"));
            cancel.HorizontalAlignment = HorizontalAlignment.Right;
            cancel.IsCancel = true;
            cancel.Click += (_, _) => _cts.Cancel();

            var root = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
            root.Children.Add(_status);
            root.Children.Add(_bar);
            root.Children.Add(_detail);
            root.Children.Add(cancel);
            Content = root;

            Loaded += async (_, _) => await RunAsync();
            Closing += OnClosing;
        }

        /// <summary>진행 창을 모달로 띄운다. 성공하면 앱이 종료되므로 이 호출 뒤로는 돌아오지 않는다.</summary>
        public static void Run(Window? owner, UpdateInfo info)
        {
            var window = new UpdateDownloadWindow(info);
            if (owner is { IsVisible: true }) window.Owner = owner;
            window.ShowDialog();
        }

        private async Task RunAsync()
        {
            var progress = new Progress<(double Percent, string Message)>(p =>
            {
                _bar.Value = p.Percent;
                _detail.Text = p.Message;
            });

            try
            {
                string path = await UpdateService.DownloadAsync(_info, progress, _cts.Token);
                _status.Text = UpdateText.T("UpdStarting");
                _bar.Value = 100;
                _finished = true;
                UpdateService.InstallAndExit(path);
            }
            catch (OperationCanceledException)
            {
                // 사용자가 취소
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, UpdateText.F("UpdDownloadFailed", ex.Message),
                    UpdateText.T("UpdTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                UpdateService.OpenReleasePage(_info.ReleaseUrl);
            }
            _finished = true;
            Close();
        }

        private void OnClosing(object? sender, CancelEventArgs e)
        {
            if (!_finished) _cts.Cancel();
        }
    }

    /// <summary>시작 시 자동 업데이트 확인. 새 버전이 있으면 지금 설치할지 묻는다.</summary>
    public static class UpdatePrompt
    {
        /// <param name="settings">CheckForUpdates 가 꺼져 있으면 아무것도 하지 않는다.</param>
        /// <param name="getOwner">질문 창의 소유자. 숨겨져 있으면(트레이 등) whenOwnerHidden 을 대신 호출한다.</param>
        public static async Task RunStartupCheckAsync(AppSettings settings, Func<Window?> getOwner,
            Action<UpdateInfo>? whenOwnerHidden = null)
        {
            if (!settings.CheckForUpdates) return;

            UpdateInfo info;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3)); // 시작 직후 부하를 피한다
                info = await UpdateService.CheckAsync();
            }
            catch
            {
                return; // 오프라인, GitHub 장애 등은 조용히 넘어간다
            }
            if (!info.IsNewer || string.IsNullOrEmpty(info.InstallerUrl)) return;

            var owner = getOwner();
            if (owner is null || !owner.IsVisible)
            {
                whenOwnerHidden?.Invoke(info);
                return;
            }
            Ask(owner, info);
        }

        /// <summary>"새 버전이 있습니다. 지금 업데이트하시겠습니까?" 를 묻고, 예를 고르면 다운로드/설치를 시작한다.</summary>
        public static void Ask(Window owner, UpdateInfo info)
        {
            var result = MessageBox.Show(owner,
                UpdateText.F("UpdPromptMsg", info.Version, UpdateService.CurrentVersion),
                UpdateText.T("UpdTitle"), MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (result == MessageBoxResult.Yes)
                UpdateDownloadWindow.Run(owner, info);
        }
    }
}
