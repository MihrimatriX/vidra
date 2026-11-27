using Vidra.Core.Models;
using Vidra.Core.Queue;
using Vidra.Core.Storage;
using Vidra.Core.Tools;
using Vidra.Core.YtDlp;

namespace Vidra.Core.Tests;

/// <summary>Elle ileri sarılan saat.</summary>
internal sealed class FakeClock : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => UtcNow;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    public void Advance(TimeSpan by) => UtcNow += by;
}

public class RateGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vidra-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRunner _runner = new();
    private readonly FakeClock _clock = new();
    private readonly AppSettings _settings = new() { OutputDir = "/tmp/out", Concurrency = 2 };
    private readonly DownloadQueue _queue;

    public RateGuardTests()
    {
        var paths = new AppPaths(Path.Combine(_dir, "data"), Path.Combine(_dir, "local"));
        var tools = new ToolManager(paths, _runner, new HttpClient());
        // Sapma 1.0: rastgele değer 0.5 → 0.8 + 0.5*0.4 = 1.0
        _queue = new DownloadQueue(_runner, tools, () => _settings, paths, guard: new RateGuard(_clock, new FixedRandom(0.5)));
    }

    public void Dispose()
    {
        _queue.BeginShutdown();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class FixedRandom(double v) : Random
    {
        public override double NextDouble() => v;
    }

    private static NewJob Job(string id) => new($"https://youtu.be/{id}", id, new DownloadOptions());

    private static async Task Until(Func<bool> cond)
    {
        for (var i = 0; i < 200 && !cond(); i++) await Task.Delay(10);
        Assert.True(cond());
    }

    /// <summary>Durum en son yazılır: Queued + RetryAt birlikte görülünce tamamlanma işlenmiştir.</summary>
    private static bool Waiting(DownloadJob j) => j.Status == JobStatus.Queued && j.RetryAt is not null;

    private void Fail(int index, string error)
    {
        _runner.Started[index].Emit("ERROR: " + error);
        _runner.Started[index].Exit(1);
    }

    private void Succeed(int index)
    {
        _runner.Started[index].Emit($"{YtDlpArgs.MarkDone}/tmp/out/{index}.mp4");
        _runner.Started[index].Exit(0);
    }

    [Fact]
    public async Task Rate_limit_cools_down_whole_queue_then_resumes_one_at_a_time()
    {
        _queue.Enqueue([Job("a"), Job("b"), Job("c")]);
        Assert.Equal(2, _runner.Started.Count);
        var a = _queue.Jobs[0];

        Fail(0, "[youtube] a: HTTP Error 429: Too Many Requests");
        await Until(() => a.Status == JobStatus.Queued && Waiting(a));
        Assert.Equal(ErrorKind.RateLimited, a.ErrorKind);
        Assert.Null(a.ErrorMessage);
        Assert.Equal(_clock.GetLocalNow() + TimeSpan.FromMinutes(5), a.RetryAt);
        Assert.Contains("YouTube sınırı", a.Stage);
        Assert.True(_queue.Guard.InCooldown);

        // Boşalan yere "c" başlamaz: soğuma sürerken yeni istek atılmaz
        Succeed(1);
        await Until(() => _queue.History.Count == 1 && _queue.Jobs.Count == 2);
        Assert.Equal(2, _runner.Started.Count);
        Assert.Equal(2, _queue.WaitingCount + _queue.Jobs.Count(j => j.Status == JobStatus.Queued && j.RetryAt is null));

        // Soğuma bitti: eşzamanlılık bir süre 1
        _clock.Advance(TimeSpan.FromMinutes(6));
        _queue.Schedule();
        await Until(() => _runner.Started.Count == 3);
        Assert.Equal(1, _queue.Jobs.Count(j => j.Status == JobStatus.Running));
        Assert.Null(a.RetryAt);
    }

    [Fact]
    public async Task Concurrent_failures_count_as_one_strike_and_steps_escalate()
    {
        _queue.Enqueue([Job("a"), Job("b")]);
        Fail(0, "HTTP Error 429: Too Many Requests");
        _clock.Advance(TimeSpan.FromSeconds(10));
        Fail(1, "HTTP Error 429: Too Many Requests");
        await Until(() => _queue.Jobs.All(Waiting));
        Assert.Equal(1, _queue.Guard.State(false).Strikes);
        Assert.Equal(_queue.Jobs[0].RetryAt, _queue.Jobs[1].RetryAt);

        _clock.Advance(TimeSpan.FromMinutes(6));
        _queue.Schedule();
        await Until(() => _runner.Started.Count == 3);
        Fail(2, "This content isn't available, try again later.");
        await Until(() => _queue.Guard.State(false).Strikes == 2);
        Assert.Equal(_clock.GetLocalNow() + TimeSpan.FromMinutes(15), _queue.Guard.CooldownUntil);
    }

    [Fact]
    public async Task Rate_limited_job_gives_up_after_max_attempts()
    {
        _settings.Concurrency = 1;
        _queue.Enqueue([Job("a")]);
        var a = _queue.Jobs[0];
        for (var i = 0; i < RateGuard.CooldownSteps.Length - 1; i++)
        {
            Fail(i, "HTTP Error 429: Too Many Requests");
            await Until(() => Waiting(a));
            _clock.Advance(TimeSpan.FromHours(2));
            _queue.Schedule();
            await Until(() => _runner.Started.Count == i + 2);
        }
        Fail(RateGuard.CooldownSteps.Length - 1, "HTTP Error 429: Too Many Requests");
        await Until(() => a.Status == JobStatus.Error);
        Assert.Equal(ErrorKind.RateLimited, a.ErrorKind);
        Assert.NotNull(a.ErrorMessage);
    }

    [Fact]
    public async Task Network_error_retries_only_that_job_then_fails()
    {
        _queue.Enqueue([Job("a"), Job("b")]);
        var a = _queue.Jobs[0];
        Fail(0, "Unable to download webpage: timed out");
        await Until(() => Waiting(a));
        Assert.False(_queue.Guard.InCooldown);
        Assert.Equal(_clock.GetLocalNow() + TimeSpan.FromSeconds(30), a.RetryAt);
        Assert.Contains("(1/2)", a.Stage);

        _clock.Advance(TimeSpan.FromSeconds(31));
        _queue.Schedule();
        await Until(() => _runner.Started.Count == 3);
        Fail(2, "unable to download video data: HTTP Error 403: Forbidden");
        await Until(() => Waiting(a) && a.AutoRetries == 2);
        Assert.Equal(_clock.GetLocalNow() + TimeSpan.FromMinutes(2), a.RetryAt);

        _clock.Advance(TimeSpan.FromMinutes(3));
        _queue.Schedule();
        await Until(() => _runner.Started.Count == 4);
        Fail(3, "unable to download video data: HTTP Error 403: Forbidden");
        await Until(() => a.Status == JobStatus.Error);
        Assert.Equal(ErrorKind.Forbidden, a.ErrorKind);
    }

    [Fact]
    public async Task Bot_check_retries_once()
    {
        _queue.Enqueue([Job("a")]);
        var a = _queue.Jobs[0];
        Fail(0, "Sign in to confirm you’re not a bot. Use --cookies-from-browser");
        await Until(() => Waiting(a));
        _clock.Advance(TimeSpan.FromMinutes(10));
        _queue.Schedule();
        await Until(() => _runner.Started.Count == 2);
        Fail(1, "Sign in to confirm you’re not a bot. Use --cookies-from-browser");
        await Until(() => a.Status == JobStatus.Error);
        Assert.Equal(ErrorKind.BotCheck, a.ErrorKind);
    }

    [Fact]
    public async Task Auth_error_is_not_retried_and_raises_event()
    {
        DownloadJob? failed = null;
        _queue.AuthFailed += (_, j) => failed = j;
        _queue.Enqueue([Job("a")]);
        Fail(0, "[youtube] a: Join this channel to get access to members-only content");
        await Until(() => _queue.Jobs[0].Status == JobStatus.Error);
        Assert.Same(_queue.Jobs[0], failed);
        Assert.False(_queue.Guard.InCooldown);
    }

    [Fact]
    public async Task Skip_cooldown_starts_waiting_jobs_now()
    {
        _queue.Enqueue([Job("a")]);
        Fail(0, "HTTP Error 429: Too Many Requests");
        await Until(() => Waiting(_queue.Jobs[0]));
        _queue.SkipCooldown();
        Assert.False(_queue.Guard.InCooldown);
        await Until(() => _runner.Started.Count == 2);
    }

    [Fact]
    public async Task Manual_retry_resets_attempts()
    {
        _queue.Enqueue([Job("a")]);
        var a = _queue.Jobs[0];
        Fail(0, "timed out");
        await Until(() => Waiting(a));
        _queue.Pause(a);
        Assert.Null(a.RetryAt);
        _queue.Retry(a);
        Assert.Equal(0, a.AutoRetries);
        // Başlatma, kuyruğun o an süren başka bir Schedule çağrısından da gelebilir
        await Until(() => _runner.Started.Count == 2);
    }

    [Fact]
    public void Recovery_restores_concurrency_after_successes()
    {
        var g = new RateGuard(_clock, new FixedRandom(0.5));
        var s = new AppSettings { Concurrency = 3 };
        Assert.Equal(3, g.EffectiveConcurrency(s));
        g.Trip(ErrorKind.RateLimited);
        _clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(1, g.EffectiveConcurrency(s));
        Assert.Equal(PoliteLevel.Safe, g.EffectivePolite(s));
        for (var i = 0; i < RateGuard.RecoverAfter; i++) g.RecordSuccess();
        Assert.Equal(3, g.EffectiveConcurrency(s));
        Assert.Equal(PoliteLevel.Balanced, g.EffectivePolite(s));
        Assert.Equal(0, g.State(false).Strikes);
    }

    [Fact]
    public void Near_hourly_budget_slows_down_and_window_slides()
    {
        var g = new RateGuard(_clock);
        var s = new AppSettings { Concurrency = 3 };
        for (var i = 0; i < RateGuard.GuestBudget * 8 / 10; i++) g.RecordStart();
        Assert.True(g.State(false).NearBudget);
        Assert.False(g.State(true).NearBudget);
        Assert.Equal(1, g.EffectiveConcurrency(s));
        Assert.Equal(PoliteLevel.Safe, g.EffectivePolite(s));
        // Kullanıcı nazik modu kapattıysa yükseltilmez
        s.Polite = PoliteLevel.Off;
        Assert.Equal(PoliteLevel.Off, g.EffectivePolite(s));

        _clock.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(0, g.State(false).StartsLastHour);
        Assert.Equal(3, g.EffectiveConcurrency(s));
    }

    [Fact]
    public void Safe_mode_with_account_caps_concurrency()
    {
        var g = new RateGuard(_clock);
        var s = new AppSettings { Concurrency = 4, Polite = PoliteLevel.Safe, Cookies = new FileCookies("c.txt") };
        Assert.Equal(2, g.EffectiveConcurrency(s));
        s.Cookies = new NoCookies();
        Assert.Equal(4, g.EffectiveConcurrency(s));
    }
}

public class YtDlpFreshnessTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("2026.11.20", 7)]
    [InlineData("2026.11.20.234512", 7)]
    [InlineData("2026.10.01", 57)]
    [InlineData("2026.12.01", 0)]
    public void Age_from_version(string v, int days) => Assert.Equal(days, Vidra.Core.Tools.YtDlpFreshness.AgeDays(v, Now));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("2026.13.40")]
    public void Unknown_versions_have_no_age(string? v) => Assert.Null(Vidra.Core.Tools.YtDlpFreshness.AgeDays(v, Now));

    [Fact]
    public void Staleness_and_check_interval()
    {
        Assert.True(Vidra.Core.Tools.YtDlpFreshness.IsStale("2026.10.01", Now));
        Assert.False(Vidra.Core.Tools.YtDlpFreshness.IsStale("2026.11.20", Now));
        Assert.True(Vidra.Core.Tools.YtDlpFreshness.IsCheckDue(null, Now));
        Assert.False(Vidra.Core.Tools.YtDlpFreshness.IsCheckDue(Now.AddHours(-2), Now));
        Assert.True(Vidra.Core.Tools.YtDlpFreshness.IsCheckDue(Now.AddDays(-1), Now));
    }

    [Theory]
    [InlineData("Current version: stable@2026.11.01 from yt-dlp/yt-dlp\nLatest version: stable@2026.11.20 from yt-dlp/yt-dlp\nCurrent Build Hash: x\nUpdating to stable@2026.11.20 from yt-dlp/yt-dlp ...\nUpdated yt-dlp to stable@2026.11.20 from yt-dlp/yt-dlp", "2026.11.20")]
    [InlineData("Updated yt-dlp to nightly@2026.11.25.001122 from yt-dlp/yt-dlp-nightly-builds", "2026.11.25.001122")]
    [InlineData("yt-dlp is up to date (stable@2026.11.20 from yt-dlp/yt-dlp)", null)]
    public void Parses_update_output(string output, string? version) => Assert.Equal(version, Vidra.Core.Tools.YtDlpFreshness.UpdatedTo(output));
}

public class PoTokenTests
{
    private static readonly ToolPaths Plain = new("y", "b", "d", "a");
    private static readonly ToolPaths WithPot = new("y", "b", "d", "a", @"C:\v\bgutil-pot\server", @"C:\v\yt-dlp-plugins");

    [Fact]
    public void Provider_args_only_when_enabled_and_installed()
    {
        var on = new AppSettings { PoTokenProvider = true };
        Assert.Empty(YtDlpArgs.PoToken(on, Plain));
        Assert.Empty(YtDlpArgs.PoToken(new AppSettings(), WithPot));
        var a = YtDlpArgs.PoToken(on, WithPot);
        Assert.Equal(@"C:\v\yt-dlp-plugins", a[a.IndexOf("--plugin-dirs") + 1]);
        Assert.Contains(@"youtubepot-bgutilscript:server_home=C:\v\bgutil-pot\server", a);
        Assert.Contains("youtube:player_client=default,mweb", a);
        // Hem analizde hem indirmede
        Assert.Contains("--plugin-dirs", YtDlpArgs.Probe("u", LinkKind.Video, on, WithPot));
    }

    [Fact]
    public void Extracts_server_subtree_and_rejects_zip_slip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vidra-pot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var zip = Path.Combine(dir, "src.zip");
            using (var z = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
            {
                void Add(string name, string text)
                {
                    using var w = new StreamWriter(z.CreateEntry(name).Open());
                    w.Write(text);
                }
                z.CreateEntry("repo-2.0.2/server/");
                Add("repo-2.0.2/server/src/generate_once.ts", "x");
                Add("repo-2.0.2/server/package.json", "{}");
                Add("repo-2.0.2/plugin/x.py", "no");
            }
            var dest = Path.Combine(dir, "out", "server");
            Vidra.Core.Tools.ToolManager.ExtractSubtree(zip, "server/", dest);
            Assert.True(File.Exists(Path.Combine(dest, "src", "generate_once.ts")));
            Assert.True(File.Exists(Path.Combine(dest, "package.json")));
            Assert.False(Directory.Exists(Path.Combine(dir, "out", "plugin")));

            var bad = Path.Combine(dir, "bad.zip");
            using (var z = System.IO.Compression.ZipFile.Open(bad, System.IO.Compression.ZipArchiveMode.Create))
                z.CreateEntry("repo/server/../../evil.txt");
            Assert.Throws<InvalidDataException>(() => Vidra.Core.Tools.ToolManager.ExtractSubtree(bad, "server/", Path.Combine(dir, "o2", "server")));
        }
        finally { Directory.Delete(dir, true); }
    }
}
