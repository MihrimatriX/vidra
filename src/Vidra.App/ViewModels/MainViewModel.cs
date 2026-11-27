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

/// <summary>Log: kenar çubuğunda yok, kuyruğun alt sayfası (bkz. LogViewModel).</summary>
public enum Page { Download, Search, Playlist, Basket, Queue, Library, Stats, Account, Settings, Log }

/// <summary>Alttaki kısa bildirim; Action varsa yanında düğme olarak (ör. "Geri al").</summary>
public sealed record ToastMessage(string Text, string? ActionLabel, Action? Action);

public enum UpdateState { None, Available, Downloading, Ready, Failed }

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
    public LogViewModel LogVm { get; }
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

    /// <summary>Dar pencere: kenar menüsü yalnızca ikon (MainWindow genişliğe göre ayarlar).</summary>
    [ObservableProperty] private bool _navCompact;

    /// <summary>Hesabım son açıldığından beri takip edilen listelerden kuyruğa eklenen yeni video sayısı (hesap satırında rozet).</summary>
    [ObservableProperty] private int _followNew;
    /// <summary>"YouTube durumu": soğuma, saatlik bütçe, yt-dlp yaşı. Level: ok / warn / bad.</summary>
    [ObservableProperty] private string _healthLabel = "YouTube: normal";
    [ObservableProperty] private string _healthLevel = "ok";
    [ObservableProperty] private string _healthTip = "";
    private string? _ytDlpVersion;
    private DateTimeOffset _lastAuthToast = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSessionCheck = DateTimeOffset.Now;
    private bool _updatingYtDlp;

    /// <summary>Pano izleme: kopyalanan bağlantı için üstte çıkan şerit.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ClipboardIsVideo))] private string? _clipboardLink;

    /// <summary>Şeritteki "Hemen indir" yalnızca tek video için (liste/kanal seçim ister).</summary>
    public bool ClipboardIsVideo => ClipboardLink is { } l && YouTubeUrl.Classify(l) == LinkKind.Video;

    /// <summary>Tüm kuyruğun ilerlemesi (görev çubuğu, tepsi ipucu); saniyede bir yenilenir.</summary>
    [ObservableProperty] private QueueProgress _progress = QueueProgress.From([]);

    [ObservableProperty] private ToastMessage? _toast;

    /// <summary>Gizli mod (yalnızca bu oturum): yeni indirmeler geçmişe ve arşive yazılmaz.</summary>
    [ObservableProperty] private bool _incognito;

    /// <summary>GitHub'da daha yeni Vidra sürümü varsa; kenar çubuğunda bağlantı olarak görünür.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateLabel), nameof(UpdateTip))]
    private AppRelease? _update;
    private readonly DispatcherTimer _toastTimer = new();
    private bool _toastHeld;
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
        LogVm = new LogViewModel(host, this);
        Library = new LibraryViewModel(host, this);
        Account = new AccountViewModel(host, this);
        Settings = new SettingsViewModel(host);
        Setup = new SetupViewModel(host);
        Palette = new CommandPaletteViewModel(host, this);
        Current = Download;
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast = null; };

        host.SettingsStore.Changed += (_, _) => RefreshStatus();
        host.Follow.Checked += (_, e) => { if (e.NewCount > 0) AppHost.Dispatch(() => { if (Page != Page.Account) FollowNew += e.NewCount; }); };
        var refreshStatus = Ui.Coalesce(RefreshStatus);
        host.Queue.Jobs.CollectionChanged += (_, _) => refreshStatus();
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
            if (Page == Page.Log) LogVm.Selected?.Log.Flush(); // açık günlük canlı kalsın
            QueueVm.CheckSchedule();
            RefreshStatus(); // duraklat/hata gibi durum değişiklikleri koleksiyon olayı üretmez
            RefreshProgress();
            if (_host.Guard.InCooldown || HealthLevel == "bad" || QueueVm.CooldownText is not null) RefreshHealth(); // geri sayım ve soğuma bitişi
            _ = MaybeCheckSessionAsync();
        };
        _timer.Start();
        RefreshStatus();
        CleanupUpdates();
        if (host.Settings.CheckAppUpdates) _ = CheckUpdatesLaterAsync();

        // Oturum düştüyse her işte ayrı ayrı değil, en fazla 10 dakikada bir söyle
        host.Queue.AuthFailed += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (DateTimeOffset.Now - _lastAuthToast < TimeSpan.FromMinutes(10)) return;
            _lastAuthToast = DateTimeOffset.Now;
            ShowToast(CookieActive ? "Oturum düşmüş olabilir: üyelik/giriş isteyen videolar inmiyor" : "Bu video giriş istiyor",
                CookieActive ? "Yeniden giriş" : "Giriş yap", () => Go(Page.Account));
        });
        host.Guard.Changed += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(RefreshHealth);
        host.Queue.Drained += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => _ = MaybeUpdateYtDlpAsync());
        _ = StartHealthAsync();
    }

    private async Task StartHealthAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(20)); // açılışı yavaşlatmasın; kurulum ekranı da bitmiş olsun
        await RefreshYtDlpVersionAsync();
        await MaybeUpdateYtDlpAsync();
    }

    private async Task RefreshYtDlpVersionAsync()
    {
        try
        {
            var st = await _host.Tools.StatusAsync();
            _ytDlpVersion = st.FirstOrDefault(t => t.Id == Core.Tools.ToolId.YtDlp)?.Version;
        }
        catch { /* bileşen yoksa kurulum ekranı ilgilenir */ }
        RefreshHealth();
    }

    /// <summary>
    /// yt-dlp'yi günde en fazla bir kez, yalnızca kuyruk boşken günceller (çalışan exe Windows'ta değiştirilemez).
    /// YouTube değişikliklerine karşı en etkili önlem budur.
    /// </summary>
    public async Task MaybeUpdateYtDlpAsync(bool force = false)
    {
        var s = _host.Settings;
        if (_updatingYtDlp || !_host.Tools.AllInstalled) return;
        if (!force && (!s.AutoUpdateYtDlp || !Core.Tools.YtDlpFreshness.IsCheckDue(s.LastYtDlpUpdateCheck, DateTimeOffset.Now))) return;
        if (_host.Queue.ActiveCount > 0)
        {
            if (force) ShowToast("İndirmeler sürerken yt-dlp güncellenemez; kuyruk boşalınca kendiliğinden güncellenir");
            return;
        }
        _updatingYtDlp = true;
        try
        {
            var r = await _host.Tools.UpdateYtDlpAsync(s.UpdateChannel);
            _host.SettingsStore.Update(x => x.LastYtDlpUpdateCheck = DateTimeOffset.Now);
            if (Core.Tools.YtDlpFreshness.UpdatedTo(r.StdOut + "\n" + r.StdErr) is { } v) ShowToast($"yt-dlp güncellendi ({v})");
            else if (force) ShowToast(r.ExitCode == 0 ? "yt-dlp zaten güncel" : "yt-dlp güncellenemedi: " + (r.StdErr.Trim() is { Length: > 0 } e ? e : "bilinmeyen hata"));
        }
        catch (Exception e) when (force) { ShowToast("yt-dlp güncellenemedi: " + e.Message); }
        catch { /* sessiz deneme; bir sonraki açılışta yine denenir */ }
        finally { _updatingYtDlp = false; }
        await RefreshYtDlpVersionAsync();
    }

    /// <summary>Oturum açıkken 12 saatte bir hafif oturum testi; düştüyse kullanıcı indirme hata vermeden önce öğrenir.</summary>
    private async Task MaybeCheckSessionAsync()
    {
        if (!CookieActive || _host.Guard.InCooldown || DateTimeOffset.Now - _lastSessionCheck < TimeSpan.FromHours(12)) return;
        _lastSessionCheck = DateTimeOffset.Now;
        try
        {
            var r = await _host.YtDlp.TestSessionAsync();
            if (!r.Ok && r.Kind == ErrorKind.Auth)
                ShowToast("YouTube oturumunun süresi dolmuş", "Yeniden giriş", () => Go(Page.Account));
        }
        catch { /* ağ yoksa sonraki turda */ }
    }

    public void RefreshHealth()
    {
        QueueVm.RefreshCooldown();
        var signedIn = _host.Settings.Cookies is not NoCookies;
        var st = _host.Guard.State(signedIn);
        var now = DateTimeOffset.Now;
        var age = Core.Tools.YtDlpFreshness.AgeDays(_ytDlpVersion, now);
        var waiting = _host.Queue.WaitingCount;
        var budget = $"Son 1 saatte {st.StartsLastHour} indirme başlatıldı (YouTube'un yaklaşık sınırı: {st.HourlyBudget}/saat {(signedIn ? "hesapla" : "girişsiz")}).";
        var ver = _ytDlpVersion is null ? "" : $"\nyt-dlp {_ytDlpVersion}{(age is { } d ? $" · {d} gün önce" : "")}";
        if (st.CooldownUntil is { } until)
        {
            HealthLevel = "bad";
            HealthLabel = $"YouTube sınırı · {until.ToLocalTime():HH:mm}'e kadar";
            HealthTip = $"YouTube {(st.CooldownReason == ErrorKind.BotCheck ? "bot doğrulaması istedi" : "istek sınırı uyguladı")}. " +
                        $"Sınırı uzatmamak için yeni indirme başlatılmıyor; {waiting} iş bekliyor ve süre dolunca kendiliğinden yeniden denenecek.\n{budget}{ver}\nTıkla: kuyruğu aç";
        }
        else if (st.Recovering || st.NearBudget)
        {
            HealthLevel = "warn";
            HealthLabel = st.NearBudget ? $"YouTube: yavaşlatıldı ({st.StartsLastHour}/{st.HourlyBudget})" : "YouTube: toparlanıyor";
            HealthTip = (st.NearBudget ? "Saatlik sınıra yaklaşıldı: " : "Yakın zamanda sınıra takıldı: ") +
                        $"indirmeler tek tek ve Güvenli nazik modla sürüyor.\n{budget}{ver}";
        }
        else if (Core.Tools.YtDlpFreshness.IsStale(_ytDlpVersion, now))
        {
            HealthLevel = "warn";
            HealthLabel = $"yt-dlp {age} gün eski";
            HealthTip = $"Eski yt-dlp YouTube'da 403, bot doğrulaması ve \"biçim yok\" hatalarının en yaygın sebebi.{ver}\nTıkla: şimdi güncelle";
        }
        else
        {
            HealthLevel = "ok";
            HealthLabel = "YouTube: normal";
            HealthTip = $"{budget}{ver}\nNazik mod: {_host.Guard.EffectivePolite(_host.Settings).Label()}";
        }
    }

    [RelayCommand]
    private async Task HealthAction()
    {
        if (HealthLevel == "bad") Go(Page.Queue);
        else if (Core.Tools.YtDlpFreshness.IsStale(_ytDlpVersion, DateTimeOffset.Now)) await MaybeUpdateYtDlpAsync(force: true);
        else Go(Page.Settings);
    }

    private async Task CheckUpdatesLaterAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(8)); // açılışı yavaşlatmasın
        await CheckUpdatesAsync(manual: false);
    }

    /// <summary>GitHub'daki son sürümü denetler; elle istenince "güncel" ya da "denetlenemedi" de der.</summary>
    public async Task CheckUpdatesAsync(bool manual)
    {
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
        var r = await AppUpdate.CheckAsync(_host.Http, cts.Token);
        if (r is not null && AppUpdate.IsNewer(r, CurrentVersion))
        {
            if (Update?.Version == r.Version && UpdateState != UpdateState.Failed) { if (manual) ShowUpdateToast(); return; }
            Update = r;
            UpdateState = UpdateState.Available;
            if (_host.Settings.AutoDownloadAppUpdates && AppUpdate.AssetFor(r, Kind) is not null) await DownloadUpdateAsync(announce: false);
            ShowUpdateToast();
        }
        else if (manual) ShowToast(r is null ? "Güncelleme denetlenemedi: GitHub'a ulaşılamadı" : $"Vidra güncel ({Version})");
    }

    // ================= Uygulama içi güncelleme =================

    private static readonly System.Version CurrentVersion = typeof(MainViewModel).Assembly.GetName().Version ?? new System.Version(0, 0, 0);
    private static readonly string ExePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Vidra.exe");
    private static readonly InstallKind Kind = AppUpdate.DetectInstallKind(Path.GetDirectoryName(ExePath)!);
    private string UpdatesDir => Path.Combine(_host.Paths.LocalDir, "updates");
    private string? _updateFile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateLabel), nameof(UpdateTip))]
    private UpdateState _updateState;

    /// <summary>İndirme ilerlemesi 0–1; boyut bilinmiyorsa null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateLabel))]
    private double? _updateProgress;

    /// <summary>Kenar çubuğundaki güncelleme düğmesinin yazısı.</summary>
    public string UpdateLabel => UpdateState switch
    {
        UpdateState.Downloading => UpdateProgress is { } p ? $"İndiriliyor %{p * 100:0}" : "İndiriliyor…",
        UpdateState.Ready => "Yeniden başlat ve kur",
        UpdateState.Failed => "Güncellenemedi: tekrar dene",
        _ => $"{Update?.Tag} çıktı · İndir",
    };

    public string UpdateTip => UpdateState switch
    {
        UpdateState.Ready => $"Vidra {Update?.Tag} indirildi. Vidra kapanıp güncellenir ve yeniden açılır; duraklatılan indirmeler kaldığı yerden sürer.",
        UpdateState.Downloading => "Güncelleme indiriliyor",
        _ => $"Vidra {Update?.Tag} sürümünü indirip kur",
    };

    private void ShowUpdateToast()
    {
        if (Update is not { } r) return;
        if (UpdateState == UpdateState.Ready) ShowToast($"Vidra {r.Tag} kurulmaya hazır", "Yeniden başlat", () => UpdateActionCommand.Execute(null));
        else ShowToast($"Vidra {r.Tag} çıktı", "İndir ve kur", () => UpdateActionCommand.Execute(null));
    }

    /// <summary>Kenar çubuğundaki düğme: duruma göre indirir ya da kurar.</summary>
    [RelayCommand]
    private async Task UpdateAction()
    {
        switch (UpdateState)
        {
            case UpdateState.Available or UpdateState.Failed:
                await DownloadUpdateAsync(announce: true);
                if (UpdateState == UpdateState.Ready) ApplyUpdate(confirmed: false);
                break;
            case UpdateState.Ready:
                ApplyUpdate(confirmed: false);
                break;
        }
    }

    [RelayCommand] private void OpenUpdate() { if (Update is { } r) OpenInBrowser(r.Url); }

    private async Task DownloadUpdateAsync(bool announce)
    {
        if (Update is not { } r || UpdateState == UpdateState.Downloading) return;
        // Sürümde bu kurulum türünün dosyası yoksa (ör. eski biçimli sürüm) eski davranış: sürüm sayfası
        if (AppUpdate.AssetFor(r, Kind) is not { } asset)
        {
            if (announce) OpenInBrowser(r.Url);
            return;
        }
        UpdateState = UpdateState.Downloading;
        UpdateProgress = null;
        try
        {
            // Progress<T> arayüz thread'inde yaratıldığı için raporları oraya taşır
            var progress = new Progress<double?>(p => UpdateProgress = p);
            _updateFile = await AppUpdater.DownloadAsync(_host.Http, asset, r.Version, UpdatesDir, progress);
            UpdateState = UpdateState.Ready;
        }
        catch (Exception e)
        {
            UpdateState = UpdateState.Failed;
            App.Log(e);
            if (announce) ShowToast(e is InvalidDataException ? "Güncelleme dosyası doğrulanamadı; silindi" : "Güncelleme indirilemedi", "Sürüm sayfası", () => OpenInBrowser(r.Url));
        }
    }

    private void ApplyUpdate(bool confirmed)
    {
        if (_updateFile is not { } file || Update is not { } r) return;
        // Etkin indirme varsa önce sor: kapanışta duraklatılır, açılışta .part dosyalarından sürer
        if (!confirmed && _host.Queue.ActiveCount > 0)
        {
            ShowToast($"{_host.Queue.ActiveCount} indirme duraklatılıp güncellemeden sonra sürecek", "Yine de kur", () => ApplyUpdate(confirmed: true));
            return;
        }
        try
        {
            ProcessStartInfo launch;
            if (Kind == InstallKind.Installed)
            {
                var dir = Path.GetDirectoryName(ExePath)!;
                var allUsers = IsUnder(dir, Environment.SpecialFolder.ProgramFiles) || IsUnder(dir, Environment.SpecialFolder.ProgramFilesX86);
                launch = new ProcessStartInfo(file, AppUpdate.SetupArguments(allUsers)) { UseShellExecute = true }; // ShellExecute: UAC istemi çıkabilsin
            }
            else
            {
                AppUpdater.SwapPortable(ExePath, file);
                launch = new ProcessStartInfo(ExePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(ExePath)! };
            }
            // Yeni süreç tek örnek kilidi bırakıldıktan sonra başlamalı (yoksa çalışan Vidra'ya iletip kapanır): bkz. App.OnExit
            ((App)System.Windows.Application.Current).LaunchAfterExit = launch;
            ((MainWindow)System.Windows.Application.Current.MainWindow!).ExitApp();
        }
        catch (Exception e)
        {
            App.Log(e);
            UpdateState = UpdateState.Failed;
            ShowToast("Güncelleme uygulanamadı", "Sürüm sayfası", () => OpenInBrowser(r.Url));
        }
    }

    private static bool IsUnder(string dir, Environment.SpecialFolder folder)
    {
        var root = Environment.GetFolderPath(folder);
        return root.Length > 0 && Path.GetFullPath(dir).StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Açılışta: kurulmuş eski indirmeleri ve taşınabilir güncellemeden kalan exe'yi sil.</summary>
    private void CleanupUpdates()
    {
        try
        {
            AppUpdater.CleanupDownloads(UpdatesDir, CurrentVersion);
            if (Kind == InstallKind.Portable) AppUpdater.CleanupPortable(ExePath);
        }
        catch (Exception e) { App.Log(e); }
    }

    partial void OnIncognitoChanged(bool value)
    {
        _host.Queue.Incognito = value;
        ShowToast(value ? "Gizli mod açık: yeni indirmeler geçmişe ve arşive yazılmaz" : "Gizli mod kapandı");
    }

    [RelayCommand] private void ToggleIncognito() => Incognito = !Incognito;

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
            Page.Log => LogVm,
            _ => Download,
        };
        if (value == Page.Account) { FollowNew = 0; _ = Account.RefreshAsync(); }
        if (value == Page.Settings) _ = Settings.RefreshToolsAsync();
        if (value == Page.Stats) Stats.Refresh(); // "bu hafta" gibi pencereler gün dönünce kayar
        if (value == Page.Library) Library.Refresh(); // silinen/taşınan dosyalar düşsün
        if (value == Page.Queue) QueueVm.OnShown();
    }

    public void Go(Page p) => Page = p;

    /// <summary>Ayarlar'ı belirli bir bölümde açar (SettingsView'daki x:Name: SecAccount, SecNet, SecTools…).</summary>
    public void OpenSettingsSection(string section)
    {
        Settings.PendingSection = section;
        if (Page == Page.Settings) Page = Page.Download; // aynı sayfadaysa görünüm yeniden yüklensin
        Go(Page.Settings);
    }

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
        // Fare zaten bildirimin üstündeyse yenisi de altından kaybolmasın (ayrılınca süre işler)
        if (!_toastHeld) _toastTimer.Start();
    }

    /// <summary>Fare bildirimin üstündeyken kapanmasın (okurken ya da "Geri al"a uzanırken).</summary>
    public void HoldToast(bool hold)
    {
        _toastHeld = hold;
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
        _toastHeld = false;
        t?.Action?.Invoke();
    }

    [RelayCommand]
    private void DismissToast()
    {
        Toast = null;
        _toastHeld = false; // bildirim kapanınca MouseLeave gelmez
    }

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

    /// <summary>Kenar çubuğundaki hesap satırı: Hesabım (oturum, akışlar, takip edilenler) artık yalnızca buradan ve Ctrl+8'den açılır.</summary>
    [RelayCommand]
    private void AccountStatus() => Go(Page.Account);

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

    /// <summary>Kanalı doğrudan istenen sekmede açar (Kurslar, Playlistler, Üyeler vb.).</summary>
    public async Task OpenChannelTabAsync(string url, ChannelTab tab)
    {
        Go(Page.Playlist);
        await Playlist.LoadTabAsync(url, tab);
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

    /// <summary>Panodaki videoyu analiz etmeden, Ayarlar'daki varsayılan biçimle kuyruğa ekler; bulunulan ekran değişmez.</summary>
    [RelayCommand]
    private void QuickDownload()
    {
        if (ClipboardLink is not { } link || YouTubeUrl.Classify(link) != LinkKind.Video) return;
        ClipboardLink = null;
        _host.Queue.Enqueue([new NewJob(link, link, _host.Settings.Defaults, Source: "Pano")]);
        ShowToast($"Kuyruğa eklendi · {_host.Settings.Defaults.Describe()}", "Kuyruğu aç", () => Go(Page.Queue));
    }

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
        // İndirirken ya da zamanlanmış başlangıç beklenirken sistem uykuya geçmesin (ekran yine kapanabilir)
        PowerService.KeepAwake(_host.Settings.KeepAwake && (p.Running > 0 || (QueueVm.IsScheduled && p.Paused + p.Queued > 0)));
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
