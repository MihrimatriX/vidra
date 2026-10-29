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

    public bool PoliteMode { get => S.PoliteMode; set => Set(s => s.PoliteMode = value); }

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
    public string PlaylistPreview => "Önizleme → " + Templates.Preview(Templates.ResolvePlaylist(S.PlaylistTemplate, "Playlist adı", 7), Sample);
    public bool UseArchive { get => S.UseArchive; set => Set(s => s.UseArchive = value); }

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

    // ---- Davranış & görünüm
    public ThemeMode Theme
    {
        get => S.Theme;
        set { Set(s => s.Theme = value); ThemeService.Apply(value); }
    }

    public bool WatchClipboard
    {
        get => S.WatchClipboard;
        set { Set(s => s.WatchClipboard = value); _host.Clipboard.Enabled = value; }
    }

    public bool Notifications { get => S.Notifications; set => Set(s => s.Notifications = value); }
    public bool MinimizeToTray { get => S.MinimizeToTray; set => Set(s => s.MinimizeToTray = value); }

    // ---- Bileşenler
    public UpdateChannel UpdateChannel { get => S.UpdateChannel; set => Set(s => s.UpdateChannel = value); }
    public string BinDir => _host.Tools.BinDir;

    public async Task RefreshToolsAsync()
    {
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
