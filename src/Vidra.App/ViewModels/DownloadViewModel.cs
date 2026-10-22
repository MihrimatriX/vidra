using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Vidra.Core;
using Vidra.Core.Models;
using Vidra.Core.Queue;
using Vidra.Core.YtDlp;

namespace Vidra.App.ViewModels;

public enum OptionTab { Video, Audio, Subs, Advanced }

/// <summary>Kalite kartı.</summary>
public sealed record QualityCard(int? Height, string Title, string Tag, string Codec, string SizeText, long? Size, StreamInfo? Stream);

public sealed record AudioCard(AudioFormat Format, string Title, string Detail, string SizeText, long? Size);

public sealed record StreamRow(string Kind, string Id, string Detail, string Rate, string Size);

public sealed partial class DownloadViewModel : ObservableObject
{

    /// <summary>Dönüştürülen formatların yaklaşık bit hızı (kbps); null = orijinal akış boyutu.</summary>
    private static readonly Dictionary<AudioFormat, (string Detail, double? Kbps)> AudioMeta = new()
    {
        [AudioFormat.Mp3] = ("VBR 0 · ~245 kbps", 245),
        [AudioFormat.M4a] = ("AAC · cihaz dostu", null),
        [AudioFormat.Opus] = ("orijinal akış", null),
        [AudioFormat.Flac] = ("kayıpsız dönüşüm", 900),
        [AudioFormat.Wav] = ("ham PCM 16-bit", 1411),
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
        _mode = d.Mode;
        _musicMode = d.MusicMode;
        Tab = d.Mode == DownloadMode.Audio ? OptionTab.Audio : OptionTab.Video;
    }

    // Profiller: hazırlar + kullanıcının kaydettikleri
    public IReadOnlyList<DownloadProfile> Profiles => DownloadProfile.BuiltIns.Concat(_host.Settings.Profiles).ToList();
    [ObservableProperty] private bool _savingProfile;
    [ObservableProperty] private string _newProfileName = "";

    public AppHost Host => _host;
    public IReadOnlyList<Choice> Containers => Choices.Containers;
    public IReadOnlyList<Choice> Codecs => Choices.Codecs;
    public IReadOnlyList<Choice> FpsChoices => Choices.Fps;
    public IReadOnlyList<Choice> Tabs { get; } =
        [new(OptionTab.Video, "Video"), new(OptionTab.Audio, "Sadece ses"), new(OptionTab.Subs, "Altyazı"), new(OptionTab.Advanced, "Gelişmiş")];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PlaylistUrl))] private string _url = "";

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

    [ObservableProperty] private OptionTab _tab;
    [ObservableProperty] private DownloadMode _mode;

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
    [ObservableProperty] private Chapter? _selectedChapter;
    [ObservableProperty] private bool _splitChapters;
    public ObservableCollection<Toggle> SponsorCats { get; } = [];

    // Ortak anahtarlar
    [ObservableProperty] private bool _embedThumbnail;
    [ObservableProperty] private bool _sponsorBlock;
    [ObservableProperty] private bool _chapters;

    // Hesaplanan özet
    public ObservableCollection<StreamRow> StreamRows { get; } = [];
    [ObservableProperty] private string _resultSummary = "";
    [ObservableProperty] private string _estimate = "";
    [ObservableProperty] private string _filePreview = "";
    [ObservableProperty] private string _clipText = "";
    [ObservableProperty] private bool _clipValid;
    [ObservableProperty] private double _clipLeft;
    [ObservableProperty] private double _clipWidth;
    [ObservableProperty] private double _clipRight = 1;
    [ObservableProperty] private string _metaHighest = "—";
    [ObservableProperty] private string _metaAudio = "—";
    [ObservableProperty] private string _metaUpload = "—";
    [ObservableProperty] private string _metaViews = "—";

    public bool UseArchive
    {
        get => _host.Settings.UseArchive;
        set { _host.SettingsStore.Update(s => s.UseArchive = value); OnPropertyChanged(); }
    }

    public string OutputDir => _host.Settings.OutputDir;
    public string SponsorHint => $"{_host.Settings.SponsorCategories.Count} kategori kesilir";

    partial void OnTabChanged(OptionTab value)
    {
        if (value is OptionTab.Video) Mode = DownloadMode.Video;
        else if (value is OptionTab.Audio) Mode = DownloadMode.Audio;
    }

    partial void OnModeChanged(DownloadMode value) => Recalculate();
    partial void OnSelectedQualityChanged(QualityCard? value) => Recalculate();
    partial void OnSelectedAudioChanged(AudioCard? value) => Recalculate();
    partial void OnMusicModeChanged(bool value) => Recalculate();
    partial void OnSubtitlesChanged(bool value) => Recalculate();
    partial void OnChaptersChanged(bool value) => Recalculate();
    partial void OnClipEnabledChanged(bool value) => Recalculate();
    partial void OnClipStartChanged(string value) => Recalculate();
    partial void OnClipEndChanged(string value) => Recalculate();
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
        _host.Queue.Enqueue(links.Select(l => new NewJob(l, l, _host.Settings.Defaults, Source: "Toplu yapıştırma")));
        _main.Go(Page.Queue);
        return true;
    }

    [RelayCommand]
    private async Task Analyze()
    {
        var url = Url.Trim();
        if (url.Length == 0) return;
        var kind = YouTubeUrl.Classify(url);
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
        Info = null;
        ClipEnabled = false;
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
        foreach (var f in Enum.GetValues<AudioFormat>())
        {
            var (detail, kbps) = AudioMeta[f];
            long? size = kbps is { } k && v.Duration is { } dur ? (long)(k * 1000 / 8 * dur) : v.Audio?.Size;
            if (f == AudioFormat.Opus && v.Audio is { } au) detail = $"{au.Codec} · {Fmt.Kbps(au.Abr)}";
            AudioCards.Add(new AudioCard(f, f.ToString().ToUpperInvariant(), detail, "≈ " + Fmt.Bytes(size), size));
        }
        SelectedAudio = AudioCards.FirstOrDefault(a => a.Format == _host.Settings.Defaults.AudioFormat) ?? AudioCards.First();

        RebuildQualities();
    }

    private void RebuildQualities()
    {
        if (Info is null) return;
        var wantHeight = SelectedQuality?.Height ?? _host.Settings.Defaults.Height;
        var keepBest = SelectedQuality is { Height: null };
        Qualities.Clear();
        // Standart dışı yükseklikler de (ör. geniş 4K → 1974p) listelenir; -S res: en küçük boyuta göre sıralar
        foreach (var q in Info.Qualities)
        {
            var s = ProbeParser.PickStream(q, Codec, Container, MaxFps30);
            long? total = s?.Size is { } sz ? sz + (Info.Audio?.Size ?? 0) : q.Size;
            var label = q.Height switch { >= 4320 => "8K", >= 2160 => "4K", >= 1440 => "2K", >= 1080 => "Full HD", >= 720 => "HD", _ => null };
            var tag = string.Join(" ", new[] { label, q.Hdr ? "HDR" : null, q.MaxFps is > 30 ? $"{q.MaxFps.Value:0}fps" : null }.Where(x => x is not null));
            var codec = s is null ? "—" : $"{CodecName(s)} · {Fmt.Kbps(s.Tbr)}";
            Qualities.Add(new QualityCard(q.Height, $"{q.Height}p", tag, codec, "≈ " + Fmt.Bytes(total), total, s));
        }
        var top = Qualities.FirstOrDefault();
        Qualities.Add(new QualityCard(null, "En iyi", "otomatik", "en yüksek mevcut", top?.SizeText ?? "", top?.Size, top?.Stream));

        SelectedQuality = keepBest ? Qualities.Last()
            : Qualities.FirstOrDefault(q => q.Height == wantHeight)
              ?? Qualities.FirstOrDefault(q => q.Height <= wantHeight)
              ?? Qualities.Last();
        Recalculate();
    }

    private static string CodecName(StreamInfo s) => s.Codec switch
    {
        CodecFamily.H264 => "H.264",
        CodecFamily.Vp9 => "VP9",
        CodecFamily.Av1 => "AV1",
        _ => s.VCodec.Split('.')[0],
    };

    private Clip? CurrentClip()
    {
        if (!ClipEnabled) return null;
        var a = TimeText.ToSeconds(ClipStart);
        var b = TimeText.ToSeconds(ClipEnd);
        return a is not null && b is not null && b > a ? new Clip(ClipStart.Trim(), ClipEnd.Trim()) : null;
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
        MusicMode = MusicMode,
    };

    private void Recalculate()
    {
        if (Info is not { } v) return;
        var o = BuildOptions();

        var clip = o.Clip;
        double? clipSecs = clip is null ? null : TimeText.ToSeconds(clip.End) - TimeText.ToSeconds(clip.Start);
        ClipValid = clip is not null;
        ClipText = !ClipEnabled ? ""
            : ClipValid ? $"Seçili aralık {TimeText.Format(clipSecs)} · yalnızca bu kısım indirilir"
            : "Zamanları 2:15 ya da 135 biçiminde gir; bitiş başlangıçtan sonra olmalı.";
        var ratio = clipSecs is { } cs && v.Duration is > 0 ? Math.Min(1, cs / v.Duration.Value) : 1;
        ClipLeft = clip is not null && v.Duration is > 0 ? TimeText.ToSeconds(clip.Start)!.Value / v.Duration.Value : 0;
        ClipWidth = clip is not null ? ratio : 0;
        ClipRight = Math.Max(0, 1 - ClipLeft - ClipWidth);

        StreamRows.Clear();
        if (o.Mode == DownloadMode.Video && SelectedQuality?.Stream is { } s)
        {
            var h = SelectedQuality.Height ?? v.Qualities.FirstOrDefault()?.Height;
            var res = s.Width is { } w ? $"{w}×{h}" : $"{h}p";
            StreamRows.Add(new StreamRow("Video", s.Id, $"{res}{(s.Fps is { } f ? $" · {f:0}fps" : "")} · {s.VCodec.Split('.')[0]}{(s.Hdr ? " · HDR" : "")}",
                Fmt.Kbps(s.Tbr), Fmt.Bytes(s.Size)));
        }
        if (v.Audio is { } a)
            StreamRows.Add(new StreamRow("Ses", a.Id,
                $"{a.Codec}{(a.Asr is { } asr ? $" · {asr / 1000:0.#} kHz" : "")}{(o.Mode == DownloadMode.Audio ? $" → {o.AudioFormat.Ext().ToUpperInvariant()}" : "")}",
                Fmt.Kbps(a.Abr), Fmt.Bytes(a.Size)));

        var ext = o.Mode == DownloadMode.Audio ? o.AudioFormat.Ext() : o.Container.Ext();
        var parts = new List<string> { ext.ToUpperInvariant() };
        if (o.Subtitles && o.SubLangs is { Count: > 0 } langs) parts.Add("altyazı " + string.Join('+', langs).ToUpperInvariant());
        if (o.Chapters && v.Chapters.Count > 0) parts.Add($"{v.Chapters.Count} bölüm");
        if (clip is not null) parts.Add($"klip {TimeText.Format(clipSecs)}");
        ResultSummary = string.Join(" · ", parts);

        long? size = o.Mode == DownloadMode.Audio ? SelectedAudio?.Size : SelectedQuality?.Size;
        Estimate = size is { } sz ? Fmt.Bytes(sz * ratio) : "—";

        FilePreview = "→ " + Templates.Preview(_host.Settings.OutputTemplate, new Dictionary<string, object?>
        {
            ["title"] = v.Title, ["uploader"] = v.Channel, ["channel"] = v.Channel, ["id"] = v.Id, ["ext"] = ext,
            ["upload_date"] = v.UploadDate, ["height"] = SelectedQuality?.Height,
        });
    }

    private void Enqueue(bool startNow)
    {
        if (Info is not { } v) return;
        _host.Queue.Enqueue([new NewJob(v.Url, v.Title, BuildOptions(), v.Channel, v.Thumbnail, v.Duration)], startNow);
        if (startNow) _main.Go(Page.Queue);
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
        Tab = o.Mode == DownloadMode.Audio ? OptionTab.Audio : OptionTab.Video;
        SelectedAudio = AudioCards.FirstOrDefault(a => a.Format == o.AudioFormat) ?? SelectedAudio;
        SelectedQuality = o.Height is not { } h ? Qualities.LastOrDefault()
            : Qualities.FirstOrDefault(q => q.Height == h) ?? Qualities.FirstOrDefault(q => q.Height < h) ?? Qualities.LastOrDefault();
    }

    [RelayCommand]
    private void SaveProfile()
    {
        var name = NewProfileName.Trim();
        if (name.Length == 0) return;
        // Videoya özel seçimler (klip, dil listesi) profile girmez
        var p = new DownloadProfile(name, BuildOptions() with { Clip = null, SubLangs = null, Playlist = null });
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
