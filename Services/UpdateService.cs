using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace OctoPlayer.Services
{
    /// <summary>GitHub 최신 릴리스 정보.</summary>
    public sealed class UpdateInfo
    {
        public required Version Version { get; init; }
        public required string TagName { get; init; }
        public required string ReleaseUrl { get; init; }
        /// <summary>설치 파일(OctoPlayer-Setup-&lt;버전&gt;.exe) 다운로드 주소. 릴리스에 설치 파일이 없으면 null.</summary>
        public string? InstallerUrl { get; init; }
        public string InstallerName { get; init; } = "";
        public long InstallerSize { get; init; }
        public string Notes { get; init; } = "";

        /// <summary>실행 중인 앱보다 새 버전인지.</summary>
        public bool IsNewer => Version > UpdateService.CurrentVersion;
    }

    /// <summary>
    /// GitHub 릴리스(kyh-octo/OctoPlayer)에서 새 버전을 확인하고, 설치 파일을 내려받아 조용히 설치한다.
    /// 설치 파일은 Inno Setup이며 /SILENT /AUTOUPDATE=1 로 실행하면 설치가 끝난 뒤 앱을 다시 실행한다
    /// (installer\OctoPlayer.iss 의 IsAutoUpdate 참고).
    /// </summary>
    public static class UpdateService
    {
        public const string AppName = "OctoPlayer";
        public const string Repo = "kyh-octo/OctoPlayer";
        public const string ReleasesUrl = "https://github.com/" + Repo + "/releases";
        private const string LatestApi = "https://api.github.com/repos/" + Repo + "/releases/latest";

        private static readonly Lazy<Version> Current = new(() =>
            Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0)));

        /// <summary>실행 중인 앱 버전 (csproj &lt;Version&gt;, 3자리).</summary>
        public static Version CurrentVersion => Current.Value;

        private static readonly Lazy<HttpClient> Http = new(() =>
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppName, CurrentVersion.ToString()));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return client;
        });

        /// <summary>4자리(1.2.3.0) 버전을 3자리(1.2.3)로 맞춘다. 비교 시 Revision 차이로 인한 오판 방지.</summary>
        public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

        /// <summary>"v1.2.3" / "1.2.3" 형식의 태그를 버전으로 변환한다.</summary>
        public static bool TryParseVersion(string? tag, out Version version)
        {
            version = new Version(0, 0, 0);
            if (string.IsNullOrWhiteSpace(tag)) return false;
            string s = tag.Trim();
            if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
            if (!Version.TryParse(s, out var parsed)) return false;
            version = Normalize(parsed);
            return true;
        }

        /// <summary>GitHub 최신 릴리스를 조회한다. 네트워크 오류 시 예외.</summary>
        public static async Task<UpdateInfo> CheckAsync(CancellationToken ct = default)
        {
            using var resp = await Http.Value.GetAsync(LatestApi, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
            if (!TryParseVersion(tag, out var version))
                throw new InvalidDataException($"릴리스 태그 형식을 알 수 없습니다: '{tag}'");

            string? url = null, name = null;
            long size = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string n = asset.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                    if (n.StartsWith(AppName + "-Setup-", StringComparison.OrdinalIgnoreCase)
                        && n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        url = asset.TryGetProperty("browser_download_url", out var uEl) ? uEl.GetString() : null;
                        size = asset.TryGetProperty("size", out var sEl) && sEl.TryGetInt64(out long sz) ? sz : 0;
                        name = n;
                        break;
                    }
                }
            }

            return new UpdateInfo
            {
                Version = version,
                TagName = tag,
                ReleaseUrl = root.TryGetProperty("html_url", out var hEl) ? hEl.GetString() ?? ReleasesUrl : ReleasesUrl,
                InstallerUrl = url,
                InstallerName = name ?? "",
                InstallerSize = size,
                Notes = root.TryGetProperty("body", out var bEl) ? bEl.GetString() ?? "" : "",
            };
        }

        /// <summary>설치 파일을 %TEMP%\OctoPlayer-Update\ 에 내려받고 경로를 돌려준다. 진행률은 (0~100, 상태 문구).</summary>
        public static async Task<string> DownloadAsync(UpdateInfo info,
            IProgress<(double Percent, string Message)>? progress, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(info.InstallerUrl))
                throw new InvalidOperationException("이 릴리스에는 설치 파일이 없습니다.");

            string dir = Path.Combine(Path.GetTempPath(), AppName + "-Update");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, string.IsNullOrEmpty(info.InstallerName) ? $"{AppName}-Setup-{info.Version}.exe" : info.InstallerName);
            string part = path + ".part";

            using var resp = await Http.Value.GetAsync(info.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? info.InstallerSize;

            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long done = 0, lastReport = -1000;
                var sw = Stopwatch.StartNew();
                int n;
                while ((n = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                    done += n;
                    if (sw.ElapsedMilliseconds - lastReport >= 200 || done == total)
                    {
                        lastReport = sw.ElapsedMilliseconds;
                        double pct = total > 0 ? done * 100.0 / total : 0;
                        double speed = done / 1048576.0 / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
                        string totalText = total > 0 ? $"{total / 1048576.0:F1}" : "?";
                        progress?.Report((pct, $"{done / 1048576.0:F1} / {totalText} MB  ({speed:F1} MB/s)"));
                    }
                }
            }

            if (total > 0 && new FileInfo(part).Length != total)
            {
                File.Delete(part);
                throw new IOException("내려받은 파일 크기가 서버와 다릅니다. 다시 시도해 주세요.");
            }
            File.Move(part, path, overwrite: true);
            return path;
        }

        /// <summary>
        /// 설치 파일을 조용히(/SILENT) 실행하고 현재 앱을 종료한다.
        /// 앱이 완전히 종료된 뒤 설치가 시작되도록 몇 초 지연시켜 실행하며, 설치가 끝나면 설치 프로그램이 앱을 다시 실행한다.
        /// </summary>
        public static void InstallAndExit(string installerPath)
        {
            string cmd = $"/C ping -n 4 127.0.0.1 >nul & start \"\" \"{installerPath}\" /SILENT /NORESTART /AUTOUPDATE=1";
            Process.Start(new ProcessStartInfo("cmd.exe", cmd)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            System.Windows.Application.Current.Shutdown();
        }

        /// <summary>릴리스 페이지를 기본 브라우저로 연다.</summary>
        public static void OpenReleasePage(string? url = null)
        {
            try { Process.Start(new ProcessStartInfo(string.IsNullOrEmpty(url) ? ReleasesUrl : url) { UseShellExecute = true }); }
            catch { /* 브라우저 실행 실패는 무시 */ }
        }
    }

    /// <summary>업데이트 UI 문자열 (Resources/Strings.*.xaml 의 S_Upd* 키).</summary>
    internal static class UpdateText
    {
        public static string T(string key) => Loc.T("S_" + key);
        public static string F(string key, params object[] args) => Loc.F("S_" + key, args);
    }
}
