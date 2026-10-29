using CommunityToolkit.Mvvm.ComponentModel;

namespace Vidra.Core.Models;

public static class SponsorCategories
{
    public static readonly IReadOnlyList<(string Id, string Label)> All =
    [
        ("sponsor", "Sponsor"),
        ("selfpromo", "Kendi tanıtımı"),
        ("interaction", "Abone ol hatırlatması"),
        ("intro", "Giriş / jenerik"),
        ("outro", "Kapanış"),
        ("music_offtopic", "Konu dışı müzik"),
    ];
}

public sealed class AppSettings
{
    public string OutputDir { get; set; } = "";
    public string OutputTemplate { get; set; } = "%(uploader)s/%(title)s [%(id)s].%(ext)s";

    /// <summary>Playlist'ten gelen işler; %(playlist)s / %(playlist_index)s kuyruğa eklenirken doldurulur.</summary>
    public string PlaylistTemplate { get; set; } = "%(playlist)s/%(playlist_index)03d - %(title)s.%(ext)s";

    public int Concurrency { get; set; } = 2;

    /// <summary>-N: tek videonun parçalarını paralel indir.</summary>
    public int Fragments { get; set; } = 4;
    public int Retries { get; set; } = 10;

    /// <summary>yt-dlp -r biçimi: "5M", "500K". null = sınırsız.</summary>
    public string? RateLimit { get; set; }
    public string Proxy { get; set; } = "";

    public CookieSource Cookies { get; set; } = new NoCookies();

    /// <summary>Giriş yapılıyken istekler arasına bekleme ekle (hesap/IP işaretlenme riskini azaltır).</summary>
    public bool PoliteMode { get; set; } = true;

    public bool UseArchive { get; set; } = true;
    public List<string> SubLangs { get; set; } = ["tr", "en"];
    public List<string> SponsorCategories { get; set; } = ["sponsor", "selfpromo", "interaction"];

    public ThemeMode Theme { get; set; } = ThemeMode.Dark;
    public UpdateChannel UpdateChannel { get; set; } = UpdateChannel.Stable;

    public bool WatchClipboard { get; set; } = true;
    public bool Notifications { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Takip edilen listelerin kontrol aralığı (saat). 0 = kapalı.</summary>
    public int FollowIntervalHours { get; set; } = 6;

    public DownloadOptions Defaults { get; set; } = new();

    /// <summary>Kullanıcının kaydettiği profiller (hazır profiller DownloadProfile.BuiltIns'te).</summary>
    public List<DownloadProfile> Profiles { get; set; } = [];

    /// <summary>Uygulama içi girişle eklenen YouTube hesapları; etkin olan Cookies'teki AppLoginCookies yoludur.</summary>
    public List<SavedAccount> Accounts { get; set; } = [];

    /// <summary>Kuyruğun duraklatılıp bu saatte kendiliğinden başlaması (null = zamanlama yok).</summary>
    public DateTimeOffset? ScheduledStart { get; set; }

    /// <summary>Playlist/kanal ekranında kart ızgarası (false: satır listesi).</summary>
    public bool PlaylistCards { get; set; } = true;

    /// <summary>Eklenti çerez gönderince oturumsuz Vidra kendiliğinden eklenti oturumuna geçsin. Eklenti oturumundan çıkış kapatır.</summary>
    public bool ExtensionSync { get; set; } = true;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

/// <summary>Kayıtlı hesap. Dosyaları AppPaths.AccountCookiesFile/AccountProfileDir(Id) altında.</summary>
public sealed record SavedAccount(string Id, string Name, DateTimeOffset ExportedAt, DateTimeOffset? ExpiresAt);

/// <summary>Sepetteki video: biçim indirme anında seçilir. Playlist'ten geldiyse klasör şablonu için liste adı ve sırası.</summary>
public sealed record BasketItem(
    string Url,
    string Title,
    string? Channel,
    string? Thumbnail,
    double? Duration,
    string? Source,
    string? PlaylistTitle = null,
    int PlaylistIndex = 0);

/// <summary>Takip edilen playlist/kanal/akış — yeni videolar otomatik kuyruğa alınır.</summary>
public sealed partial class FollowedSource : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public DownloadOptions Options { get; set; } = new();
    [ObservableProperty] private DateTimeOffset? _lastChecked;
    [ObservableProperty] private int _lastNewCount;
    public bool Enabled { get; set; } = true;
}
