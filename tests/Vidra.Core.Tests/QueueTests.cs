using Vidra.Core.Models;
using Vidra.Core.Process;
using Vidra.Core.Queue;
using Vidra.Core.Storage;
using Vidra.Core.Tools;
using Vidra.Core.YtDlp;

namespace Vidra.Core.Tests;

/// <summary>Sahte yt-dlp: her Start çağrısı kontrol edilebilir bir süreç döndürür.</summary>
internal sealed class FakeRunner : IProcessRunner
{
    public List<FakeProcess> Started { get; } = [];

    public IRunningProcess Start(string exe, IReadOnlyList<string> args, Action<string> onLine)
    {
        var p = new FakeProcess(onLine, args);
        lock (Started) Started.Add(p);
        return p;
    }

    public Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct = default) =>
        Task.FromResult(new ProcessResult(0, "", ""));
}

internal sealed class FakeProcess(Action<string> onLine, IReadOnlyList<string> args) : IRunningProcess
{
    private readonly TaskCompletionSource<int> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IReadOnlyList<string> Args { get; } = args;
    public Task<int> Completion => _tcs.Task;
    public bool Killed { get; private set; }
    public void Emit(string line) => onLine(line);
    public void Exit(int code) => _tcs.TrySetResult(code);
    public void Kill() { Killed = true; _tcs.TrySetResult(1); }
}

public class QueueTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vidra-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRunner _runner = new();
    private readonly AppSettings _settings = new() { OutputDir = "/tmp/out", Concurrency = 2 };
    private readonly DownloadQueue _queue;

    public QueueTests()
    {
        var paths = new AppPaths(Path.Combine(_dir, "data"), Path.Combine(_dir, "local"));
        var tools = new ToolManager(paths, _runner, new HttpClient());
        _queue = new DownloadQueue(_runner, tools, () => _settings, paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static NewJob Job(string id) => new($"https://youtu.be/{id}", id, new DownloadOptions());

    private static async Task Until(Func<bool> cond)
    {
        for (var i = 0; i < 200 && !cond(); i++) await Task.Delay(10);
        Assert.True(cond());
    }

    [Fact]
    public void Respects_concurrency()
    {
        _queue.Enqueue([Job("a"), Job("b"), Job("c")]);
        Assert.Equal(2, _runner.Started.Count);
        Assert.Equal(JobStatus.Queued, _queue.Jobs[2].Status);
    }

    [Fact]
    public async Task Progress_streams_and_completion_move_to_history()
    {
        _queue.Enqueue([Job("a")]);
        var p = _runner.Started[0];
        var job = _queue.Jobs[0];

        p.Emit($"{YtDlpArgs.MarkDl}downloading|500|1000|NA|100|5|137");
        Assert.Equal(50, job.Percent);
        Assert.Equal("Video akışı (1/2)", job.Stage);

        p.Emit($"{YtDlpArgs.MarkDl}downloading|10|100|NA|100|1|140");
        Assert.Equal(2, job.Stream);

        p.Emit($"{YtDlpArgs.MarkPp}started|Merger");
        Assert.Equal(JobStatus.Processing, job.Status);

        p.Emit($"{YtDlpArgs.MarkDone}/tmp/out/a.mp4");
        p.Exit(0);

        await Until(() => _queue.Jobs.Count == 0);
        Assert.Single(_queue.History);
        Assert.Equal(JobStatus.Done, _queue.History[0].Status);
        Assert.Equal("/tmp/out/a.mp4", _queue.History[0].FilePath);
    }

    [Fact]
    public async Task Side_step_error_with_file_is_success_with_warning()
    {
        _queue.Enqueue([Job("a")]);
        var p = _runner.Started[0];
        p.Emit("ERROR: Unable to download video subtitles for 'en': HTTP Error 429");
        p.Emit($"{YtDlpArgs.MarkDone}/tmp/out/a.mp4");
        p.Exit(1);
        await Until(() => _queue.History.Count == 1);
        Assert.Equal(JobStatus.Done, _queue.History[0].Status);
        Assert.Contains("429", _queue.History[0].Warning);
    }

    [Fact]
    public async Task Auth_error_stays_in_queue()
    {
        _queue.Enqueue([Job("a")]);
        var p = _runner.Started[0];
        p.Emit("ERROR: [youtube] a: Join this channel to get access to members-only content");
        p.Exit(1);
        var job = _queue.Jobs[0];
        await Until(() => job.Status == JobStatus.Error);
        Assert.Equal(ErrorKind.Auth, job.ErrorKind);
    }

    [Fact]
    public async Task Pause_kills_and_resume_restarts_next_job_starts()
    {
        _settings.Concurrency = 1;
        _queue.Enqueue([Job("a"), Job("b")]);
        var job = _queue.Jobs[0];
        _queue.Pause(job);
        Assert.True(_runner.Started[0].Killed);
        await Until(() => job.Status == JobStatus.Paused);
        // Boşalan yere sıradaki iş geçer
        await Until(() => _runner.Started.Count == 2);

        _queue.Resume(job);
        Assert.Equal(JobStatus.Queued, job.Status);
    }

    [Fact]
    public async Task Drained_fires_once_after_busy()
    {
        var fired = 0;
        _queue.Drained += (_, _) => fired++;
        _queue.Schedule();
        Assert.Equal(0, fired);
        _queue.Enqueue([Job("a")]);
        _runner.Started[0].Exit(0);
        await Until(() => fired == 1);
    }
}

public class PendingTests
{
    [Fact]
    public async Task Unfinished_jobs_survive_restart_as_paused()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vidra-p-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(Path.Combine(dir, "d"), Path.Combine(dir, "l"));
        var runner = new FakeRunner();
        var settings = new AppSettings { OutputDir = "/tmp", Concurrency = 1 };
        var tools = new ToolManager(paths, runner, new HttpClient());
        var q1 = new DownloadQueue(runner, tools, () => settings, paths);
        q1.Enqueue([new NewJob("https://youtu.be/a", "A", new DownloadOptions()), new NewJob("https://youtu.be/b", "B", new DownloadOptions { Mode = DownloadMode.Audio })]);
        q1.PauseAll();
        await q1.SavePendingAsync();

        var q2 = new DownloadQueue(runner, tools, () => settings, paths);
        Assert.Equal(["A", "B"], q2.Jobs.Select(j => j.Title));
        Assert.All(q2.Jobs, j => Assert.Equal(JobStatus.Paused, j.Status));
        Assert.Equal(DownloadMode.Audio, q2.Jobs[1].Options.Mode);
        Directory.Delete(dir, true);
    }
}

public class ProcessRunnerTests
{
    /// <summary>Gerileme testi: son satır geri çağrısı içinde WaitForExit() kilitlenmesi.</summary>
    [Fact]
    public async Task Start_completes_and_delivers_all_lines()
    {
        if (OperatingSystem.IsWindows()) return; // /bin/sh kullanıyoruz
        var lines = new List<string>();
        var p = new ProcessRunner().Start("/bin/sh", ["-c", "for i in 1 2 3; do echo out$i; echo err$i 1>&2; done; exit 3"], l => { lock (lines) lines.Add(l); });
        var done = await Task.WhenAny(p.Completion, Task.Delay(5000));
        Assert.Same(p.Completion, done);
        Assert.Equal(3, p.Completion.Result);
        Assert.Equal(6, lines.Count);
    }

    /// <summary>yt-dlp --cookies dosyasını geri yazar: her süreç ana dosyanın kopyasını almalı.</summary>
    [Fact]
    public void Cookies_arg_points_to_private_copy_with_same_content()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vidra-cc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var master = Path.Combine(dir, "youtube-cookies.txt");
        File.WriteAllText(master, "# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t0\tSID\tx\n");

        var (args, copy) = ProcessRunner.WithPrivateCookies(["-J", "--cookies", master, "--", "https://youtu.be/x"], Path.Combine(dir, "copies"));

        Assert.NotNull(copy);
        Assert.NotEqual(master, copy);
        Assert.Equal(["-J", "--cookies", copy!, "--", "https://youtu.be/x"], args);
        Assert.Equal(File.ReadAllText(master), File.ReadAllText(copy!));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Args_without_cookie_file_are_untouched()
    {
        string[] input = ["-J", "--cookies-from-browser", "firefox", "--", "https://youtu.be/x"];
        var (args, copy) = ProcessRunner.WithPrivateCookies(input, Path.GetTempPath());
        Assert.Null(copy);
        Assert.Equal(input, args);
    }
}

public class DrainTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vidra-d-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRunner _runner = new();
    private readonly DownloadQueue _queue;

    public DrainTests()
    {
        var paths = new AppPaths(Path.Combine(_dir, "d"), Path.Combine(_dir, "l"));
        _queue = new DownloadQueue(_runner, new ToolManager(paths, _runner, new HttpClient()), () => new AppSettings { OutputDir = "/tmp" }, paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public async Task Pausing_everything_is_not_drained()
    {
        var fired = 0;
        _queue.Drained += (_, _) => fired++;
        _queue.Enqueue([new NewJob("https://youtu.be/a", "a", new DownloadOptions())]);
        _queue.PauseAll();
        await Task.Delay(200);
        Assert.Equal(0, fired);
    }

    [Fact]
    public async Task Shutdown_suppresses_drained()
    {
        var fired = 0;
        _queue.Drained += (_, _) => fired++;
        _queue.Enqueue([new NewJob("https://youtu.be/a", "a", new DownloadOptions())]);
        _queue.BeginShutdown();
        _runner.Started[0].Exit(0);
        await Task.Delay(200);
        Assert.Equal(0, fired);
    }
}
