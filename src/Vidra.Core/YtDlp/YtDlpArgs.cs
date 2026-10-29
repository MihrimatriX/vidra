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

    /// <summary>
    /// Kalite/format seçimi. Sıralama (-S) -f filtrelerinden daha dayanıklıdır.
    /// Video ve ses her zaman kopyalanarak birleştirilir; yeniden kodlama yalnızca ses dönüşümünde
    /// ve kesin kesimde olur (bkz. Conversion.Reencodes).
    /// </summary>
    public static List<string> Format(DownloadOptions o)
    {
        if (o.Mode == DownloadMode.Audio)
        {
            // M4A/Opus istenirse o kodekteki akışı seç: ffmpeg yeniden kodlamak yerine kopyalar
            var pick = o.AudioFormat switch
            {
                AudioFormat.M4a => "ba[acodec^=mp4a]/ba/b",
                AudioFormat.Opus => "ba[acodec=opus]/ba/b",
                _ => "ba/b",
            };
            var a = new List<string> { "-f", pick, "-x" };
            // --audio-format verilmezse yt-dlp "best" kullanır: akışın kendi kodeği, dönüştürme yok
            if (o.AudioFormat != AudioFormat.Original) a.AddRange(["--audio-format", o.AudioFormat.Ext()]);
            // Kayıplı formatlarda en iyi VBR (yalnızca dönüştürme gerekirse kullanılır); kayıpsızda anlamsız
            if (o.AudioFormat is AudioFormat.Mp3 or AudioFormat.M4a or AudioFormat.Opus) a.AddRange(["--audio-quality", "0"]);
            return a;
        }

        // Sıra önemli: çözünürlük → kare hızı → kodek. yt-dlp -S alanlarını kendi varsayılan sıralamasının
        // önüne koyar; "res" yazılmazsa kodek tercihi çözünürlüğü ezer ("En iyi" + MP4 → 4K yerine 1080p H.264).
        var sort = new List<string> { o.Height is { } h ? $"res:{h}" : "res" };
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
        // Tek başına "res" zaten yt-dlp'nin varsayılanı
        if (sort.Count > 1 || o.Height is not null) args.AddRange(["-S", string.Join(',', sort)]);
        var merge = o.Container switch
        {
            ContainerFormat.Original => null,
            // WebM yalnızca VP9/AV1 + Opus taşır; H.264 seçilirse birleştirme hata vermesin, MKV'ye düşsün
            ContainerFormat.Webm => "webm/mkv",
            _ => o.Container.Ext(),
        };
        if (merge is not null) args.AddRange(["--merge-output-format", merge]);
        return args;
    }

    public static List<string> Cookies(CookieSource c) => c switch
    {
        BrowserCookies b => ["--cookies-from-browser", b.Profile is { Length: > 0 } p ? $"{b.Browser.Arg()}:{p}" : b.Browser.Arg()],
        FileCookies f => ["--cookies", f.Path],
        AppLoginCookies a => ["--cookies", a.Path],
        ExtensionCookies x => ["--cookies", x.Path],
        _ => [],
    };

    public static List<string> ClipArgs(Clip? clip)
    {
        if (clip is null || !TimeText.IsTime(clip.Start) || !TimeText.IsTime(clip.End)) return [];
        var a = new List<string> { "--download-sections", $"*{clip.Start.Trim()}-{clip.End.Trim()}" };
        // Anahtar karede kesmek kopyalar ama birkaç saniye kayabilir. Kesin kesim kesim noktalarına anahtar kare
        // ekler, bunun için videoyu yeniden kodlar; bayrak geneldir, SponsorBlock ve bölümlere ayırmayı da etkiler.
        if (clip.Precise) a.Add("--force-keyframes-at-cuts");
        return a;
    }

    public static List<string> Extras(DownloadOptions o, AppSettings s)
    {
        var a = new List<string>();
        var langs = o.SubLangs ?? s.SubLangs;
        // Müzik dosyasının yanında altyazı dosyası istenmez
        var subs = o.Subtitles && langs.Count > 0 && !(o.Mode == DownloadMode.Audio && o.MusicMode);
        if (subs || (o.Transcript && langs.Count > 0))
        {
            a.AddRange(["--write-subs", "--write-auto-subs", "--sub-langs", string.Join(',', langs)]);
            // Döküm VTT'den çıkarılır; --write-subs açıkken gömme sonrası dosyalar silinmez
            if (o.Transcript) a.AddRange(["--sub-format", "vtt/best"]);
            // Ses modunda gömülecek video yok; altyazı ayrı dosya olarak kalır
            if (subs && o.Mode == DownloadMode.Video) a.Add("--embed-subs");
        }
        if (o.Comments) a.AddRange(["--write-info-json", "--write-comments"]);
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
