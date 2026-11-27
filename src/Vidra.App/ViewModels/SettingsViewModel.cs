using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Vidra.App.Services;
using Vidra.Core.Models;
using Vidra.Core.Tools;
using Vidra.Core.YtDlp;

namespace Vidra.App.ViewModels;

public sealed record CookieChoice(string Id, string Name, string Hint);

public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly Dictionary<string, object?> Sample = new()
    {
        ["title"] = "Örnek video", ["uploader"] = "Kanal adı", ["channel"] = "Kanal adı", ["id"] = "dQw4w9WgXcQ",
        ["ext"] = "mp4", ["upload_date"] = "20260115", ["height"] = 1080,
    };

    private readonly AppHost _host;
    private AppSettings S => _host.Settings;

    public SettingsViewModel(AppHost host)
    {
        _host = host;
        foreach (var (id, label) in Vidra.Core.Models.SponsorCategories.All)
        {
            var t = new Toggle(id, label, null, S.SponsorCategories.Contains(id));
            t.PropertyChanged += (_, _) => Set(s => s.SponsorCategories = SponsorCats.Where(x => x.IsOn).Select(x => x.Id).ToList(), nameof(SponsorCats));
            SponsorCats.Add(t);
        }
        host.SettingsStore.Changed += (_, _) => OnPropertyChanged(nameof(SelectedCookie));
    }

    // Seçenek listeleri
    public IReadOnlyList<Choice> Themes => Choices.Themes;
    public IReadOnlyList<Choice> Heights => Choices.Heights;
    public IReadOnlyList<Choice> Containers => Choices.Containers;
    public IReadOnlyList<Choice> Codecs => Choices.Codecs;
    public IReadOnlyList<Choice> AudioFormats => Choices.AudioFormats;
    public IReadOnlyList<Choice> ConcurrencyChoices => Choices.Concurrency;
    public IReadOnlyList<Choice> FragmentChoices => Choices.Fragments;
    public IReadOnlyList<Choice> RetryChoices => Choices.Retries;
    public IReadOnlyList<Choice> RateLimits => Choices.RateLimits;
    public IReadOnlyList<Choice> UpdateChannels => Choices.Channels;
    public IReadOnlyList<Choice> PoliteLevels => Choices.PoliteLevels;
    public ObservableCollection<Toggle> SponsorCats { get; } = [];
    public ObservableCollection<ToolStatus> Tools { get; } = [];

    public IReadOnlyList<CookieChoice> CookieChoices { get; } =
    [
        new("extension", "Tarayıcı eklentisi", "Önerilen: oturum düşmez"),
        new("app", "Vidra hesabı", "Uygulama içi giriş"),
        new("firefox", "Firefox", "Önerilen tarayıcı"),
        new("chrome", "Chrome", "Kapalıyken"),
        new("edge", "Edge", "Kapalıyken"),
        new("brave", "Brave", "Kapalıyken"),
        new("file", "cookies.txt", "Dosyadan"),
        new("none", "Kullanma", "Herkese açık"),
    ];

    [ObservableProperty] private string? _notice;

    /// <summary>Ayarlar açılınca kaydırılacak bölüm (ör. "SecTools"); görünüm okuyup temizler.</summary>
    public string? PendingSection { get; set; }

    /// <summary>Ayarlar'da gelişmiş bölümler (Ağ &amp; performans, Bileşenler) açık mı; oturum boyunca hatırlanır.</summary>
    public bool ShowAdvanced { get; set; }
    [ObservableProperty] private string? _updateLog;
    [ObservableProperty] private bool _updating;

    private void Set(Action<AppSettings> change, [CallerMemberName] string? name = null)
    {
        _host.SettingsStore.Update(change);
        OnPropertyChanged(name);
    }

    // ---- Hesap & çerezler
    public CookieChoice? SelectedCookie
    {
        get
        {
            var id = S.Cookies switch
            {
                AppLoginCookies => "app",
                ExtensionCookies => "extension",
                BrowserCookies b => b.Browser.Arg(),
                FileCookies => "file",
                _ => "none",
            };
            return CookieChoices.FirstOrDefault(c => c.Id == id);
        }
        set
        {
            if (value is null || value == SelectedCookie) return;
            switch (value.Id)
            {
                case "none": Set(s => s.Cookies = new NoCookies()); break;
                case "app":
                    if (_host.Settings.Accounts.FirstOrDefault(a => File.Exists(_host.Paths.AccountCookiesFile(a.Id))) is { } acc) _host.Account.Use(acc);
                    else Notice = "Önce Hesabım ekranından giriş yap.";
                    break;
                case "extension":
                    if (File.Exists(_host.Paths.ExtensionCookiesFile)) _host.Account.UseExtension();
                    else Notice = "Önce Hesabım ekranından tarayıcı eklentisini kur; eklenti ilk eşitlemeden sonra seçilebilir.";
                    break;
                case "file":
                    var dlg = new OpenFileDialog { Filter = "Netscape çerez dosyası (*.txt)|*.txt", Title = "cookies.txt seç" };
                    if (dlg.ShowDialog() == true) Set(s => s.Cookies = new FileCookies(dlg.FileName));
                    break;
                default:
                    var b = Enum.Parse<BrowserKind>(value.Id, ignoreCase: true);
                    Set(s => s.Cookies = new BrowserCookies(b));
                    break;
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsBrowserCookie));
            OnPropertyChanged(nameof(ChromiumWarning));
        }
    }

    public bool IsBrowserCookie => S.Cookies is BrowserCookies;
    public bool ChromiumWarning => S.Cookies is BrowserCookies b && b.Browser.IsChromium();

    public string BrowserProfile
    {
        get => S.Cookies is BrowserCookies b ? b.Profile ?? "" : "";
        set
        {
            if (S.Cookies is BrowserCookies b) Set(s => s.Cookies = b with { Profile = string.IsNullOrWhiteSpace(value) ? null : value.Trim() });
        }
    }

    public PoliteLevel Polite
    {
        get => S.EffectivePolite;
        set { Set(s => s.Polite = value); OnPropertyChanged(nameof(PoliteHint)); }
    }

    public string PoliteHint => S.EffectivePolite switch
    {
        PoliteLevel.Off => "Bekleme yok. Tek tük indirmede en hızlısı; toplu indirmede 429 riski yüksek.",
        PoliteLevel.Safe => "yt-dlp'nin önerisi: videolar arası 10–20 sn, altyazılar arası 5 sn; oturumla aynı anda en fazla 2 iş. Büyük playlist'ler ve hesapla yoğun kullanım için.",
        _ => "Videolar arası 3–8 sn, altyazılar arası 3 sn bekleme. Analiz ve arama hızlı kalır. YouTube sınırı gelirse Vidra kendiliğinden Güvenli'ye geçer.",
    };

    public bool AutoUpdateYtDlp { get => S.AutoUpdateYtDlp; set => Set(s => s.AutoUpdateYtDlp = value); }

    // ---- PO token sağlayıcısı (deneysel)
    public bool PotInstalled => _host.Tools.PotInstalled;

    public string PotHint => PotInstalled
        ? $"bgutil {Core.Tools.ToolManager.PotVersion} kurulu. YouTube'un istediği PO token'ları Deno ile üretir; 403 ve \"biçim yok\" hatalarını azaltır. Analizi biraz yavaşlatabilir."
        : "YouTube bazı videolarda PO token ister; yoksa 403 ya da eksik biçim görülür. Açınca bgutil sağlayıcısı ve npm bağımlılıkları Deno ile kurulur.";

    public bool PoTokenProvider
    {
        get => S.PoTokenProvider && PotInstalled;
        set
        {
            if (value && !PotInstalled) { _ = InstallPot(); return; }
            Set(s => s.PoTokenProvider = value);
        }
    }

    [RelayCommand]
    private async Task InstallPot()
    {
        Updating = true;
        var progress = new Progress<SetupProgress>(p => UpdateLog = p.Message);
        try
        {
            await _host.Tools.InstallPotProviderAsync(progress);
            _host.SettingsStore.Update(s => s.PoTokenProvider = true);
        }
        catch (Exception e) { UpdateLog = e.Message; }
        finally { Updating = false; }
        NotifyPot();
    }

    [RelayCommand]
    private void RemovePot()
    {
        _host.SettingsStore.Update(s => s.PoTokenProvider = false);
        _host.Tools.RemovePotProvider();
        NotifyPot();
    }

    private void NotifyPot()
    {
        OnPropertyChanged(nameof(PotInstalled));
        OnPropertyChanged(nameof(PotHint));
        OnPropertyChanged(nameof(PoTokenProvider));
    }

    // ---- İndirme & dosyalar
    public string OutputDir { get => S.OutputDir; set => Set(s => s.OutputDir = value); }

    public string OutputTemplate
    {
        get => S.OutputTemplate;
        set { Set(s => s.OutputTemplate = value); OnPropertyChanged(nameof(OutputPreview)); }
    }

    public string PlaylistTemplate
    {
        get => S.PlaylistTemplate;
        set { Set(s => s.PlaylistTemplate = value); OnPropertyChanged(nameof(PlaylistPreview)); }
    }

    public string OutputPreview => "Önizleme → " + Templates.Preview(S.OutputTemplate, Sample);
    public string PlaylistPreview => "Önizleme → " + Templates.Preview(Templates.ResolvePlaylist(S.PlaylistTemplate, "Kurs adı", 7, "Kanal adı"), Sample);
    public bool UseArchive { get => S.UseArchive; set => Set(s => s.UseArchive = value); }
    public IReadOnlyList<Choice> PlaylistPresets { get; } = Templates.PlaylistPresets.Select(p => new Choice(p.Template, p.Label)).ToList();
    public string ListCacheInfo => $"{_host.ListCache.Count} liste önbellekte";

    [RelayCommand]
    private void ApplyPlaylistPreset(string? template)
    {
        if (!string.IsNullOrWhiteSpace(template)) PlaylistTemplate = template;
    }

    [RelayCommand]
    private void InsertPlaylistToken(string token)
    {
        var t = S.PlaylistTemplate;
        var i = t.LastIndexOf(".%(ext)s", StringComparison.Ordinal);
        PlaylistTemplate = i >= 0 ? t[..i] + " " + token + t[i..] : t + " " + token;
    }

    [RelayCommand]
    private void ClearListCache()
    {
        _host.ListCache.Clear();
        OnPropertyChanged(nameof(ListCacheInfo));
        Notice = "Playlist/kurs önbelleği temizlendi.";
    }

    [RelayCommand]
    private void InsertToken(string token)
    {
        var t = S.OutputTemplate;
        var i = t.LastIndexOf(".%(ext)s", StringComparison.Ordinal);
        OutputTemplate = i >= 0 ? t[..i] + " " + token + t[i..] : t + " " + token;
    }

    [RelayCommand]
    private void PickFolder()
    {
        var dlg = new OpenFolderDialog { InitialDirectory = S.OutputDir, Title = "İndirme klasörünü seç" };
        if (dlg.ShowDialog() == true) OutputDir = dlg.FolderName;
    }

    // ---- Format varsayılanları
    private DownloadOptions D => S.Defaults;
    private void SetDefault(Func<DownloadOptions, DownloadOptions> f, [CallerMemberName] string? name = null) => Set(s => s.Defaults = f(s.Defaults), name);

    public int DefaultHeight { get => D.Height ?? 0; set => SetDefault(d => d with { Height = value == 0 ? null : value }); }
    public ContainerFormat DefaultContainer { get => D.Container; set => SetDefault(d => d with { Container = value }); }
    public CodecPreference DefaultCodec { get => D.Codec; set => SetDefault(d => d with { Codec = value }); }
    // Opus artık listede yok: eski ayarda kalmışsa aynı işi gören Orijinal olarak göster
    public AudioFormat DefaultAudio { get => D.AudioFormat == AudioFormat.Opus ? AudioFormat.Original : D.AudioFormat; set => SetDefault(d => d with { AudioFormat = value }); }
    public bool DefaultSubs { get => D.Subtitles; set => SetDefault(d => d with { Subtitles = value }); }
    public bool DefaultSponsor { get => D.SponsorBlock; set => SetDefault(d => d with { SponsorBlock = value }); }
    public bool DefaultThumb { get => D.EmbedThumbnail; set => SetDefault(d => d with { EmbedThumbnail = value, EmbedMetadata = value }); }
    public bool DefaultChapters { get => D.Chapters; set => SetDefault(d => d with { Chapters = value }); }

    public string SubLangs
    {
        get => string.Join(", ", S.SubLangs);
        set => Set(s => s.SubLangs = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList());
    }

    // ---- Ağ & performans
    public int Concurrency { get => S.Concurrency; set { Set(s => s.Concurrency = value); _host.Queue.Schedule(); } }
    public int Fragments { get => S.Fragments; set => Set(s => s.Fragments = value); }
    public int Retries { get => S.Retries; set => Set(s => s.Retries = value); }
    public string RateLimit { get => S.RateLimit ?? ""; set => Set(s => s.RateLimit = string.IsNullOrEmpty(value) ? null : value); }
    public string Proxy { get => S.Proxy; set => Set(s => s.Proxy = value); }

    /// <summary>İleri düzey: her indirmeye eklenen yt-dlp argümanları. Hatalıysa hiç kullanılmaz ve sebebi yazar.</summary>
    public string ExtraArgs
    {
        get => S.ExtraArgs;
        set { Set(s => s.ExtraArgs = value ?? ""); OnPropertyChanged(nameof(ExtraArgsError)); }
    }

    public string? ExtraArgsError => Vidra.Core.YtDlp.ExtraArgs.Parse(S.ExtraArgs).Error;

    // ---- Davranış & görünüm
    public ThemeMode Theme
    {
        get => S.Theme;
        set { Set(s => s.Theme = value); ThemeService.Apply(value); }
    }

    public IReadOnlyList<Choice> Densities { get; } = [new(UiDensity.Compact, "Kompakt"), new(UiDensity.Comfortable, "Rahat")];

    public UiDensity Density
    {
        get => S.Density;
        set { Set(s => s.Density = value); ThemeService.ApplyDensity(value); }
    }

    public bool WatchClipboard
    {
        get => S.WatchClipboard;
        set { Set(s => s.WatchClipboard = value); _host.Clipboard.Enabled = value; }
    }

    public bool Notifications { get => S.Notifications; set => Set(s => s.Notifications = value); }
    public bool MinimizeToTray { get => S.MinimizeToTray; set => Set(s => s.MinimizeToTray = value); }
    public bool KeepAwake { get => S.KeepAwake; set => Set(s => s.KeepAwake = value); }
    public bool CheckAppUpdates { get => S.CheckAppUpdates; set => Set(s => s.CheckAppUpdates = value); }
    public bool AutoDownloadAppUpdates { get => S.AutoDownloadAppUpdates; set => Set(s => s.AutoDownloadAppUpdates = value); }

    // ---- Bileşenler
    public UpdateChannel UpdateChannel { get => S.UpdateChannel; set => Set(s => s.UpdateChannel = value); }
    public string BinDir => _host.Tools.BinDir;

    public async Task RefreshToolsAsync()
    {
        OnPropertyChanged(nameof(ListCacheInfo));
        var st = await _host.Tools.StatusAsync();
        Tools.Clear();
        foreach (var t in st) Tools.Add(t);
    }

    [RelayCommand]
    private async Task UpdateYtDlp()
    {
        Updating = true;
        try
        {
            var r = await _host.Tools.UpdateYtDlpAsync(S.UpdateChannel);
            UpdateLog = (r.StdOut + Environment.NewLine + r.StdErr).Trim();
        }
        catch (Exception e) { UpdateLog = e.Message; }
        finally { Updating = false; }
        await RefreshToolsAsync();
    }

    [RelayCommand]
    private async Task Reinstall(object? p)
    {
        if (p is not ToolStatus t) return;
        Updating = true;
        var progress = new Progress<SetupProgress>(p => UpdateLog = p.Message);
        try { await _host.Tools.InstallAsync(t.Id, progress); }
        catch (Exception e) { UpdateLog = e.Message; }
        finally { Updating = false; }
        await RefreshToolsAsync();
    }

    [RelayCommand] private void OpenBinDir() => Process.Start("explorer.exe", $"\"{_host.Tools.BinDir}\"");
    [RelayCommand] private void OpenDataDir() => Process.Start("explorer.exe", $"\"{_host.Paths.DataDir}\"");
}
