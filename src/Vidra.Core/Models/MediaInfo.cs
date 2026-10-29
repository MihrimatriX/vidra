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

/// <summary>
/// Language: çok dilli videolarda ses izinin dili ("en-US"); IsDefault: YouTube'un varsayılan (orijinal) izi;
/// AutoDub: YouTube'un otomatik dublajı.
/// </summary>
public sealed record AudioInfo(string Id, string Codec, double? Abr, double? Asr, long? Size,
    string? Language = null, bool IsDefault = true, bool AutoDub = false);

/// <summary>Seçilebilir ses dili (yalnızca birden çok dil varsa listelenir).</summary>
public sealed record AudioLanguage(string Code, string Name, bool Original, bool AutoDub);

/// <summary>YouTube'un "en çok tekrar oynatılan" grafiğinden bir dilim; Value 0..1.</summary>
public sealed record HeatPoint(double Start, double End, double Value);

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

    /// <summary>Birden çok ses dili varsa (dublaj) seçenekler; orijinal en başta. Tek dilde boş.</summary>
    public IReadOnlyList<AudioLanguage> AudioLanguages { get; init; } = [];

    /// <summary>Tüm ses akışları (dil seçimine göre kopyalanacak akışı bulmak için).</summary>
    public IReadOnlyList<AudioInfo> Audios { get; init; } = [];

    /// <summary>"En çok tekrar oynatılan" grafiği; yeterince izlenmemiş videolarda boş.</summary>
    public IReadOnlyList<HeatPoint> Heatmap { get; init; } = [];
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
