using Vidra.Core.Models;
using Vidra.Core.Process;
using Vidra.Core.Queue;
using Vidra.Core.Storage;
using Vidra.Core.Tools;
using Vidra.Core.YtDlp;

namespace Vidra.Core.Tests;

/// <summary>Hız iyileştirmeleri: önbellekler, analiz bilgisinin yeniden kullanımı, onedir yt-dlp.</summary>
public class PerformanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vidra-perf-" + Guid.NewGuid().ToString("N"));
    private static readonly ToolPaths Tools = new("/bin/yt-dlp", "/bin", "/bin/deno", "/a.txt");
    private static readonly AppSettings Polite = new() { OutputDir = "/tmp", Cookies = new BrowserCookies(BrowserKind.Firefox), PoliteMode = true };

    public PerformanceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static async Task Until(Func<bool> cond)
    {
        for (var i = 0; i < 200 && !cond(); i++) await Task.Delay(10);
        Assert.True(cond());
    }

    // ================= LruCache =================

    [Fact]
    public void Lru_evicts_least_recently_used_not_everything()
    {
        var c = new LruCache<string, int>(2);
        c.Set("a", 1);
        c.Set("b", 2);
        Assert.True(c.TryGet("a", out _)); // a artık en yeni
        c.Set("c", 3);
        Assert.Equal(2, c.Count);
        Assert.True(c.TryGet("a", out var a));
        Assert.Equal(1, a);
        Assert.False(c.TryGet("b", out _));
        Assert.True(c.TryGet("c", out _));
    }

    [Fact]
    public void Lru_get_or_add_creates_once_and_remove_forgets()
    {
        var c = new LruCache<string, int>(4);
        var calls = 0;
        Assert.Equal(7, c.GetOrAdd("k", _ => { calls++; return 7; }));
        Assert.Equal(7, c.GetOrAdd("k", _ => { calls++; return 8; }));
        Assert.Equal(1, calls);
        c.Remove("k");
        Assert.False(c.TryGet("k", out _));
    }

    // ================= Kapak disk önbelleği =================

    [Fact]
    public void Thumbnail_path_is_stable_per_url_and_width()
    {
        var a = ThumbnailDiskCache.PathFor(_dir, "https://i.ytimg.com/vi/x/hq.jpg?sqp=1", 192);
        Assert.Equal(a, ThumbnailDiskCache.PathFor(_dir, "https://i.ytimg.com/vi/x/hq.jpg?sqp=1", 192));
        Assert.NotEqual(a, ThumbnailDiskCache.PathFor(_dir, "https://i.ytimg.com/vi/x/hq.jpg?sqp=1", 320));
        Assert.NotEqual(a, ThumbnailDiskCache.PathFor(_dir, "https://i.ytimg.com/vi/x/hq.jpg?sqp=2", 192));
        Assert.EndsWith(".jpg", a);
    }

    [Fact]
    public void Thumbnail_prune_deletes_oldest_until_under_limit()
    {
        for (var i = 0; i < 10; i++)
        {
            var f = Path.Combine(_dir, $"{i}.jpg");
            File.WriteAllBytes(f, new byte[1000]);
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(-100 + i));
        }
        Assert.Equal(0, ThumbnailDiskCache.Prune(_dir, maxBytes: 20_000));
        var deleted = ThumbnailDiskCache.Prune(_dir, maxBytes: 5_000);
        Assert.Equal(6, deleted); // %80 hedef: 4000 bayta iner
        Assert.False(File.Exists(Path.Combine(_dir, "0.jpg")));
        Assert.True(File.Exists(Path.Combine(_dir, "9.jpg")));
    }

    // ================= Arşiv önbelleği =================

    [Fact]
    public void Archive_ids_are_cached_but_refresh_when_file_changes_and_callers_get_copies()
    {
        var path = Path.Combine(_dir, "archive.txt");
        File.WriteAllText(path, "youtube aaa\n");
        var store = new ArchiveStore(path);
        var first = store.ReadIds();
        first.Add("mutated");
        Assert.Equal(["aaa"], store.ReadIds()); // çağıranın değişikliği önbelleğe sızmaz

        File.AppendAllText(path, "youtube bbb\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        Assert.Equal(["aaa", "bbb"], store.ReadIds().Order());
    }

    [Fact]
    public void Archive_missing_file_is_empty() => Assert.Empty(new ArchiveStore(Path.Combine(_dir, "none.txt")).ReadIds());

    // ================= Liste önbelleği =================

    [Fact]
    public async Task List_cache_loads_in_background_and_writes_compact_json()
    {
        var path = Path.Combine(_dir, "list-cache.json");
        var cache = new ListCacheStore(path);
        cache.Put("https://www.youtube.com/playlist?list=PL1", new PlaylistInfo { Kind = LinkKind.Playlist, Id = "PL1", Title = "Liste", Url = "https://www.youtube.com/playlist?list=PL1" });
        await cache.SaveNowAsync();
        Assert.DoesNotContain("\n  ", File.ReadAllText(path)); // girintisiz

        var reloaded = new ListCacheStore(path);
        await reloaded.Loaded;
        Assert.Equal("Liste", reloaded.TryGet("https://www.youtube.com/playlist?list=PL1")?.Title);
    }

    [Fact]
    public async Task List_cache_reads_old_indented_files()
    {
        var path = Path.Combine(_dir, "old.json");
        var info = new PlaylistInfo { Kind = LinkKind.Playlist, Id = "PL2", Title = "Eski", Url = "https://www.youtube.com/playlist?list=PL2" };
        await JsonStore.WriteAsync(path, new Dictionary<string, CachedList> { ["https://www.youtube.com/playlist?list=PL2"] = new(info, DateTimeOffset.Now) });
        Assert.Contains("\n  ", File.ReadAllText(path));
        Assert.Equal("Eski", new ListCacheStore(path).TryGet("https://www.youtube.com/playlist?list=PL2")?.Title);
    }

    // ================= İndirme argümanları =================

    [Fact]
    public void Download_throttles_progress_lines()
    {
        var a = YtDlpArgs.Download("u", new DownloadOptions(), Polite, Tools);
        Assert.Equal("0.5", a[a.IndexOf("--progress-delta") + 1]);
    }

    [Fact]
    public void Info_json_replaces_url()
    {
        var a = YtDlpArgs.Download("https://youtu.be/x", new DownloadOptions(), Polite, Tools, infoJson: "/c/x.json");
        Assert.Equal("/c/x.json", a[a.IndexOf("--load-info-json") + 1]);
        Assert.DoesNotContain("https://youtu.be/x", a);
        Assert.DoesNotContain("--", a);
    }

    // ================= InfoJsonCache =================

    [Fact]
    public void Info_cache_returns_fresh_files_only()
    {
        var c = new InfoJsonCache(Path.Combine(_dir, "info"), TimeSpan.FromHours(2));
        Assert.Null(c.TryGetFresh("jNQXAC9IVRw"));
        c.Save("jNQXAC9IVRw", "{}");
        var path = c.TryGetFresh("jNQXAC9IVRw");
        Assert.NotNull(path);

        File.SetLastWriteTimeUtc(path!, DateTime.UtcNow.AddHours(-3));
        Assert.Null(c.TryGetFresh("jNQXAC9IVRw"));
        Assert.Equal(1, c.Prune());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Info_cache_rejects_unsafe_ids_and_invalidates()
    {
        var c = new InfoJsonCache(Path.Combine(_dir, "info"));
        c.Save("../evil", "{}");
        Assert.False(File.Exists(Path.Combine(_dir, "evil.json")));
        Assert.Null(c.TryGetFresh("../evil"));
        Assert.Null(c.TryGetFresh(null));

        c.Save("abc_DEF-123", "{}");
        c.Invalidate("abc_DEF-123");
        Assert.Null(c.TryGetFresh("abc_DEF-123"));
    }

    private sealed class JsonRunner(string stdout) : IProcessRunner
    {
        public IRunningProcess Start(string exe, IReadOnlyList<string> args, Action<string> onLine) => throw new NotSupportedException();
        public Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct = default) =>
            Task.FromResult(new ProcessResult(0, stdout, ""));
    }

    [Fact]
    public async Task Video_probe_saves_info_for_the_download()
    {
        var paths = new AppPaths(Path.Combine(_dir, "d"), Path.Combine(_dir, "l"));
        var json = """{"id":"jNQXAC9IVRw","title":"Me at the zoo","webpage_url":"https://www.youtube.com/watch?v=jNQXAC9IVRw","formats":[]}""";
        var runner = new JsonRunner(json);
        var cache = new InfoJsonCache(paths.InfoJsonDir);
        var svc = new YtDlpService(runner, new ToolManager(paths, runner, new HttpClient()), () => new AppSettings(), infoCache: cache);

        Assert.IsType<VideoInfo>(await svc.ProbeAsync("https://www.youtube.com/watch?v=jNQXAC9IVRw"));
        var saved = cache.TryGetFresh("jNQXAC9IVRw");
        Assert.NotNull(saved);
        Assert.Equal(json, File.ReadAllText(saved!));
    }

    // ================= Kuyruk =================

    private (DownloadQueue Queue, FakeRunner Runner, InfoJsonCache Cache) Queue(AppSettings? settings = null)
    {
        var paths = new AppPaths(Path.Combine(_dir, "qd"), Path.Combine(_dir, "ql"));
        var runner = new FakeRunner();
        var cache = new InfoJsonCache(paths.InfoJsonDir);
        var s = settings ?? new AppSettings { OutputDir = "/tmp", Concurrency = 2, Cookies = new BrowserCookies(BrowserKind.Firefox), PoliteMode = true };
        return (new DownloadQueue(runner, new ToolManager(paths, runner, new HttpClient()), () => s, paths, null, infoCache: cache), runner, cache);
    }

    private static NewJob Job(string id) => new($"https://www.youtube.com/watch?v={id}", id, new DownloadOptions());

    [Fact]
    public async Task Download_reuses_probe_info_then_drops_it()
    {
        var (q, runner, cache) = Queue();
        cache.Save("jNQXAC9IVRw", "{}");
        q.Enqueue([Job("jNQXAC9IVRw")]);
        var p = runner.Started[0];
        Assert.Contains("--load-info-json", p.Args);

        p.Emit($"{YtDlpArgs.MarkDone}/tmp/a.mp4");
        p.Exit(0);
        await Until(() => q.Jobs.Count == 0);
        Assert.Null(cache.TryGetFresh("jNQXAC9IVRw")); // çerez taşıyabilir: iş bitince silinir
    }

    [Fact]
    public async Task Failed_download_with_info_retries_with_fresh_extraction()
    {
        var (q, runner, cache) = Queue();
        cache.Save("jNQXAC9IVRw", "{}");
        q.Enqueue([Job("jNQXAC9IVRw")]);
        runner.Started[0].Emit("ERROR: [download] Got error: HTTP Error 403: Forbidden");
        runner.Started[0].Exit(1);
        var job = q.Jobs[0];
        await Until(() => job.AutoRetries == 1); // 403: kuyruk kendisi yeniden dener

        q.Retry(job); // beklemeden şimdi dene
        Assert.DoesNotContain("--load-info-json", runner.Started[1].Args);
        Assert.Contains("https://www.youtube.com/watch?v=jNQXAC9IVRw", runner.Started[1].Args);
    }

    [Fact]
    public void Extra_args_disable_info_reuse()
    {
        var (q, runner, cache) = Queue(new AppSettings { OutputDir = "/tmp", ExtraArgs = "--extractor-args youtube:player_client=web" });
        cache.Save("jNQXAC9IVRw", "{}");
        q.Enqueue([Job("jNQXAC9IVRw")]);
        Assert.DoesNotContain("--load-info-json", runner.Started[0].Args);
    }

    [Fact]
    public void Progress_lines_do_not_flood_the_log()
    {
        var (q, runner, _) = Queue();
        q.Enqueue([Job("aaaaaaaaaaa")]);
        var p = runner.Started[0];
        p.Emit("[youtube] aaaaaaaaaaa: Downloading webpage");
        for (var i = 0; i < 50; i++) p.Emit($"{YtDlpArgs.MarkDl}downloading|{i * 10}|1000|NA|100|5|137");
        p.Emit("WARNING: bir uyarı");
        q.Jobs[0].Log.Flush();
        var lines = q.Jobs[0].Log.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length); // web sayfası + ilk ilerleme + uyarı
        Assert.Equal(49, q.Jobs[0].Percent); // arayüz yine her satırı görür
    }

    // ================= Onedir yt-dlp =================

    [Fact]
    public void Onedir_pointer_resolves_only_existing_safe_folders()
    {
        var root = Path.Combine(_dir, "yt-dlp-dir");
        Assert.Null(ToolManager.ResolveOnedirExe(root));
        Directory.CreateDirectory(Path.Combine(root, "v1"));
        File.WriteAllText(Path.Combine(root, "current.txt"), "v1\n");
        Assert.Null(ToolManager.ResolveOnedirExe(root)); // exe yok
        File.WriteAllText(Path.Combine(root, "v1", "yt-dlp.exe"), "");
        Assert.Equal(Path.Combine(root, "v1", "yt-dlp.exe"), ToolManager.ResolveOnedirExe(root));
        File.WriteAllText(Path.Combine(root, "current.txt"), "../v1");
        Assert.Null(ToolManager.ResolveOnedirExe(root));
    }

    [Fact]
    public void Onedir_assets_and_channels()
    {
        Assert.Equal("yt-dlp_win.zip", ToolManager.WinOnedirAsset(arm: false));
        Assert.Equal("yt-dlp_win_arm64.zip", ToolManager.WinOnedirAsset(arm: true));
        Assert.Equal("yt-dlp/yt-dlp", ToolManager.YtDlpRepo(UpdateChannel.Stable));
        Assert.Equal("yt-dlp/yt-dlp-nightly-builds", ToolManager.YtDlpRepo(UpdateChannel.Nightly));
    }

    // ================= Perf =================

    [Fact]
    public void Perf_line_has_name_duration_and_detail()
    {
        var line = Perf.Write("test:block", TimeSpan.FromMilliseconds(1234), "detay");
        Assert.Contains("test:block", line);
        Assert.Contains("1234 ms", line);
        Assert.EndsWith("detay", line);
    }
}
