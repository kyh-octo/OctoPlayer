using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace OctoPlayer
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 설치 직후 인스톨러가 호출하는 숨김 모드: libVLC 플러그인 캐시(plugins.dat)를
            // 미리 생성해 사용자의 첫 실행부터 빠르게 시작되도록 합니다. 창은 표시하지 않습니다.
            if (e.Args.Length > 0 && e.Args[0] == "--gen-plugins-cache")
            {
                try
                {
                    LibVLCSharp.Shared.Core.Initialize();
                    using var lib = new LibVLCSharp.Shared.LibVLC("--reset-plugins-cache");
                }
                catch
                {
                    // 캐시 생성 실패는 치명적이지 않습니다(첫 실행 시 앱이 다시 시도).
                }
                Shutdown();
                return;
            }

            // 시작 실패 진단용: 예외를 로그로 남기고 사용자에게 알립니다.
            DispatcherUnhandledException += (_, args) =>
            {
                LogStartupError(args.Exception);
                MessageBox.Show(args.Exception.ToString(), "OctoPlayer 오류");
                args.Handled = true;
                Shutdown();
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                LogStartupError(args.ExceptionObject as Exception);

            try
            {
                var window = new MainWindow(e.Args);
                MainWindow = window;
                window.Show();
            }
            catch (Exception ex)
            {
                LogStartupError(ex);
                MessageBox.Show(ex.ToString(), "OctoPlayer 시작 오류");
                Shutdown();
            }
        }

        private static void LogStartupError(Exception? ex)
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OctoPlayer");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(dir, "error.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
            }
            catch
            {
                // 로그 실패는 무시
            }
        }
    }

    /// <summary>
    /// 슬라이더 진행 비율을 계산합니다.
    /// 값 3개(Value/Min/Max) → 0~1 비율(ScaleX용), 값 4개(+너비) → 픽셀 너비.
    /// </summary>
    public sealed class SliderProgressConverter : IMultiValueConverter
    {
        public static readonly SliderProgressConverter Instance = new();

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 3
                || values[0] is not double value
                || values[1] is not double min
                || values[2] is not double max
                || max <= min)
            {
                return 0d;
            }

            double fraction = Math.Clamp((value - min) / (max - min), 0d, 1d);

            if (values.Length >= 4 && values[3] is double width)
            {
                return fraction * width;
            }

            return fraction;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
