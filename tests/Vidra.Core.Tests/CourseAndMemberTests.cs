using System.Text.Json;
using Vidra.Core.Follow;
using Vidra.Core.Models;
using Vidra.Core.Process;
using Vidra.Core.Queue;
using Vidra.Core.Storage;
using Vidra.Core.Tools;
using Vidra.Core.YtDlp;

namespace Vidra.Core.Tests;

public class CourseAndMemberTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "vidra-cm-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    // ================= URL & Şablonlar =================

    [Fact]
    public void Channel_root_id_and_canonical_urls()
    {
        Assert.Equal("https://www.youtube.com/@kanal", YouTubeUrl.ChannelRootUrl("https://www.youtube.com/@kanal/courses?si=abc"));
        Assert.Equal("https://www.youtube.com/channel/UCabc123", YouTubeUrl.ChannelRootUrl("https://www.youtube.com/channel/UCabc123/membership"));
        Assert.Equal("UCabc123", YouTubeUrl.ChannelId("https://www.youtube.com/channel/UCabc123/videos"));
        Assert.Null(YouTubeUrl.ChannelId("https://www.youtube.com/@kanal/courses"));

        Assert.Equal("https://www.youtube.com/@kanal", YouTubeUrl.CanonicalSourceUrl("https://www.youtube.com/@kanal/playlists"));
        Assert.Equal("https://www.youtube.com/playlist?list=PLxyz", YouTubeUrl.CanonicalSourceUrl("https://www.youtube.com/playlist?list=PLxyz&si=1"));
        Assert.Equal(":ytsubs", YouTubeUrl.CanonicalSourceUrl(":ytsubs"));
    }

    [Fact]
    public void Playlist_template_resolves_channel_and_falls_back_when_missing()
    {
        var withChannel = Templates.ResolvePlaylist("%(playlist_channel)s/%(playlist)s/%(playlist_index)03d - %(title)s.%(ext)s", "C# Kursu", 4, "Ahmet: %100");
        Assert.Equal("Ahmet_ %%100/C# Kursu/004 - %(title)s.%(ext)s", withChannel);

        var withoutChannel = Templates.ResolvePlaylist("%(playlist_uploader)s/%(playlist)s/%(playlist_index)02d.%(ext)s", "Dersler", 2, null);
        Assert.Equal("%(channel,uploader)s/Dersler/02.%(ext)s", withoutChannel);

        // Varsayılan şablon ilk hazır şablonla aynı kalır
        Assert.Equal(new AppSettings().PlaylistTemplate, Templates.PlaylistPresets[0].Template);
    }

    [Fact]
    public void Download_args_honor_description_skip_archive_and_custom_output_dir()
    {
        var tools = new ToolPaths(@"C:\v\yt-dlp.exe", @"C:\v", @"C:\v\deno.exe", @"C:\v\archive.txt");
        var settings = new AppSettings { OutputDir = @"C:\default", UseArchive = true };
        var opts = new DownloadOptions { WriteDescription = true, SkipArchive = true, OutputDir = @"D:\Kurslar" };

        var args = YtDlpArgs.Download("https://youtu.be/abc", opts, settings, tools);
        Assert.Contains("--write-description", args);
        Assert.DoesNotContain("--download-archive", args);
        Assert.Equal(@"D:\Kurslar", args[args.IndexOf("-P") + 1]);
    }

    // ================= Üyeler sekmesi probe optimizasyonu =================

    private sealed class ScriptedRunner(Func<IReadOnlyList<string>, ProcessResult> run) : IProcessRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public List<FakeProcess> Started { get; } = [];

        public IRunningProcess Start(string exe, IReadOnlyList<string> args, Action<string> onLine)
        {
            var p = new FakeProcess(onLine, args);
            lock (Started) Started.Add(p);
            return p;
        }

        public Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct = default)
        {
            Calls.Add(args);
            return Task.FromResult(run(args));
        }
    }

    [Fact]
    public async Task Membership_probe_uses_channel_id_directly_without_extra_call()
    {
        var paths = new AppPaths(Path.Combine(_dir, "d1"), Path.Combine(_dir, "l1"));
        var runner = new ScriptedRunner(args =>
            new ProcessResult(0, """{"_type":"playlist","id":"UUMOxyz999","title":"Members","entries":[{"id":"m1","title":"Üye dersi","availability":"subscriber_only"}]}""", ""));
        var svc = new YtDlpService(runner, new ToolManager(paths, runner, new HttpClient()), () => new AppSettings());

        var res = Assert.IsType<PlaylistInfo>(await svc.ProbeAsync("https://www.youtube.com/channel/UCxyz999", ChannelTab.Membership));
        Assert.Single(runner.Calls); // ekstra /videos sorgusu yok
        Assert.Equal("https://www.youtube.com/playlist?list=UUMOxyz999", runner.Calls[0][^1]);
        Assert.Single(res.Entries);
        Assert.True(res.Entries[0].MembersOnly);
    }

    [Fact]
    public async Task Membership_probe_falls_back_to_channel_root_when_videos_tab_is_missing()
    {
        var paths = new AppPaths(Path.Combine(_dir, "d2"), Path.Combine(_dir, "l2"));
        var runner = new ScriptedRunner(args =>
        {
            var target = args[^1];
            if (target.EndsWith("/videos", StringComparison.Ordinal))
                return new ProcessResult(1, "", "ERROR: [youtube:tab] @onlymembers: This channel does not have a videos tab\n");
            if (target == "https://www.youtube.com/@onlymembers")
                return new ProcessResult(0, """{"_type":"playlist","id":"UCmem42","title":"Only Members","entries":[]}""", "");
            return new ProcessResult(0, """{"_type":"playlist","id":"UUMOmem42","title":"Members","entries":[{"id":"v1","title":"Özel","availability":"subscriber_only"}]}""", "");
        });
        var svc = new YtDlpService(runner, new ToolManager(paths, runner, new HttpClient()), () => new AppSettings());

        var res = Assert.IsType<PlaylistInfo>(await svc.ProbeAsync("https://www.youtube.com/@onlymembers", ChannelTab.Membership));
        Assert.Equal(3, runner.Calls.Count);
        Assert.Equal("https://www.youtube.com/playlist?list=UUMOmem42", runner.Calls[2][^1]);
        Assert.Single(res.Entries);
    }

    // ================= M3U8 & Kuyruk otomatik M3U8 =================

    [Fact]
    public void Course_m3u_orders_by_index_deduplicates_and_uses_relative_paths()
    {
        var folder = Path.Combine(_dir, "kurs");
        Directory.CreateDirectory(folder);
        var f1 = Path.Combine(folder, "001 - Giriş.mp4");
        var f2 = Path.Combine(folder, "002 - İleri.mp4");
        File.WriteAllText(f1, "1");
        File.WriteAllText(f2, "2");

        var written = Export.WriteCourseM3u(folder, "Python: Temel", [
            (2, "İleri\nSeviye", 125.4, f2),
            (1, "Giriş", 60, f1),
            (1, "Giriş kopya", 60, f1),
        ]);
        Assert.NotNull(written);
        Assert.True(File.Exists(written));
        var content = File.ReadAllText(written!);
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("#EXTM3U", lines[0]);
        Assert.Equal("#PLAYLIST:Python: Temel", lines[1]);
        Assert.Equal("#EXTINF:60,Giriş", lines[2]);
        Assert.Equal("001 - Giriş.mp4", lines[3]);
        Assert.Equal("#EXTINF:125,İleri Seviye", lines[4]);
        Assert.Equal("002 - İleri.mp4", lines[5]);
        Assert.Equal(6, lines.Length);
    }

    [Fact]
    public async Task Queue_auto_updates_course_m3u8_in_lesson_order()
    {
        var paths = new AppPaths(Path.Combine(_dir, "dq"), Path.Combine(_dir, "lq"));
        var courseDir = Directory.CreateDirectory(Path.Combine(_dir, "out", "Rust Kursu")).FullName;
        var f1 = Path.Combine(courseDir, "001 - Ders 1.mp4");
        var f2 = Path.Combine(courseDir, "002 - Ders 2.mp4");
        File.WriteAllText(f1, "a");
        File.WriteAllText(f2, "b");

        var runner = new FakeRunner();
        var queue = new DownloadQueue(runner, new ToolManager(paths, runner, new HttpClient()),
            () => new AppSettings { OutputDir = Path.Combine(_dir, "out"), Concurrency = 2 }, paths);

        var opts1 = new DownloadOptions { WriteM3u = true, Playlist = new PlaylistRef("Rust Kursu", 1, "https://www.youtube.com/playlist?list=PLrust", "Hoca") };
        var opts2 = new DownloadOptions { WriteM3u = true, Playlist = new PlaylistRef("Rust Kursu", 2, "https://www.youtube.com/playlist?list=PLrust", "Hoca") };
        queue.Enqueue([
            new NewJob("https://youtu.be/d1", "Ders 1", opts1, "Hoca", Duration: 100),
            new NewJob("https://youtu.be/d2", "Ders 2", opts2, "Hoca", Duration: 200),
        ]);

        // 2. ders önce bitsin, sonra 1. ders bitsin: .m3u8 yine 1 → 2 sırasında olmalı
        runner.Started[1].Emit($"{YtDlpArgs.MarkDone}{f2}");
        runner.Started[1].Exit(0);
        runner.Started[0].Emit($"{YtDlpArgs.MarkDone}{f1}");
        runner.Started[0].Exit(0);

        for (var i = 0; i < 200 && queue.Jobs.Count > 0; i++) await Task.Delay(10);
        Assert.Empty(queue.Jobs);

        var m3uPath = Path.Combine(courseDir, "Rust Kursu.m3u8");
        Assert.True(File.Exists(m3uPath));
        var text = File.ReadAllText(m3uPath);
        Assert.True(text.IndexOf("001 - Ders 1.mp4", StringComparison.Ordinal) < text.IndexOf("002 - Ders 2.mp4", StringComparison.Ordinal));
    }

    // ================= ListCacheStore & ListStats =================

    [Fact]
    public async Task List_cache_persists_canonicalizes_and_computes_progress()
    {
        var path = Path.Combine(_dir, "cache.json");
        var cache = new ListCacheStore(path);
        var pl = new PlaylistInfo
        {
            Kind = LinkKind.Playlist, Id = "PL1", Title = "Kurs 1", Channel = "Kanal",
            Entries = [new PlaylistEntry { Id = "a", Url = "https://youtu.be/a", Title = "A", Duration = 100 },
                       new PlaylistEntry { Id = "b", Url = "https://youtu.be/b", Title = "B", Duration = 200, MembersOnly = true }],
        };
        cache.Put("https://www.youtube.com/playlist?list=PL1&si=foo", pl);
        await cache.SaveNowAsync();

        var reloaded = new ListCacheStore(path);
        Assert.Equal(1, reloaded.Count);
        var hit = reloaded.TryGet("https://www.youtube.com/playlist?list=PL1");
        Assert.NotNull(hit);
        Assert.Equal(2, hit!.Entries.Count);

        var stats = ListStats.Of(hit.Entries, new HashSet<string> { "a" });
        Assert.Equal(0.5, stats.Progress, 6);
        Assert.False(stats.Complete);
        Assert.True(ListStats.Of(hit.Entries, new HashSet<string> { "a", "b" }).Complete);

        reloaded.Remove("https://www.youtube.com/playlist?list=PL1");
        Assert.Null(reloaded.TryGet("https://www.youtube.com/playlist?list=PL1"));
    }

    // ================= WatchProgressStore & LibraryCourses =================

    [Fact]
    public async Task Watch_progress_and_library_courses_grouping()
    {
        var wpPath = Path.Combine(_dir, "watched.json");
        var wp = new WatchProgressStore(wpPath);
        Assert.True(wp.Toggle("vid1"));
        Assert.True(wp.IsWatched("vid1"));
        await wp.SaveNowAsync();
        Assert.True(new WatchProgressStore(wpPath).IsWatched("vid1"));

        var courseDir = Path.Combine(_dir, "courses", "React");
        var h2 = new HistoryEntry
        {
            Id = "2", Url = "https://youtu.be/vid2", VideoId = "vid2", Title = "State", Channel = "Hoca", Duration = 300, Size = 2000,
            Status = JobStatus.Done, FilePath = Path.Combine(courseDir, "002 - State.mp4"), FinishedAt = DateTimeOffset.Now,
            Options = new DownloadOptions { Playlist = new PlaylistRef("React Kursu", 2, "https://www.youtube.com/playlist?list=PLr", "Hoca") },
        };
        var h1 = new HistoryEntry
        {
            Id = "1", Url = "https://youtu.be/vid1", VideoId = "vid1", Title = "Giriş", Channel = "Hoca", Duration = 120, Size = 1000,
            Status = JobStatus.Done, FilePath = Path.Combine(courseDir, "001 - Giriş.mp4"), FinishedAt = DateTimeOffset.Now.AddMinutes(-5),
            Options = new DownloadOptions { Playlist = new PlaylistRef("React Kursu", 1, "https://www.youtube.com/playlist?list=PLr", "Hoca") },
        };
        var single = new HistoryEntry
        {
            Id = "3", Url = "https://youtu.be/solo", VideoId = "solo", Title = "Tek video", Channel = "Başka",
            Status = JobStatus.Done, FilePath = Path.Combine(_dir, "solo.mp4"), FinishedAt = DateTimeOffset.Now,
            Options = new DownloadOptions(),
        };

        var groups = LibraryCourses.Group([h2, h1, single], wp.Snapshot());
        var course = Assert.Single(groups);
        Assert.Equal("React Kursu", course.Title);
        Assert.Equal("Hoca", course.Channel);
        Assert.Equal(2, course.TotalCount);
        Assert.Equal(1, course.WatchedCount);
        Assert.Equal(0.5, course.Progress, 6);
        Assert.Equal(["Giriş", "State"], course.Lessons.Select(l => l.Entry.Title));
        Assert.Equal("State", course.NextUnwatched?.Entry.Title);
        Assert.Equal(courseDir, course.FolderPath);

        wp.MarkAll(["vid1", "vid2"], true);
        var doneCourse = Assert.Single(LibraryCourses.Group([h2, h1], wp.Snapshot()));
        Assert.True(doneCourse.AllWatched);
        Assert.Equal("Giriş", doneCourse.NextUnwatched?.Entry.Title);
    }

    // ================= FollowService: Üyeler sekmesi & uzun kurs takibi =================

    [Fact]
    public async Task Follow_supports_membership_tab_and_long_course_playlist_with_folder_template()
    {
        var paths = new AppPaths(Path.Combine(_dir, "df"), Path.Combine(_dir, "lf"));
        var settings = new AppSettings
        {
            OutputDir = Path.Combine(_dir, "out"),
            PlaylistTemplate = "%(playlist_channel)s/%(playlist)s/%(playlist_index)03d - %(title)s.%(ext)s",
        };

        // 61 derslik bir kurs (61. ders yeni eklenmiş) ve bir kanalın Üyeler sekmesi
        var courseEntries = Enumerable.Range(1, 61).Select(i => new { id = $"c{i}", title = $"Ders {i}", duration = 120 }).ToList();
        var courseJson = JsonSerializer.Serialize(new { _type = "playlist", id = "PLlong", title = "Derin Kurs", channel = "Eğitmen", entries = courseEntries });
        var membersJson = """{"_type":"playlist","id":"UUMOabc","title":"Members","channel":"Katıl Kanalı","entries":[{"id":"m2","title":"Yeni Üye Videosu","availability":"subscriber_only"},{"id":"m1","title":"Eski Üye Videosu","availability":"subscriber_only"}]}""";

        var runner = new ScriptedRunner(args =>
        {
            var url = args[^1];
            if (url.Contains("PLlong", StringComparison.Ordinal))
            {
                Assert.DoesNotContain("-I", args); // uzun kurslarda 50 sınırı yok!
                return new ProcessResult(0, courseJson, "");
            }
            return new ProcessResult(0, membersJson, "");
        });

        var tools = new ToolManager(paths, runner, new HttpClient());
        var ytdlp = new YtDlpService(runner, tools, () => settings);
        var queue = new DownloadQueue(runner, tools, () => settings, paths) { Held = true };
        await using var follow = new FollowService(ytdlp, queue, paths, () => settings);

        var fCourse = await follow.FollowAsync("https://www.youtube.com/playlist?list=PLlong", "Derin Kurs", new DownloadOptions(),
            Enumerable.Range(1, 60).Select(i => $"c{i}"), ChannelTab.Videos, "Derin Kurs", "Eğitmen");
        var fMember = await follow.FollowAsync("https://www.youtube.com/channel/UCabc/membership", "Katıl Kanalı · Üyeler", new DownloadOptions(),
            ["m1"], ChannelTab.Membership, null, "Katıl Kanalı");

        // Aynı kanalda Videolar takip edilmiyor, Üyeler takip ediliyor
        Assert.True(follow.IsFollowed("https://www.youtube.com/channel/UCabc", ChannelTab.Membership));
        Assert.False(follow.IsFollowed("https://www.youtube.com/channel/UCabc", ChannelTab.Videos));

        var (checkedCount, totalNew, errors) = await follow.CheckAllAsync();
        Assert.Equal((2, 2, 0), (checkedCount, totalNew, errors));

        var courseJob = Assert.Single(queue.Jobs, j => j.VideoId == "c61");
        Assert.Equal("Eğitmen/Derin Kurs/061 - %(title)s.%(ext)s", courseJob.OutputTemplate);
        Assert.Equal(new PlaylistRef("Derin Kurs", 61, "https://www.youtube.com/playlist?list=PLlong", "Eğitmen"), courseJob.Options.Playlist);

        var memberJob = Assert.Single(queue.Jobs, j => j.VideoId == "m2");
        Assert.Equal("Takip: Katıl Kanalı · Üyeler", memberJob.Source);
        Assert.Equal(1, fCourse.LastNewCount);
        Assert.Equal(1, fMember.LastNewCount);
    }

    // ================= JSON Geriye Uyumluluk =================

    [Fact]
    public async Task Pinned_and_recent_sources_roundtrip_and_old_json_stays_valid()
    {
        var path = Path.Combine(_dir, "settings.json");
        var s = new AppSettings
        {
            PinnedSources = [new PinnedSource("https://www.youtube.com/@hoca", "Hoca", LinkKind.Channel, "https://img/1.jpg", "Hoca", IsMember: true, DefaultTab: ChannelTab.Courses)],
            RecentSources = [new RecentSource("https://www.youtube.com/playlist?list=PL1", "Kurs 1", LinkKind.Playlist, null, "Hoca", null, DateTimeOffset.UtcNow)],
        };
        await JsonStore.WriteAsync(path, s);
        var back = JsonStore.Read<AppSettings>(path)!;
        var pin = Assert.Single(back.PinnedSources);
        Assert.True(pin.IsMember);
        Assert.Equal(ChannelTab.Courses, pin.DefaultTab);
        Assert.Single(back.RecentSources);

        // Eski PlaylistRef JSON'ı (Url/Channel olmadan) hâlâ okunur
        var oldRef = JsonSerializer.Deserialize<PlaylistRef>("""{"title":"Eski Liste","index":3}""", JsonStore.Options)!;
        Assert.Equal(new PlaylistRef("Eski Liste", 3, null, null), oldRef);
    }
}
