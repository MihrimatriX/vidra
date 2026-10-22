using Vidra.Core.Models;

namespace Vidra.Core.Queue;

public sealed record DayStat(DateOnly Day, int Count, long Bytes);

public sealed record ChannelStat(string Channel, int Count, long Bytes);

public sealed record HistoryStats(
    int Count, long Bytes, double Seconds, int VideoCount, int AudioCount,
    int WeekCount, long WeekBytes, IReadOnlyList<DayStat> Days, IReadOnlyList<ChannelStat> TopChannels)
{
    /// <summary>Yalnızca tamamlanan işler sayılır. Günler bugün dahil son <paramref name="days"/> gün, eskiden yeniye.</summary>
    public static HistoryStats From(IEnumerable<HistoryEntry> history, DateTimeOffset now, int days = 14, int topChannels = 5)
    {
        var done = history.Where(h => h.Status == JobStatus.Done).ToList();
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var byDay = done.GroupBy(h => DateOnly.FromDateTime(h.FinishedAt.LocalDateTime)).ToDictionary(g => g.Key, g => g.ToList());
        var dayStats = Enumerable.Range(0, days).Select(i => today.AddDays(i - days + 1)).Select(d =>
            byDay.TryGetValue(d, out var l) ? new DayStat(d, l.Count, l.Sum(h => h.Size ?? 0)) : new DayStat(d, 0, 0)).ToList();
        var week = done.Where(h => DateOnly.FromDateTime(h.FinishedAt.LocalDateTime) > today.AddDays(-7)).ToList();

        return new HistoryStats(
            done.Count,
            done.Sum(h => h.Size ?? 0),
            done.Sum(h => h.Duration ?? 0),
            done.Count(h => h.Options.Mode == DownloadMode.Video),
            done.Count(h => h.Options.Mode == DownloadMode.Audio),
            week.Count,
            week.Sum(h => h.Size ?? 0),
            dayStats,
            done.Where(h => !string.IsNullOrWhiteSpace(h.Channel))
                .GroupBy(h => h.Channel!)
                .Select(g => new ChannelStat(g.Key, g.Count(), g.Sum(h => h.Size ?? 0)))
                .OrderByDescending(c => c.Count).ThenByDescending(c => c.Bytes)
                .Take(topChannels)
                .ToList());
    }
}
