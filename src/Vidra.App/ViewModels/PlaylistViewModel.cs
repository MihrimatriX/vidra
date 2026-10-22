using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vidra.Core;
using Vidra.Core.Models;
using Vidra.Core.Queue;
using Vidra.Core.Storage;
using Vidra.Core.YtDlp;

namespace Vidra.App.ViewModels;

public sealed partial class PlaylistRow(PlaylistEntry entry, int index, bool done) : ObservableObject
{
    public PlaylistEntry Entry { get; } = entry;
    public int Index { get; } = index;
    public bool Done { get; } = done;
    public bool Selectable => !Entry.Unavailable && !Entry.IsList;
    public string DurationText => TimeText.Format(Entry.Duration);
    public string Meta => string.Join(" · ", new[] { Fmt.Date(Entry.UploadDate), Entry.Views is { } v ? $"{Fmt.Count(v)} görüntülenme" : "" }.Where(s => s.Length > 0));
    // İndirilmiş videolar seçili gelmez; istenirse elle seçilip yeniden indirilebilir
    [ObservableProperty] private bool _isSelected = !entry.Unavailable && !entry.IsList && !done;
}

public sealed partial class PlaylistViewModel : ObservableObject
{
    /// <summary>Flat listede format bilgisi yok: boyutu süre × tipik YouTube bit hızıyla tahmin ederiz (kbps).</summary>
    private static readonly Dictionary<int, double> TypicalKbps = new()
    {
        [0] = 9000, [2160] = 18000, [1440] = 9000, [1080] = 4300, [720] = 2500, [480] = 1200, [360] = 700,
    };

    private readonly AppHost _host;
    private readonly MainViewModel _main;
    private readonly Stack<string> _back = new();
    private List<PlaylistRow> _all = [];
    private bool _bulk;
    private CancellationTokenSource? _cts;

    public PlaylistViewModel(AppHost host, MainViewModel main)
    {
        _host = host;
        _main = main;
        var d = host.Settings.Defaults;
        _mode = d.Mode;
        _height = d.Height ?? 0;
        _audioFormat = d.AudioFormat;
        _musicMode = d.MusicMode;
    }

    /// <summary>Boş ekrandaki kutu: playlist/kanal/akış bağlantısını burada da açabilmek için.</summary>
    [RelayCommand] private Task OpenUrl() => SourceUrl.Trim() is { Length: > 0 } u ? _main.OpenLinkAsync(u) : Task.CompletedTask;

    public IReadOnlyList<Choice> ChannelTabs => Choices.ChannelTabs;
    public IReadOnlyList<Choice> Modes => Choices.Modes;
    public IReadOnlyList<Choice> Heights => Choices.Heights;
    public IReadOnlyList<Choice> AudioFormats => Choices.AudioFormats;
    public IReadOnlyList<Choice> Buckets => Choices.DurationBuckets;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasInfo), nameof(HasVideos), nameof(IsChannel), nameof(KindLabel))] private PlaylistInfo? _info;
    public bool HasInfo => Info is not null;
    public bool IsChannel => Info?.Kind == LinkKind.Channel;
    public string KindLabel => Info?.Kind switch { LinkKind.Channel => "Kanal", LinkKind.Feed => "Hesap akışı", _ => "Playlist" };

    /// <summary>"Playlistlerim" gibi yalnızca listelerden oluşan sayfa: video tablosu yerine liste kartları.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasVideos))] private bool _isListOfLists;
    public bool HasVideos => HasInfo && !IsListOfLists;

    [ObservableProperty] private string _sourceUrl = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private ChannelTab _channelTab = ChannelTab.Videos;
    [ObservableProperty] private string? _cover;
    [ObservableProperty] private string _metaLine = "";
    /// <summary>Hesap akışlarında yt-dlp'nin İngilizce adı ("Liked videos", "playlists") yerine menüdeki Türkçe ad.</summary>
    [ObservableProperty] private string _title = "";

    // Filtreler: arama + "indirilmemiş" her zaman görünür, gerisi "Daha fazla" altında
    [ObservableProperty] private bool _moreFilters;
    [ObservableProperty] private string _range = "";
    [ObservableProperty] private string _dateFrom = "";
    [ObservableProperty] private string _dateTo = "";
    [ObservableProperty] private string _bucket = "all";
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _onlyNew;
    [ObservableProperty] private bool _onlyMembers;

    public ObservableCollection<PlaylistRow> Rows { get; } = [];

    [ObservableProperty] private int _membersCount;
    [ObservableProperty] private int _doneCount;
    [ObservableProperty] private int _unavailableCount;

    // Seçim çubuğu
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanEnqueue))] private int _selectedCount;
    public bool CanEnqueue => SelectedCount > 0;
    [ObservableProperty] private string _selectedSummary = "";
    [ObservableProperty] private bool _allSelected;

    // İndirme seçenekleri
    [ObservableProperty] private DownloadMode _mode;
    [ObservableProperty] private int _height;
    [ObservableProperty] private AudioFormat _audioFormat;
    [ObservableProperty] private bool _musicMode;
    [ObservableProperty] private bool _isFollowed;

    partial void OnRangeChanged(string value) => ApplyFilter();
    partial void OnDateFromChanged(string value) => ApplyFilter();
    partial void OnDateToChanged(string value) => ApplyFilter();
    partial void OnBucketChanged(string value) => ApplyFilter();
    partial void OnQueryChanged(string value) => ApplyFilter();
    partial void OnOnlyNewChanged(bool value) => ApplyFilter();
    partial void OnOnlyMembersChanged(bool value) => ApplyFilter();
    partial void OnModeChanged(DownloadMode value) => UpdateSelection();
    partial void OnHeightChanged(int value) => UpdateSelection();
    partial void OnAudioFormatChanged(AudioFormat value) => UpdateSelection();

    partial void OnChannelTabChanged(ChannelTab value)
    {
        if (IsChannel && !_tabFromUrl) _ = LoadAsync(SourceUrl, keepHistory: true);
    }

    private bool _tabFromUrl;

    /// <param name="keepHistory">Alt listeye girerken/geri dönerken "Geri" geçmişi korunur; dışarıdan açılışta sıfırlanır.</param>
    public async Task LoadAsync(string url, bool keepHistory = false)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        SourceUrl = url;
        // Yeni açılan kanal bağlantısının sekmesi (…/playlists) seçilir; sekmesizse Videolar
        if (!keepHistory && YouTubeUrl.Classify(url) == LinkKind.Channel)
        {
            _tabFromUrl = true;
            ChannelTab = YouTubeUrl.TabOf(url) ?? ChannelTab.Videos;
            _tabFromUrl = false;
        }
        IsLoading = true;
        Error = null;
        try
        {
            var r = await _host.YtDlp.ProbeAsync(url, ChannelTab, ct: cts.Token);
            if (cts.IsCancellationRequested) return;
            if (r is PlaylistInfo p) Show(p, url, keepHistory);
            // Video döndüyse tekrar analiz ettirme (OpenLinkAsync ↔ LoadAsync döngüsü): bilgiyi doğrudan göster
            else if (r is VideoInfo v) { _main.Download.Url = url; _main.Download.ShowVideo(v); _main.Go(Page.Download); }
        }
        catch (ProbeException e) { Error = e.Message; }
        catch (OperationCanceledException) { }
        catch (Exception e) { Error = e.Message; }
        finally
        {
            if (_cts == cts) IsLoading = false;
        }
    }

    public void Show(PlaylistInfo p, string url, bool keepHistory = false)
    {
        if (!keepHistory) _back.Clear();
        SourceUrl = url;
        var archived = new ArchiveStore(_host.Paths.ArchiveFile).ReadIds();
        foreach (var h in _host.Queue.History.Where(h => h.Status == JobStatus.Done && h.VideoId is not null)) archived.Add(h.VideoId!);
        _all = p.Entries.Select((e, i) => new PlaylistRow(e, i + 1, archived.Contains(e.Id))).ToList();
        foreach (var r in _all) r.PropertyChanged += (_, a) => { if (a.PropertyName == nameof(PlaylistRow.IsSelected)) UpdateSelection(); };
        IsListOfLists = _all.Count > 0 && _all.All(r => r.Entry.IsList);
        // Kanal sekmeleri "Ad - Videos" / "Members-only videos" gibi İngilizce başlık taşır; sekme zaten seçili göründüğü için kanal adı yeter
        var channelTitle = p.Kind == LinkKind.Channel && !string.IsNullOrEmpty(p.Channel);
        Title = YouTubeUrl.Feeds.FirstOrDefault(f => f.Key == url).Label ?? (channelTitle ? p.Channel : p.Title);
        Info = p;
        Cover = p.Entries.FirstOrDefault(e => e.Thumbnail is not null)?.Thumbnail;
        MembersCount = _all.Count(r => r.Entry.MembersOnly);
        DoneCount = _all.Count(r => r.Done);
        UnavailableCount = _all.Count(r => r.Entry.Unavailable);
        var secs = _all.Sum(r => r.Entry.Duration ?? 0);
        MetaLine = string.Join(" · ", new[]
        {
            channelTitle ? "" : p.Channel ?? "",
            IsListOfLists ? $"{_all.Count} liste" : $"{_all.Count} video",
            secs > 0 ? TimeText.Format(secs) : "",
        }.Where(s => s.Length > 0));
        IsFollowed = _host.Follow.IsFollowed(url);
        MoreFilters = false;
        ApplyFilter();
    }

    private double Kbps => Mode == DownloadMode.Audio
        ? AudioFormat switch { AudioFormat.Mp3 => 245, AudioFormat.Flac => 900, AudioFormat.Wav => 1411, _ => 150 }
        : (TypicalKbps.TryGetValue(Height, out var k) ? k : 4300) + 130;

    private string EstimateFor(double seconds) => seconds > 0 ? Fmt.Bytes(seconds * Kbps * 1000 / 8) : "—";

    private void ApplyFilter()
    {
        var set = ParseRange(Range, _all.Count);
        var from = DateFrom.Replace("-", "").Trim();
        var to = DateTo.Replace("-", "").Trim();
        var q = Query.Trim();
        (double Min, double Max) b = Bucket switch { "short" => (0, 240), "mid" => (240, 1200), "long" => (1200, double.MaxValue), _ => (0, double.MaxValue) };

        var visible = _all.Where(r =>
            (set is null || set.Contains(r.Index)) &&
            (from.Length != 8 || r.Entry.UploadDate is null || string.CompareOrdinal(r.Entry.UploadDate, from) >= 0) &&
            (to.Length != 8 || r.Entry.UploadDate is null || string.CompareOrdinal(r.Entry.UploadDate, to) <= 0) &&
            (Bucket == "all" || (r.Entry.Duration is { } d && d >= b.Min && d < b.Max)) &&
            (q.Length == 0 || r.Entry.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)) &&
            (!OnlyNew || !r.Done) &&
            (!OnlyMembers || r.Entry.MembersOnly));

        Rows.Clear();
        foreach (var r in visible) Rows.Add(r);
        UpdateSelection();
    }

    private IEnumerable<PlaylistRow> Selected => Rows.Where(r => r.IsSelected && r.Selectable);

    private void UpdateSelection()
    {
        if (_bulk) return;
        var sel = Selected.ToList();
        SelectedCount = sel.Count;
        var secs = sel.Sum(r => r.Entry.Duration ?? 0);
        SelectedSummary = sel.Count == 0 ? "Listeden video seç" : $"{TimeText.Format(secs)} · ≈ {EstimateFor(secs)}";
        var candidates = Rows.Where(r => r.Selectable && !r.Done).ToList();
        AllSelected = candidates.Count > 0 && candidates.All(r => r.IsSelected);
    }

    /// <summary>"Tümü": indirilmemişleri seçer; tekrar basınca indirilmişler dahil hepsini bırakır.</summary>
    [RelayCommand]
    private void ToggleAll()
    {
        var target = !AllSelected;
        _bulk = true;
        try { foreach (var r in Rows.Where(r => r.Selectable && (!target || !r.Done))) r.IsSelected = target; }
        finally { _bulk = false; }
        UpdateSelection();
    }

    [RelayCommand]
    private async Task Reload()
    {
        if (SourceUrl.Length > 0) await LoadAsync(SourceUrl, keepHistory: true);
    }

    /// <summary>Alt listeden üst listeye; geçmiş yoksa başlangıç ekranına (bağlantı kutusu + hesap akışları).</summary>
    [RelayCommand]
    private async Task Back()
    {
        if (_back.TryPop(out var url)) { await LoadAsync(url, keepHistory: true); return; }
        _cts?.Cancel();
        Info = null;
        Rows.Clear();
        _all = [];
        SourceUrl = "";
        Error = null;
    }

    private DownloadOptions Options() => _host.Settings.Defaults with
    {
        Mode = Mode,
        Height = Height == 0 ? null : Height,
        AudioFormat = AudioFormat,
        MusicMode = MusicMode,
    };

    [RelayCommand]
    private void EnqueueSelected()
    {
        if (Info is not { } p) return;
        var o = Options();
        var source = $"{KindLabel}: {Title}";
        _host.Queue.Enqueue(Selected.Select(r => new NewJob(
            r.Entry.Url, r.Entry.Title, o with { Playlist = new PlaylistRef(Title, r.Index) }, p.Channel, r.Entry.Thumbnail, r.Entry.Duration,
            Templates.ResolvePlaylist(_host.Settings.PlaylistTemplate, Title, r.Index), source)).ToList());
        _main.Go(Page.Queue);
    }

    /// <summary>Takip: mevcut videolar "görüldü" sayılır, yalnızca bundan sonra gelenler otomatik iner.</summary>
    [RelayCommand]
    private async Task ToggleFollow()
    {
        if (Info is not { } p) return;
        var existing = _host.Follow.Items.FirstOrDefault(f => string.Equals(f.Url, SourceUrl, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) await _host.Follow.UnfollowAsync(existing);
        else await _host.Follow.FollowAsync(SourceUrl, Title, Options(), p.Entries.Select(e => e.Id));
        IsFollowed = existing is null;
    }

    /// <summary>Alt liste (ör. Playlistlerim içindeki bir playlist): o listeyi aç, "Geri" ile dönülebilsin.</summary>
    [RelayCommand]
    private async Task OpenSubList(object? p)
    {
        if (p is not PlaylistRow r) return;
        _back.Push(SourceUrl);
        await LoadAsync(r.Entry.Url, keepHistory: true);
    }

    /// <summary>"1-20, 31" → {1..20, 31}; 1 tabanlı. Geçersizse null (filtre yok).</summary>
    public static HashSet<int>? ParseRange(string input, int max)
    {
        var s = input.Trim();
        if (s.Length == 0) return null;
        var set = new HashSet<int>();
        foreach (var raw in s.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim();
            var dash = part.IndexOf('-');
            if (dash >= 0)
            {
                if (!int.TryParse(part[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out var a)) return null;
                var rest = part[(dash + 1)..].Trim();
                var b = rest.Length == 0 ? max : int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var x) ? x : -1;
                if (b < 0) return null;
                for (var i = Math.Max(1, a); i <= Math.Min(b, max); i++) set.Add(i);
            }
            else if (int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) { if (n >= 1 && n <= max) set.Add(n); }
            else return null;
        }
        return set;
    }
}
