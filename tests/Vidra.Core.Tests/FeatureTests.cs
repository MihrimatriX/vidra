using System.Text.Json;
using Vidra.Core.Models;
using Vidra.Core.Process;
using Vidra.Core.Queue;
using Vidra.Core.Storage;
using Vidra.Core.Tools;
using Vidra.Core.YtDlp;

namespace Vidra.Core.Tests;

/// <summary>Rakiplerden esinlenen yt-dlp özellikleri: dublaj, çoklu kesim, canlı bekleme, gizli mod, ek argümanlar.</summary>
public class FeatureArgsTests
{
    private static readonly DownloadOptions Opts = new();
    private static readonly ToolPaths Tools = new(@"C:\v\bin\yt-dlp.exe", @"C:\v\bin", @"C:\v\bin\deno.exe", @"C:\v\archive.txt");
    private static AppSettings Settings(string extra = "") => new() { OutputDir = @"C:\out", OutputTemplate = "%(title)s [%(id)s].%(ext)s", ExtraArgs = extra };
    private static string After(List<string> a, string flag) => a[a.IndexOf(flag) + 1];

    [Fact]
    public void Audio_language_prefers_dub_and_falls_back_to_default()
    {
        Assert.Equal("bv*+ba[language^=tr]/bv*+ba/b", After(YtDlpArgs.Format(Opts with { AudioLanguage = "tr" }), "-f"));
        Assert.Equal("ba[language^=es-US]/ba/b", After(YtDlpArgs.Format(Opts with { Mode = DownloadMode.Audio, AudioFormat = AudioFormat.Mp3, AudioLanguage = "es-US" }), "-f"));
        Assert.Equal("ba[language^=de][acodec^=mp4a]/ba[language^=de]/ba[acodec^=mp4a]/ba/b",
            After(YtDlpArgs.Format(Opts with { Mode = DownloadMode.Audio, AudioFormat = AudioFormat.M4a, AudioLanguage = "de" }), "-f"));
    }

    [Theory]
    [InlineData("tr]/b[")]
    [InlineData("en US")]
    [InlineData("")]
    public void Odd_language_codes_are_ignored(string code) => Assert.Equal("bv*+ba/b", After(YtDlpArgs.Format(Opts with { AudioLanguage = code }), "-f"));

    [Fact]
    public void Multiple_sections_get_distinct_names_and_skip_archive()
    {
        var o = Opts with { Clip = new Clip("0:10", "0:40"), Clips = [new Clip("1:00", "1:30", Precise: true), new Clip("x", "y")] };
        var a = YtDlpArgs.Download("u", o, Settings(), Tools);
        Assert.Equal(2, a.Count(x => x == "--download-sections"));
        Assert.Contains("*1:00-1:30", a);
        Assert.Single(a, x => x == "--force-keyframes-at-cuts");
        Assert.Equal("%(title)s [%(id)s] (%(section_start)d-%(section_end)d).%(ext)s", After(a, "-o"));
        Assert.DoesNotContain("--download-archive", a);
    }

    [Fact]
    public void Single_clip_keeps_name_but_skips_archive()
    {
        var a = YtDlpArgs.Download("u", Opts with { Clip = new Clip("0:10", "0:40") }, Settings(), Tools);
        Assert.Equal("%(title)s [%(id)s].%(ext)s", After(a, "-o"));
        Assert.DoesNotContain("--download-archive", a);
        Assert.Contains("--download-archive", YtDlpArgs.Download("u", Opts, Settings(), Tools));
    }

    [Fact]
    public void Live_wait_and_from_start()
    {
        var a = YtDlpArgs.Download("u", Opts with { WaitForLive = true, LiveFromStart = true }, Settings(), Tools);
        Assert.Equal("30-300", After(a, "--wait-for-video"));
        Assert.Contains("--live-from-start", a);
        Assert.DoesNotContain("--wait-for-video", YtDlpArgs.Download("u", Opts, Settings(), Tools));
    }

    [Fact]
    public void Incognito_never_writes_archive() =>
        Assert.DoesNotContain("--download-archive", YtDlpArgs.Download("u", Opts with { Incognito = true }, Settings(), Tools));

    [Fact]
    public void Extra_args_go_last_before_url()
    {
        var a = YtDlpArgs.Download("https://youtu.be/x", Opts, Settings("--limit-rate 2M --embed-subs --sub-format \"srt/best\""), Tools);
        Assert.Equal(["--limit-rate", "2M", "--embed-subs", "--sub-format", "srt/best", "--", "https://youtu.be/x"], a.TakeLast(7));
    }

    [Fact]
    public void Invalid_extra_args_are_not_used()
    {
        var a = YtDlpArgs.Download("u", Opts, Settings("-o x.mp4 --limit-rate 2M"), Tools);
        Assert.DoesNotContain("x.mp4", a);
        Assert.DoesNotContain("--limit-rate", a);
    }

    [Fact]
    public void Download_reports_title_before_download() =>
        Assert.Contains($"before_dl:{YtDlpArgs.MarkMeta}%(channel,uploader|)s|%(title)s", YtDlpArgs.Download("u", Opts, Settings(), Tools));
}

public class ExtraArgsTests
{
    [Fact]
    public void Tokenizes_quotes_and_keeps_windows_paths()
    {
        var r = ExtraArgs.Parse(@"--ppa ""ffmpeg:-ss 5"" --cookies 'C:\My Files\c.txt' --no-mtime");
        Assert.Null(r.Error);
        Assert.Equal(["--ppa", "ffmpeg:-ss 5", "--cookies", @"C:\My Files\c.txt", "--no-mtime"], r.Args);
    }

    [Theory]
    [InlineData("-o foo.%(ext)s", "-o")]
    [InlineData("--output=foo", "--output")]
    [InlineData("--print title", "--print")]
    [InlineData("-J", "-J")]
    [InlineData("--yes-playlist", "--yes-playlist")]
    public void Rejects_flags_vidra_manages(string text, string flag)
    {
        var r = ExtraArgs.Parse(text);
        Assert.Empty(r.Args);
        Assert.Contains(flag, r.Error);
    }

    [Fact]
    public void Rejects_bare_urls_and_separator_but_allows_url_values()
    {
        Assert.NotNull(ExtraArgs.Parse("https://youtu.be/other").Error);
        Assert.NotNull(ExtraArgs.Parse("-- x").Error);
        Assert.Null(ExtraArgs.Parse("--referer https://example.com").Error);
    }

    [Fact]
    public void Empty_is_fine() => Assert.Equal((0, (string?)null), (ExtraArgs.Parse("  ").Args.Count, ExtraArgs.Parse(null).Error));
}

public class ProbeFeatureTests
{
    private const string Json = """
    {"id":"abc","title":"T","duration":200,
     "formats":[
       {"format_id":"251-0","vcodec":"none","acodec":"opus","abr":130,"language":"en","language_preference":10,"format_note":"English original (default), medium"},
       {"format_id":"251-1","vcodec":"none","acodec":"opus","abr":140,"language":"es-US","language_preference":-10,"format_note":"Spanish (US) dubbed-auto, medium"},
       {"format_id":"140-1","vcodec":"none","acodec":"mp4a.40.2","abr":129,"language":"es-US","language_preference":-10},
       {"format_id":"137","vcodec":"avc1.640028","acodec":"none","height":1080,"filesize":1000}],
     "heatmap":[{"start_time":0,"end_time":50,"value":0.2},{"start_time":50,"end_time":100,"value":1.0},
                {"start_time":100,"end_time":150,"value":0.4},{"start_time":150,"end_time":200,"value":0.9}]}
    """;

    private static readonly VideoInfo V = ProbeParser.ParseVideo(JsonDocument.Parse(Json).RootElement);

    [Fact]
    public void Default_audio_track_wins_even_with_lower_bitrate()
    {
        Assert.Equal("251-0", V.Audio!.Id);
        Assert.Equal("en", V.Audio.Language);
    }

    [Fact]
    public void Audio_languages_list_original_first()
    {
        Assert.Equal(["en", "es-US"], V.AudioLanguages.Select(l => l.Code));
        Assert.True(V.AudioLanguages[0].Original);
        Assert.True(V.AudioLanguages[1].AutoDub);
        Assert.Equal(3, V.Audios.Count);
    }

    [Fact]
    public void Single_language_has_no_choices()
    {
        var v = ProbeParser.ParseVideo(JsonDocument.Parse("""{"id":"x","formats":[{"format_id":"1","vcodec":"none","acodec":"opus","language":"en"}]}""").RootElement);
        Assert.Empty(v.AudioLanguages);
    }

    [Fact]
    public void Heatmap_is_parsed() => Assert.Equal(4, V.Heatmap.Count);
}

public class HeatmapTests
{
    private static readonly HeatPoint[] P =
        [new(0, 50, 0.2), new(50, 100, 1.0), new(100, 150, 0.4), new(150, 200, 0.9)];

    [Fact]
    public void Best_single_window_is_the_peak() => Assert.Equal([(50.0, 80.0)], Heatmap.BestWindows(P, 30));

    [Fact]
    public void Several_windows_do_not_overlap_and_are_sorted()
    {
        var w = Heatmap.BestWindows(P, 30, 2);
        Assert.Equal([(50.0, 80.0), (150.0, 180.0)], w);
    }

    [Fact]
    public void Window_longer_than_video_is_whole_video() => Assert.Equal([(0.0, 200.0)], Heatmap.BestWindows(P, 500));

    [Fact]
    public void Average_is_overlap_weighted() => Assert.Equal(0.6, Heatmap.Average(P, 25, 75), 6);
}

public class AppUpdateTests
{
    [Theory]
    [InlineData("v0.4.0", "0.4.0")]
    [InlineData("0.4", "0.4.0")]
    [InlineData("V1.2.3-beta.1", "1.2.3")]
    public void Parses_tags(string tag, string expected) => Assert.Equal(Version.Parse(expected), AppUpdate.ParseVersion(tag));

    [Fact]
    public void Newer_only_when_greater()
    {
        var r = AppUpdate.Parse("""{"tag_name":"v0.4.0","html_url":"https://github.com/MihrimatriX/vidra/releases/tag/v0.4.0","body":"notlar"}""")!;
        Assert.True(AppUpdate.IsNewer(r, new Version(0, 3, 0, 0)));
        Assert.False(AppUpdate.IsNewer(r, new Version(0, 4, 0, 0)));
        Assert.Equal("notlar", r.Notes);
    }

    [Fact]
    public void Garbage_is_null() => Assert.Null(AppUpdate.Parse("""{"message":"Not Found"}"""));
}

public class LiveAndMetaTests
{
    [Theory]
    [InlineData("[youtube] abc: This live event will begin in 2 hours.")]
    [InlineData("[youtube] abc: Premieres in 35 minutes")]
    public void Upcoming_streams_are_recognized(string message) => Assert.Equal(ErrorKind.Upcoming, OutputParser.Classify(message));

    [Fact]
    public void Meta_line_splits_channel_and_title_with_pipes()
    {
        Assert.Equal(new MetaLine("Kanal", "Başlık | bölüm 2"), OutputParser.Parse($"{YtDlpArgs.MarkMeta}Kanal|Başlık | bölüm 2"));
        Assert.Equal(new MetaLine(null, "T"), OutputParser.Parse($"{YtDlpArgs.MarkMeta}|T"));
    }
}

public class IncognitoQueueTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vidra-i-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRunner _runner = new();
    private readonly DownloadQueue _queue;

    public IncognitoQueueTests()
    {
        var paths = new AppPaths(Path.Combine(_dir, "d"), Path.Combine(_dir, "l"));
        _queue = new DownloadQueue(_runner, new ToolManager(paths, _runner, new HttpClient()), () => new AppSettings { OutputDir = "/tmp" }, paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static async Task Until(Func<bool> cond)
    {
        for (var i = 0; i < 200 && !cond(); i++) await Task.Delay(10);
        Assert.True(cond());
    }

    [Fact]
    public async Task Incognito_jobs_leave_no_history()
    {
        _queue.Incognito = true;
        _queue.Enqueue([new NewJob("https://youtu.be/a", "https://youtu.be/a", new DownloadOptions())]);
        var job = _queue.Jobs[0];
        Assert.True(job.Options.Incognito);
        Assert.DoesNotContain("--download-archive", _runner.Started[0].Args);
        _runner.Started[0].Exit(0);
        await Until(() => _queue.Jobs.Count == 0);
        Assert.Empty(_queue.History);
    }

    [Fact]
    public async Task Meta_line_replaces_link_title()
    {
        _queue.Enqueue([new NewJob("https://youtu.be/a", "https://youtu.be/a", new DownloadOptions())]);
        var job = _queue.Jobs[0];
        _runner.Started[0].Emit($"{YtDlpArgs.MarkMeta}Blender|Big Buck Bunny");
        Assert.Equal("Big Buck Bunny", job.Title);
        Assert.Equal("Blender", job.Channel);
        _runner.Started[0].Exit(0);
        await Until(() => _queue.History.Count == 1);
        Assert.Equal("Big Buck Bunny", _queue.History[0].Title);
    }
}
