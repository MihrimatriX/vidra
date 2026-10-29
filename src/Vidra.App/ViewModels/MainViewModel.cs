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
using Vidra.Core.YtDlp;

namespace Vidra.App.ViewModels;

public enum Page { Download, Search, Playlist, Basket, Queue, Library, Stats, Account, Settings }

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

    public MainViewModel(AppHost host)
    {
        _host = host;
        Download = new DownloadViewModel(host, this);
        Search = new SearchViewModel(host, this);
        Stats = new StatsViewModel(host);
        Playlist = new PlaylistViewModel(host, this);
        Basket = new BasketViewModel(host, this);
        QueueVm = new QueueViewModel(host, this);
        Library = new LibraryViewModel(host);
        Account = new AccountViewModel(host, this);
        Settings = new SettingsViewModel(host);
        Setup = new SetupViewModel(host);
        Current = Download;

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
        };
        _timer.Start();
        RefreshStatus();
    }

    partial void OnPageChanged(Page value)
    {
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
