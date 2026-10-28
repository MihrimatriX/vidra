namespace Vidra.Core.Models;

/// <summary>
/// "2:15", "135" ya da "01:02:03" — yt-dlp'nin anladığı zaman biçimleri.
/// Precise: kare hassasiyetinde kesim; videoyu yeniden kodlar. Kapalıyken anahtar karede, kopyalayarak keser.
/// </summary>
public sealed record Clip(string Start, string End, bool Precise = false);

/// <summary>İşin geldiği playlist ve videonun listedeki sırası (1 tabanlı) — albüm/parça etiketi için.</summary>
public sealed record PlaylistRef(string Title, int Index);

/// <summary>Adlandırılmış biçim seçimi. Hazır profiller koddadır; kullanıcınınkiler AppSettings.Profiles'ta.</summary>
public sealed record DownloadProfile(string Name, DownloadOptions Options, bool BuiltIn = false)
{
    public static readonly IReadOnlyList<DownloadProfile> BuiltIns =
    [
        new("Müzik · MP3", new() { Mode = DownloadMode.Audio, AudioFormat = AudioFormat.Mp3, MusicMode = true }, true),
        new("1080p · MP4", new() { Height = 1080, Container = ContainerFormat.Mp4 }, true),
        new("Telefon · 720p", new() { Height = 720, Container = ContainerFormat.Mp4, Codec = CodecPreference.H264, MaxFps30 = true }, true),
        new("Orijinal · en iyi", new() { Height = null, Container = ContainerFormat.Original }, true),
    ];

    /// <summary>Profil yalnızca biçimi belirler; altyazı, klip gibi videoya özel seçimler kullanıcıda kalır.</summary>
    public DownloadOptions ApplyTo(DownloadOptions o) => o with
    {
        Mode = Options.Mode,
        Height = Options.Height,
        Container = Options.Container,
        Codec = Options.Codec,
        MaxFps30 = Options.MaxFps30,
        AudioFormat = Options.AudioFormat,
        MusicMode = Options.MusicMode,
    };
}

/// <summary>Tek bir indirmenin biçim seçimi. Değişmez; arayüz "with" ile kopyalar.</summary>
public sealed record DownloadOptions
{
    public DownloadMode Mode { get; init; } = DownloadMode.Video;

    /// <summary>null = en iyi; aksi hâlde hedef yükseklik (px). Videoda yoksa sıralama en yakın düşüğe iner.</summary>
    public int? Height { get; init; } = 1080;

    /// <summary>Varsayılan Original: YouTube'daki akışlar olduğu gibi birleştirilir, kodek zorlanmaz.</summary>
    public ContainerFormat Container { get; init; } = ContainerFormat.Original;
    public CodecPreference Codec { get; init; } = CodecPreference.Auto;
    public bool MaxFps30 { get; init; }
    public AudioFormat AudioFormat { get; init; } = AudioFormat.Mp3;

    public bool Subtitles { get; init; } = true;

    /// <summary>Bu indirmeye özel dil listesi; null ise ayarlardaki varsayılan.</summary>
    public IReadOnlyList<string>? SubLangs { get; init; }

    public bool EmbedThumbnail { get; init; } = true;
    public bool EmbedMetadata { get; init; } = true;
    public bool SponsorBlock { get; init; }
    public bool Chapters { get; init; } = true;
    public bool SplitChapters { get; init; }
    public Clip? Clip { get; init; }

    /// <summary>Ses modunda: kare kapak, ses yüksekliği eşitleme, temiz sanatçı adı (bkz. YtDlpArgs.MusicArgs).</summary>
    public bool MusicMode { get; init; }

    /// <summary>Playlist'ten kuyruğa alınan işlerde dolu; tek video indirmesinde null.</summary>
    public PlaylistRef? Playlist { get; init; }

    public string Describe()
    {
        if (Mode == DownloadMode.Audio) return $"Ses · {AudioFormat.Label()}{(MusicMode ? " · müzik" : "")}";
        var q = Height is { } h ? $"{h}p" : "En iyi";
        var codec = Codec == CodecPreference.Auto ? "" : $" · {Codec.ToString().ToUpperInvariant()}";
        var clip = Clip is null ? "" : " · klip";
        return $"{q} · {Container.Label()}{codec}{clip}";
    }
}
