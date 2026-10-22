using Vidra.Core.Models;
using Vidra.Core.Storage;
using Vidra.Core.YtDlp;

namespace Vidra.Core.Tests;

public class StorageTests
{
    [Fact]
    public async Task Settings_roundtrip_keeps_polymorphic_cookies_and_records()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vidra-s-{Guid.NewGuid():N}.json");
        var s = new AppSettings
        {
            Cookies = new AppLoginCookies("C:\\c.txt", DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), null),
            Defaults = new DownloadOptions { Height = null, Codec = CodecPreference.Av1, SubLangs = ["de"], Clip = new Clip("1:00", "2:00") },
            Theme = ThemeMode.Light,
        };
        await JsonStore.WriteAsync(path, s);
        var back = JsonStore.Read<AppSettings>(path)!;
        var c = Assert.IsType<AppLoginCookies>(back.Cookies);
        Assert.Equal("C:\\c.txt", c.Path);
        Assert.Null(back.Defaults.Height);
        Assert.Equal(CodecPreference.Av1, back.Defaults.Codec);
        Assert.Equal(["de"], back.Defaults.SubLangs);
        Assert.Equal(new Clip("1:00", "2:00"), back.Defaults.Clip);
        Assert.Equal(ThemeMode.Light, back.Theme);
        File.Delete(path);
    }

    [Fact]
    public void Corrupt_file_returns_default_and_backs_up()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vidra-c-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ bozuk");
        Assert.Null(JsonStore.Read<AppSettings>(path));
        Assert.True(File.Exists(path + ".bad"));
        File.Delete(path);
        File.Delete(path + ".bad");
    }

    [Fact]
    public void Archive_reads_youtube_ids()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vidra-a-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "youtube abc\nyoutube def\nvimeo 123\n\n");
        Assert.Equal(["abc", "def"], new ArchiveStore(path).ReadIds().Order());
        File.Delete(path);
    }
}

/// <summary>
/// Gerçek yt-dlp ile uçtan uca: VIDRA_BIN_DIR ayarlıysa çalışır (yt-dlp, ffmpeg, deno o klasörde).
/// Ağ erişimi gerekir; CI'da atlanır.
/// </summary>
public class LiveYtDlpTests
{
    private static string? Bin => Environment.GetEnvironmentVariable("VIDRA_BIN_DIR");

    [Fact]
    public async Task Probe_real_video()
    {
        if (string.IsNullOrEmpty(Bin)) return;
        var dir = Path.Combine(Path.GetTempPath(), "vidra-live-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(Path.Combine(dir, "d"), Path.Combine(dir, "l"));
        var runner = new Vidra.Core.Process.ProcessRunner();
        var tools = new Vidra.Core.Tools.ToolManager(paths, runner, new HttpClient());
        var svc = new YtDlpService(runner, tools, () => new AppSettings());

        object r;
        try { r = await svc.ProbeAsync("https://www.youtube.com/watch?v=jNQXAC9IVRw"); }
        catch (ProbeException e) when (e.Kind is ErrorKind.RateLimited or ErrorKind.BotCheck or ErrorKind.Network)
        {
            // Veri merkezi IP'leri YouTube'dan sık 429 alır: ortam sorunu, kod değil
            return;
        }
        var v = Assert.IsType<VideoInfo>(r);
        Assert.Equal("Me at the zoo", v.Title);
        Assert.NotEmpty(v.Qualities);
        Assert.NotNull(v.Audio);
        Assert.Equal(3, v.Chapters.Count);

        var status = await tools.StatusAsync();
        Assert.All(status, s => Assert.True(s.Installed, s.Name));
        Directory.Delete(dir, true);
    }
}

public class ProbeErrorTests
{
    private sealed class StubRunner(string stdout, string stderr, int code) : Vidra.Core.Process.IProcessRunner
    {
        public Vidra.Core.Process.IRunningProcess Start(string exe, IReadOnlyList<string> args, Action<string> onLine) => throw new NotSupportedException();
        public Task<Vidra.Core.Process.ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct = default) =>
            Task.FromResult(new Vidra.Core.Process.ProcessResult(code, stdout, stderr));
    }

    private static YtDlpService Svc(string stdout, string stderr, int code)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vidra-e-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(Path.Combine(dir, "d"), Path.Combine(dir, "l"));
        var runner = new StubRunner(stdout, stderr, code);
        return new YtDlpService(runner, new Vidra.Core.Tools.ToolManager(paths, runner, new HttpClient()), () => new AppSettings());
    }

    [Fact]
    public async Task Null_stdout_with_429_warning_is_rate_limited()
    {
        var e = await Assert.ThrowsAsync<ProbeException>(() => Svc("null\n", "WARNING: [youtube] x: Unable to download webpage: HTTP Error 429: Too Many Requests\n", 1)
            .ProbeAsync("https://youtu.be/x"));
        Assert.Equal(ErrorKind.RateLimited, e.Kind);
    }

    [Fact]
    public async Task Members_only_error_is_auth()
    {
        var e = await Assert.ThrowsAsync<ProbeException>(() => Svc("", "ERROR: [youtube] x: Join this channel to get access to members-only content\n", 1)
            .ProbeAsync("https://youtu.be/x"));
        Assert.Equal(ErrorKind.Auth, e.Kind);
    }

    [Fact]
    public async Task Feed_returns_playlist()
    {
        var p = Assert.IsType<PlaylistInfo>(await Svc("""{"_type":"playlist","id":"subs","title":"Abonelikler","entries":[{"id":"a","title":"A"}]}""", "", 0)
            .ProbeAsync(":ytsubs"));
        Assert.Equal(LinkKind.Feed, p.Kind);
        Assert.Single(p.Entries);
    }
}
