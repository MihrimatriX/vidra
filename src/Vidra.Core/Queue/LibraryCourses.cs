using Vidra.Core.Models;
using Vidra.Core.Storage;
using Vidra.Core.YtDlp;

namespace Vidra.Core.Queue;

/// <summary>Kütüphanedeki bir kursun/playlistin tek dersi.</summary>
public sealed record CourseLesson(
    HistoryEntry Entry,
    int Index,
    string WatchKey,
    bool Watched)
{
    public string IndexText => Index > 0 ? Index.ToString("00", System.Globalization.CultureInfo.InvariantCulture) : "—";
}

/// <summary>Kütüphanede diskte duran derslerden oluşturulan kurs/playlist grubu.</summary>
public sealed record CourseGroup(
    string Key,
    string Title,
    string Channel,
    string? PlaylistUrl,
    string? FolderPath,
    string? Cover,
    IReadOnlyList<CourseLesson> Lessons,
    int WatchedCount,
    double TotalDuration,
    long TotalSize,
    DateTimeOffset LastDownloaded)
{
    public int TotalCount => Lessons.Count;
    public double Progress => TotalCount > 0 ? Math.Clamp((double)WatchedCount / TotalCount, 0, 1) : 0;
    public bool AllWatched => TotalCount > 0 && WatchedCount >= TotalCount;
    public CourseLesson? NextUnwatched => Lessons.FirstOrDefault(l => !l.Watched) ?? Lessons.FirstOrDefault();
    public string ProgressText => $"{WatchedCount} / {TotalCount} izlendi";
    public string SummaryLine => string.Join(" · ", new[]
    {
        Channel,
        $"{TotalCount} ders",
        TotalDuration > 0 ? TimeText.Format(TotalDuration) : "",
        TotalSize > 0 ? Fmt.Bytes(TotalSize) : "",
    }.Where(s => !string.IsNullOrEmpty(s)));
}

/// <summary>
/// Geçmişteki diskte duran playlist/kurs dosyalarını ders sırasına göre gruplar. Saf; testlenebilir.
/// </summary>
public static class LibraryCourses
{
    public static IReadOnlyList<CourseGroup> Group(IEnumerable<HistoryEntry> existingFiles, IReadOnlySet<string> watched)
    {
        var withPlaylist = existingFiles
            .Where(h => h.Status == JobStatus.Done && h.Options.Playlist is { Title.Length: > 0 })
            .DistinctBy(h => h.FilePath ?? h.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var groups = new List<CourseGroup>();
        foreach (var g in withPlaylist.GroupBy(GroupKey, StringComparer.OrdinalIgnoreCase))
        {
            var sample = g.First();
            var pl = sample.Options.Playlist!;
            var title = pl.Title;
            var channel = g.Select(x => x.Options.Playlist?.Channel ?? x.Channel).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";
            var playlistUrl = g.Select(x => x.Options.Playlist?.Url).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            var folder = g.Select(x => x.FilePath is { } p ? Path.GetDirectoryName(p) : null)
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .GroupBy(d => d!, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(x => x.Count())
                .Select(x => x.Key)
                .FirstOrDefault();

            var lessons = g
                .OrderBy(x => x.Options.Playlist!.Index > 0 ? x.Options.Playlist!.Index : int.MaxValue)
                .ThenBy(x => x.FinishedAt)
                .Select(x =>
                {
                    var key = WatchProgressStore.KeyFor(x.VideoId, x.FilePath);
                    return new CourseLesson(x, x.Options.Playlist!.Index, key, watched.Contains(key));
                })
                .ToList();

            var cover = lessons.FirstOrDefault(l => !string.IsNullOrEmpty(l.Entry.ThumbUrl))?.Entry.ThumbUrl;
            groups.Add(new CourseGroup(
                g.Key,
                title,
                channel,
                playlistUrl,
                folder,
                cover,
                lessons,
                lessons.Count(l => l.Watched),
                lessons.Sum(l => l.Entry.Duration ?? 0),
                lessons.Sum(l => l.Entry.Size ?? 0),
                lessons.Max(l => l.Entry.FinishedAt)));
        }

        return groups.OrderByDescending(c => c.LastDownloaded).ToList();
    }

    private static string GroupKey(HistoryEntry h)
    {
        var pl = h.Options.Playlist!;
        if (!string.IsNullOrWhiteSpace(pl.Url)) return "url:" + YouTubeUrl.CanonicalSourceUrl(pl.Url);
        var ch = pl.Channel ?? h.Channel ?? "";
        return $"name:{ch}|{pl.Title}";
    }
}
