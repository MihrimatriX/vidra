using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Vidra.Core;
using Vidra.Core.Models;
using Vidra.Core.Playlists;
using Vidra.Core.Queue;
using Vidra.Core.Storage;
using Vidra.Core.YtDlp;

namespace Vidra.App.ViewModels;

public sealed partial class PlaylistRow(PlaylistEntry entry, int index, bool done) : ObservableObject, IPlaylistItem
{
    public PlaylistEntry Entry { get; } = entry;
    public int Index { get; } = index;
    public bool Done { get; } = done;
    /// <summary>Alt listeler de seçilebilir: indirirken içleri okunur (bkz. CollectAsync).</summary>
    public bool Selectable => !Entry.Unavailable;
    public string DurationText => TimeText.Format(Entry.Duration);
    public string Meta => string.Join(" · ", new[] { Fmt.Date(Entry.UploadDate), Entry.Views is { } v ? $"{Fmt.Count(v)} görüntülenme" : "" }.Where(s => s.Length > 0));
    // İndirilmiş videolar seçili gelmez; istenirse elle seçilip yeniden indirilebilir
    [ObservableProperty] private bool _isSelected = !entry.Unavailable && !entry.IsList && !done;

    // Alt liste kartı: içerik önbellekten ya da arka planda okunur (bkz. PlaylistViewModel.LoadListStatsAsync)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText), nameof(MembersText), nameof(MembersHint), nameof(StatsText),
        nameof(Progress), nameof(ShowProgress), nameof(Complete), nameof(HasMembers))]
    private ListStats? _stats;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StatsText))] private string? _statsError;
    [ObservableProperty] private bool _isListFollowed;
    [ObservableProperty] private bool _isListPinned;

    public string? CountText => ListStatsText.Count(Stats);
    public string? MembersText => ListStatsText.Members(Stats);
    public string? MembersHint => ListStatsText.MembersHint(Stats);
    public double Progress => Stats?.Progress ?? 0;
    public bool ShowProgress => Stats is { Done: > 0 };
    public bool Complete => Stats?.Complete ?? false;
    public bool HasMembers => Stats is { Members: > 0 };
    public string StatsText => ListStatsText.Status(Stats, StatsError, Selectable);
}

public sealed partial class PlaylistViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly MainViewModel _main;
    private readonly Stack<string> _back = new();
    private readonly Action _coalescedFilter;
    private List<PlaylistRow> _all = [];
    private PlaylistRow? _anchorRow;
    private bool _bulk;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _statsCts;
    /// <summary>Oturum içi okunmuş alt listeler; kalıcı kopya AppHost.ListCache'tedir.</summary>
    private readonly Dictionary<string, PlaylistInfo> _lists = new(StringComparer.OrdinalIgnoreCase);

    public PlaylistViewModel(AppHost host, MainViewModel main)
    {
        _host = host;
        _main = main;
        _coalescedFilter = Ui.Coalesce(ApplyFilter);
        var d = host.Settings.Defaults;
        _mode = d.Mode;
        _height = d.Height ?? 0;
        _container = d.Container;
        _audioFormat = d.AudioFormat == AudioFormat.Opus ? AudioFormat.Original : d.AudioFormat;
        _musicMode = d.MusicMode;
        _subtitles = d.Subtitles;
        _transcript = d.Transcript;
        _sponsorBlock = d.SponsorBlock;

        host.SettingsStore.Changed += (_, _) => AppHost.Dispatch(RefreshBookmarks);
        main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CookieActive)) OnPropertyChanged(nameof(ShowMemberLoginHint));
        };
        RefreshBookmarks();
    }

    /// <summary>Boş ekrandaki kutu: playlist/kanal/akış bağlantısını burada da açabilmek için.</summary>
    [RelayCommand] private Task OpenUrl() => SourceUrl.Trim() is { Length: > 0 } u ? _main.OpenLinkAsync(u) : Task.CompletedTask;

    public IReadOnlyList<Choice> ChannelTabs => Choices.ChannelTabs;
    public IReadOnlyList<Choice> Modes => Choices.Modes;
    public IReadOnlyList<Choice> Heights => Choices.Heights;
    public IReadOnlyList<Choice> Containers => Choices.Containers;
    public IReadOnlyList<Choice> AudioFormats => Choices.AudioFormats;
    public IReadOnlyList<Choice> Buckets => Choices.DurationBuckets;
    public IReadOnlyList<Choice> VideoSorts => Choices.VideoSorts;
    public IReadOnlyList<Choice> ListSorts => Choices.ListSorts;
    public IReadOnlyList<DownloadProfile> Profiles => DownloadProfile.BuiltIns.Concat(_host.Settings.Profiles).ToList();

    // Sabitlenen kanallar (özellikle Katıl/üye kanalları) ve son açılanlar
    public ObservableCollection<PinnedSource> Pinned { get; } = [];
    public ObservableCollection<RecentSource> Recents { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInfo), nameof(HasVideos), nameof(IsChannel), nameof(KindLabel), nameof(CanPin), nameof(ShowMemberLoginHint), nameof(ShowPinMemberToggle))]
    private PlaylistInfo? _info;
    public bool HasInfo => Info is not null;
    public bool IsChannel => Info?.Kind == LinkKind.Channel;
    public bool CanPin => Info is { Kind: LinkKind.Channel or LinkKind.Playlist };
    public bool ShowPinMemberToggle => IsPinned && IsChannel;
    public string KindLabel => Info?.Kind switch { LinkKind.Channel => "Kanal", LinkKind.Feed => "Hesap akışı", _ => "Playlist" };

    /// <summary>"Playlistlerim", "Playlistler", "Kurslar" gibi yalnızca listelerden oluşan sayfa: liste kartları.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasVideos))] private bool _isListOfLists;
    public bool HasVideos => HasInfo && !IsListOfLists;

    [ObservableProperty] private string _sourceUrl = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _errorIsAuth;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowMemberLoginHint))] private ChannelTab _channelTab = ChannelTab.Videos;
    [ObservableProperty] private string? _cover;
    [ObservableProperty] private string _metaLine = "";
    /// <summary>Hesap akışlarında yt-dlp'nin İngilizce adı ("Liked videos", "playlists") yerine menüdeki Türkçe ad.</summary>
    [ObservableProperty] private string _title = "";

    // Sabitleme durumu (açık kaynak için)
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowPinMemberToggle))] private bool _isPinned;
    [ObservableProperty] private bool _isPinnedMember;

    // Filtreler ve sıralama
    [ObservableProperty] private bool _moreFilters;
    [ObservableProperty] private string _range = "";
    [ObservableProperty] private string _dateFrom = "";
    [ObservableProperty] private string _dateTo = "";
    [ObservableProperty] private DurationBucket _bucket;
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _onlyNew;
    [ObservableProperty] private bool _onlyMembers;
    [ObservableProperty] private VideoSort _videoSort;
    [ObservableProperty] private ListSort _listSort;

    public ObservableCollection<PlaylistRow> Rows { get; } = [];
    public CardGrid Cards { get; } = new();

    public bool CardView
    {
        get => _host.Settings.PlaylistCards;
        set { _host.SettingsStore.Update(s => s.PlaylistCards = value); OnPropertyChanged(); }
    }

    /// <summary>Seçili listeler okunurken ilerleme ("Listeler okunuyor 2/5…").</summary>
    [ObservableProperty] private string? _busyText;
    /// <summary>Sepete ekleme / dışa aktarma sonucu; seçim değişince kaybolur.</summary>
    [ObservableProperty] private string? _toast;
    [ObservableProperty] private string _selectedLabel = "";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowMemberLoginHint))] private int _membersCount;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowMemberLoginHint))] private bool _hasAnyListMembers;
    [ObservableProperty] private int _doneCount;
    [ObservableProperty] private int _unavailableCount;
    [ObservableProperty] private int _fromCacheCount;

    /// <summary>Oturum yokken üyelere özel içerik açıldığında üstte Hesabım'a yönlendiren bilgi şeridi.</summary>
    public bool ShowMemberLoginHint => HasInfo && !_main.CookieActive && (ChannelTab == ChannelTab.Membership || MembersCount > 0 || HasAnyListMembers);

    // Seçim çubuğu
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanEnqueue))] private int _selectedCount;
    public bool CanEnqueue => SelectedCount > 0;
    [ObservableProperty] private string _selectedSummary = "";
    [ObservableProperty] private bool _allSelected;

    // İndirme seçenekleri (alt çubuk + açılır kurs seçenekleri)
    [ObservableProperty] private DownloadMode _mode;
    [ObservableProperty] private int _height;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(OptionsLabel))] private ContainerFormat _container;
    [ObservableProperty] private AudioFormat _audioFormat;
    [ObservableProperty] private bool _musicMode;
    [ObservableProperty] private bool _isFollowed;

    [ObservableProperty] private bool _showOptions;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(OptionsLabel))] private bool _subtitles;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(OptionsLabel))] private bool _transcript;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(OptionsLabel))] private bool _writeDescription;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(OptionsLabel))] private bool _sponsorBlock;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(OptionsLabel))] private bool _writeM3u;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(OptionsLabel))] private bool _reverseOrder;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasCustomOutputDir), nameof(CustomOutputLabel), nameof(OptionsLabel))] private string _customOutputDir = "";

    public bool HasCustomOutputDir => !string.IsNullOrWhiteSpace(CustomOutputDir);
    public string CustomOutputLabel => HasCustomOutputDir ? Path.GetFileName(CustomOutputDir.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : CustomOutputDir : "Varsayılan klasör";

    public string OptionsLabel
    {
        get
        {
            var n = 0;
            if (Transcript) n++;
            if (WriteDescription) n++;
            if (WriteM3u) n++;
            if (ReverseOrder) n++;
            if (SponsorBlock) n++;
            if (HasCustomOutputDir) n++;
            if (Mode == DownloadMode.Video && Container != _host.Settings.Defaults.Container) n++;
            return n > 0 ? $"Seçenekler · {n}" : "Seçenekler";
        }
    }

    partial void OnRangeChanged(string value) => ApplyFilter();
    partial void OnDateFromChanged(string value) => ApplyFilter();
    partial void OnDateToChanged(string value) => ApplyFilter();
    partial void OnBucketChanged(DurationBucket value) => ApplyFilter();
    partial void OnQueryChanged(string value) => ApplyFilter();
    partial void OnOnlyNewChanged(bool value) => ApplyFilter();
    partial void OnOnlyMembersChanged(bool value) => ApplyFilter();
    partial void OnVideoSortChanged(VideoSort value) => ApplyFilter();
    partial void OnListSortChanged(ListSort value) => ApplyFilter();
    partial void OnModeChanged(DownloadMode value) { UpdateSelection(); OnPropertyChanged(nameof(OptionsLabel)); }
    partial void OnHeightChanged(int value) => UpdateSelection();
    partial void OnAudioFormatChanged(AudioFormat value) => UpdateSelection();

    partial void OnChannelTabChanged(ChannelTab value)
    {
        if (IsChannel && !_tabFromUrl) _ = LoadAsync(SourceUrl, keepHistory: true);
    }

    private bool _tabFromUrl;

    /// <summary>Kanalı doğrudan seçilen sekmede açar (sabitlenen kanallar, takip ve komut paleti için).</summary>
    public Task LoadTabAsync(string url, ChannelTab tab) =>
        LoadAsync(YouTubeUrl.Classify(url) == LinkKind.Channel ? YouTubeUrl.ChannelTabUrl(url, tab) : url);

    /// <param name="keepHistory">Alt listeye girerken/geri dönerken "Geri" geçmişi korunur; dışarıdan açılışta sıfırlanır.</param>
    public async Task LoadAsync(string url, bool keepHistory = false)
    {
        _cts?.Cancel();
        _statsCts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        SourceUrl = url;
        // Yeni açılan kanal bağlantısının sekmesi (…/playlists, …/courses, …/membership) seçilir; sekmesizse Videolar
        if (!keepHistory && YouTubeUrl.Classify(url) == LinkKind.Channel)
        {
            _tabFromUrl = true;
            ChannelTab = YouTubeUrl.TabOf(url) ?? ChannelTab.Videos;
            _tabFromUrl = false;
        }
        IsLoading = true;
        Error = null;
        ErrorIsAuth = false;
        try
        {
            var r = await _host.YtDlp.ProbeAsync(url, ChannelTab, ct: cts.Token);
            if (cts.IsCancellationRequested) return;
            if (r is PlaylistInfo p)
            {
                if (p.Kind == LinkKind.Playlist) _host.ListCache.Put(url, p);
                Show(p, url, keepHistory);
            }
            // Video döndüyse tekrar analiz ettirme (OpenLinkAsync ↔ LoadAsync döngüsü): bilgiyi doğrudan göster
            else if (r is VideoInfo v) { _main.Download.Url = url; _main.Download.ShowVideo(v); _main.Go(Page.Download); }
        }
        catch (ProbeException e)
        {
            Error = e.Message;
            ErrorIsAuth = e.Kind is ErrorKind.Auth or ErrorKind.BotCheck;
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Error = e.Message; ErrorIsAuth = false; }
        finally
        {
            if (_cts == cts) IsLoading = false;
        }
    }

    public void Show(PlaylistInfo p, string url, bool keepHistory = false)
    {
        if (!keepHistory)
        {
            _back.Clear();
            _lists.Clear();
        }
        SourceUrl = url;
        Error = null;
        ErrorIsAuth = false;
        _anchorRow = null;
        var archived = ArchivedIds();
        var pinnedSet = _host.Settings.PinnedSources.Select(x => YouTubeUrl.CanonicalSourceUrl(x.Url)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _all = p.Entries.Select((e, i) =>
        {
            var row = new PlaylistRow(e, i + 1, archived.Contains(e.Id));
            if (e.IsList)
            {
                row.IsListFollowed = _host.Follow.IsFollowed(e.Url);
                row.IsListPinned = pinnedSet.Contains(YouTubeUrl.CanonicalSourceUrl(e.Url));
            }
            return row;
        }).ToList();
        foreach (var r in _all) r.PropertyChanged += (_, a) => { if (a.PropertyName == nameof(PlaylistRow.IsSelected)) UpdateSelection(); };
        IsListOfLists = _all.Count > 0 && _all.All(r => r.Entry.IsList);
        // Kanal sekmeleri "Ad - Videos" / "Members-only videos" gibi İngilizce başlık taşır; sekme zaten seçili göründüğü için kanal adı yeter
        var channelTitle = p.Kind == LinkKind.Channel && !string.IsNullOrEmpty(p.Channel);
        Title = YouTubeUrl.Feeds.FirstOrDefault(f => f.Key == url).Label ?? (channelTitle ? p.Channel : p.Title);
        Info = p;
        Cover = p.Entries.FirstOrDefault(e => e.Thumbnail is not null)?.Thumbnail;
        MembersCount = _all.Count(r => r.Entry.MembersOnly);
        HasAnyListMembers = false;
        FromCacheCount = 0;
        DoneCount = _all.Count(r => r.Done);
        UnavailableCount = _all.Count(r => r.Entry.Unavailable);
        var secs = _all.Sum(r => r.Entry.Duration ?? 0);
        MetaLine = PlaylistSummary.MetaLine(channelTitle ? null : p.Channel, _all.Count, IsListOfLists, secs);
        IsFollowed = _host.Follow.IsFollowed(url, ChannelTab);
        MoreFilters = false;
        // Yeni sayfada eski arama/filtre kalmasın (kullanıcı başka sekmeye/listeye geçti)
        _suppressFilter = true;
        try { Query = ""; OnlyNew = false; OnlyMembers = false; }
        finally { _suppressFilter = false; }
        RefreshPinState();
        RecordRecent(p, url);
        ApplyFilter();
        _ = LoadListStatsAsync(IsListOfLists ? _all.Where(r => r.Selectable).ToList() : [], archived);
    }

    /// <summary>
    /// Liste kartlarındaki video sayısı ve üyelere özel videolar: önce kalıcı önbelleğe bakılır (anında),
    /// olmayanlar YouTube'u boğmamak için arka planda ikişer ikişer okunup önbelleğe yazılır.
    /// İndirilmiş sayısı her seferinde güncel arşivden hesaplanır.
    /// </summary>
    private async Task LoadListStatsAsync(IReadOnlyList<PlaylistRow> lists, HashSet<string> archived)
    {
        _statsCts?.Cancel();
        if (lists.Count == 0) return;
        var cts = _statsCts = new CancellationTokenSource();

        var toProbe = new List<PlaylistRow>();
        var cachedCount = 0;
        foreach (var r in lists)
        {
            var cached = _lists.GetValueOrDefault(r.Entry.Url) ?? _host.ListCache.TryGet(r.Entry.Url);
            if (cached is not null)
            {
                _lists[r.Entry.Url] = cached;
                r.Stats = ListStats.Of(cached.Entries, archived);
                cachedCount++;
            }
            else toProbe.Add(r);
        }
        FromCacheCount = cachedCount;
        if (cachedCount > 0) OnListStatsUpdated();
        if (toProbe.Count == 0) return;

        using var gate = new SemaphoreSlim(2);
        await Task.WhenAll(toProbe.Select(async r =>
        {
            try
            {
                await gate.WaitAsync(cts.Token);
                try
                {
                    if (!_lists.TryGetValue(r.Entry.Url, out var p))
                    {
                        if (await _host.YtDlp.ProbeAsync(r.Entry.Url, ct: cts.Token) is not PlaylistInfo q) { r.StatsError = "Liste okunamadı."; return; }
                        _lists[r.Entry.Url] = p = q;
                        _host.ListCache.Put(r.Entry.Url, q);
                    }
                    r.Stats = ListStats.Of(p.Entries, archived);
                    OnListStatsUpdated();
                    if (r.IsSelected) UpdateSelection(keepToast: true);
                }
                finally { gate.Release(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { r.StatsError = e.Message; }
        }));
    }

    private void OnListStatsUpdated()
    {
        HasAnyListMembers = _all.Any(r => r.Stats is { Members: > 0 });
        if (Criteria().DependsOnListStats) _coalescedFilter();
    }

    private bool _suppressFilter;

    private PlaylistFilterCriteria Criteria() => new()
    {
        Query = Query, Range = Range, DateFrom = DateFrom, DateTo = DateTo, Bucket = Bucket,
        OnlyNew = OnlyNew, OnlyMembers = OnlyMembers, VideoSort = VideoSort, ListSort = ListSort,
    };

    private void ApplyFilter()
    {
        if (_suppressFilter) return;
        _anchorRow = null;
        var visible = PlaylistFilter.Apply(_all, Criteria(), IsListOfLists);

        Rows.Clear();
        foreach (var r in visible) Rows.Add(r);
        Cards.SetItems(Rows);
        UpdateSelection();
    }

    /// <summary>Satır tıklaması: Shift basılıysa son tıklanan satırla arası toplu seçilir/bırakılır.</summary>
    public void ToggleRow(PlaylistRow row, bool shift)
    {
        if (!row.Selectable) return;
        if (shift && _anchorRow is not null)
        {
            var a = Rows.IndexOf(_anchorRow);
            var b = Rows.IndexOf(row);
            if (a >= 0 && b >= 0)
            {
                var target = _anchorRow.IsSelected;
                var lo = Math.Min(a, b);
                var hi = Math.Max(a, b);
                _bulk = true;
                try
                {
                    for (var i = lo; i <= hi; i++)
                        if (Rows[i].Selectable) Rows[i].IsSelected = target;
                }
                finally { _bulk = false; }
                UpdateSelection();
                return;
            }
        }
        row.IsSelected = !row.IsSelected;
        _anchorRow = row;
    }

    private HashSet<string> ArchivedIds()
    {
        var ids = new ArchiveStore(_host.Paths.ArchiveFile).ReadIds();
        foreach (var h in _host.Queue.History.Where(h => h.Status == JobStatus.Done && h.VideoId is not null)) ids.Add(h.VideoId!);
        return ids;
    }

    private IEnumerable<PlaylistRow> Selected => Rows.Where(r => r.IsSelected && r.Selectable);

    /// <param name="keepToast">Seçim değişmedi, yalnızca liste özeti geldi: sepete ekleme bildirimi silinmesin.</param>
    private void UpdateSelection(bool keepToast = false)
    {
        if (_bulk) return;
        var sel = Selected.ToList();
        SelectedCount = sel.Count;
        if (!keepToast) Toast = null;
        SelectedLabel = PlaylistSummary.SelectedLabel(sel);
        // Seçili listelerin içeriği okunduysa indirilecek yeni videolar baştan bilinir (bkz. LoadListStatsAsync)
        SelectedSummary = PlaylistSummary.SelectedSummary(sel, IsListOfLists, SizeEstimate.Kbps(Mode, Height, AudioFormat));
        var candidates = Rows.Where(r => PlaylistFilter.IsBulkCandidate(r, IsListOfLists)).ToList();
        AllSelected = candidates.Count > 0 && candidates.All(r => r.IsSelected);
    }

    /// <summary>"Tümü": indirilmemiş videoları (ya da eksik videosu olan listeleri) seçer; tekrar basınca hepsini bırakır.</summary>
    [RelayCommand]
    private void ToggleAll()
    {
        var target = !AllSelected;
        _bulk = true;
        try
        {
            foreach (var r in Rows.Where(r => r.Selectable))
            {
                if (!target) r.IsSelected = false;
                else if (PlaylistFilter.IsBulkCandidate(r, IsListOfLists)) r.IsSelected = true;
            }
        }
        finally { _bulk = false; }
        UpdateSelection();
    }

    [RelayCommand]
    private async Task Reload()
    {
        _lists.Clear();
        _host.ListCache.Remove(SourceUrl);
        _host.ListCache.RemoveMany(_all.Where(r => r.Entry.IsList).Select(r => r.Entry.Url));
        if (SourceUrl.Length > 0) await LoadAsync(SourceUrl, keepHistory: true);
    }

    /// <summary>Alt listeden üst listeye; geçmiş yoksa başlangıç ekranına (bağlantı kutusu + sabitlenenler + hesap akışları).</summary>
    [RelayCommand]
    private async Task Back()
    {
        if (_back.TryPop(out var url)) { await LoadAsync(url, keepHistory: true); return; }
        _cts?.Cancel();
        _statsCts?.Cancel();
        Info = null;
        Rows.Clear();
        _all = [];
        SourceUrl = "";
        Error = null;
        ErrorIsAuth = false;
    }

    [RelayCommand] private void OpenAccount() => _main.Go(Page.Account);

    // ================= Sabitlenen kanallar & son açılanlar =================

    private void RefreshBookmarks()
    {
        Pinned.Clear();
        // Katıl/Üye kanalları en başta görünür
        foreach (var p in _host.Settings.PinnedSources.OrderByDescending(x => x.IsMember)) Pinned.Add(p);
        Recents.Clear();
        foreach (var r in _host.Settings.RecentSources) Recents.Add(r);
        RefreshPinState();
        var pinnedSet = _host.Settings.PinnedSources.Select(x => YouTubeUrl.CanonicalSourceUrl(x.Url)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _all.Where(r => r.Entry.IsList))
            row.IsListPinned = pinnedSet.Contains(YouTubeUrl.CanonicalSourceUrl(row.Entry.Url));
    }

    private void RefreshPinState()
    {
        if (Info is null || string.IsNullOrWhiteSpace(SourceUrl))
        {
            IsPinned = false;
            IsPinnedMember = false;
            return;
        }
        var canonical = YouTubeUrl.CanonicalSourceUrl(SourceUrl);
        var match = _host.Settings.PinnedSources.FirstOrDefault(x => string.Equals(YouTubeUrl.CanonicalSourceUrl(x.Url), canonical, StringComparison.OrdinalIgnoreCase));
        IsPinned = match is not null;
        IsPinnedMember = match?.IsMember ?? false;
    }

    private void RecordRecent(PlaylistInfo p, string url)
    {
        if (p.Kind is not (LinkKind.Channel or LinkKind.Playlist) || string.IsNullOrWhiteSpace(Title)) return;
        var canonical = YouTubeUrl.CanonicalSourceUrl(url);
        var entry = new RecentSource(canonical, Title, p.Kind, Cover, string.IsNullOrWhiteSpace(p.Channel) ? null : p.Channel,
            p.Kind == LinkKind.Channel ? ChannelTab : null, DateTimeOffset.Now);
        _host.SettingsStore.Update(s =>
        {
            var next = s.RecentSources
                .Where(x => !string.Equals(YouTubeUrl.CanonicalSourceUrl(x.Url), canonical, StringComparison.OrdinalIgnoreCase))
                .Prepend(entry)
                .Take(8)
                .ToList();
            s.RecentSources = next;
        });
    }

    [RelayCommand]
    private void TogglePin()
    {
        if (Info is not { Kind: LinkKind.Channel or LinkKind.Playlist } p) return;
        var canonical = YouTubeUrl.CanonicalSourceUrl(SourceUrl);
        var existing = _host.Settings.PinnedSources.FirstOrDefault(x => string.Equals(YouTubeUrl.CanonicalSourceUrl(x.Url), canonical, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _host.SettingsStore.Update(s => s.PinnedSources = s.PinnedSources.Where(x => x != existing).ToList());
            _main.ShowToast($"“{existing.Title}” sabitlenenlerden kaldırıldı", "Geri al",
                () => _host.SettingsStore.Update(s => s.PinnedSources = [.. s.PinnedSources, existing]));
        }
        else
        {
            var isMember = p.Kind == LinkKind.Channel && (ChannelTab == ChannelTab.Membership || MembersCount > 0 || HasAnyListMembers);
            var defTab = ChannelTab is ChannelTab.Courses or ChannelTab.Playlists or ChannelTab.Membership ? ChannelTab : ChannelTab.Courses;
            var item = new PinnedSource(canonical, Title, p.Kind, Cover, string.IsNullOrWhiteSpace(p.Channel) ? null : p.Channel, isMember, defTab);
            _host.SettingsStore.Update(s => s.PinnedSources = [.. s.PinnedSources, item]);
            _main.ShowToast(isMember ? $"“{Title}” Katıl (üye) kanalı olarak sabitlendi" : $"“{Title}” sabitlendi");
        }
    }

    [RelayCommand]
    private void TogglePinMember()
    {
        if (Info is null || !IsPinned) return;
        var canonical = YouTubeUrl.CanonicalSourceUrl(SourceUrl);
        _host.SettingsStore.Update(s => s.PinnedSources = s.PinnedSources
            .Select(x => string.Equals(YouTubeUrl.CanonicalSourceUrl(x.Url), canonical, StringComparison.OrdinalIgnoreCase) ? x with { IsMember = !x.IsMember } : x)
            .ToList());
    }

    [RelayCommand]
    private Task OpenPinned(PinnedSource? p) => p is null ? Task.CompletedTask
        : p.Kind == LinkKind.Channel ? LoadTabAsync(p.Url, p.DefaultTab) : LoadAsync(p.Url);

    [RelayCommand] private Task OpenPinnedCourses(PinnedSource? p) => p is null ? Task.CompletedTask : LoadTabAsync(p.Url, ChannelTab.Courses);
    [RelayCommand] private Task OpenPinnedPlaylists(PinnedSource? p) => p is null ? Task.CompletedTask : LoadTabAsync(p.Url, ChannelTab.Playlists);
    [RelayCommand] private Task OpenPinnedMembers(PinnedSource? p) => p is null ? Task.CompletedTask : LoadTabAsync(p.Url, ChannelTab.Membership);
    [RelayCommand] private Task OpenPinnedVideos(PinnedSource? p) => p is null ? Task.CompletedTask : LoadTabAsync(p.Url, ChannelTab.Videos);

    [RelayCommand]
    private void TogglePinnedMemberItem(PinnedSource? p)
    {
        if (p is null) return;
        _host.SettingsStore.Update(s => s.PinnedSources = s.PinnedSources.Select(x => x == p ? x with { IsMember = !x.IsMember } : x).ToList());
    }

    [RelayCommand]
    private void UnpinSource(PinnedSource? p)
    {
        if (p is null) return;
        _host.SettingsStore.Update(s => s.PinnedSources = s.PinnedSources.Where(x => x != p).ToList());
        _main.ShowToast($"“{p.Title}” sabitlenenlerden kaldırıldı", "Geri al",
            () => _host.SettingsStore.Update(s => s.PinnedSources = [.. s.PinnedSources, p]));
    }

    [RelayCommand]
    private Task OpenRecent(RecentSource? r) => r is null ? Task.CompletedTask
        : r.Kind == LinkKind.Channel && r.Tab is { } t ? LoadTabAsync(r.Url, t) : LoadAsync(r.Url);

    [RelayCommand]
    private void ClearRecents()
    {
        var prev = _host.Settings.RecentSources.ToList();
        if (prev.Count == 0) return;
        _host.SettingsStore.Update(s => s.RecentSources = []);
        _main.ShowToast("Son açılanlar temizlendi", "Geri al", () => _host.SettingsStore.Update(s => s.RecentSources = prev));
    }

    // ================= Kurs indirme seçenekleri =================

    private DownloadOptions Options() => _host.Settings.Defaults with
    {
        Mode = Mode,
        Height = Height == 0 ? null : Height,
        Container = Container,
        AudioFormat = AudioFormat,
        MusicMode = MusicMode,
        Subtitles = Subtitles,
        Transcript = Transcript,
        WriteDescription = WriteDescription,
        SponsorBlock = SponsorBlock,
        WriteM3u = WriteM3u,
        OutputDir = HasCustomOutputDir ? CustomOutputDir.Trim() : null,
    };

    [RelayCommand]
    private void ApplyProfile(DownloadProfile? p)
    {
        if (p is null) return;
        var o = p.Options;
        Mode = o.Mode;
        Height = o.Height ?? 0;
        Container = o.Container;
        AudioFormat = o.AudioFormat == AudioFormat.Opus ? AudioFormat.Original : o.AudioFormat;
        MusicMode = o.MusicMode;
        Toast = $"Profil uygulandı: {p.Name}";
    }

    [RelayCommand]
    private void PickCustomFolder()
    {
        var dlg = new OpenFolderDialog
        {
            InitialDirectory = HasCustomOutputDir ? CustomOutputDir : _host.Settings.OutputDir,
            Title = "Bu kurs/playlist için indirme klasörü seç",
        };
        if (dlg.ShowDialog() == true) CustomOutputDir = dlg.FolderName;
    }

    [RelayCommand] private void ClearCustomFolder() => CustomOutputDir = "";

    // ================= Toplama & kuyruğa ekleme =================

    /// <summary>Seçili videolar + seçili alt listelerin videoları. Listeler önbellekten ya da tek tek okunur; indirilmişler ve erişilemeyenler atlanır.</summary>
    private async Task<List<CollectItem>> CollectAsync()
    {
        var picked = Selected.ToList();
        var curListUrl = Info?.Kind == LinkKind.Playlist ? YouTubeUrl.CanonicalSourceUrl(SourceUrl) : null;
        var result = picked.Where(r => !r.Entry.IsList)
            .Select(r => new CollectItem(r.Entry, Title, r.Index, curListUrl, r.Entry.Channel ?? Info?.Channel, r.Done))
            .ToList();
        var lists = picked.Where(r => r.Entry.IsList).ToList();
        if (lists.Count == 0) return result;

        var archived = ArchivedIds();
        var errors = new List<string>();
        IsLoading = true;
        try
        {
            for (var i = 0; i < lists.Count; i++)
            {
                var list = lists[i].Entry;
                BusyText = $"Listeler okunuyor {i + 1}/{lists.Count}: {list.Title}";
                try
                {
                    if (await ResolveSubListAsync(list.Url) is { } p)
                        result.AddRange(PlaylistCollect.Pending(list, p, Info?.Channel, archived));
                }
                catch (Exception e) { errors.Add($"{list.Title}: {e.Message}"); }
            }
        }
        finally
        {
            IsLoading = false;
            BusyText = null;
        }
        Error = errors.Count > 0 ? string.Join("\n", errors) : null;
        return result;
    }

    private async Task<PlaylistInfo?> ResolveSubListAsync(string url)
    {
        if (_lists.TryGetValue(url, out var cached)) return cached;
        if (_host.ListCache.TryGet(url) is { } disk)
        {
            _lists[url] = disk;
            return disk;
        }
        if (await _host.YtDlp.ProbeAsync(url) is not PlaylistInfo p) return null;
        _lists[url] = p;
        _host.ListCache.Put(url, p);
        return p;
    }

    private void EnqueueItems(IReadOnlyList<CollectItem> items, string source)
    {
        var o = Options();
        // Ters sıra açıksa her liste kendi içinde ters çevrilip 1..N yeniden numaralandırılır (Üyeler sekmesi / ters kurslar için)
        var prepared = PlaylistCollect.Number(items, ReverseOrder);

        _host.Queue.Enqueue(prepared.Select(x => new NewJob(
            x.Item.Entry.Url,
            x.Item.Entry.Title,
            o with { Playlist = new PlaylistRef(x.Item.List, x.Index, x.Item.ListUrl, x.Item.Channel), SkipArchive = x.Item.WasDone },
            x.Item.Entry.Channel ?? x.Item.Channel ?? Info?.Channel,
            x.Item.Entry.Thumbnail,
            x.Item.Entry.Duration,
            Templates.ResolvePlaylist(_host.Settings.PlaylistTemplate, x.Item.List, x.Index, x.Item.Channel ?? Info?.Channel),
            source)).ToList());
        _main.Go(Page.Queue);
    }

    [RelayCommand]
    private async Task EnqueueSelected()
    {
        if (Info is null) return;
        var items = await CollectAsync();
        if (items.Count == 0) { Toast = "İndirilecek yeni video yok"; return; }
        EnqueueItems(items, $"{KindLabel}: {Title}");
    }

    [RelayCommand]
    private async Task AddToBasket()
    {
        var items = await CollectAsync();
        var source = $"{KindLabel}: {Title}";
        var n = _main.Basket.Add(items.Select(x => new BasketItem(x.Entry.Url, x.Entry.Title, x.Entry.Channel ?? x.Channel ?? Info?.Channel, x.Entry.Thumbnail,
            x.Entry.Duration, source, x.List, x.Index, x.ListUrl)));
        Toast = n > 0 ? $"{n} video sepete eklendi" : "Seçilenler zaten sepette";
    }

    // ================= Satır / alt liste sağ tık eylemleri =================

    [RelayCommand]
    private Task AnalyzeEntry(object? p) => p is PlaylistRow { Entry.IsList: false } r ? _main.OpenLinkAsync(r.Entry.Url) : Task.CompletedTask;

    [RelayCommand]
    private void AddSingleToBasket(object? p)
    {
        if (p is not PlaylistRow { Entry.IsList: false } r) return;
        var curListUrl = Info?.Kind == LinkKind.Playlist ? YouTubeUrl.CanonicalSourceUrl(SourceUrl) : null;
        var n = _main.Basket.Add([new BasketItem(r.Entry.Url, r.Entry.Title, r.Entry.Channel ?? Info?.Channel, r.Entry.Thumbnail,
            r.Entry.Duration, $"{KindLabel}: {Title}", Title, r.Index, curListUrl)]);
        _main.ShowToast(n > 0 ? "Sepete eklendi" : "Zaten sepette");
    }

    [RelayCommand]
    private void CopyEntryLink(object? p)
    {
        if (p is not PlaylistRow r) return;
        _host.Clipboard.Ignore(r.Entry.Url);
        try { System.Windows.Clipboard.SetDataObject(r.Entry.Url, copy: true); } catch { }
        _main.ShowToast("Bağlantı kopyalandı");
    }

    [RelayCommand]
    private void OpenEntryInBrowser(object? p)
    {
        if (p is PlaylistRow r) _main.OpenInBrowserCommand.Execute(r.Entry.Url);
    }

    [RelayCommand]
    private Task OpenEntryChannel(object? p) =>
        p is PlaylistRow { Entry.ChannelUrl: { } u } ? _main.OpenChannelCommand.ExecuteAsync(u) : Task.CompletedTask;

    [RelayCommand] private Task QuickDownloadSubList(object? p) => DownloadSingleSubListAsync(p as PlaylistRow, onlyMembers: false);
    [RelayCommand] private Task QuickDownloadSubListMembers(object? p) => DownloadSingleSubListAsync(p as PlaylistRow, onlyMembers: true);

    private async Task DownloadSingleSubListAsync(PlaylistRow? row, bool onlyMembers)
    {
        if (row is not { Entry.IsList: true }) return;
        IsLoading = true;
        BusyText = $"Liste okunuyor: {row.Entry.Title}";
        try
        {
            var pl = await ResolveSubListAsync(row.Entry.Url);
            if (pl is null) { _main.ShowToast("Liste okunamadı"); return; }
            var items = PlaylistCollect.Pending(row.Entry, pl, Info?.Channel, ArchivedIds(), onlyMembers);
            if (items.Count == 0)
            {
                _main.ShowToast(onlyMembers ? "Bu listede indirilecek yeni üye videosu yok" : "Bu listedeki tüm videolar zaten indirilmiş");
                return;
            }
            EnqueueItems(items, $"Liste: {row.Entry.Title}");
        }
        catch (Exception e) { Error = e.Message; }
        finally { IsLoading = false; BusyText = null; }
    }

    [RelayCommand]
    private async Task AddSubListToBasket(object? p)
    {
        if (p is not PlaylistRow { Entry.IsList: true } row) return;
        IsLoading = true;
        BusyText = $"Liste okunuyor: {row.Entry.Title}";
        try
        {
            var pl = await ResolveSubListAsync(row.Entry.Url);
            if (pl is null) return;
            var n = _main.Basket.Add(PlaylistCollect.Pending(row.Entry, pl, Info?.Channel, ArchivedIds())
                .Select(x => new BasketItem(x.Entry.Url, x.Entry.Title, x.Entry.Channel ?? x.Channel, x.Entry.Thumbnail, x.Entry.Duration,
                    $"Liste: {row.Entry.Title}", x.List, x.Index, x.ListUrl)));
            _main.ShowToast(n > 0 ? $"{n} video sepete eklendi" : "Yeni video yok ya da zaten sepette");
        }
        catch (Exception e) { Error = e.Message; }
        finally { IsLoading = false; BusyText = null; }
    }

    [RelayCommand]
    private async Task ToggleSubListFollow(object? p)
    {
        if (p is not PlaylistRow { Entry.IsList: true } row) return;
        var existing = _host.Follow.Find(row.Entry.Url);
        if (existing is not null)
        {
            await _host.Follow.UnfollowAsync(existing);
            row.IsListFollowed = false;
            _main.ShowToast($"“{row.Entry.Title}” takipten çıkarıldı");
            return;
        }
        var pl = await ResolveSubListAsync(row.Entry.Url);
        var ids = pl?.Entries.Select(e => e.Id) ?? [];
        var ch = !string.IsNullOrWhiteSpace(pl?.Channel) ? pl!.Channel : row.Entry.Channel ?? Info?.Channel;
        await _host.Follow.FollowAsync(row.Entry.Url, row.Entry.Title, Options(), ids, ChannelTab.Videos, row.Entry.Title, ch);
        row.IsListFollowed = true;
        _main.ShowToast($"“{row.Entry.Title}” takip ediliyor — yeni dersler kurs klasörüne iner");
    }

    [RelayCommand]
    private void ToggleSubListPin(object? p)
    {
        if (p is not PlaylistRow { Entry.IsList: true } row) return;
        var canonical = YouTubeUrl.CanonicalSourceUrl(row.Entry.Url);
        var existing = _host.Settings.PinnedSources.FirstOrDefault(x => string.Equals(YouTubeUrl.CanonicalSourceUrl(x.Url), canonical, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _host.SettingsStore.Update(s => s.PinnedSources = s.PinnedSources.Where(x => x != existing).ToList());
            _main.ShowToast($"“{row.Entry.Title}” sabitlenenlerden kaldırıldı");
        }
        else
        {
            var item = new PinnedSource(canonical, row.Entry.Title, LinkKind.Playlist, row.Entry.Thumbnail, row.Entry.Channel ?? Info?.Channel, row.HasMembers);
            _host.SettingsStore.Update(s => s.PinnedSources = [.. s.PinnedSources, item]);
            _main.ShowToast($"“{row.Entry.Title}” sabitlendi");
        }
    }

    /// <summary>Görünen (filtrelenmiş) satırları CSV'ye yazar; ayırıcı bölgesel ayardan (Türkçe Windows'ta ';').</summary>
    [RelayCommand]
    private void ExportCsv()
    {
        if (Info is null || Rows.Count == 0) return;
        var dlg = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = string.Concat(Title.Split(Path.GetInvalidFileNameChars())) + ".csv",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            // BOM: Excel UTF-8'i ancak onunla tanır (yoksa Türkçe karakterler bozulur)
            File.WriteAllText(dlg.FileName, Export.PlaylistCsv(Rows.Select(r => (r.Index, r.Entry, r.Done)), CultureInfo.CurrentCulture.TextInfo.ListSeparator),
                new UTF8Encoding(true));
            Toast = $"{Rows.Count} satır dışa aktarıldı";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Error = e.Message; }
    }

    /// <summary>Takip: kanal sekmelerinde o sekme (Üyeler dahil), kurs/playlistte tüm liste takip edilir.</summary>
    [RelayCommand]
    private async Task ToggleFollow()
    {
        if (Info is not { } p) return;
        var existing = _host.Follow.Find(SourceUrl, ChannelTab);
        if (existing is not null)
        {
            await _host.Follow.UnfollowAsync(existing);
            IsFollowed = false;
            return;
        }
        var label = p.Kind == LinkKind.Channel && ChannelTab != ChannelTab.Videos ? $"{Title} · {ChannelTab.Label()}" : Title;
        await _host.Follow.FollowAsync(SourceUrl, label, Options(), p.Entries.Select(e => e.Id), ChannelTab, Title, p.Channel);
        IsFollowed = true;
        _main.ShowToast($"“{label}” takip ediliyor");
    }

    /// <summary>Alt liste (ör. Playlistlerim ya da Kurslar içindeki bir playlist): o listeyi aç, "Geri" ile dönülebilsin.</summary>
    [RelayCommand]
    private async Task OpenSubList(object? p)
    {
        if (p is not PlaylistRow r) return;
        _back.Push(IsChannel ? YouTubeUrl.ChannelTabUrl(SourceUrl, ChannelTab) : SourceUrl);
        await LoadAsync(r.Entry.Url, keepHistory: true);
    }
}
