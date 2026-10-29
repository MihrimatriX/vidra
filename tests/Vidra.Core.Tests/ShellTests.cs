using Vidra.Core.Models;
using Vidra.Core.Queue;
using Vidra.Core.YtDlp;

namespace Vidra.Core.Tests;

public class FuzzyTests
{
    [Theory]
    [InlineData("gecmis", "Kuyruk & geçmiş")]
    [InlineData("kyrk", "Kuyruk")]
    [InlineData("AYAR", "Ayarlar")]
    [InlineData("ıstatistik", "İstatistik")]
    [InlineData("tum dur", "Tümünü duraklat")]
    public void Matches_folded_and_subsequence(string q, string text) => Assert.NotNull(Fuzzy.Score(q, text));

    [Theory]
    [InlineData("xyz", "Kuyruk")]
    [InlineData("kuyruk sepet", "Kuyruk & geçmiş")]
    [InlineData("kuyrukk", "Kuyruk")]
    public void Rejects_non_matches(string q, string text) => Assert.Null(Fuzzy.Score(q, text));

    [Fact]
    public void Empty_query_matches_everything() => Assert.Equal(0, Fuzzy.Score("  ", "Ne olursa"));

    [Fact]
    public void Contiguous_and_word_start_rank_higher()
    {
        // Bütün geçen kelime, harfleri dağınık geçenden; baştaki ve kısa olan da sondakinden önde
        var scattered = Fuzzy.Score("sepet", "Sesi performansa etki");
        Assert.NotNull(scattered);
        Assert.True(Fuzzy.Score("sepet", "Sepet") > scattered);
        Assert.True(Fuzzy.Score("ind", "İndir") > Fuzzy.Score("ind", "Kütüphane: bir video indirildi"));
        Assert.True(Fuzzy.Score("ses", "Ses biçimi") > Fuzzy.Score("ses", "Kuyruğu sesle bildir"));
    }
}

public class LinkExtractionTests
{
    [Fact]
    public void Finds_links_inside_free_text_and_url_files()
    {
        var text = """
            [InternetShortcut]
            URL=https://www.youtube.com/watch?v=abc123
            Şuna bak (https://youtu.be/xyz789). Bir de youtube.com/@kanal ve tekrar https://youtu.be/xyz789
            olmayan: https://example.com/watch?v=1
            """;
        Assert.Equal(
            ["https://www.youtube.com/watch?v=abc123", "https://youtu.be/xyz789", "https://youtube.com/@kanal"],
            YouTubeUrl.ExtractLinks(text));
    }

    [Fact]
    public void Keeps_playlist_query_and_drops_trailing_punctuation()
    {
        var links = YouTubeUrl.ExtractLinks("liste: https://www.youtube.com/playlist?list=PL123, bitti.");
        Assert.Equal(["https://www.youtube.com/playlist?list=PL123"], links);
        Assert.Equal(LinkKind.Playlist, YouTubeUrl.Classify(links[0]));
    }

    [Fact]
    public void Nothing_in_plain_text() => Assert.Empty(YouTubeUrl.ExtractLinks("merhaba dünya"));

    [Theory]
    [InlineData("https://www.notyoutube.com/watch?v=abc")]
    [InlineData("https://evil-youtube.com/watch?v=abc")]
    [InlineData("https://youtube.com.example.org/watch?v=abc")]
    public void Look_alike_hosts_are_ignored(string text) => Assert.Empty(YouTubeUrl.ExtractLinks(text));

    [Fact]
    public void Same_video_in_two_forms_counts_once() =>
        Assert.Single(YouTubeUrl.ExtractLinks("https://youtu.be/abc123 ve https://www.youtube.com/watch?v=abc123"));
}

public class QueueProgressTests
{
    private static DownloadJob Job(JobStatus s, double percent = 0, int stream = 1, DownloadMode mode = DownloadMode.Video, double? speed = null) =>
        new() { Url = "https://youtu.be/a", Options = new DownloadOptions { Mode = mode }, Status = s, Percent = percent, Stream = stream, Speed = speed };

    [Fact]
    public void Empty_queue()
    {
        var p = QueueProgress.From([]);
        Assert.True(p.IsEmpty);
        Assert.Equal(0, p.Fraction);
        Assert.Equal("Kuyruk boş", p.Describe());
    }

    [Fact]
    public void Video_streams_do_not_rewind()
    {
        var endOfVideo = QueueProgress.JobFraction(Job(JobStatus.Running, 100, stream: 1));
        var startOfAudio = QueueProgress.JobFraction(Job(JobStatus.Running, 0, stream: 2));
        Assert.Equal(endOfVideo, startOfAudio, 6);
        Assert.Equal(0.5, QueueProgress.JobFraction(Job(JobStatus.Running, 50, mode: DownloadMode.Audio)), 6);
        Assert.True(QueueProgress.JobFraction(Job(JobStatus.Processing)) > 0.9);
    }

    [Fact]
    public void Aggregates_counts_speed_and_ignores_errors_in_fraction()
    {
        var p = QueueProgress.From([
            Job(JobStatus.Running, 100, stream: 2, speed: 1000),
            Job(JobStatus.Queued),
            Job(JobStatus.Paused, 50, mode: DownloadMode.Audio),
            Job(JobStatus.Error, 80),
        ]);
        Assert.Equal((1, 1, 1, 1), (p.Running, p.Queued, p.Paused, p.Errors));
        Assert.Equal(1000, p.Speed);
        Assert.Equal(0.5, p.Fraction, 6);
        Assert.Equal("1 indiriliyor · 1 sırada · 1 duraklatıldı · 1 hata · %50", p.Describe());
    }
}
