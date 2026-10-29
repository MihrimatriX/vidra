namespace Vidra.Core.YtDlp;

/// <summary>
/// Ayarlardaki serbest yt-dlp argümanları (ileri düzey). Tırnaklı parçalar tek argümandır; ters eğik çizgi kaçış
/// değildir (Windows yolları). Vidra'nın ilerleme takibi, dosya yolu ve tek-video düzeniyle çakışan bayraklar reddedilir.
/// </summary>
public static class ExtraArgs
{
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        // Çıktı yeri ve adı: Ayarlar › Klasör ve şablonlar
        "-o", "--output", "-P", "--paths",
        // İlerleme ve bitiş satırları bunlarla okunuyor
        "-O", "--print", "--print-to-file", "--progress-template", "--newline", "--no-newline",
        "-q", "--quiet", "--no-progress", "--progress", "--console-title",
        "--encoding", "--color", "--no-color",
        // Sadece bilgi / taklit: dosya inmez ama iş "tamamlandı" görünür
        "-j", "--dump-json", "-J", "--dump-single-json", "-s", "--simulate", "--skip-download", "--flat-playlist",
        // Tek video işi (playlist'ler Vidra'da video video kuyruğa girer)
        "--yes-playlist", "--no-playlist", "-a", "--batch-file", "--load-info-json",
        // Bileşen yolları Vidra'da
        "--ffmpeg-location", "--js-runtimes",
        "-U", "--update", "--update-to", "--version", "-h", "--help",
    };

    public sealed record Result(IReadOnlyList<string> Args, string? Error);

    public static Result Parse(string? text)
    {
        var args = Tokenize(text ?? "");
        foreach (var a in args)
        {
            if (a == "--") return new([], "“--” kullanılamaz: bağlantıyı Vidra ekler.");
            var flag = a.Split('=', 2)[0];
            if (Reserved.Contains(flag))
                return new([], $"“{flag}” Vidra tarafından yönetiliyor; ek argümanlarda kullanılamaz.");
            if (!a.StartsWith('-') && LooksLikeUrl(a) && args.IndexOf(a) is var i && (i == 0 || !args[i - 1].StartsWith('-')))
                return new([], $"Bağlantı eklenemez ({a}): bu alan yalnızca seçenek içindir.");
        }
        return new(args, null);
    }

    public static List<string> Tokenize(string text)
    {
        var list = new List<string>();
        var cur = new System.Text.StringBuilder();
        char? quote = null;
        var any = false;
        foreach (var ch in text)
        {
            if (quote is { } q)
            {
                if (ch == q) quote = null;
                else cur.Append(ch);
            }
            else if (ch is '"' or '\'') { quote = ch; any = true; }
            else if (char.IsWhiteSpace(ch))
            {
                if (cur.Length > 0 || any) list.Add(cur.ToString());
                cur.Clear();
                any = false;
            }
            else cur.Append(ch);
        }
        if (cur.Length > 0 || any) list.Add(cur.ToString());
        return list;
    }

    private static bool LooksLikeUrl(string s) => s.Contains("://", StringComparison.Ordinal) || s.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
}
