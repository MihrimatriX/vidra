namespace Vidra.Core.Models;

/// <summary>Biten (veya iptal edilen) işin diske yazılan, sade kaydı.</summary>
public sealed record HistoryEntry
{
    public required string Id { get; init; }
    public required string Url { get; init; }
    public string? VideoId { get; init; }
    public required string Title { get; init; }
    public string? Channel { get; init; }
    public string? Thumbnail { get; init; }
    public double? Duration { get; init; }
    public required DownloadOptions Options { get; init; }
    public required JobStatus Status { get; init; }
    public long? Size { get; init; }
    public string? FilePath { get; init; }
    public string? Warning { get; init; }
    public string? Source { get; init; }
    public DateTimeOffset FinishedAt { get; init; }

    public static HistoryEntry From(DownloadJob j) => new()
    {
        Id = j.Id,
        Url = j.Url,
        VideoId = j.VideoId,
        Title = j.Title,
        Channel = j.Channel,
        Thumbnail = j.Thumbnail,
        Duration = j.Duration,
        Options = j.Options,
        Status = j.Status,
        Size = j.Total,
        FilePath = j.FilePath,
        Warning = j.Warning,
        Source = j.Source,
        FinishedAt = j.FinishedAt ?? DateTimeOffset.Now,
    };
}
