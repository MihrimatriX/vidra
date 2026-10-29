namespace Vidra.Core.Models;

public enum CodecFamily { H264, Vp9, Av1, Other }

public sealed record StreamInfo(
    string Id,
    CodecFamily Codec,
    string VCodec,
    int? Width,
    double? Fps,
    double? Tbr,      // kbps
    long? Size,       // byte, yalnızca bu akış
    bool Hdr);

public sealed record QualityOption(int Height, double? MaxFps, bool Hdr, long? Size, IReadOnlyList<StreamInfo> Streams);

public sealed record AudioInfo(string Id, string Codec, double? Abr, double? Asr, long? Size);

public sealed record SubtitleTrack(string Code, string Name, bool Auto);

public sealed record Chapter(string Title, double Start, double End);

public sealed record VideoInfo
{
    public required string Id { get; init; }
    public required string Url { get; init; }
    public required string Title { get; init; }
    public string Channel { get; init; } = "";
    public string? Thumbnail { get; init; }
    public string Description { get; init; } = "";
    public double? Duration { get; init; }
    public string? UploadDate { get; init; }
    public long? Views { get; init; }
    public bool MembersOnly { get; init; }
    public bool Live { get; init; }
    public IReadOnlyList<Chapter> Chapters { get; init; } = [];
    public IReadOnlyList<QualityOption> Qualities { get; init; } = [];
    public AudioInfo? Audio { get; init; }

    /// <summary>En iyi AAC akışı: MP4 ve M4A seçimlerinde kopyalanan ses (Audio genelde Opus'tur).</summary>
    public AudioInfo? AudioAac { get; init; }
    public IReadOnlyList<SubtitleTrack> Subtitles { get; init; } = [];
    public int AutoTranslateCount { get; init; }
}

public sealed record PlaylistEntry
{
    public required string Id { get; init; }
    public required string Url { get; init; }
    public required string Title { get; init; }
    public double? Duration { get; init; }
    public string? UploadDate { get; init; }
    public long? Views { get; init; }
    public bool MembersOnly { get; init; }
    public bool Unavailable { get; init; }
    public string? Thumbnail { get; init; }
    public string? Channel { get; init; }

    /// <summary>Kanal sayfası (abonelik akışı ve aramada her video farklı kanaldan gelir: "kanala git").</summary>
    public string? ChannelUrl { get; init; }

    /// <summary>Öğe video değil, alt liste (ör. "Playlistlerim" akışındaki her playlist).</summary>
    public bool IsList { get; init; }
}

public sealed record PlaylistInfo
{
    public required LinkKind Kind { get; init; }
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Channel { get; init; } = "";
    public string Url { get; init; } = "";
    public IReadOnlyList<PlaylistEntry> Entries { get; init; } = [];
}
