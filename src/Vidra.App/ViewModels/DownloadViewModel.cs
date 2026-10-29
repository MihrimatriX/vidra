using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Vidra.Core;
using Vidra.Core.Models;
using Vidra.Core.Queue;
using Vidra.Core.YtDlp;

namespace Vidra.App.ViewModels;

/// <summary>
/// Kalite kartı. Height indirme seçeneğidir (null = en iyi: listedeki en yüksek kart), Res kartın gösterdiği yükseklik.
/// Hint: seçilecek akış ("AV1 · 2,6 Mbps · 30 fps"), araç ipucunda.
/// </summary>
public sealed record QualityCard(int? Height, int Res, string Title, string Tag, string SizeText, string Hint, long? Size, StreamInfo? Stream);

public sealed record AudioCard(AudioFormat Format, string Title, string Detail, string SizeText, long? Size);

public sealed record StreamRow(string Kind, string Id, string Detail, string Rate, string Size);

/// <summary>Çoklu kesimde ek aralık (ilk aralık ClipStart/ClipEnd).</summary>
public sealed partial class ClipRange(string start, string end) : ObservableObject
{
    [ObservableProperty] private string _start = start;
    [ObservableProperty] private string _end = end;
}

/// <summary>Klip çubuğunda bir aralık; 0..1000 birimlik tuvalde (Viewbox ile genişliğe yayılır).</summary>
public sealed record RangeBar(double Left, double Width);

public sealed partial class DownloadViewModel : ObservableObject
{

    /// <summary>Dönüştürülen formatların yaklaşık bit hızı (kbps); null = kopyalanan akışın kendi boyutu.</summary>
    private static readonly Dictionary<AudioFormat, (string Detail, double? Kbps)> AudioMeta = new()
    {
        [AudioFormat.Original] = ("en iyi akış", null),
        [AudioFormat.Mp3] = ("VBR · dönüşüm", 245),
        [AudioFormat.M4a] = ("AAC akışı", null),
        [AudioFormat.Flac] = ("kayıpsız · dönüşüm", 900),
        [AudioFormat.Wav] = ("ham PCM · dönüşüm", 1411),
    };

    private readonly AppHost _host;
    private readonly MainViewModel _main;
    private CancellationTokenSource? _probeCts;

    public DownloadViewModel(AppHost host, MainViewModel main)
    {
        _host = host;
        _main = main;
        var d = host.Settings.Defaults;
        _container = d.Container;
        _codec = d.Codec;
        _maxFps30 = d.MaxFps30;
        _subtitles = d.Subtitles;
        _embedThumbnail = d.EmbedThumbnail;
        _sponsorBlock = d.SponsorBlock;
        _chapters = d.Chapters;
        _transcript = d.Transcript;
        _comments = d.Comments;
        _mode = d.Mode;
        _musicMode = d.MusicMode;
        host.Queue.History.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Recent));
    }

    /// <summary>Boş ekranda: son indirilenler (aynı video bir kez) — farklı kalitede tekrar açmak ya da oynatmak için.</summary>
    public IReadOnlyList<HistoryEntry> Recent =>
        _host.Queue.History.Where(h => h.Status == JobStatus.Done).DistinctBy(h => h.Url).Take(5).ToList();

    [RelayCommand]
    private async Task Reopen(HistoryEntry? h)
    {
        if (h is null) return;
        Url = h.Url;
        await Analyze();
    }

    // Profiller: hazırlar + kullanıcının kaydettikleri
    public IReadOnlyList<DownloadProfile> Profiles => DownloadProfile.BuiltIns.Concat(_host.Settings.Profiles).ToList();
    [ObservableProperty] private bool _savingProfile;
    [ObservableProperty] private string _newProfileName = "";

    public AppHost Host => _host;
    public IReadOnlyList<Choice> Containers => Choices.Containers;
    public IReadOnlyList<Choice> Codecs => Choices.Codecs;
    public IReadOnlyList<Choice> FpsChoices => Choices.Fps;
    public IReadOnlyList<Choice> ModeChoices { get; } = [new(DownloadMode.Video, "Video"), new(DownloadMode.Audio, "Sadece ses")];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PlaylistUrl), nameof(IsQuery))] private string _url = "";

    /// <summary>Kutudaki metin bağlantı değil: Enter YouTube'da arar (kutu hem bağlantı hem arama kabul eder).</summary>
    public bool IsQuery => Url.Trim() is { Length: > 0 } t && YouTubeUrl.Classify(t) is null && !LooksLikeUrl(t);

    private static bool LooksLikeUrl(string t) =>
        t.Contains("://", StringComparison.Ordinal) || t.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ||
        (!t.Contains(' ') && t.Contains('/') && t.Contains('.'));

    /// <summary>İzleme bağlantısı bir playlistin içindeyse (watch?v=…&amp;list=…) o listenin adresi.</summary>
    public string? PlaylistUrl => YouTubeUrl.Classify(Url) == LinkKind.Video && YouTubeUrl.PlaylistId(Url) is { } id
        ? $"https://www.youtube.com/playlist?list={Uri.EscapeDataString(id)}"
        : null;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowEmpty))] private bool _isLoading;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _errorIsAuth;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasInfo), nameof(ShowEmpty))] private VideoInfo? _info;
    public bool HasInfo => Info is not null;
    public bool ShowEmpty => Info is null && !IsLoading;

    [ObservableProperty] private DownloadMode _mode;

    /// <summary>"Diğer seçenekler" açık mı: altyazı, klip, SponsorBlock, kodek… varsayılan olarak gizli.</summary>
    [ObservableProperty] private bool _showMore;

    // Video
    public ObservableCollection<QualityCard> Qualities { get; } = [];
    [ObservableProperty] private QualityCard? _selectedQuality;
    [ObservableProperty] private ContainerFormat _container;
    [ObservableProperty] private CodecPreference _codec;
    [ObservableProperty] private bool _maxFps30;

    // Ses
    public ObservableCollection<AudioCard> AudioCards { get; } = [];
    [ObservableProperty] private AudioCard? _selectedAudio;
    [ObservableProperty] private bool _musicMode;

    // Altyazı
    public ObservableCollection<Toggle> SubtitleLangs { get; } = [];
    [ObservableProperty] private bool _subtitles;
    [ObservableProperty] private string _subsInfo = "";

    // Gelişmiş
    [ObservableProperty] private bool _clipEnabled;
    [ObservableProperty] private string _clipStart = "";
    [ObservableProperty] private string _clipEnd = "";

    /// <summary>Kare hassasiyetinde kesim: videoyu yeniden kodlar. Kapalıyken anahtar karede kopyalayarak keser.</summary>
    [ObservableProperty] private bool _clipPrecise;
    [ObservableProperty] private Chapter? _selectedChapter;
    [ObservableProperty] private bool _splitChapters;
    public ObservableCollection<Toggle> SponsorCats { get; } = [];

    /// <summary>Ek kesim aralıkları; her biri ayrı dosya olarak iner.</summary>
    public ObservableCollection<ClipRange> ExtraClips { get; } = [];
    public ObservableCollection<RangeBar> ClipBars { get; } = [];

    /// <summary>YouTube'un "en çok tekrar oynatılan" grafiği, 1000×40 birimlik tuvalde.</summary>
    [ObservableProperty] private PointCollection _heatPoints = [];
    public bool HasHeatmap => Info?.Heatmap.Count > 0;
    public string ClipHint => HasHeatmap ? "Bir ya da birkaç aralık · en çok izleneni önerir" : "Yalnızca bir ya da birkaç aralığı indir";

    // Ses dili (dublaj): "" = YouTube'un varsayılanı (orijinal)
    public ObservableCollection<Choice> AudioLanguageChoices { get; } = [];
    [ObservableProperty] private string _audioLanguage = "";
    public bool HasAudioLanguages => AudioLanguageChoices.Count > 1;
    public string AudioLanguageHint => Info?.AudioLanguages is { Count: > 1 } l
        ? $"{l.Count} dil" + (l.Any(x => x.AutoDub) ? " · otomatik dublaj dahil" : "")
        : "";

    // Canlı yayın
    [ObservableProperty] private bool _liveFromStart;

    /// <summary>Henüz başlamamış yayın/prömiyer bağlantısı: "Başlayınca indir" düğmesi bunu kuyruğa alır.</summary>
    [ObservableProperty] private string? _upcomingUrl;

    // Ortak anahtarlar
    [ObservableProperty] private bool _embedThumbnail;
    [ObservableProperty] private bool _sponsorBlock;
    [ObservableProperty] private bool _chapters;
    [ObservableProperty] private bool _transcript;
    [ObservableProperty] private bool _comments;

    // Hesaplanan özet
    public ObservableCollection<StreamRow> StreamRows { get; } = [];
    [ObservableProperty] private string _estimate = "";
    [ObservableProperty] private string _filePreview = "";
    [ObservableProperty] private string _clipText = "";
    [ObservableProperty] private bool _clipValid;
    [ObservableProperty] private string _metaHighest = "—";
    [ObservableProperty] private string _metaAudio = "—";
    [ObservableProperty] private string _metaUpload = "—";
    [ObservableProperty] private string _metaViews = "—";

    /// <summary>Dönüştürme satırı: akışlar kopyalanıyor mu, yoksa neyin yeniden kodlandığı.</summary>
    [ObservableProperty] private bool _isLossless = true;
    [ObservableProperty] private string _conversionText = "";

    /// <summary>Kapalı "Diğer seçenekler" başlığında açık olanların özeti.</summary>
    [ObservableProperty] private string _moreSummary = "";

    public bool UseArchive
    {
        get => _host.Settings.UseArchive;
        set { _host.SettingsStore.Update(s => s.UseArchive = value); OnPropertyChanged(); }
    }

    public string OutputDir => _host.Settings.OutputDir;
    public string SponsorHint => $"{_host.Settings.SponsorCategories.Count} kategori kesilir";

    partial void OnModeChanged(DownloadMode value) => Recalculate();
    partial void OnSelectedQualityChanged(QualityCard? value) => Recalculate();
    partial void OnSelectedAudioChanged(AudioCard? value) => Recalculate();
    partial void OnMusicModeChanged(bool value) => Recalculate();
    partial void OnSubtitlesChanged(bool value) => Recalculate();
    partial void OnChaptersChanged(bool value) => Recalculate();
    partial void OnClipEnabledChanged(bool value) => Recalculate();
    partial void OnClipStartChanged(string value) => Recalculate();
    partial void OnClipEndChanged(string value) => Recalculate();
    partial void OnClipPreciseChanged(bool value) => Recalculate();
    partial void OnAudioLanguageChanged(string value) => Recalculate();
    partial void OnLiveFromStartChanged(bool value) => Recalculate();
    partial void OnEmbedThumbnailChanged(bool value) => Recalculate();
    partial void OnSponsorBlockChanged(bool value) => Recalculate();
    partial void OnSplitChaptersChanged(bool value) => Recalculate();
    partial void OnTranscriptChanged(bool value) => Recalculate();
    partial void OnCommentsChanged(bool value) => Recalculate();
    partial void OnContainerChanged(ContainerFormat value) => RebuildQualities();
    partial void OnCodecChanged(CodecPreference value) => RebuildQualities();
    partial void OnMaxFps30Changed(bool value) => RebuildQualities();

    partial void OnSelectedChapterChanged(Chapter? value)
    {
        if (value is null) return;
        ClipEnabled = true;
        ClipStart = TimeText.Format(value.Start);
        ClipEnd = TimeText.Format(value.End);
    }

    /// <summary>Bir metin yapıştırıldı: birden çok video bağlantısıysa doğrudan kuyruğa al.</summary>
    public bool TryEnqueueMany(string text)
    {
        var links = YouTubeUrl.ExtractVideoLinks(text);
        if (links.Count < 2) return false;
        EnqueueLinks(links, "Toplu yapıştırma");
        return true;
    }

    /// <summary>Video bağlantılarını varsayılan biçimle kuyruğa alır (başlıklar indirme başlayınca dolar).</summary>
    public void EnqueueLinks(IReadOnlyList<string> links, string source)
    {
        _host.Queue.Enqueue(links.Select(l => new NewJob(l, l, _host.Settings.Defaults, Source: source)));
        _main.Go(Page.Queue);
        _main.ShowToast($"{links.Count} video kuyruğa eklendi");
    }

    [RelayCommand]
    private async Task Analyze()
    {
        var url = Url.Trim();
        if (url.Length == 0) return;
        var kind = YouTubeUrl.Classify(url);
        if (kind is null && IsQuery)
        {
            Error = null;
            Url = "";
            _main.Search.Query = url;
            _main.Go(Page.Search);
            await _main.Search.SearchCommand.ExecuteAsync(null);
            return;
        }
        if (kind is null)
        {
            Error = "Bu bir YouTube video, Shorts, playlist, kanal ya da canlı yayın bağlantısı değil.";
            ErrorIsAuth = false;
            return;
        }
        if (kind is not LinkKind.Video)
        {
            await _main.OpenLinkAsync(url);
            return;
        }

        _probeCts?.Cancel();
        var cts = _probeCts = new CancellationTokenSource();
        IsLoading = true;
        Error = null;
        UpcomingUrl = null;
        Info = null;
        ClipEnabled = false;
        ClipPrecise = false;
        try
        {
            var r = await _host.YtDlp.ProbeAsync(url, ct: cts.Token);
            if (cts.IsCancellationRequested) return;
            if (r is VideoInfo v) Load(v);
            else if (r is PlaylistInfo p) { _main.Playlist.Show(p, url); _main.Go(Page.Playlist); }
        }
        catch (ProbeException e)
        {
            Error = e.Message;
            ErrorIsAuth = e.Kind is ErrorKind.Auth or ErrorKind.BotCheck;
            UpcomingUrl = e.Kind == ErrorKind.Upcoming ? url : null;
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Error = e.Message;
            ErrorIsAuth = false;
        }
        finally
        {
            if (_probeCts == cts) IsLoading = false;
        }
    }

#if DEBUG
    /// <summary>Ekran görüntüsü turu: boş ekran (son indirilenler).</summary>
    internal void ShowEmptyForTour()
    {
        Info = null;
        Url = "";
        Error = null;
    }
#endif

    public void ShowVideo(VideoInfo v)
    {
        Error = null;
        Load(v);
    }

    private void Load(VideoInfo v)
    {
        Info = v;
        var top = v.Qualities.FirstOrDefault();
        MetaHighest = top is null ? "—" : $"{top.Height}p{(top.MaxFps is > 30 ? top.MaxFps.Value.ToString("0") : "")}{(top.Hdr ? " HDR" : "")}";
        MetaAudio = v.Audio is { } a ? $"{a.Codec} {Fmt.Kbps(a.Abr)}" : "—";
        MetaUpload = Fmt.Date(v.UploadDate) is { Length: > 0 } up ? up : "—";
        MetaViews = Fmt.Count(v.Views);

        // Altyazı: videodaki diller + ayarlardaki varsayılanlar (otomatik çeviri olarak)
        SubtitleLangs.Clear();
        var wanted = _host.Settings.SubLangs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tracks = v.Subtitles.ToList();
        foreach (var l in _host.Settings.SubLangs.Where(l => tracks.All(t => !t.Code.Equals(l, StringComparison.OrdinalIgnoreCase))))
            tracks.Add(new SubtitleTrack(l, l.ToUpperInvariant(), true));
        foreach (var t in tracks)
            SubtitleLangs.Add(new Toggle(t.Code, t.Name, t.Auto ? "otomatik" : "manuel", wanted.Contains(t.Code)));
        foreach (var t in SubtitleLangs) t.PropertyChanged += (_, _) => Recalculate();
        SubsInfo = $"{v.Subtitles.Count(s => !s.Auto)} manuel · {v.AutoTranslateCount} dile otomatik çeviri";

        AudioLanguageChoices.Clear();
        foreach (var l in v.AudioLanguages)
            AudioLanguageChoices.Add(new Choice(l.Original ? "" : l.Code, l.Original ? $"{l.Name} · orijinal" : l.AutoDub ? $"{l.Name} · otomatik dublaj" : l.Name));
        _audioLanguage = "";
        OnPropertyChanged(nameof(AudioLanguage));
        OnPropertyChanged(nameof(HasAudioLanguages));
        OnPropertyChanged(nameof(AudioLanguageHint));

        ExtraClips.Clear();
        LiveFromStart = false;
        HeatPoints = BuildHeat(v);
        OnPropertyChanged(nameof(HasHeatmap));
        OnPropertyChanged(nameof(ClipHint));

        SponsorCats.Clear();
        foreach (var (id, label) in Vidra.Core.Models.SponsorCategories.All)
        {
            var t = new Toggle(id, label, null, _host.Settings.SponsorCategories.Contains(id));
            t.PropertyChanged += (_, _) =>
            {
                _host.SettingsStore.Update(s => s.SponsorCategories = SponsorCats.Where(x => x.IsOn).Select(x => x.Id).ToList());
                OnPropertyChanged(nameof(SponsorHint));
            };
            SponsorCats.Add(t);
        }

        AudioCards.Clear();
        foreach (var f in Choices.AudioOrder)
        {
            var (detail, kbps) = AudioMeta[f];
            var src = AudioFor(f);
            long? size = kbps is { } k && v.Duration is { } dur ? (long)(k * 1000 / 8 * dur) : src?.Size;
            if (kbps is null && src is not null)
                // Kart dar: kopyalanan akış yalnızca kodek ve bit hızıyla; dönüştürme durumu alttaki satırda yazar
                detail = Conversion.Reencodes(new DownloadOptions { Mode = DownloadMode.Audio, AudioFormat = f }, src.Codec).Count > 0
                    ? $"{AudioName(src.Codec)}'tan dönüşüm"
                    : $"{AudioName(src.Codec)} · {Fmt.Kbps(src.Abr)}";
            AudioCards.Add(new AudioCard(f, f.Label(), detail, "≈ " + Fmt.Bytes(size), size));
        }
        var wantAudio = _host.Settings.Defaults.AudioFormat == AudioFormat.Opus ? AudioFormat.Original : _host.Settings.Defaults.AudioFormat;
        SelectedAudio = AudioCards.FirstOrDefault(a => a.Format == wantAudio) ?? AudioCards.First();

        RebuildQualities();
    }

    private void RebuildQualities()
    {
        if (Info is null) return;
        var keepBest = SelectedQuality is { Height: null };
        var wantHeight = keepBest ? null : SelectedQuality?.Res ?? _host.Settings.Defaults.Height;
        Qualities.Clear();
        // 360p altı yalnızca başka seçenek azsa listelenir (kartlar tek satıra sığsın).
        // Standart dışı yükseklikler de (ör. geniş 4K → 1974p) listelenir; -S res: en küçük boyuta göre sıralar.
        var list = Info.Qualities.Count(q => q.Height >= 360) >= 3 ? Info.Qualities.Where(q => q.Height >= 360).ToList() : Info.Qualities.ToList();
        var audio = Container == ContainerFormat.Mp4 ? Info.AudioAac ?? Info.Audio : Info.Audio;
        foreach (var q in list)
        {
            var s = ProbeParser.PickStream(q, Codec, Container, MaxFps30);
            long? total = s?.Size is { } sz ? sz + (audio?.Size ?? 0) : q.Size;
            var label = q.Height switch { >= 4320 => "8K", >= 2160 => "4K", >= 1440 => "2K", _ => null };
            // En yüksek kart "en iyi"dir: Height null → yt-dlp yeni çıkan daha yüksek akışı da alır, profil/varsayılan "en iyi" buna eşlenir
            var best = Qualities.Count == 0;
            var tag = string.Join(" · ", new[] { best ? "En iyi" : null, label, q.Hdr ? "HDR" : null, q.MaxFps is > 30 ? $"{q.MaxFps.Value:0}fps" : null }.Where(x => x is not null));
            var hint = s is null ? "" : string.Join(" · ", new[] { CodecName(s), Fmt.Kbps(s.Tbr), s.Fps is { } f ? $"{f:0} fps" : null }.Where(x => !string.IsNullOrEmpty(x)));
            Qualities.Add(new QualityCard(best ? null : q.Height, q.Height, $"{q.Height}p", tag, "≈ " + Fmt.Bytes(total), hint, total, s));
        }
        if (Qualities.Count == 0) Qualities.Add(new QualityCard(null, 0, "En iyi", "", "", "", null, null));

        SelectedQuality = wantHeight is not { } want ? Qualities.FirstOrDefault()
            : Qualities.FirstOrDefault(q => q.Res == want)
              ?? Qualities.FirstOrDefault(q => q.Res <= want)
              ?? Qualities.LastOrDefault();
        Recalculate();
    }

    private static string CodecName(StreamInfo s) => s.Codec switch
    {
        CodecFamily.H264 => "H.264",
        CodecFamily.Vp9 => "VP9",
        CodecFamily.Av1 => "AV1",
        _ => s.VCodec.Split('.')[0],
    };

    private static string AudioName(string codec) => codec switch { "opus" => "Opus", "mp4a" => "AAC", _ => codec };

    /// <summary>Seçime göre kopyalanacak ses akışı: MP4/M4A'da AAC, aksi hâlde en iyisi (genelde Opus).</summary>
    private AudioInfo? AudioFor(AudioFormat f) => PickAudio(f == AudioFormat.M4a);

    private AudioInfo? CurrentAudio(DownloadOptions o) => o.Mode == DownloadMode.Audio ? AudioFor(o.AudioFormat) : PickAudio(o.Container == ContainerFormat.Mp4);

    /// <summary>Kopyalanacak ses akışı: seçili dildeki iz (yoksa varsayılan), MP4/M4A'da AAC tercihli.</summary>
    private AudioInfo? PickAudio(bool aac)
    {
        if (Info is not { } v) return null;
        var pool = AudioLanguage.Length > 0
            ? v.Audios.Where(a => a.Language?.StartsWith(AudioLanguage, StringComparison.OrdinalIgnoreCase) == true).ToList()
            : [];
        if (pool.Count == 0) return aac ? v.AudioAac ?? v.Audio : v.Audio;
        return aac ? pool.FirstOrDefault(a => a.Codec == "mp4a") ?? pool[0] : pool[0];
    }

    /// <summary>Basamaklı alan grafiği: her dilim kendi yüksekliğinde düz çizgi (YouTube'daki gibi tepeler okunur).</summary>
    private static PointCollection BuildHeat(VideoInfo v)
    {
        var pts = new PointCollection();
        var dur = v.Heatmap.Count > 0 ? v.Heatmap.Max(p => p.End) : 0;
        if (dur > 0)
        {
            pts.Add(new Point(0, 40));
            foreach (var h in v.Heatmap)
            {
                var y = 40 - h.Value * 36;
                pts.Add(new Point(h.Start / dur * 1000, y));
                pts.Add(new Point(h.End / dur * 1000, y));
            }
            pts.Add(new Point(1000, 40));
        }
        pts.Freeze();
        return pts;
    }

    /// <summary>Geçerli ek aralıklar (bitiş başlangıçtan sonra).</summary>
    private List<Clip> ExtraClipList() => ExtraClips
        .Select(r => (r, a: TimeText.ToSeconds(r.Start), b: TimeText.ToSeconds(r.End)))
        .Where(x => x.a is not null && x.b is not null && x.b > x.a)
        .Select(x => new Clip(x.r.Start.Trim(), x.r.End.Trim(), ClipPrecise))
        .ToList();

    private void AddRange(string start, string end)
    {
        var r = new ClipRange(start, end);
        r.PropertyChanged += (_, _) => Recalculate();
        ExtraClips.Add(r);
    }

    /// <summary>Yeni aralık: sonuncunun bitişinden başlayan 30 saniye (video sonunu aşmadan).</summary>
    [RelayCommand]
    private void AddClipRange()
    {
        var last = ExtraClips.Count > 0 ? TimeText.ToSeconds(ExtraClips[^1].End) : TimeText.ToSeconds(ClipEnd);
        var start = last ?? 0;
        var end = Info?.Duration is { } d ? Math.Min(d, start + 30) : start + 30;
        if (end <= start) start = Math.Max(0, end - 30);
        ClipEnabled = true;
        AddRange(TimeText.Format(start), TimeText.Format(end));
        Recalculate();
    }

    [RelayCommand]
    private void RemoveClipRange(ClipRange? r)
    {
        if (r is not null && ExtraClips.Remove(r)) Recalculate();
    }

    /// <summary>"30" / "60": en çok izlenen tek pencere; "3x20": çakışmayan en iyi üç an, her biri ayrı klip.</summary>
    [RelayCommand]
    private void PickHighlights(string? spec)
    {
        if (Info is not { Heatmap.Count: > 0 } v) return;
        var (count, len) = spec switch { "60" => (1, 60d), "3x20" => (3, 20d), _ => (1, 30d) };
        var w = Heatmap.BestWindows(v.Heatmap, len, count);
        if (w.Count == 0) return;
        ExtraClips.Clear();
        ClipEnabled = true;
        ShowMore = true;
        ClipStart = TimeText.Format(w[0].Start);
        ClipEnd = TimeText.Format(w[0].End);
        foreach (var (a, b) in w.Skip(1)) AddRange(TimeText.Format(a), TimeText.Format(b));
        Recalculate();
    }

    /// <summary>Henüz başlamamış yayın/prömiyer: kuyruğa al, yt-dlp başlama saatini bekleyip indirir.</summary>
    [RelayCommand]
    private void EnqueueWhenLive()
    {
        if (UpcomingUrl is not { } url) return;
        _host.Queue.Enqueue([new NewJob(url, url, _host.Settings.Defaults with { WaitForLive = true }, Source: "Canlı yayın / prömiyer")]);
        UpcomingUrl = null;
        Error = null;
        _main.Go(Page.Queue);
        _main.ShowToast("Yayın başlayınca kendiliğinden indirilecek");
    }

    private Clip? CurrentClip()
    {
        if (!ClipEnabled) return null;
        var a = TimeText.ToSeconds(ClipStart);
        var b = TimeText.ToSeconds(ClipEnd);
        return a is not null && b is not null && b > a ? new Clip(ClipStart.Trim(), ClipEnd.Trim(), ClipPrecise) : null;
    }

    public DownloadOptions BuildOptions() => _host.Settings.Defaults with
    {
        Mode = Mode,
        Height = SelectedQuality?.Height,
        Container = Container,
        Codec = Codec,
        MaxFps30 = MaxFps30,
        AudioFormat = SelectedAudio?.Format ?? _host.Settings.Defaults.AudioFormat,
        Subtitles = Subtitles,
        SubLangs = SubtitleLangs.Count > 0 ? SubtitleLangs.Where(t => t.IsOn).Select(t => t.Id).ToList() : null,
        EmbedThumbnail = EmbedThumbnail,
        EmbedMetadata = EmbedThumbnail,
        SponsorBlock = SponsorBlock,
        Chapters = Chapters,
        SplitChapters = SplitChapters,
        Clip = CurrentClip(),
        Clips = ClipEnabled && ExtraClipList() is { Count: > 0 } more ? more : null,
        AudioLanguage = AudioLanguage.Length > 0 ? AudioLanguage : null,
        LiveFromStart = Info?.Live == true && LiveFromStart,
        MusicMode = MusicMode,
        Transcript = Transcript,
        Comments = Comments,
    };

    private void Recalculate()
    {
        if (Info is not { } v) return;
        var o = BuildOptions();

        var clips = YtDlpArgs.AllClips(o)
            .Select(c => (Start: TimeText.ToSeconds(c.Start)!.Value, End: TimeText.ToSeconds(c.End)!.Value))
            .Where(c => c.End > c.Start)
            .ToList();
        double? clipSecs = clips.Count == 0 ? null : clips.Sum(c => c.End - c.Start);
        ClipValid = clips.Count > 0;
        ClipText = !ClipEnabled ? ""
            : clips.Count > 1 ? $"{clips.Count} aralık · toplam {TimeText.Format(clipSecs)} · her aralık ayrı dosya olarak iner"
            : ClipValid ? $"Seçili aralık {TimeText.Format(clipSecs)} · yalnızca bu kısım indirilir"
            : "Zamanları 2:15 ya da 135 biçiminde gir; bitiş başlangıçtan sonra olmalı.";
        var ratio = clipSecs is { } cs && v.Duration is > 0 ? Math.Min(1, cs / v.Duration.Value) : 1;
        ClipBars.Clear();
        if (v.Duration is > 0 and var dur)
            foreach (var c in clips)
                ClipBars.Add(new RangeBar(Math.Clamp(c.Start / dur, 0, 1) * 1000, Math.Clamp((c.End - c.Start) / dur, 0, 1) * 1000));

        var video = o.Mode == DownloadMode.Video ? SelectedQuality?.Stream : null;
        var audio = CurrentAudio(o);
        var ext = Conversion.PredictExt(o, video?.VCodec, audio?.Codec);

        StreamRows.Clear();
        if (video is { } s)
        {
            var h = SelectedQuality!.Res;
            var res = s.Width is { } w ? $"{w}×{h}" : $"{h}p";
            StreamRows.Add(new StreamRow("Video", s.Id, $"{res}{(s.Fps is { } f ? $" · {f:0}fps" : "")} · {s.VCodec.Split('.')[0]}{(s.Hdr ? " · HDR" : "")}",
                Fmt.Kbps(s.Tbr), Fmt.Bytes(s.Size)));
        }
        if (audio is { } a)
            StreamRows.Add(new StreamRow("Ses", a.Id,
                $"{a.Codec}{(a.Asr is { } asr ? $" · {asr / 1000:0.#} kHz" : "")} → .{ext}",
                Fmt.Kbps(a.Abr), Fmt.Bytes(a.Size)));

        var reencodes = Conversion.Reencodes(o, audio?.Codec);
        IsLossless = reencodes.Count == 0;
        ConversionText = !IsLossless ? "Yeniden kodlanır: " + string.Join(", ", reencodes)
            : o.Mode == DownloadMode.Video
                ? video is not null && audio is not null
                    ? $"Dönüştürme yok · {CodecName(video)} + {AudioName(audio.Codec)} akışları olduğu gibi .{ext} dosyasına kopyalanır"
                    : "Dönüştürme yok · akışlar olduğu gibi kopyalanır"
                : audio is not null ? $"Dönüştürme yok · {AudioName(audio.Codec)} akışı olduğu gibi .{ext} olarak kaydedilir"
                : $"Dönüştürme yok · .{ext} olarak kaydedilir";

        var more = new List<string>();
        if (o.Mode == DownloadMode.Video && o.Codec != CodecPreference.Auto) more.Add(Choices.Codecs.First(c => Equals(c.Value, o.Codec)).Label);
        if (o.Mode == DownloadMode.Video && o.MaxFps30) more.Add("≤ 30 fps");
        if (o.Subtitles && o.SubLangs is { Count: > 0 } langs && !(o.Mode == DownloadMode.Audio && o.MusicMode))
            more.Add("altyazı " + string.Join('+', langs).ToUpperInvariant());
        if (clips.Count > 1) more.Add($"{clips.Count} klip");
        else if (clips.Count == 1) more.Add($"klip {TimeText.Format(clipSecs)}");
        if (o.AudioLanguage is not null && AudioLanguageChoices.FirstOrDefault(c => Equals(c.Value, o.AudioLanguage)) is { } lang)
            more.Add("ses: " + lang.Label.Split(" · ")[0]);
        if (o.SponsorBlock) more.Add("SponsorBlock");
        if (o.Chapters && v.Chapters.Count > 0) more.Add("bölüm işaretleri");
        if (o.SplitChapters) more.Add("bölümlere ayır");
        if (o.EmbedThumbnail) more.Add("kapak & etiket");
        if (o.Transcript) more.Add("metin döküm");
        if (o.Comments) more.Add("yorumlar");
        MoreSummary = more.Count > 0 ? string.Join(" · ", more) : "Altyazı, klip, SponsorBlock, kodek";

        long? size = o.Mode == DownloadMode.Audio ? SelectedAudio?.Size : SelectedQuality?.Size;
        Estimate = size is { } sz ? Fmt.Bytes(sz * ratio) : "—";

        FilePreview = "→ " + Templates.Preview(_host.Settings.OutputTemplate, new Dictionary<string, object?>
        {
            ["title"] = v.Title, ["uploader"] = v.Channel, ["channel"] = v.Channel, ["id"] = v.Id, ["ext"] = ext,
            ["upload_date"] = v.UploadDate, ["height"] = SelectedQuality?.Res,
        });
    }

    private void Enqueue(bool startNow)
    {
        if (Info is not { } v) return;
        _host.Queue.Enqueue([new NewJob(v.Url, v.Title, BuildOptions(), v.Channel, v.Thumbnail, v.Duration)], startNow);
        if (startNow) _main.Go(Page.Queue);
        else _main.ShowToast("Kuyruğa eklendi (bekletiliyor)", "Kuyruğu aç", () => _main.Go(Page.Queue));
    }

    [RelayCommand] private void DownloadNow() => Enqueue(true);
    [RelayCommand] private void AddToQueue() => Enqueue(false);

    [RelayCommand]
    private void PickFolder()
    {
        var dlg = new OpenFolderDialog { InitialDirectory = _host.Settings.OutputDir, Title = "İndirme klasörünü seç" };
        if (dlg.ShowDialog() != true) return;
        _host.SettingsStore.Update(s => s.OutputDir = dlg.FolderName);
        OnPropertyChanged(nameof(OutputDir));
        Recalculate();
    }

    [RelayCommand] private void OpenSettings() => _main.Go(Page.Settings);
    [RelayCommand] private void OpenAccount() => _main.Go(Page.Account);
    [RelayCommand] private void OpenQueue() => _main.Go(Page.Queue);

    [RelayCommand]
    private void ApplyProfile(DownloadProfile? p)
    {
        if (p is null) return;
        var o = p.Options;
        // Kapsayıcı/kodek değişimi kalite kartlarını yeniden kurar; kaliteyi en son seç
        Container = o.Container;
        Codec = o.Codec;
        MaxFps30 = o.MaxFps30;
        MusicMode = o.MusicMode;
        Mode = o.Mode;
        var audio = o.AudioFormat == AudioFormat.Opus ? AudioFormat.Original : o.AudioFormat;
        SelectedAudio = AudioCards.FirstOrDefault(a => a.Format == audio) ?? SelectedAudio;
        SelectedQuality = o.Height is not { } h ? Qualities.FirstOrDefault()
            : Qualities.FirstOrDefault(q => q.Res == h) ?? Qualities.FirstOrDefault(q => q.Res < h) ?? Qualities.LastOrDefault();
    }

    [RelayCommand]
    private void SaveProfile()
    {
        var name = NewProfileName.Trim();
        if (name.Length == 0) return;
        // Videoya özel seçimler (klip, dil listesi) profile girmez
        var p = new DownloadProfile(name, BuildOptions() with { Clip = null, Clips = null, SubLangs = null, Playlist = null, AudioLanguage = null, LiveFromStart = false });
        _host.SettingsStore.Update(s => s.Profiles = [.. s.Profiles.Where(x => x.Name != name), p]);
        NewProfileName = "";
        SavingProfile = false;
        OnPropertyChanged(nameof(Profiles));
    }

    [RelayCommand]
    private void DeleteProfile(DownloadProfile? p)
    {
        if (p is null || p.BuiltIn) return;
        _host.SettingsStore.Update(s => s.Profiles = s.Profiles.Where(x => x.Name != p.Name).ToList());
        OnPropertyChanged(nameof(Profiles));
    }

    [RelayCommand] private Task LoadPlaylist() => PlaylistUrl is { } p ? _main.OpenLinkAsync(p) : Task.CompletedTask;

    /// <summary>URL çubuğundaki yapıştır düğmesi: Ctrl+V ile aynı yol (birden çok bağlantı → kuyruk).</summary>
    [RelayCommand]
    private async Task Paste()
    {
        string text;
        try { text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText().Trim() : ""; }
        catch (System.Runtime.InteropServices.ExternalException) { return; } // pano başka uygulamada kilitli
        if (text.Length == 0 || TryEnqueueMany(text)) return;
        Url = text;
        await Analyze();
    }
}
