using Vidra.Core.Models;
using Vidra.Core.YtDlp;

namespace Vidra.Core.Tests;

public class ArgsTests
{
    private static readonly DownloadOptions Opts = new();
    private static readonly ToolPaths Tools = new(@"C:\v\bin\yt-dlp.exe", @"C:\v\bin", @"C:\v\bin\deno.exe", @"C:\v\archive.txt");

    private static AppSettings Settings(CookieSource? cookies = null) => new()
    {
        OutputDir = @"C:\Users\a\Videos\Vidra",
        OutputTemplate = "%(title)s.%(ext)s",
        Cookies = cookies ?? new BrowserCookies(BrowserKind.Firefox),
        SponsorCategories = ["sponsor", "selfpromo"],
    };

    private static string After(List<string> a, string flag) => a[a.IndexOf(flag) + 1];

    [Fact]
    public void Mp4_1080_prefers_h264_and_aac()
    {
        Assert.Equal(["-f", "bv*+ba/b", "-S", "res:1080,vcodec:h264,acodec:m4a", "--merge-output-format", "mp4"], YtDlpArgs.Format(Opts));
    }

    [Fact]
    public void Mkv_best_has_no_sort() =>
        Assert.Equal(["-f", "bv*+ba/b", "--merge-output-format", "mkv"], YtDlpArgs.Format(Opts with { Height = null, Container = ContainerFormat.Mkv }));

    [Fact]
    public void Sort_order_is_resolution_fps_codec() =>
        Assert.Equal("res:1080,fps:30,vcodec:av01",
            After(YtDlpArgs.Format(Opts with { Container = ContainerFormat.Mkv, Codec = CodecPreference.Av1, MaxFps30 = true }), "-S"));

    [Fact]
    public void Flac_has_no_quality_flag() =>
        Assert.Equal(["-f", "ba/b", "-x", "--audio-format", "flac"], YtDlpArgs.Format(Opts with { Mode = DownloadMode.Audio, AudioFormat = AudioFormat.Flac }));

    [Fact]
    public void Cookies_variants()
    {
        Assert.Equal(["--cookies-from-browser", "chrome:Profile 1"], YtDlpArgs.Cookies(new BrowserCookies(BrowserKind.Chrome, "Profile 1")));
        Assert.Equal(["--cookies", "c.txt"], YtDlpArgs.Cookies(new AppLoginCookies("c.txt", DateTimeOffset.Now, null)));
        Assert.Empty(YtDlpArgs.Cookies(new NoCookies()));
    }

    [Fact]
    public void Download_puts_url_last_after_double_dash()
    {
        var a = YtDlpArgs.Download("https://youtu.be/x", Opts, Settings(), Tools);
        Assert.Equal(["--", "https://youtu.be/x"], a[^2..]);
        Assert.Equal(@"C:\v\archive.txt", After(a, "--download-archive"));
        Assert.Equal(@"C:\v\bin", After(a, "--ffmpeg-location"));
        Assert.Equal(@"deno:C:\v\bin\deno.exe", After(a, "--js-runtimes"));
        Assert.Contains("--ignore-errors", a);
        Assert.Contains("--embed-subs", a);
        Assert.Equal("tr,en", After(a, "--sub-langs"));
    }

    [Fact]
    public void Polite_mode_only_with_cookies()
    {
        Assert.Contains("--sleep-requests", YtDlpArgs.Download("u", Opts, Settings(), Tools));
        Assert.DoesNotContain("--sleep-requests", YtDlpArgs.Download("u", Opts, Settings(new NoCookies()), Tools));
    }

    [Fact]
    public void Sponsorblock_uses_setting_categories() =>
        Assert.Equal("sponsor,selfpromo", After(YtDlpArgs.Download("u", Opts with { SponsorBlock = true }, Settings(), Tools), "--sponsorblock-remove"));

    [Fact]
    public void Per_job_sub_langs_override() =>
        Assert.Equal("de", After(YtDlpArgs.Download("u", Opts with { SubLangs = ["de"] }, Settings(), Tools), "--sub-langs"));

    [Fact]
    public void Audio_mode_does_not_embed_subs() =>
        Assert.DoesNotContain("--embed-subs", YtDlpArgs.Download("u", Opts with { Mode = DownloadMode.Audio }, Settings(), Tools));

    [Fact]
    public void Output_template_override() =>
        Assert.Equal("X/%(title)s.%(ext)s", After(YtDlpArgs.Download("u", Opts, Settings(), Tools, "X/%(title)s.%(ext)s"), "-o"));

    [Fact]
    public void Clip_args()
    {
        Assert.Equal(["--download-sections", "*2:15-5:40", "--force-keyframes-at-cuts"], YtDlpArgs.ClipArgs(new Clip("2:15", "5:40")));
        Assert.Empty(YtDlpArgs.ClipArgs(new Clip("abc", "5:40")));
    }

    [Fact]
    public void Probe_flat_for_lists_with_limit()
    {
        var a = YtDlpArgs.Probe(":ytsubs", LinkKind.Feed, Settings(), Tools, 50);
        Assert.Contains("--flat-playlist", a);
        Assert.Equal("1:50", After(a, "-I"));
        Assert.Contains("--no-playlist", YtDlpArgs.Probe("u", LinkKind.Video, Settings(), Tools));
    }

    [Fact]
    public void Command_line_masks_cookie_path()
    {
        var cmd = YtDlpArgs.ToCommandLine("yt-dlp", ["--cookies", @"C:\secret\c.txt", "--", "https://x y"], @"C:\secret\c.txt");
        Assert.Equal("yt-dlp --cookies \"<çerez dosyası>\" -- \"https://x y\"", cmd);
    }

    [Fact]
    public void Playlist_item_without_metadata_gets_no_tags() =>
        Assert.DoesNotContain("--parse-metadata", YtDlpArgs.Extras(Opts with { EmbedMetadata = false, Playlist = new("Liste", 1) }, Settings()));

    /// <summary>
    /// yt-dlp --parse-metadata "FROM:TO" değerini ilk kaçışsız ':' işaretinden böler (MetadataFromFieldPP.to_action),
    /// FROM'u çıktı şablonu gibi değerlendirir (% → %%), TO'yu regex olarak uygular. Burada aynısını taklit ediyoruz.
    /// </summary>
    [Fact]
    public void Playlist_tags_survive_ytdlp_parsing()
    {
        var a = YtDlpArgs.Extras(Opts with { Playlist = new("Yol: %100 Rock", 7) }, Settings());
        var tags = new Dictionary<string, string>();
        for (var i = a.IndexOf("--parse-metadata"); i >= 0; i = a.IndexOf("--parse-metadata", i + 1))
        {
            var m = System.Text.RegularExpressions.Regex.Match(a[i + 1], @"^(?<in>.*?)(?<!\\):(?<out>.+)$");
            Assert.True(m.Success, a[i + 1]);
            var from = m.Groups["in"].Value.Replace("\\:", ":");
            Assert.DoesNotMatch("^[a-zA-Z_]+$", from); // yalnızca harfse yt-dlp bunu alan adı sanar
            var data = from.Replace("%%", "%");        // alan içermeyen şablonun değerlendirilmiş hâli
            var r = System.Text.RegularExpressions.Regex.Match(data, m.Groups["out"].Value.Replace("(?P<", "(?<"));
            foreach (System.Text.RegularExpressions.Group g in r.Groups)
                if (r.Success && !int.TryParse(g.Name, out _)) tags[g.Name] = g.Value;
        }
        Assert.Equal("Yol: %100 Rock", tags.GetValueOrDefault("album"));
        Assert.Equal("7", tags.GetValueOrDefault("track_number"));
    }

    [Fact]
    public void Music_mode_mp3_crops_cover_normalizes_and_cleans_artist()
    {
        var a = YtDlpArgs.Extras(Opts with { Mode = DownloadMode.Audio, AudioFormat = AudioFormat.Mp3, MusicMode = true }, Settings());
        Assert.Equal("jpg", After(a, "--convert-thumbnails"));
        Assert.Contains(a, x => x.StartsWith("ThumbnailsConvertor+ffmpeg_o:", StringComparison.Ordinal) && x.Contains("crop="));
        Assert.Contains(a, x => x.StartsWith("ExtractAudio+ffmpeg_o:-af loudnorm", StringComparison.Ordinal));
        Assert.Contains(a, x => x.StartsWith("%(artist,uploader)s:", StringComparison.Ordinal));
        Assert.Contains("title:^(?P<meta_artist>.+?) - (?P<meta_title>.+)$", a);
        Assert.DoesNotContain("--write-subs", a);
    }

    [Fact]
    public void Music_mode_skips_loudnorm_for_stream_copy_formats_and_video()
    {
        var m4a = YtDlpArgs.Extras(Opts with { Mode = DownloadMode.Audio, AudioFormat = AudioFormat.M4a, MusicMode = true }, Settings());
        Assert.DoesNotContain(m4a, x => x.Contains("loudnorm"));
        Assert.Empty(YtDlpArgs.MusicArgs(Opts with { MusicMode = true })); // video modunda etkisiz
    }

    [Fact]
    public void Profile_sets_format_but_keeps_per_video_choices()
    {
        var mine = Opts with { SubLangs = ["de"], Clip = new Clip("0:10", "0:20") };
        var o = DownloadProfile.BuiltIns[0].ApplyTo(mine);
        Assert.Equal(DownloadMode.Audio, o.Mode);
        Assert.True(o.MusicMode);
        Assert.Equal(["de"], o.SubLangs);
        Assert.Equal(new Clip("0:10", "0:20"), o.Clip);
    }

    [Fact]
    public void Search_uses_ytsearch_flat() =>
        Assert.Equal(["-J", "--flat-playlist", "--", "ytsearch24:lofi beats"],
            YtDlpArgs.Search("  lofi beats ", 24, Settings(), Tools).TakeLast(4));

    [Fact]
    public void History_stats_count_only_done_and_bucket_days()
    {
        var now = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        HistoryEntry E(string ch, JobStatus st, int daysAgo, long size, DownloadMode mode = DownloadMode.Video) => new()
        {
            Id = Guid.NewGuid().ToString(), Url = "u", Title = "t", Channel = ch, Status = st, Size = size, Duration = 60,
            Options = Opts with { Mode = mode }, FinishedAt = now.AddDays(-daysAgo),
        };
        var s = Vidra.Core.Queue.HistoryStats.From(
            [E("A", JobStatus.Done, 0, 100), E("A", JobStatus.Done, 1, 50, DownloadMode.Audio), E("B", JobStatus.Done, 20, 10), E("C", JobStatus.Error, 0, 999)],
            now);
        Assert.Equal(3, s.Count);
        Assert.Equal(160, s.Bytes);
        Assert.Equal(2, s.WeekCount);
        Assert.Equal(1, s.AudioCount);
        Assert.Equal(14, s.Days.Count);
        Assert.Equal(100, s.Days[^1].Bytes); // bugün en sonda
        Assert.Equal("A", s.TopChannels[0].Channel);
    }
}
