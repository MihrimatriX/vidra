using System.Globalization;
using Vidra.Core.Models;
using Vidra.Core.Playlists;

namespace Vidra.Core.Tests;

public class PlaylistTests
{
    private sealed record Row(PlaylistEntry Entry, int Index, bool Done = false, ListStats? Stats = null) : IPlaylistItem;

    private static PlaylistEntry Video(string id, string title = "", double? dur = null, string? date = null, long? views = null,
        bool members = false, bool unavailable = false, string? channel = null) => new()
    {
        Id = id, Url = $"https://www.youtube.com/watch?v={id}", Title = title.Length > 0 ? title : id,
        Duration = dur, UploadDate = date, Views = views, MembersOnly = members, Unavailable = unavailable, Channel = channel,
    };

    private static PlaylistEntry List(string id, string title, string? channel = null) => new()
    {
        Id = id, Url = $"https://www.youtube.com/playlist?list={id}", Title = title, IsList = true, Channel = channel,
    };

    private static ListStats Stats(int videos, int done = 0, int members = 0, double secs = 0, double newSecs = 0) =>
        new(videos, members, done, secs, newSecs);

    private static List<Row> Rows(params PlaylistEntry[] entries) => entries.Select((e, i) => new Row(e, i + 1)).ToList();

    private static int[] Ids<T>(IEnumerable<T> rows) where T : IPlaylistItem => rows.Select(r => r.Index).ToArray();

    // ================= IndexRange =================

    [Theory]
    [InlineData("1-3, 5", new[] { 1, 2, 3, 5 })]
    [InlineData("8-", new[] { 8, 9, 10 })]
    [InlineData("1 - 3", new[] { 1, 2, 3 })]
    [InlineData("0-2, 11, 9-99", new[] { 1, 2, 9, 10 })]
    [InlineData("5-3", new int[0])]
    [InlineData("4,4,,4", new[] { 4 })]
    public void Range_parses_and_clamps_to_list(string input, int[] expected) =>
        Assert.Equal(expected, IndexRange.Parse(input, 10)!.Order());

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("1-x")]
    [InlineData("-5")]
    [InlineData("+3")]
    public void Range_empty_or_invalid_means_no_filter(string? input) => Assert.Null(IndexRange.Parse(input, 10));

    // ================= Video filtresi =================

    [Fact]
    public void Video_filters_by_range_date_and_query()
    {
        var rows = Rows(Video("a", "İstanbul turu", date: "20240101"), Video("b", "Ankara", date: "20240615"),
            Video("c", "istanbul gece", date: null), Video("d", "Izmir", date: "20250101"));

        Assert.Equal([1, 2], Ids(PlaylistFilter.Videos(rows, new() { Range = "1-2" })));
        // Tarihi bilinmeyen video (c) tarih filtresinde görünür kalır
        Assert.Equal([2, 3], Ids(PlaylistFilter.Videos(rows, new() { DateFrom = "2024-02-01", DateTo = "20241231" })));
        // 8 haneye inmeyen tarih yok sayılır
        Assert.Equal(4, PlaylistFilter.Videos(rows, new() { DateFrom = "2024-02" }).Count());
        Assert.Equal([1, 3], Ids(PlaylistFilter.Videos(rows, new() { Query = "  İSTANBUL " })));
        Assert.Equal([1, 3], Ids(PlaylistFilter.Videos(rows, new() { Query = "ISTANBUL" })));
        Assert.Equal([4], Ids(PlaylistFilter.Videos(rows, new() { Query = "izmir" })));
    }

    [Fact]
    public void Search_does_not_depend_on_current_culture()
    {
        var prev = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Assert.True(PlaylistFilter.Matches("İstanbul turu", "istanbul"));
            Assert.True(PlaylistFilter.Matches("ILIK SU", "ılık"));
            Assert.True(PlaylistFilter.Matches("Çorba dersleri", "corba"));
            Assert.True(PlaylistFilter.Matches("Liked videos", "LIKED"));
            Assert.False(PlaylistFilter.Matches("Ankara", "istanbul"));
        }
        finally { CultureInfo.CurrentCulture = prev; }
    }

    [Theory]
    [InlineData(DurationBucket.Short, new[] { 1, 2 })]
    [InlineData(DurationBucket.Mid, new[] { 3, 4 })]
    [InlineData(DurationBucket.Long, new[] { 5 })]
    [InlineData(DurationBucket.All, new[] { 1, 2, 3, 4, 5, 6 })]
    public void Duration_buckets_are_half_open_and_skip_unknown(DurationBucket bucket, int[] expected)
    {
        var rows = Rows(Video("a", dur: 0), Video("b", dur: 239.9), Video("c", dur: 240), Video("d", dur: 1199),
            Video("e", dur: 1200), Video("f", dur: null));
        Assert.Equal(expected, Ids(PlaylistFilter.Videos(rows, new() { Bucket = bucket })));
    }

    [Fact]
    public void Only_new_and_only_members_for_videos()
    {
        var rows = new List<Row>
        {
            new(Video("a"), 1, Done: true), new(Video("b", members: true), 2), new(Video("c", members: true), 3, Done: true), new(Video("d"), 4),
        };
        Assert.Equal([2, 4], Ids(PlaylistFilter.Videos(rows, new() { OnlyNew = true })));
        Assert.Equal([2, 3], Ids(PlaylistFilter.Videos(rows, new() { OnlyMembers = true })));
        Assert.Equal([2], Ids(PlaylistFilter.Videos(rows, new() { OnlyNew = true, OnlyMembers = true })));
    }

    [Theory]
    [InlineData(VideoSort.List, new[] { 1, 2, 3, 4 })]
    [InlineData(VideoSort.Reverse, new[] { 4, 3, 2, 1 })]
    [InlineData(VideoSort.Newest, new[] { 3, 1, 4, 2 })]
    [InlineData(VideoSort.Longest, new[] { 2, 4, 1, 3 })]
    [InlineData(VideoSort.Views, new[] { 4, 1, 3, 2 })]
    public void Video_sorts_with_list_order_as_tiebreak(VideoSort sort, int[] expected)
    {
        var rows = Rows(Video("a", dur: 100, date: "20240101", views: 50), Video("b", dur: 900, date: null, views: null),
            Video("c", dur: null, date: "20250101", views: 50), Video("d", dur: 900, date: "20240101", views: 1000));
        Assert.Equal(expected, Ids(PlaylistFilter.Videos(rows, new() { VideoSort = sort })));
    }

    // ================= Liste kartları =================

    private static List<Row> Cards() =>
    [
        new(List("p1", "Çorba dersleri", "Usta Mutfak"), 1, Stats: Stats(10, done: 10)),
        new(List("p2", "Ana yemekler"), 2, Stats: Stats(20, done: 5, members: 20)),
        new(List("p3", "Bilinmeyen"), 3),
        new(List("p4", "Ekmek"), 4, Stats: Stats(5, members: 2)),
    ];

    [Fact]
    public void List_query_matches_title_or_channel()
    {
        Assert.Equal([1], Ids(PlaylistFilter.Lists(Cards(), new() { Query = "usta" })));
        Assert.Equal([1], Ids(PlaylistFilter.Lists(Cards(), new() { Query = "ÇORBA" })));
    }

    [Fact]
    public void List_only_new_keeps_unread_lists_and_only_members_needs_stats()
    {
        Assert.Equal([2, 3, 4], Ids(PlaylistFilter.Lists(Cards(), new() { OnlyNew = true })));
        Assert.Equal([2, 4], Ids(PlaylistFilter.Lists(Cards(), new() { OnlyMembers = true })));
    }

    [Theory]
    [InlineData(ListSort.Default, new[] { 1, 2, 3, 4 })]
    [InlineData(ListSort.Members, new[] { 2, 4, 1, 3 })]
    [InlineData(ListSort.Missing, new[] { 2, 4, 1, 3 })]
    [InlineData(ListSort.Count, new[] { 2, 1, 4, 3 })]
    [InlineData(ListSort.Name, new[] { 2, 3, 1, 4 })]
    public void List_sorts_put_unread_lists_last(ListSort sort, int[] expected) =>
        Assert.Equal(expected, Ids(PlaylistFilter.Lists(Cards(), new() { ListSort = sort })));

    [Fact]
    public void Apply_dispatches_on_page_kind()
    {
        var rows = Cards();
        // Kart sayfasında Range yok sayılır, video sayfasında uygulanır
        Assert.Equal(4, PlaylistFilter.Apply(rows, new() { Range = "1" }, listOfLists: true).Count());
        Assert.Single(PlaylistFilter.Apply(rows, new() { Range = "1" }, listOfLists: false));
    }

    [Fact]
    public void Criteria_knows_when_list_stats_change_the_result()
    {
        Assert.False(new PlaylistFilterCriteria().DependsOnListStats);
        Assert.False(new PlaylistFilterCriteria { ListSort = ListSort.Name, Query = "x" }.DependsOnListStats);
        Assert.True(new PlaylistFilterCriteria { OnlyNew = true }.DependsOnListStats);
        Assert.True(new PlaylistFilterCriteria { ListSort = ListSort.Count }.DependsOnListStats);
    }

    [Fact]
    public void Bulk_candidates()
    {
        Assert.True(PlaylistFilter.IsBulkCandidate(new Row(Video("a"), 1), listOfLists: false));
        Assert.False(PlaylistFilter.IsBulkCandidate(new Row(Video("a"), 1, Done: true), listOfLists: false));
        Assert.False(PlaylistFilter.IsBulkCandidate(new Row(Video("a", unavailable: true), 1), listOfLists: false));
        var cards = Cards();
        Assert.Equal([2, 3, 4], cards.Where(r => PlaylistFilter.IsBulkCandidate(r, listOfLists: true)).Select(r => r.Index));
    }

    // ================= Özet metinleri =================

    [Fact]
    public void Meta_line_skips_empty_parts()
    {
        Assert.Equal("Usta Mutfak · 12 video · 1:01:01", PlaylistSummary.MetaLine("Usta Mutfak", 12, false, 3661));
        Assert.Equal("3 liste", PlaylistSummary.MetaLine(null, 3, true, 0));
        Assert.Equal("0 video", PlaylistSummary.MetaLine("", 0, false, 0));
    }

    [Theory]
    [InlineData(0, 0, "0 video seçili")]
    [InlineData(0, 3, "3 video seçili")]
    [InlineData(2, 0, "2 liste seçili")]
    [InlineData(1, 4, "1 liste + 4 video seçili")]
    public void Selected_label(int lists, int videos, string expected) => Assert.Equal(expected, PlaylistSummary.SelectedLabel(lists, videos));

    [Fact]
    public void Selected_summary_for_videos()
    {
        const double kbps = 8; // 1 sn = 1000 B
        Assert.Equal("Listeden video seç", PlaylistSummary.SelectedSummary([], false, kbps));
        Assert.Equal("İndirmek istediğin listeleri işaretle", PlaylistSummary.SelectedSummary([], true, kbps));
        IPlaylistItem[] sel = [new Row(Video("a", dur: 60), 1), new Row(Video("b", dur: 3000), 2), new Row(Video("c"), 3)];
        Assert.Equal($"51:00 · ≈ {Fmt.Bytes(3_060_000)}", PlaylistSummary.SelectedSummary(sel, false, kbps));
        Assert.Equal("0:00 · ≈ —", PlaylistSummary.SelectedSummary([new Row(Video("c"), 1)], false, kbps));
    }

    [Fact]
    public void Selected_summary_with_lists_counts_only_new_videos()
    {
        const double kbps = 8;
        var unread = new Row(List("x", "X"), 9);
        var partial = new Row(List("p", "P"), 1, Stats: Stats(10, done: 7, secs: 1000, newSecs: 300));
        var complete = new Row(List("c", "C"), 2, Stats: Stats(4, done: 4, secs: 400));
        var video = new Row(Video("v", dur: 60), 3);

        Assert.Equal("Listelerin videoları indirirken okunur, indirilmişler atlanır", PlaylistSummary.SelectedSummary([partial, unread], true, kbps));
        Assert.Equal("Seçili listelerde indirilmemiş video yok", PlaylistSummary.SelectedSummary([complete], true, kbps));
        Assert.Equal($"4 video · 6:00 · ≈ {Fmt.Bytes(360_000)}", PlaylistSummary.SelectedSummary([partial, complete, video], false, kbps));
    }

    [Fact]
    public void List_card_texts()
    {
        Assert.Null(ListStatsText.Count(null));
        Assert.Equal("12 video", ListStatsText.Count(Stats(12)));
        Assert.Equal("ÜYELERE ÖZEL", ListStatsText.Members(Stats(3, members: 3)));
        Assert.Equal("2 ÜYE", ListStatsText.Members(Stats(3, members: 2)));
        Assert.Null(ListStatsText.Members(Stats(3)));
        Assert.Null(ListStatsText.Members(Stats(0)));
        Assert.Equal("Üyelere özel: 2 / 3 video", ListStatsText.MembersHint(Stats(3, members: 2)));

        Assert.Equal("Okunamadı", ListStatsText.Status(Stats(3), "zaman aşımı", true));
        Assert.Equal("Okunuyor…", ListStatsText.Status(null, null, true));
        Assert.Equal("", ListStatsText.Status(null, null, false));
        Assert.Equal("1:00 · Tümü indirildi", ListStatsText.Status(Stats(2, done: 2, secs: 60), null, true));
        Assert.Equal("1/2 indirildi", ListStatsText.Status(Stats(2, done: 1), null, true));
        Assert.Equal("", ListStatsText.Status(Stats(2), null, true));
        Assert.Equal("Tümü indirildi", ListStatsText.Status(Stats(2, done: 2), null, true));
    }

    // ================= Boyut tahmini =================

    [Theory]
    [InlineData(DownloadMode.Video, 1080, AudioFormat.Original, 4430)]
    [InlineData(DownloadMode.Video, 0, AudioFormat.Original, 9130)]
    [InlineData(DownloadMode.Video, 2160, AudioFormat.Mp3, 18130)]
    [InlineData(DownloadMode.Video, 900, AudioFormat.Original, 4430)]
    [InlineData(DownloadMode.Audio, 2160, AudioFormat.Mp3, 245)]
    [InlineData(DownloadMode.Audio, 0, AudioFormat.Flac, 900)]
    [InlineData(DownloadMode.Audio, 0, AudioFormat.Wav, 1411)]
    [InlineData(DownloadMode.Audio, 0, AudioFormat.M4a, 150)]
    public void Kbps_by_mode_height_and_audio(DownloadMode mode, int height, AudioFormat audio, double expected) =>
        Assert.Equal(expected, SizeEstimate.Kbps(mode, height, audio));

    [Fact]
    public void Size_text()
    {
        Assert.Equal(1_000_000, SizeEstimate.Bytes(1000, 8));
        Assert.Equal("—", SizeEstimate.Text(0, 4430));
        Assert.Equal(Fmt.Bytes(600.0 * 4430 * 1000 / 8), SizeEstimate.Text(600, 4430));
    }

    // ================= Alt liste toplama =================

    private static PlaylistInfo Resolved(string channel, params PlaylistEntry[] entries) => new()
    {
        Kind = LinkKind.Playlist, Id = "p", Title = "Okunan", Channel = channel, Entries = entries,
    };

    [Fact]
    public void Pending_skips_lists_unavailable_and_archived_keeping_positions()
    {
        var list = List("PLx", "Kurs A", channel: "Satır Kanalı");
        var resolved = Resolved("", Video("v1"), List("sub", "İç liste"), Video("v3", unavailable: true), Video("v4"), Video("v5", members: true));
        var items = PlaylistCollect.Pending(list, resolved, "Sayfa", new HashSet<string> { "v4" });

        Assert.Equal(["v1", "v5"], items.Select(x => x.Entry.Id));
        Assert.Equal([1, 5], items.Select(x => x.Index));
        Assert.All(items, x =>
        {
            Assert.Equal("Kurs A", x.List);
            Assert.Equal("Satır Kanalı", x.Channel);
            Assert.Equal("https://www.youtube.com/playlist?list=PLx", x.ListUrl);
            Assert.False(x.WasDone);
        });

        Assert.Equal(["v5"], PlaylistCollect.Pending(list, resolved, null, new HashSet<string>(), onlyMembers: true).Select(x => x.Entry.Id));
    }

    [Theory]
    [InlineData("Liste Kanalı", "Satır", "Sayfa", "Liste Kanalı")]
    [InlineData("  ", "Satır", "Sayfa", "Satır")]
    [InlineData("", null, "Sayfa", "Sayfa")]
    [InlineData("", null, null, null)]
    public void Pending_channel_falls_back(string listChannel, string? rowChannel, string? page, string? expected)
    {
        var items = PlaylistCollect.Pending(List("p", "P", rowChannel), Resolved(listChannel, Video("v")), page, new HashSet<string>());
        Assert.Equal(expected, Assert.Single(items).Channel);
    }

    [Fact]
    public void Number_keeps_or_reverses_per_list()
    {
        CollectItem Item(string id, string list, int index) => new(Video(id), list, index, $"u/{list}", null, false);
        var items = new[] { Item("a1", "A", 1), Item("a3", "A", 3), Item("b2", "B", 2), Item("a5", "A", 5), Item("b4", "B", 4) };

        Assert.Equal([("a1", 1), ("a3", 3), ("b2", 2), ("a5", 5), ("b4", 4)],
            PlaylistCollect.Number(items, reverse: false).Select(x => (x.Item.Entry.Id, x.Index)));
        Assert.Equal([("a5", 1), ("a3", 2), ("a1", 3), ("b4", 1), ("b2", 2)],
            PlaylistCollect.Number(items, reverse: true).Select(x => (x.Item.Entry.Id, x.Index)));
    }
}
