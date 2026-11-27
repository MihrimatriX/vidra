using Vidra.Core.Models;
using Vidra.Core.Process;
using Vidra.Core.Storage;
using Vidra.Core.Tools;
using Vidra.Core.YtDlp;

namespace Vidra.Core.Tests;

/// <summary>"Yalnızca 360p geliyor": uyarıların okunması, yedek istemcilerle yeniden analiz ve indirmenin aynı yolu kullanması.</summary>
public class FormatFallbackTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "vidra-ff-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    private const string Only18 = """
        {"id":"abc123def45","title":"Video","formats":[
          {"format_id":"18","vcodec":"avc1.42001E","acodec":"mp4a.40.2","height":360,"width":640,"filesize":1000}]}
        """;

    private const string Full = """
        {"id":"abc123def45","title":"Video","formats":[
          {"format_id":"18","vcodec":"avc1.42001E","acodec":"mp4a.40.2","height":360,"width":640,"filesize":1000},
          {"format_id":"137","vcodec":"avc1.640028","acodec":"none","height":1080,"width":1920,"filesize":9000},
          {"format_id":"140","vcodec":"none","acodec":"mp4a.40.2","abr":128,"filesize":500}]}
        """;

    private const string SabrWarning =
        "WARNING: [youtube] abc123def45: Some web client https formats have been skipped as they are missing a URL. YouTube is forcing SABR streaming for this client.\n";

    private sealed class Runner(Func<IReadOnlyList<string>, ProcessResult> run) : IProcessRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public IRunningProcess Start(string exe, IReadOnlyList<string> args, Action<string> onLine) => throw new NotSupportedException();

        public Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct = default)
        {
            Calls.Add(args);
            return Task.FromResult(run(args));
        }
    }

    private static bool Tv(IReadOnlyList<string> a) => a.Any(x => x.StartsWith("youtube:player_client=tv", StringComparison.Ordinal));

    private (YtDlpService Svc, ToolManager Tools, Runner Runner) Make(Func<IReadOnlyList<string>, ProcessResult> run, AppSettings? s = null)
    {
        var paths = new AppPaths(Path.Combine(_dir, "d"), Path.Combine(_dir, "l"));
        var runner = new Runner(run);
        var tools = new ToolManager(paths, runner, new HttpClient());
        var settings = s ?? new AppSettings();
        return (new YtDlpService(runner, tools, () => settings), tools, runner);
    }

    [Fact]
    public void Reads_reasons_from_ytdlp_warnings()
    {
        var w = FormatDiagnostics.Warnings(
            "[youtube] x: Downloading webpage\n" + SabrWarning +
            "WARNING: [youtube] x: n challenge solving failed: Some formats may be missing. Ensure you have a supported JavaScript runtime\n" +
            "WARNING: [youtube] x: web_creator client https formats require a GVS PO Token which was not provided.\n" + SabrWarning);
        Assert.Equal(3, w.Count);
        Assert.Equal([FormatIssue.Sabr, FormatIssue.ChallengeFailed, FormatIssue.PoToken], FormatDiagnostics.Detect(w));
        Assert.Contains(FormatIssue.NoJsRuntime, FormatDiagnostics.Detect(["No supported JavaScript runtime could be found."]));
    }

    [Fact]
    public void Only_format_18_counts_as_limited_but_a_real_360p_video_does_not()
    {
        static VideoInfo V(params (string Id, int H)[] f) => new()
        {
            Id = "v", Url = "u", Title = "t",
            Qualities = f.GroupBy(x => x.H).Select(g => new QualityOption(g.Key, null, false, null,
                g.Select(x => new StreamInfo(x.Id, CodecFamily.H264, "avc1", null, null, null, null, false)).ToList())).ToList(),
        };
        Assert.True(FormatDiagnostics.LooksLimited(V(("18", 360)), []));
        // Eski videolar gerçekten 360p'dir ama DASH akışları da gelir
        Assert.False(FormatDiagnostics.LooksLimited(V(("18", 360), ("134", 360), ("243", 360)), []));
        Assert.True(FormatDiagnostics.LooksLimited(V(("18", 360), ("134", 360)), [FormatIssue.Sabr]));
        Assert.False(FormatDiagnostics.LooksLimited(V(("18", 360), ("137", 1080)), [FormatIssue.Sabr]));
        Assert.False(FormatDiagnostics.LooksLimited(V(("18", 360)) with { Live = true }, []));
    }

    [Fact]
    public async Task Retries_with_tv_clients_and_download_uses_the_same_path()
    {
        var (svc, tools, runner) = Make(a => Tv(a) ? new(0, Full, "") : new(0, Only18, SabrWarning));

        var v = Assert.IsType<VideoInfo>(await svc.ProbeAsync("https://www.youtube.com/watch?v=abc123def45"));

        Assert.Equal(2, runner.Calls.Count);
        Assert.Contains("--remote-components", runner.Calls[1]);
        Assert.Equal(1080, v.Qualities[0].Height);
        Assert.Null(v.FormatNote);
        Assert.Equal(FallbackStep.TvClients, tools.Fallback.For("abc123def45"));
        // TV yolu bir süre tüm işlere uygulanır (playlistteki diğer videolar da aynı kısıta takılır)
        Assert.Equal(FallbackStep.TvClients, tools.Fallback.For("baska-video"));
        var dl = YtDlpArgs.Download("u", new DownloadOptions(), new AppSettings(), tools.Paths, fallback: tools.Fallback.For("abc123def45"));
        Assert.True(Tv(dl));

        // Sonraki analiz doğrudan TV yoluyla başlar: ikinci deneme yok
        await svc.ProbeAsync("https://www.youtube.com/watch?v=abc123def45");
        Assert.Equal(3, runner.Calls.Count);
    }

    [Fact]
    public async Task Falls_back_to_no_cookies_only_for_that_video()
    {
        var cookie = Path.Combine(_dir, "c.txt");
        File.WriteAllText(cookie, "# Netscape HTTP Cookie File\n");
        var s = new AppSettings { Cookies = new FileCookies(cookie) };
        var (svc, tools, runner) = Make(a => a.Contains("--cookies") ? new(0, Only18, SabrWarning) : new(0, Full, ""), s);

        var v = Assert.IsType<VideoInfo>(await svc.ProbeAsync("https://youtu.be/abc123def45"));

        Assert.Equal(3, runner.Calls.Count);
        Assert.DoesNotContain("--cookies", runner.Calls[2]);
        Assert.Equal(1080, v.Qualities[0].Height);
        Assert.Equal(FallbackStep.NoCookies, tools.Fallback.For("abc123def45"));
        Assert.Equal(FallbackStep.None, tools.Fallback.For("baska-video"));
        Assert.DoesNotContain("--cookies", YtDlpArgs.Download("u", new DownloadOptions(), s, tools.Paths, fallback: FallbackStep.NoCookies));
    }

    [Fact]
    public async Task Explains_why_when_nothing_helps()
    {
        var (svc, tools, _) = Make(_ => new(0, Only18, SabrWarning));

        var v = Assert.IsType<VideoInfo>(await svc.ProbeAsync("https://www.youtube.com/watch?v=abc123def45"));

        Assert.NotNull(v.FormatNote);
        Assert.Contains("360p", v.FormatNote);
        Assert.Contains("SABR", v.FormatNote);
        Assert.Contains("SABR", v.FormatDetail);
        Assert.Equal(FallbackStep.None, tools.Fallback.For("abc123def45"));
    }

    [Fact]
    public async Task Probe_error_carries_raw_stderr_as_detail()
    {
        var (svc, _, _) = Make(_ => new(1, "", "[youtube] x: Downloading webpage\n[debug] noise\nERROR: [youtube] abc123def45: Video unavailable. This video is private\n"));

        var e = await Assert.ThrowsAsync<ProbeException>(() => svc.ProbeAsync("https://www.youtube.com/watch?v=abc123def45"));

        Assert.Contains("ERROR: [youtube] abc123def45: Video unavailable", e.Detail);
        Assert.DoesNotContain("[debug]", e.Detail);
        Assert.Contains("Analiz hatası", AppLog.Last);
    }

    [Fact]
    public void Global_fallback_expires()
    {
        var now = DateTimeOffset.UnixEpoch;
        var f = new FormatFallback(() => now);
        f.Remember("a", FallbackStep.TvClients);
        Assert.Equal(FallbackStep.TvClients, f.For("b"));
        now += FormatFallback.GlobalTtl + TimeSpan.FromMinutes(1);
        Assert.Equal(FallbackStep.None, f.For("b"));
        Assert.Equal(FallbackStep.TvClients, f.For("a"));
    }
}
