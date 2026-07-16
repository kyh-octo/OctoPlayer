using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OctoPlayer.Services
{
    /// <summary>
    /// 자막 파일 탐색·인코딩 정규화·회전 변환을 담당합니다.
    /// - libVLC는 UTF-8 자막만 안정적으로 표시하므로 CP949/UTF-16 자막은 UTF-8 사본으로 변환합니다.
    /// - 영상 회전(transform 필터)은 자막 합성 이전 단계에 적용되어 자막이 회전되지 않으므로,
    ///   회전 시에는 자막을 ASS 형식으로 변환하면서 회전 태그(\frz)와 위치를 재계산해
    ///   자막이 영상에 붙어 함께 회전한 것처럼 보이게 합니다.
    /// </summary>
    public static class SubtitleFiles
    {
        static SubtitleFiles()
        {
            // .NET(Core)에서 CP949 등 코드페이지 인코딩을 쓰려면 공급자 등록이 필요합니다.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        /// <summary>지원하는 자막 확장자(선행 점 포함)입니다.</summary>
        public static readonly string[] Extensions = { ".srt", ".ass", ".ssa", ".smi", ".sub", ".vtt" };

        public static bool IsSubtitleFile(string path) =>
            Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 자막 파일명이 영상 파일명과 짝인지 판단합니다.
        /// "영상.smi"(동일 이름) 또는 "영상.ko.srt"(언어 접미사)만 허용합니다.
        /// </summary>
        public static bool MatchesVideo(string subtitlePath, string videoBaseName)
        {
            string name = Path.GetFileNameWithoutExtension(subtitlePath);
            return name.Equals(videoBaseName, StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(videoBaseName + ".", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// libVLC에 전달할 자막 파일 경로를 반환합니다.
        /// rotation이 0이면 인코딩 정규화만, 90/180/270이면 회전 반영 ASS로 변환합니다.
        /// 변환 실패 시 원본(또는 인코딩 정규화본)으로 대체합니다.
        /// </summary>
        public static string PrepareForLibVlc(string path, int rotation)
        {
            rotation = ((rotation % 360) + 360) % 360;
            if (rotation != 0)
            {
                try
                {
                    string? rotated = TryBuildRotatedAss(path, rotation);
                    if (rotated != null)
                    {
                        return rotated;
                    }
                }
                catch
                {
                    // 회전 변환 실패 → 아래의 일반 변환으로 대체(자막은 나오되 회전만 안 됨)
                }
            }

            return PrepareForLibVlc(path);
        }

        /// <summary>
        /// 인코딩만 정규화한 자막 경로를 반환합니다(이미 UTF-8이면 원본 그대로).
        /// </summary>
        public static string PrepareForLibVlc(string path)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);

                // UTF-8 BOM → 그대로 사용
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                {
                    return path;
                }

                (string text, bool alreadyUtf8) = DecodeText(bytes);
                if (alreadyUtf8)
                {
                    return path;
                }

                string converted = Path.Combine(TempDirFor(path), Path.GetFileName(path));
                File.WriteAllText(converted, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                return converted;
            }
            catch
            {
                return path;
            }
        }

        // ---------------------------------------------------------------------
        // 인코딩 감지
        // ---------------------------------------------------------------------

        /// <summary>바이트를 텍스트로 해석합니다. (UTF-8 BOM/무BOM → UTF-8, UTF-16 BOM, 그 외 CP949)</summary>
        private static (string Text, bool AlreadyUtf8) DecodeText(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), true);
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), false);
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), false);
            }

            try
            {
                return (new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes), true);
            }
            catch (DecoderFallbackException)
            {
                // 한국어 자막의 사실상 표준 인코딩
                return (Encoding.GetEncoding(949).GetString(bytes), false);
            }
        }

        private static string TempDirFor(string path)
        {
            string hash = Convert.ToHexString(
                MD5.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..12];
            string dir = Path.Combine(Path.GetTempPath(), "OctoPlayer", "subs", hash);
            Directory.CreateDirectory(dir);
            return dir;
        }

        // ---------------------------------------------------------------------
        // 회전 자막 (ASS 변환)
        // ---------------------------------------------------------------------

        private readonly record struct SubtitleCue(long StartMs, long EndMs, string Text);

        /// <summary>
        /// SRT/VTT/SMI 자막을 회전 태그가 적용된 ASS로 변환해 임시 파일 경로를 반환합니다.
        /// 지원하지 않는 형식(.ass/.ssa/.sub 등)은 null을 반환합니다.
        /// </summary>
        private static string? TryBuildRotatedAss(string path, int rotation)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            (string text, _) = DecodeText(File.ReadAllBytes(path));

            List<SubtitleCue> cues = ext switch
            {
                ".srt" or ".vtt" => ParseSrtLike(text),
                ".smi" => ParseSmi(text),
                _ => new List<SubtitleCue>()
            };

            if (cues.Count == 0)
            {
                return null;
            }

            // 회전별 위치/글자 회전. PlayRes 1000x1000 좌표는 영상 프레임에 비율로 매핑되므로
            // 실제 해상도를 몰라도 "회전 후 영상의 원래 하단" 가장자리에 정확히 배치됩니다.
            // transform-type은 시계방향 회전: 90° → 원래 하단이 화면 왼쪽, 270° → 오른쪽.
            // ASS \frz는 반시계 양수이므로 시계방향 90°는 \frz270입니다.
            string overrideTag = rotation switch
            {
                90 => @"{\an5\pos(60,500)\frz270}",
                180 => @"{\an5\pos(500,60)\frz180}",
                270 => @"{\an5\pos(940,500)\frz90}",
                _ => @"{\an2}"
            };

            var sb = new StringBuilder();
            sb.AppendLine("[Script Info]");
            sb.AppendLine("ScriptType: v4.00+");
            sb.AppendLine("PlayResX: 1000");
            sb.AppendLine("PlayResY: 1000");
            sb.AppendLine("ScaledBorderAndShadow: yes");
            sb.AppendLine();
            sb.AppendLine("[V4+ Styles]");
            sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
            sb.AppendLine("Style: Rot,Malgun Gothic,46,&H00FFFFFF,&H00FFFFFF,&H00000000,&H7F000000,0,0,0,0,100,100,0,0,1,2,1,5,0,0,0,1");
            sb.AppendLine();
            sb.AppendLine("[Events]");
            sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

            foreach (SubtitleCue cue in cues)
            {
                string assText = cue.Text
                    .Replace("{", "(").Replace("}", ")")
                    .Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\\N");
                sb.AppendLine($"Dialogue: 0,{AssTime(cue.StartMs)},{AssTime(cue.EndMs)},Rot,,0,0,0,,{overrideTag}{assText}");
            }

            string output = Path.Combine(
                TempDirFor(path),
                $"{Path.GetFileNameWithoutExtension(path)}.rot{rotation}.ass");
            File.WriteAllText(output, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return output;
        }

        private static string AssTime(long ms)
        {
            if (ms < 0)
            {
                ms = 0;
            }

            TimeSpan t = TimeSpan.FromMilliseconds(ms);
            return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}";
        }

        // ----- SRT / VTT -----

        private static readonly Regex TimeLinePattern = new(
            @"(?:(\d+):)?(\d+):(\d+)[,.](\d{1,3})\s*-->\s*(?:(\d+):)?(\d+):(\d+)[,.](\d{1,3})",
            RegexOptions.Compiled);

        private static List<SubtitleCue> ParseSrtLike(string content)
        {
            var cues = new List<SubtitleCue>();
            string[] lines = content.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                Match m = TimeLinePattern.Match(lines[i]);
                if (!m.Success)
                {
                    continue;
                }

                long start = ToMs(m.Groups[1], m.Groups[2], m.Groups[3], m.Groups[4]);
                long end = ToMs(m.Groups[5], m.Groups[6], m.Groups[7], m.Groups[8]);

                var textLines = new List<string>();
                for (int j = i + 1; j < lines.Length && !string.IsNullOrWhiteSpace(lines[j]); j++)
                {
                    textLines.Add(lines[j].Trim());
                    i = j;
                }

                string text = string.Join("\n", textLines).Trim();
                if (text.Length > 0 && end > start)
                {
                    cues.Add(new SubtitleCue(start, end, text));
                }
            }

            return cues;
        }

        private static long ToMs(Capture hours, Capture minutes, Capture seconds, Capture fraction)
        {
            long h = hours.Length > 0 ? long.Parse(hours.Value) : 0;
            long frac = long.Parse(fraction.Value.PadRight(3, '0'));
            return ((h * 60 + long.Parse(minutes.Value)) * 60 + long.Parse(seconds.Value)) * 1000 + frac;
        }

        // ----- SMI (SAMI) -----

        private static readonly Regex SmiSyncPattern = new(
            @"<SYNC\s+Start\s*=\s*""?(\d+)""?[^>]*>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex HtmlTagPattern = new("<[^>]+>", RegexOptions.Compiled);

        private static List<SubtitleCue> ParseSmi(string content)
        {
            var cues = new List<SubtitleCue>();
            MatchCollection syncs = SmiSyncPattern.Matches(content);

            for (int i = 0; i < syncs.Count; i++)
            {
                long start = long.Parse(syncs[i].Groups[1].Value);
                long end = i + 1 < syncs.Count ? long.Parse(syncs[i + 1].Groups[1].Value) : start + 4000;

                int from = syncs[i].Index + syncs[i].Length;
                int to = i + 1 < syncs.Count ? syncs[i + 1].Index : content.Length;
                string raw = content[from..to];

                string text = Regex.Replace(raw, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
                text = HtmlTagPattern.Replace(text, string.Empty);
                text = text.Replace("&nbsp;", " ").Replace("&amp;", "&")
                           .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"");
                text = text.Trim();

                // 빈 SYNC는 이전 자막을 지우는 용도 → 큐로 만들지 않음(이전 큐의 end가 여기서 끝남)
                if (text.Length > 0 && end > start)
                {
                    cues.Add(new SubtitleCue(start, end, text));
                }
            }

            return cues;
        }
    }
}
