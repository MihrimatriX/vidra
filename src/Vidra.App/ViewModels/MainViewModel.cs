using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vidra.App.Services;
using Vidra.Core;
using Vidra.Core.Models;
using Vidra.Core.Queue;
using Vidra.Core.YtDlp;

namespace Vidra.App.ViewModels;

public enum Page { Download, Search, Playlist, Basket, Queue, Library, Stats, Account, Settings }

/// <summary>Alttaki kısa bildirim; Action varsa yanında düğme olarak (ör. "Geri al").</summary>
public sealed record ToastMessage(string Text, string? ActionLabel, Action? Action);

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _timer;

    public DownloadViewModel Download { get; }
    public SearchViewModel Search { get; }
    public StatsViewModel Stats { get; }
    public PlaylistViewModel Playlist { get; }
    public BasketViewModel Basket { get; }
    public QueueViewModel QueueVm { get; }
    public LibraryViewModel Library { get; }
    public AccountViewModel Account { get; }
    public SettingsViewModel Settings { get; }
    public SetupViewModel Setup { get; }
    public CommandPaletteViewModel Palette { get; }

    [ObservableProperty] private Page _page = Page.Download;
    [ObservableProperty] private object? _current;

    // Kenar çubuğu durumu
    [ObservableProperty] private string _cookieLabel = "";
    [ObservableProperty] private bool _cookieActive;
    [ObservableProperty] private string _diskLabel = "";
    [ObservableProperty] private int _activeCount;
    [ObservableProperty] private string _speedLabel = "";

    /// <summary>Pano izleme: kopyalanan bağlantı için üstte çıkan şerit.</summary>
    [ObservableProperty] private string? _clipboardLink;

    /// <summary>Tüm kuyruğun ilerlemesi (görev çubuğu, tepsi ipucu); saniyede bir yenilenir.</summary>
    [ObservableProperty] private QueueProgress _progress = QueueProgress.From([]);

    [ObservableProperty] private ToastMessage? _toast;
    private readonly DispatcherTimer _toastTimer = new();
    private string _trayTip = "";

    public MainViewModel(AppHost host)
    {
        _host = host;
        Download = new DownloadViewModel(host, this);
        Search = new SearchViewModel(host, this);
        Stats = new StatsViewModel(host);
        Playlist = new PlaylistViewModel(host, this);
        Basket = new BasketViewModel(host, this);
        QueueVm = new QueueViewModel(host, this);
        Library = new LibraryViewModel(host, this);
        Account = new AccountViewModel(host, this);
        Settings = new SettingsViewModel(host);
        Setup = new SetupViewModel(host);
        Palette = new CommandPaletteViewModel(host, this);
        Current = Download;
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast = null; };

        host.SettingsStore.Changed += (_, _) => RefreshStatus();
        host.Queue.Jobs.CollectionChanged += (_, _) => RefreshStatus();
        host.Clipboard.LinkCopied += (_, link) =>
        {
            if (Download.Url.Trim() == link) return;
            ClipboardLink = link;
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            var speed = host.Queue.TotalSpeed;
            SpeedLabel = speed > 0 ? Fmt.Speed(speed) : "";
            QueueVm.SampleSpeed(speed);
            QueueVm.CheckSchedule();
            RefreshStatus(); // duraklat/hata gibi durum değişiklikleri koleksiyon olayı üretmez
            RefreshProgress();
        };
        _timer.Start();
        RefreshStatus();
    }

    partial void OnPageChanged(Page value)
    {
        Palette.Close();
        Current = value switch
        {
            Page.Search => Search,
            Page.Playlist => Playlist,
            Page.Basket => Basket,
            Page.Queue => QueueVm,
            Page.Library => Library,
            Page.Stats => Stats,
            Page.Account => Account,
            Page.Settings => Settings,
            _ => Download,
        };
        if (value == Page.Account) _ = Account.RefreshAsync();
        if (value == Page.Settings) _ = Settings.RefreshToolsAsync();
        if (value == Page.Stats) Stats.Refresh(); // "bu hafta" gibi pencereler gün dönünce kayar
        if (value == Page.Library) Library.Refresh(); // silinen/taşınan dosyalar düşsün
    }

    public void Go(Page p) => Page = p;

    /// <summary>Ctrl+1…9 ve palet: sayfa adıyla geçiş.</summary>
    [RelayCommand]
    private void GoTo(string? page)
    {
        if (Enum.TryParse<Page>(page, out var p)) Go(p);
    }

    [RelayCommand]
    private void OpenPalette()
    {
        if (!Setup.IsVisible) Palette.Open();
    }

    /// <summary>Metin kutusu dışında Ctrl+V: panodaki bağlantıyı hangi ekrandan olursa olsun aç.</summary>
    public async Task PasteAsync()
    {
        string text;
        try { text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : ""; }
        catch (System.Runtime.InteropServices.ExternalException) { return; } // pano başka uygulamada kilitli
        ClipboardLink = null; // aynı bağlantı için üstteki şerit artık gereksiz
        if (!await OpenTextAsync(text, "Pano")) ShowToast("Panoda YouTube bağlantısı yok");
    }

    /// <summary>
    /// Sürükle-bırak, pano, .url/.txt dosyası: birden çok video bağlantısı → hepsi kuyruğa; tek bağlantı → türüne göre ekran.
    /// </summary>
    /// <returns>Metinde YouTube bağlantısı yoksa false.</returns>
    public async Task<bool> OpenTextAsync(string text, string source)
    {
        var links = YouTubeUrl.ExtractLinks(text);
        if (links.Count == 0) return false;
        var videos = links.Where(l => YouTubeUrl.Classify(l) == LinkKind.Video).ToList();
        if (videos.Count >= 2) Download.EnqueueLinks(videos, source);
        else await OpenLinkAsync(links[0]);
        return true;
    }

    /// <summary>Kısa bildirim; eylemli olanlar ("Geri al") daha uzun kalır. Yenisi eskisinin yerine geçer.</summary>
    public void ShowToast(string text, string? actionLabel = null, Action? action = null)
    {
        Toast = new ToastMessage(text, actionLabel, action);
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(action is null ? 3.5 : 7);
        _toastTimer.Start();
    }

    /// <summary>Fare bildirimin üstündeyken kapanmasın (okurken ya da "Geri al"a uzanırken).</summary>
    public void HoldToast(bool hold)
    {
        if (Toast is null) return;
        _toastTimer.Stop();
        if (hold) return;
        _toastTimer.Interval = TimeSpan.FromSeconds(2.5);
        _toastTimer.Start();
    }

    [RelayCommand]
    private void ToastAction()
    {
        var t = Toast;
        Toast = null;
        t?.Action?.Invoke();
    }

    [RelayCommand] private void DismissToast() => Toast = null;

    /// <summary>Kartlardaki "YouTube'da aç": yalnızca http(s) adresleri varsayılan tarayıcıya gider.</summary>
    [RelayCommand]
    private void OpenInBrowser(string? url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "https" or "http")
            Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
    }

    /// <summary>Karttaki kanal adı: kanalı uygulama içinde (Playlist &amp; kanal ekranında) açar.</summary>
    [RelayCommand]
    private Task OpenChannel(string? url) => url is not null && YouTubeUrl.Classify(url) == LinkKind.Channel ? OpenLinkAsync(url) : Task.CompletedTask;

    /// <summary>Kenar çubuğundaki hesap satırı: bağlıysa Hesabım'ı açar, değilse girişi başlatır.</summary>
    [RelayCommand]
    private async Task AccountStatus()
    {
        if (CookieActive) Go(Page.Account);
        else await Account.SignInCommand.ExecuteAsync(null);
    }

    /// <summary>Herhangi bir ekrandan bağlantı açma: türüne göre doğru ekrana yönlendirir.</summary>
    public async Task OpenLinkAsync(string url)
    {
        var kind = YouTubeUrl.Classify(url);
        if (kind is LinkKind.Playlist or LinkKind.Channel or LinkKind.Feed)
        {
            Go(Page.Playlist);
            await Playlist.LoadAsync(url);
        }
        else
        {
            Go(Page.Download);
            Download.Url = url;
            await Download.AnalyzeCommand.ExecuteAsync(null);
        }
    }

    [RelayCommand]
    private async Task AnalyzeClipboard()
    {
        var link = ClipboardLink;
        ClipboardLink = null;
        if (link is not null) await OpenLinkAsync(link);
    }

    [RelayCommand]
    private void DismissClipboard() => ClipboardLink = null;

    public string Version { get; } = "v" + typeof(MainViewModel).Assembly.GetName().Version?.ToString(3);

    /// <summary>Kenar çubuğundaki hızlı geçiş: Sistem seçiliyse de o anki görünümün tersine geçer.</summary>
    [RelayCommand]
    private void ToggleTheme()
    {
        var next = ThemeService.IsDark ? ThemeMode.Light : ThemeMode.Dark;
        _host.SettingsStore.Update(s => s.Theme = next);
        ThemeService.Apply(next);
    }

    private void RefreshProgress()
    {
        var p = QueueProgress.From(_host.Queue.Jobs);
        if (p != Progress) Progress = p;
        var tip = p.IsEmpty ? "Vidra" : $"Vidra · {p.Describe()}" + (p.Speed > 0 ? $" · {Fmt.Speed(p.Speed)}" : "");
        if (tip == _trayTip) return;
        _trayTip = tip;
        _host.Tray?.SetTooltip(tip);
    }

    public void RefreshStatus()
    {
        var c = _host.Settings.Cookies;
        CookieActive = c is not NoCookies;
        CookieLabel = c switch
        {
            AppLoginCookies => _host.Account.Active is { } a ? $"YouTube: {a.Name}" : "YouTube hesabı bağlı",
            ExtensionCookies => "Tarayıcı oturumu (eklenti)",
            BrowserCookies b => $"Çerezler: {b.Browser.Label()}",
            FileCookies => "Çerezler: cookies.txt",
            _ => "Giriş yapılmadı",
        };
        ActiveCount = _host.Queue.Jobs.Count(j => j.Status is not (JobStatus.Paused or JobStatus.Error));

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_host.Settings.OutputDir));
            if (!string.IsNullOrEmpty(root))
            {
                var d = new DriveInfo(root);
                DiskLabel = $"{d.Name.TrimEnd('\\')} · {Fmt.Bytes(d.AvailableFreeSpace)} boş";
            }
        }
        catch { DiskLabel = ""; }
    }
}
