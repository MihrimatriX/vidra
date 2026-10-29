using System.Text.Json;
using Vidra.Core.Auth;
using Vidra.Core.Models;
using Vidra.Core.YtDlp;

namespace Vidra.Core.Tests;

public class UrlTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc123", LinkKind.Video)]
    [InlineData("https://youtu.be/abc123", LinkKind.Video)]
    [InlineData("https://www.youtube.com/shorts/abc123", LinkKind.Video)]
    [InlineData("https://m.youtube.com/watch?v=abc&list=PL1", LinkKind.Video)]
    [InlineData("https://music.youtube.com/watch?v=abc", LinkKind.Video)]
    [InlineData("https://www.youtube.com/playlist?list=PL1", LinkKind.Playlist)]
    [InlineData("https://www.youtube.com/@kanal", LinkKind.Channel)]
    [InlineData("https://www.youtube.com/channel/UC123/videos", LinkKind.Channel)]
    [InlineData(":ytsubs", LinkKind.Feed)]
    [InlineData("https://www.youtube.com/feed/playlists", LinkKind.Feed)]
    public void Classify(string url, LinkKind kind) => Assert.Equal(kind, YouTubeUrl.Classify(url));

    [Theory]
    [InlineData("https://example.com/watch?v=1")]
    [InlineData("metin")]
    [InlineData("https://www.youtube.com/watch")]
    public void Classify_rejects(string url) => Assert.Null(YouTubeUrl.Classify(url));

    [Fact]
    public void Video_id()
    {
        Assert.Equal("abc", YouTubeUrl.VideoId("https://www.youtube.com/watch?v=abc&t=10"));
        Assert.Equal("abc", YouTubeUrl.VideoId("https://youtu.be/abc?si=x"));
        Assert.Equal("abc", YouTubeUrl.VideoId("https://www.youtube.com/shorts/abc"));
    }

    [Fact]
    public void Flat_video_entries_use_jpeg_thumbnail_and_channel()
    {
        var p = ProbeParser.ParsePlaylist(JsonDocument.Parse("""
            {"_type":"playlist","id":"q","title":"q","entries":[
              {"id":"abc","title":"T","channel":"Kanal","thumbnails":[{"url":"https://i.ytimg.com/vi/abc/hq720.jpg?sqp=x","width":360}]}]}
            """).RootElement, LinkKind.Playlist);
        Assert.Equal("https://i.ytimg.com/vi/abc/mqdefault.jpg", p.Entries[0].Thumbnail);
        Assert.Equal("Kanal", p.Entries[0].Channel);
    }

    [Fact]
    public void Playlist_id_skips_auto_mixes()
    {
        Assert.Equal("PLx1", YouTubeUrl.PlaylistId("https://www.youtube.com/watch?v=abc&list=PLx1&index=3"));
        Assert.Null(YouTubeUrl.PlaylistId("https://www.youtube.com/watch?v=abc&list=RDabc"));
        Assert.Null(YouTubeUrl.PlaylistId("https://www.youtube.com/watch?v=abc"));
    }

    [Fact]
    public void Channel_tab() =>
        Assert.Equal("https://www.youtube.com/@kanal/membership",
            YouTubeUrl.ChannelTabUrl("https://www.youtube.com/@kanal/featured?x=1", ChannelTab.Membership));

    [Fact]
    public void Tab_of_channel_link()
    {
        Assert.Equal(ChannelTab.Playlists, YouTubeUrl.TabOf("https://www.youtube.com/@kanal/playlists"));
        Assert.Equal(ChannelTab.Courses, YouTubeUrl.TabOf("https://www.youtube.com/@kanal/courses"));
        Assert.Equal(ChannelTab.Videos, YouTubeUrl.TabOf("https://www.youtube.com/channel/UCx/videos/"));
        Assert.Null(YouTubeUrl.TabOf("https://www.youtube.com/@kanal"));
        Assert.Null(YouTubeUrl.TabOf("https://www.youtube.com/@kanal/featured"));
        Assert.Null(YouTubeUrl.TabOf("https://www.youtube.com/playlist?list=PL1"));
    }

    [Fact]
    public void Members_playlist() =>
        Assert.Equal("https://www.youtube.com/playlist?list=UUMOabc123",
            YouTubeUrl.MembersPlaylistUrl("UCabc123"));

    [Fact]
    public void Extract_many_links() =>
        Assert.Equal(2, YouTubeUrl.ExtractVideoLinks("https://youtu.be/a\nhttps://youtu.be/b https://youtu.be/a nope").Count);
}

public class OutputParserTests
{
    [Fact]
    public void Download_line()
    {
        var l = OutputParser.Parse($"{YtDlpArgs.MarkDl}downloading|1048576|NA|4194304|524288.5|6|137");
        Assert.Equal(new DownloadLine("downloading", 1048576, 4194304, 524288.5, 6, "137"), l);
    }

    [Fact]
    public void Postprocess_line() =>
        Assert.Equal(new PostprocessLine("started", "Merger"), OutputParser.Parse($"{YtDlpArgs.MarkPp}started|Merger"));

    [Fact]
    public void Done_line_keeps_pipes_in_path() =>
        Assert.Equal(new DoneLine(@"C:\a|b\v.mp4"), OutputParser.Parse($"{YtDlpArgs.MarkDone}C:\\a|b\\v.mp4\r"));

    [Theory]
    [InlineData("Join this channel to get access to members-only content", ErrorKind.Auth)]
    [InlineData("Sign in to confirm you’re not a bot. Use --cookies-from-browser", ErrorKind.BotCheck)]
    [InlineData("This video is private. Sign in if you've been granted access", ErrorKind.Auth)]
    [InlineData("HTTP Error 429: Too Many Requests", ErrorKind.RateLimited)]
    [InlineData("Video unavailable. This video has been removed", ErrorKind.Unavailable)]
    [InlineData("Unable to download webpage: timed out", ErrorKind.Network)]
    public void Classify_errors(string msg, ErrorKind kind) => Assert.Equal(kind, OutputParser.Classify(msg));

    [Fact]
    public void Error_line() => Assert.IsType<ErrorLine>(OutputParser.Parse("ERROR: [youtube] x: Private video"));

    [Fact]
    public void Missing_channel_tab()
    {
        Assert.Equal(ChannelTab.Courses, OutputParser.MissingTab("[youtube:tab] @membersclub: This channel does not have a courses tab"));
        Assert.Equal(ChannelTab.Streams, OutputParser.MissingTab("The channel does not have a streams tab"));
        Assert.Null(OutputParser.MissingTab("This channel does not have a podcasts tab"));
        Assert.Null(OutputParser.MissingTab("Video unavailable"));
    }
}

public class TemplateTests
{
    [Fact]
    public void Playlist_template_sanitizes_escapes_and_pads() =>
        Assert.Equal("A_B_ %%50/007 - %(title)s.%(ext)s",
            Templates.ResolvePlaylist("%(playlist)s/%(playlist_index)03d - %(title)s.%(ext)s", "A/B: %50", 7));

    [Fact]
    public void Preview()
    {
        var f = new Dictionary<string, object?> { ["uploader"] = "K", ["title"] = "T:1", ["id"] = "x", ["ext"] = "mp4" };
        Assert.Equal("K/T_1 [x].mp4", Templates.Preview("%(uploader)s/%(title)s [%(id)s].%(ext)s", f));
        Assert.Equal("[foo]", Templates.Preview("%(foo)s", f));
    }

    [Theory]
    [InlineData("1:02:03", 3723)]
    [InlineData("90", 90)]
    [InlineData("2:15", 135)]
    public void Time(string t, double s) => Assert.Equal(s, TimeText.ToSeconds(t));

    [Fact]
    public void Time_invalid() => Assert.Null(TimeText.ToSeconds("x"));

    [Fact]
    public void Time_format()
    {
        Assert.Equal("2:05", TimeText.Format(125));
        Assert.Equal("1:00:05", TimeText.Format(3605));
    }
}

public class ProbeParserTests
{
    private const string Json = """
    {
      "id": "x", "title": "T", "availability": "subscriber_only", "duration": 100,
      "chapters": [{"title": "A", "start_time": 0, "end_time": 50}, {"start_time": 50, "end_time": 100}],
      "subtitles": {"tr": [{"name": "Türkçe"}], "live_chat": [{}]},
      "automatic_captions": {"en-orig": [{"name": "English (Original)"}], "de": [{"name": "German"}]},
      "formats": [
        {"format_id": "140", "vcodec": "none", "acodec": "mp4a.40.2", "tbr": 128, "filesize": 1000},
        {"format_id": "251", "vcodec": "none", "acodec": "opus", "tbr": 160, "abr": 160, "filesize": 2000},
        {"format_id": "251-drc", "vcodec": "none", "acodec": "opus", "tbr": 170, "filesize": 9999},
        {"format_id": "233", "vcodec": "none"},
        {"format_id": "137", "vcodec": "avc1.640028", "acodec": "none", "height": 1080, "fps": 30, "tbr": 4000, "filesize": 10000},
        {"format_id": "299", "vcodec": "avc1.64002a", "acodec": "none", "height": 1080, "fps": 60, "tbr": 6000, "filesize": 15000},
        {"format_id": "248", "vcodec": "vp9", "acodec": "none", "height": 1080, "fps": 30, "tbr": 3000, "filesize": 9000},
        {"format_id": "337", "vcodec": "vp09.02", "acodec": "none", "height": 2160, "fps": 60, "tbr": 20000, "filesize_approx": 50000, "dynamic_range": "HDR10"}
      ]
    }
    """;

    private static readonly VideoInfo V = ProbeParser.ParseVideo(JsonDocument.Parse(Json).RootElement);

    [Fact]
    public void Meta_chapters_subtitles()
    {
        Assert.True(V.MembersOnly);
        Assert.Equal([new Chapter("A", 0, 50), new Chapter("Bölüm 2", 50, 100)], V.Chapters);
        Assert.Equal([new SubtitleTrack("tr", "Türkçe", false), new SubtitleTrack("en-orig", "English", true)], V.Subtitles);
        Assert.Equal(2, V.AutoTranslateCount);
    }

    [Fact]
    public void Audio_skips_drc() => Assert.Equal(new AudioInfo("251", "opus", 160, null, 2000), V.Audio);

    [Fact]
    public void Qualities_grouped()
    {
        Assert.Equal([2160, 1080], V.Qualities.Select(q => q.Height));
        Assert.Equal(52000, V.Qualities[0].Size);
        Assert.True(V.Qualities[0].Hdr);
        Assert.Equal(17000, V.Qualities[1].Size);
        Assert.Equal(60, V.Qualities[1].MaxFps);
    }

    [Fact]
    public void Pick_stream_respects_preferences()
    {
        var q = V.Qualities[1];
        Assert.Equal("299", ProbeParser.PickStream(q, CodecPreference.Auto, ContainerFormat.Mp4, false)?.Id);
        Assert.Equal("137", ProbeParser.PickStream(q, CodecPreference.Auto, ContainerFormat.Mp4, true)?.Id);
        Assert.Equal("248", ProbeParser.PickStream(q, CodecPreference.Vp9, ContainerFormat.Mkv, false)?.Id);
        Assert.Equal("299", ProbeParser.PickStream(q, CodecPreference.Av1, ContainerFormat.Mkv, false)?.Id);
        Assert.Equal("248", ProbeParser.PickStream(q, CodecPreference.Auto, ContainerFormat.Original, true)?.Id); // VP9, H.264'ten önce
    }

    [Fact]
    public void Aac_audio_kept_for_mp4_and_m4a() => Assert.Equal("140", V.AudioAac?.Id);

    [Fact]
    public void Playlist_marks_hidden_entries()
    {
        var p = ProbeParser.ParsePlaylist(JsonDocument.Parse("""
            {"_type":"playlist","id":"PL1","title":"L","entries":[
              {"id":"a","title":"One","url":"https://www.youtube.com/watch?v=a","duration":60},
              {"id":"b","title":"[Private video]"},
              {"id":"c","title":"Members","availability":"subscriber_only"}]}
            """).RootElement, LinkKind.Playlist);
        Assert.Equal(3, p.Entries.Count);
        Assert.True(p.Entries[1].Unavailable);
        Assert.True(p.Entries[2].MembersOnly);
        Assert.Equal("https://www.youtube.com/watch?v=b", p.Entries[1].Url);
    }
}

public class CookieTests
{
    private static readonly BrowserCookie[] Cookies =
    [
        new("SAPISID", "abc", ".youtube.com", "/", true, false, DateTimeOffset.FromUnixTimeSeconds(2_000_000_000)),
        new("SID", "s", ".youtube.com", "/", false, true, DateTimeOffset.FromUnixTimeSeconds(1_900_000_000)),
        new("PREF", "p", "www.youtube.com", "/", false, false, null),
        new("tracker", "t", ".example.com", "/", false, false, null),
    ];

    [Fact]
    public void Writes_netscape_format()
    {
        var text = NetscapeCookies.Write(Cookies);
        Assert.StartsWith("# Netscape HTTP Cookie File", text);
        Assert.Contains(".youtube.com\tTRUE\t/\tTRUE\t2000000000\tSAPISID\tabc\n", text);
        Assert.Contains("#HttpOnly_.youtube.com\tTRUE\t/\tFALSE\t1900000000\tSID\ts\n", text);
        Assert.Contains("www.youtube.com\tFALSE\t/\tFALSE\t0\tPREF\tp\n", text);
        Assert.DoesNotContain("example.com", text);
    }

    [Fact]
    public void Detects_login_and_expiry()
    {
        Assert.True(NetscapeCookies.LooksLoggedIn(Cookies));
        Assert.False(NetscapeCookies.LooksLoggedIn(Cookies.Where(c => c.Name != "SAPISID")));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_900_000_000), NetscapeCookies.SessionExpiry(Cookies));
    }
}

public class FeedParsingTests
{
    [Fact]
    public void Playlists_feed_entries_are_sublists()
    {
        var p = ProbeParser.ParsePlaylist(JsonDocument.Parse("""
            {"_type":"playlist","id":"playlists","title":"Playlistlerim","entries":[
              {"_type":"url","ie_key":"YoutubeTab","id":"PLabc","title":"Oyun müzikleri","url":"https://www.youtube.com/playlist?list=PLabc"},
              {"_type":"url","ie_key":"Youtube","id":"vid1","title":"Video","url":"https://www.youtube.com/watch?v=vid1"}]}
            """).RootElement, LinkKind.Feed);
        Assert.True(p.Entries[0].IsList);
        Assert.Equal("https://www.youtube.com/playlist?list=PLabc", p.Entries[0].Url);
        Assert.False(p.Entries[1].IsList);
    }

    [Fact]
    public void List_stats_count_members_and_downloaded()
    {
        var p = ProbeParser.ParsePlaylist(JsonDocument.Parse("""
            {"_type":"playlist","id":"PL1","title":"Kurs","entries":[
              {"id":"a","title":"Ders 1","duration":600},
              {"id":"b","title":"Ders 2","duration":300,"availability":"subscriber_only"},
              {"id":"c","title":"[Private video]"},
              {"id":"d","title":"Ders 3","duration":100,"availability":"subscriber_only"}]}
            """).RootElement, LinkKind.Playlist);
        var s = ListStats.Of(p.Entries, new HashSet<string> { "a" });
        Assert.Equal(new ListStats(3, 2, 1, 1000, 400), s);
        Assert.Equal(2, s.New);
        Assert.False(s.AllMembers);
        Assert.True(ListStats.Of(p.Entries.Skip(1), new HashSet<string>()).AllMembers);
        Assert.False(ListStats.Of([], new HashSet<string>()).AllMembers);
    }
}

public class FeatureTests
{
    [Fact]
    public void Flat_entries_carry_channel_url()
    {
        var p = ProbeParser.ParsePlaylist(JsonDocument.Parse("""
            {"_type":"playlist","id":"subs","title":"s","entries":[
              {"id":"a","title":"A","channel":"Kanal","channel_url":"https://www.youtube.com/@kanal"},
              {"id":"b","title":"B","channel_id":"UC123"},
              {"id":"c","title":"C"}
            ]}
            """).RootElement, LinkKind.Feed);
        Assert.Equal("https://www.youtube.com/@kanal", p.Entries[0].ChannelUrl);
        Assert.Equal("https://www.youtube.com/channel/UC123", p.Entries[1].ChannelUrl);
        Assert.Null(p.Entries[2].ChannelUrl);
    }

    [Fact]
    public void Transcript_without_subtitles_fetches_vtt_but_does_not_embed()
    {
        var a = YtDlpArgs.Extras(new DownloadOptions { Subtitles = false, Transcript = true }, new AppSettings());
        Assert.Contains("--write-auto-subs", a);
        Assert.Contains("vtt/best", a);
        Assert.DoesNotContain("--embed-subs", a);
        Assert.DoesNotContain("--write-comments", a);
    }

    [Fact]
    public void Comments_go_to_info_json_and_extension_cookies_are_a_file()
    {
        var a = YtDlpArgs.Extras(new DownloadOptions { Comments = true }, new AppSettings());
        Assert.Contains("--write-comments", a);
        Assert.Contains("--write-info-json", a);
        Assert.Equal(["--cookies", "C:\\x.txt"], YtDlpArgs.Cookies(new ExtensionCookies("C:\\x.txt")));
    }

    [Fact]
    public void Csv_quotes_separators_and_neutralizes_formulas()
    {
        var csv = Export.PlaylistCsv([
            (1, new PlaylistEntry { Id = "a", Url = "https://youtu.be/a", Title = "Şarkı; \"canlı\"", UploadDate = "20260102", Duration = 61.7, Views = 5 }, true),
            (2, new PlaylistEntry { Id = "b", Url = "https://youtu.be/b", Title = "=HYPERLINK(\"x\")" }, false),
        ], ";");
        var lines = csv.Split("\r\n");
        Assert.StartsWith("Sıra;Başlık;", lines[0]);
        Assert.Equal("1;\"Şarkı; \"\"canlı\"\"\";https://youtu.be/a;;2026-01-02;61;5;;evet", lines[1]);
        Assert.StartsWith("2;\"'=HYPERLINK(\"\"x\"\")\";", lines[2]);
    }

    [Fact]
    public void Vtt_transcript_drops_timing_tags_and_rolling_duplicates()
    {
        const string vtt = """
            WEBVTT
            Kind: captions
            Language: en

            00:00:00.000 --> 00:00:02.000 align:start position:0%
            merhaba<00:00:00.500><c> dünya</c>

            00:00:02.000 --> 00:00:04.000 align:start position:0%
            merhaba dünya
            bugün &amp; yarın
            """;
        Assert.Equal("merhaba dünya\nbugün & yarın\n", Export.TranscriptFromVtt(vtt));
    }

    [Fact]
    public async Task Native_messages_roundtrip_and_eof_is_null()
    {
        var ms = new MemoryStream();
        await Vidra.Core.Browser.BrowserBridge.WriteAsync(ms, new { type = "open", url = "https://youtu.be/x" });
        ms.Position = 0;
        using var doc = await Vidra.Core.Browser.BrowserBridge.ReadAsync(ms);
        Assert.Equal("open", doc!.RootElement.GetProperty("type").GetString());
        Assert.Null(await Vidra.Core.Browser.BrowserBridge.ReadAsync(ms));
        await Assert.ThrowsAsync<InvalidDataException>(() => Vidra.Core.Browser.BrowserBridge.ReadAsync(new MemoryStream([0xFF, 0xFF, 0xFF, 0x7F])));
    }

    [Fact]
    public void Extension_cookies_become_a_logged_in_session()
    {
        var cookies = Vidra.Core.Browser.BrowserBridge.ParseCookies(JsonDocument.Parse("""
            [{"name":"SAPISID","value":"a","domain":".youtube.com","path":"/","secure":true,"expirationDate":1800000000.5},
             {"name":"SID","value":"b","domain":".youtube.com","path":"/","session":true},
             {"name":"","value":"skip","domain":".youtube.com"}]
            """).RootElement);
        Assert.Equal(2, cookies.Count);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_500), cookies[0].Expires);
        Assert.Null(cookies[1].Expires);
        Assert.True(NetscapeCookies.LooksLoggedIn(cookies));
    }

    /// <summary>Manifest'teki anahtar değişip kimlik sabiti güncellenmezse tarayıcı host'a bağlanmayı reddeder.</summary>
    [Fact]
    public void Extension_manifest_key_matches_allowed_origin()
    {
        var manifest = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Vidra.App", "Extension", "manifest.json"));
        var key = JsonDocument.Parse(File.ReadAllText(manifest)).RootElement.GetProperty("key").GetString()!;
        // Chrome: SHA-256(genel anahtar) ilk 16 bayt, her yarım bayt 'a' + değer
        var hash = System.Security.Cryptography.SHA256.HashData(Convert.FromBase64String(key));
        var id = string.Concat(hash.Take(16).SelectMany(b => new[] { (char)('a' + (b >> 4)), (char)('a' + (b & 15)) }));
        Assert.Equal(Vidra.Core.Browser.BrowserBridge.ExtensionId, id);
    }

    [Fact]
    public void Legacy_single_login_becomes_first_account()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vidra-acc-" + Guid.NewGuid().ToString("N"));
        var paths = new Vidra.Core.Storage.AppPaths(Path.Combine(dir, "d"), Path.Combine(dir, "l"));
        File.WriteAllText(paths.AppCookiesFile, "# Netscape HTTP Cookie File\n");
        var s = new Vidra.Core.Storage.SettingsStore(paths).Load();
        var acc = Assert.Single(s.Accounts);
        Assert.Equal("default", acc.Id);
        Assert.Equal(paths.AppCookiesFile, paths.AccountCookiesFile(acc.Id));
        Directory.Delete(dir, true);
    }
}
