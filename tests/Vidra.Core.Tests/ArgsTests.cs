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
        Assert.Equal(["-f", "bv*+ba/b", "-S", "res:1080,vcodec:h264,acodec:m4a", "--merge-output-format", "mp4"],
            YtDlpArgs.Format(Opts with { Container = ContainerFormat.Mp4 }));
    }

    [Fact]
    public void Mkv_best_has_no_sort() =>
        Assert.Equal(["-f", "bv*+ba/b", "--merge-output-format", "mkv"], YtDlpArgs.Format(Opts with { Height = null, Container = ContainerFormat.Mkv }));

    [Fact]
    public void Original_is_the_default_and_forces_neither_container_nor_codec()
    {
        Assert.Equal(ContainerFormat.Original, Opts.Container);
        Assert.Equal(["-f", "bv*+ba/b", "-S", "res:1080"], YtDlpArgs.Format(Opts));
        Assert.Equal(["-f", "bv*+ba/b"], YtDlpArgs.Format(Opts with { Height = null }));
    }

    /// <summary>yt-dlp -S alanlarını varsayılan sıralamanın önüne koyar: "res" olmadan kodek tercihi çözünürlüğü ezerdi.</summary>
    [Fact]
    public void Best_quality_keeps_resolution_ahead_of_codec_preference()
    {
        Assert.Equal("res,vcodec:h264,acodec:m4a", After(YtDlpArgs.Format(Opts with { Height = null, Container = ContainerFormat.Mp4 }), "-S"));
        Assert.Equal("res,fps:30", After(YtDlpArgs.Format(Opts with { Height = null, MaxFps30 = true }), "-S"));
    }

    [Fact]
    public void Webm_falls_back_to_mkv_for_incompatible_codecs() =>
        Assert.Equal("webm/mkv", After(YtDlpArgs.Format(Opts with { Container = ContainerFormat.Webm, Codec = CodecPreference.H264 }), "--merge-output-format"));

    [Fact]
    public void Original_audio_extracts_without_converting() =>
        Assert.Equal(["-f", "ba/b", "-x"], YtDlpArgs.Format(Opts with { Mode = DownloadMode.Audio, AudioFormat = AudioFormat.Original }));

    [Fact]
    public void M4a_and_opus_pick_matching_stream_so_ffmpeg_copies()
    {
        Assert.Equal("ba[acodec^=mp4a]/ba/b", After(YtDlpArgs.Format(Opts with { Mode = DownloadMode.Audio, AudioFormat = AudioFormat.M4a }), "-f"));
        Assert.Equal("ba[acodec=opus]/ba/b", After(YtDlpArgs.Format(Opts with { Mode = DownloadMode.Audio, AudioFormat = AudioFormat.Opus }), "-f"));
    }

    [Fact]
    public void Reencodes_lists_only_lossy_steps()
    {
        Assert.Empty(Conversion.Reencodes(Opts));
        Assert.Empty(Conversion.Reencodes(Opts with { Container = ContainerFormat.Mp4, SponsorBlock = true, Clip = new Clip("1:00", "2:00") }));
        Assert.Empty(Conversion.Reencodes(Opts with { Mode = DownloadMode.Audio, AudioFormat = AudioFormat.M4a, MusicMode = true }));
        Assert.Equal(["ses MP3 biçimine dönüştürülür", "ses yüksekliği eşitlenir"],
            Conversion.Reencodes(Opts with { Mode = DownloadMode.Audio, AudioFormat = AudioFormat.Mp3, MusicMode = true }));
        Assert.Single(Conversion.Reencodes(Opts with { Clip = new Clip("1:00", "2:00", Precise: true) }));
        Assert.Empty(Conversion.Reencodes(Opts with { Clip = new Clip("x", "2:00", Precise: true) })); // geçersiz klip uygulanmaz
        // Videoda AAC akışı yoksa M4A, Opus'tan dönüştürülerek elde edilir
        Assert.Empty(Conversion.Reencodes(Opts with { Mode = DownloadMode.Audio, AudioFormat = AudioFormat.M4a }, "mp4a.40.2"));
        Assert.Equal(["ses M4A biçimine dönüştürülür"], Conversion.Reencodes(Opts with { Mode = DownloadMode.Audio, AudioFormat = AudioFormat.M4a }, "opus"));
        Assert.Empty(Conversion.Reencodes(Opts with { Mode = DownloadMode.Audio, AudioFormat = AudioFormat.Original }, "opus"));
    }

    [Theory]
    [InlineData(ContainerFormat.Original, "avc1.640028", "mp4a.40.2", true, "mp4")]
    [InlineData(ContainerFormat.Original, "vp09.00.40.08", "opus", false, "webm")]
    [InlineData(ContainerFormat.Original, "av01.0.08M.08", "opus", true, "mkv")] // WebM kapak taşımaz
    [InlineData(ContainerFormat.Original, "avc1.640028", "opus", false, "mkv")]
    [InlineData(ContainerFormat.Webm, "avc1.640028", "opus", false, "mkv")]
    [InlineData(ContainerFormat.Mp4, "vp9", "opus", true, "mp4")]
    public void Predicts_video_extension(ContainerFormat c, string v, string a, bool thumb, string ext) =>
        Assert.Equal(ext, Conversion.PredictExt(Opts with { Container = c, EmbedThumbnail = thumb }, v, a));

    [Fact]
    public void Predicts_audio_extension()
    {
        var o = Opts with { Mode = DownloadMode.Audio, AudioFormat = AudioFormat.Original };
        Assert.Equal("opus", Conversion.PredictExt(o, null, "opus"));
        Assert.Equal("m4a", Conversion.PredictExt(o, null, "mp4a"));
        Assert.Equal("mp3", Conversion.PredictExt(o with { AudioFormat = AudioFormat.Mp3 }, null, "opus"));
    }

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
        Assert.Equal(["--download-sections", "*2:15-5:40"], YtDlpArgs.ClipArgs(new Clip("2:15", "5:40")));
        Assert.Equal(["--download-sections", "*2:15-5:40", "--force-keyframes-at-cuts"], YtDlpArgs.ClipArgs(new Clip("2:15", "5:40", Precise: true)));
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
