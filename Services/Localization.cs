using System.Globalization;
using System.Windows;

namespace OctoPlayer.Services
{
    /// <summary>
    /// 언어(한국어/영어) 적용을 담당합니다.
    /// - 문자열은 Resources/Strings.ko.xaml(기본)과 Strings.en.xaml(영어 덮어쓰기)에 있습니다.
    /// - XAML은 {DynamicResource S_키}로 참조하므로 사전 교체 즉시 UI에 반영됩니다.
    /// - 코드에서는 <see cref="T"/>로 조회합니다.
    /// </summary>
    public static class Loc
    {
        private const string EnglishDictionary = "Resources/Strings.en.xaml";

        /// <summary>현재 적용된 언어 코드("ko" 또는 "en")입니다.</summary>
        public static string Current { get; private set; } = "ko";

        /// <summary>
        /// 언어를 적용합니다. null/빈 문자열이면 시스템 UI 언어를 따릅니다(한국어면 ko, 그 외 en).
        /// </summary>
        public static void Apply(string? language)
        {
            string lang = language switch
            {
                "ko" => "ko",
                "en" => "en",
                _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko" ? "ko" : "en"
            };
            Current = lang;

            var dictionaries = Application.Current.Resources.MergedDictionaries;
            ResourceDictionary? english = null;
            foreach (ResourceDictionary dict in dictionaries)
            {
                if (dict.Source?.OriginalString.EndsWith(EnglishDictionary, StringComparison.OrdinalIgnoreCase) == true)
                {
                    english = dict;
                    break;
                }
            }

            if (lang == "en")
            {
                // 영어 사전을 한국어(기본) 뒤에 추가하면 같은 키는 영어가 우선합니다.
                if (english == null)
                {
                    dictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri($"pack://application:,,,/{EnglishDictionary}")
                    });
                }
            }
            else if (english != null)
            {
                dictionaries.Remove(english);
            }
        }

        /// <summary>키에 해당하는 현재 언어 문자열을 반환합니다(없으면 키 그대로).</summary>
        public static string T(string key) =>
            Application.Current.TryFindResource(key) as string ?? key;

        /// <summary>키의 형식 문자열에 인자를 채워 반환합니다.</summary>
        public static string F(string key, params object[] args) =>
            string.Format(T(key), args);
    }
}
