using System.Globalization;
using Vidra.Core.Models;

namespace Vidra.Core.YtDlp;

/// <summary>Bileşenlerin yeri. ffmpeg/ffprobe/deno aynı klasörde olmak zorunda değil ama genelde öyledir.</summary>
public sealed record ToolPaths(string YtDlp, string FfmpegDir, string Deno, string ArchiveFile);

/// <summary>
/// Saf fonksiyonlar: ayarlar + seçim → yt-dlp argüman listesi.
/// ProcessStartInfo.ArgumentList ile verildiği için tırnaklama/kaçış derdi yok.
/// </summary>
public static class YtDlpArgs
{
    public const string MarkDl = "VIDRA:dl|";
    public const string MarkPp = "VIDRA:pp|";
    public const string MarkDone = "VIDRA:done|";

    private const string ProgressTemplate =
        MarkDl + "%(progress.status)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|" +
        "%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s|%(info.format_id)s";

    private const string PpTemplate = MarkPp + "%(progress.status)s|%(progress.postprocessor)s";

    /// <summary>Kalite/format seçimi. Sıralama (-S) -f filtrelerinden daha dayanıklıdır.</summary>
    public static List<string> Format(DownloadOptions o)
    {
        if (o.Mode == DownloadMode.Audio)
        {
            var a = new List<string> { "-f", "ba/b", "-x", "--audio-format", o.AudioFormat.Ext() };
            // Kayıplı formatlarda en iyi VBR; kayıpsızda kalite parametresi anlamsız
            if (o.AudioFormat is AudioFormat.Mp3 or AudioFormat.M4a or AudioFormat.Opus) a.AddRange(["--audio-quality", "0"]);
            return a;
        }

        // Sıra önemli: çözünürlük → kare hızı → kodek. Kodek tercihi asla çözünürlüğü düşürmez.
        var sort = new List<string>();
        if (o.Height is { } h) sort.Add($"res:{h}");
        if (o.MaxFps30) sort.Add("fps:30");
        var codec = o.Codec switch
        {
            CodecPreference.H264 => "h264",
            CodecPreference.Vp9 => "vp9",
            CodecPreference.Av1 => "av01",
            _ => o.Container == ContainerFormat.Mp4 ? "h264" : null,
        };
        if (codec is not null) sort.Add($"vcodec:{codec}");
        // MP4'e AAC konur; Opus'u MP4'e koymak bazı oynatıcılarda sorun çıkarır
        if (o.Container == ContainerFormat.Mp4) sort.Add("acodec:m4a");

        var args = new List<string> { "-f", "bv*+ba/b" };
        if (sort.Count > 0) args.AddRange(["-S", string.Join(',', sort)]);
        args.AddRange(["--merge-output-format", o.Container.Ext()]);
        return args;
    }

    public static List<string> Cookies(CookieSource c) => c switch
    {
        BrowserCookies b => ["--cookies-from-browser", b.Profile is { Length: > 0 } p ? $"{b.Browser.Arg()}:{p}" : b.Browser.Arg()],
        FileCookies f => ["--cookies", f.Path],
        AppLoginCookies a => ["--cookies", a.Path],
        _ => [],
    };

    public static List<string> ClipArgs(Clip? clip)
    {
        if (clip is null || !TimeText.IsTime(clip.Start) || !TimeText.IsTime(clip.End)) return [];
        // Anahtar karede kesmek hızlı ama kayar; kesin kesim için ffmpeg kesim noktalarına anahtar kare ekler
        return ["--download-sections", $"*{clip.Start.Trim()}-{clip.End.Trim()}", "--force-keyframes-at-cuts"];
    }

    public static List<string> Extras(DownloadOptions o, AppSettings s)
    {
        var a = new List<string>();
        var langs = o.SubLangs ?? s.SubLangs;
        // Müzik dosyasının yanında altyazı dosyası istenmez
        if (o.Subtitles && langs.Count > 0 && !(o.Mode == DownloadMode.Audio && o.MusicMode))
        {
            a.AddRange(["--write-subs", "--write-auto-subs", "--sub-langs", string.Join(',', langs)]);
            // Ses modunda gömülecek video yok; altyazı ayrı dosya olarak kalır
            if (o.Mode == DownloadMode.Video) a.Add("--embed-subs");
        }
        if (o.EmbedThumbnail) a.Add("--embed-thumbnail");
        if (o.EmbedMetadata)
        {
            a.Add("--embed-metadata");
            if (o.Playlist is { } p) a.AddRange(PlaylistTags(p));
        }
        if (o.Chapters) a.Add("--embed-chapters");
        if (o.SponsorBlock && s.SponsorCategories.Count > 0)
            a.AddRange(["--sponsorblock-remove", string.Join(',', s.SponsorCategories)]);
        if (o.SplitChapters)
            a.AddRange(["--split-chapters", "-o", "chapter:%(title)s/%(section_number)02d - %(section_title)s.%(ext)s"]);
        a.AddRange(ClipArgs(o.Clip));
        a.AddRange(MusicArgs(o));
        return a;
    }

    /// <summary>
    /// Playlist videoları tek tek (--no-playlist) indirildiği için yt-dlp listeyi bilmez; albüm ve parça
    /// numarasını --parse-metadata ile biz veririz. --embed-metadata bunları MP3/M4A etiketine yazar.
    /// </summary>
    public static List<string> PlaylistTags(PlaylistRef p)
    {
        // FROM şablon olarak değerlendirilir (% → %%) ve ilk kaçışsız ':' işaretinden bölünür (: → \:).
        // "Vidra|" öneki, yalnızca harften oluşan bir başlığın (ör. "Favoriler") alan adı sanılmasını önler.
        var title = p.Title.Replace("%", "%%").Replace(":", "\\:");
        return ["--parse-metadata", $"Vidra|{title}|{p.Index}:^Vidra\\|(?P<album>.*)\\|(?P<track_number>\\d+)$"];
    }

    /// <summary>
    /// Müzik modu (yalnızca ses): kapak 1:1 kırpılır, yeniden kodlanan formatlarda ses yüksekliği eşitlenir,
    /// otomatik "Sanatçı - Topic" kanallarında sanatçı adı temizlenir.
    /// </summary>
    public static List<string> MusicArgs(DownloadOptions o)
    {
        if (o.Mode != DownloadMode.Audio || !o.MusicMode) return [];
        var a = new List<string>();
        if (o.EmbedThumbnail)
            a.AddRange(["--convert-thumbnails", "jpg", "--ppa",
                "ThumbnailsConvertor+ffmpeg_o:-c:v mjpeg -qmin 1 -qscale:v 1 -vf crop=\"'if(gt(ih,iw),iw,ih)':'if(gt(iw,ih),ih,iw)'\""]);
        // M4A/Opus kaynağı çoğu zaman yeniden kodlanmadan kopyalanır; filtre ile akış kopyalama birlikte çalışmaz
        if (o.AudioFormat is AudioFormat.Mp3 or AudioFormat.Flac or AudioFormat.Wav)
            a.AddRange(["--ppa", "ExtractAudio+ffmpeg_o:-af loudnorm=I=-14:TP=-1.5:LRA=11"]);
        if (o.EmbedMetadata)
            a.AddRange([
                "--parse-metadata", "%(artist,uploader)s:^(?P<meta_artist>.+?)(?: - Topic)?$",
                // "Sanatçı - Şarkı" başlıkları (plak şirketi kanallarında yaygın) yukarıdakini ezer; eşleşmezse yt-dlp yalnızca uyarır
                "--parse-metadata", "title:^(?P<meta_artist>.+?) - (?P<meta_title>.+)$",
            ]);
        return a;
    }

    /// <summary>YouTube araması: ytsearchN: düz liste olarak (her sonuç için başlık, kanal, süre, kapak).</summary>
    public static List<string> Search(string query, int count, AppSettings s, ToolPaths t)
    {
        var a = Base(s, t);
        a.AddRange(["-J", "--flat-playlist", "--", $"ytsearch{count}:{query.Trim()}"]);
        return a;
    }

    /// <summary>Hem indirme hem probe için ortak: kodlama, bileşen yolları, ağ ve oturum.</summary>
    public static List<string> Base(AppSettings s, ToolPaths t)
    {
        var a = new List<string>
        {
            "--encoding", "utf-8",
            "--color", "never",
            "--ffmpeg-location", t.FfmpegDir,
            // YouTube JS doğrulamaları için harici runtime (yt-dlp ≥ 2025.11)
            "--js-runtimes", $"deno:{t.Deno}",
        };
        if (!string.IsNullOrWhiteSpace(s.Proxy)) a.AddRange(["--proxy", s.Proxy.Trim()]);
        a.AddRange(Cookies(s.Cookies));
        // Oturumla yoğun istek hesabın/IP'nin işaretlenmesine yol açabilir: araya bekleme koy
        if (s.PoliteMode && s.Cookies is not NoCookies)
            a.AddRange(["--sleep-requests", "1", "--sleep-interval", "2", "--max-sleep-interval", "6"]);
        return a;
    }

    public static List<string> Download(string url, DownloadOptions o, AppSettings s, ToolPaths t, string? outputTemplate = null)
    {
        var a = Base(s, t);
        a.AddRange([
            "--newline",
            "--progress", // --print, --quiet'i ima eder; ilerlemeyi yine de istiyoruz
            "--progress-template", $"download:{ProgressTemplate}",
            "--progress-template", $"postprocess:{PpTemplate}",
            "--print", $"after_move:{MarkDone}%(filepath)s",
            "--no-playlist", // tek video işi: URL'de list= olsa bile yalnızca bu video
            // Altyazı/kapak gibi yan adımlardaki hatalar (ör. 429) videoyu iptal etmesin.
            // Asıl indirme başarısız olursa yt-dlp yine 0 dışı kodla çıkar.
            "--ignore-errors",
            "--no-mtime",
            "-P", s.OutputDir,
            "-o", outputTemplate ?? s.OutputTemplate,
            "-N", Math.Max(1, s.Fragments).ToString(CultureInfo.InvariantCulture),
            "-R", s.Retries.ToString(CultureInfo.InvariantCulture),
        ]);
        if (!string.IsNullOrWhiteSpace(s.RateLimit)) a.AddRange(["-r", s.RateLimit!]);
        if (s.UseArchive) a.AddRange(["--download-archive", t.ArchiveFile]);
        a.AddRange(Format(o));
        a.AddRange(Extras(o, s));
        a.AddRange(["--", url]);
        return a;
    }

    public static List<string> Probe(string url, LinkKind kind, AppSettings s, ToolPaths t, int? limit = null)
    {
        var a = Base(s, t);
        a.Add("-J");
        if (kind == LinkKind.Video) a.Add("--no-playlist");
        else a.Add("--flat-playlist");
        if (limit is { } n) a.AddRange(["-I", $"1:{n}"]);
        a.AddRange(["--", url]);
        return a;
    }

    /// <summary>
    /// Oturum testi: abonelik akışının ilk öğesi yalnızca giriş yapılmışsa listelenir.
    /// </summary>
    public static List<string> SessionTest(AppSettings s, ToolPaths t)
    {
        var a = Base(s, t);
        a.AddRange(["--flat-playlist", "-I", "1", "--print", "%(channel)s", "--", ":ytsubs"]);
        return a;
    }

    /// <summary>Hata ayıklama için kopyalanabilir komut satırı.</summary>
    public static string ToCommandLine(string exe, IEnumerable<string> args, string? maskCookiePath = null)
    {
        static string Q(string a) =>
            a.Length > 0 && a.All(c => char.IsLetterOrDigit(c) || "@%+=:,./\\-_*".Contains(c)) ? a : "\"" + a.Replace("\"", "\\\"") + "\"";
        var list = args.Select(a => maskCookiePath is not null && a == maskCookiePath ? "<çerez dosyası>" : a);
        return string.Join(' ', new[] { Q(exe) }.Concat(list.Select(Q)));
    }
}
