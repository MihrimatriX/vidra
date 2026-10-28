namespace Vidra.Core.Storage;

/// <summary>
/// Uygulamanın yazdığı her şey kullanıcı profilinde durur:
///   %APPDATA%\Vidra       → ayarlar, geçmiş, takip listesi, arşiv (roaming, küçük)
///   %LOCALAPPDATA%\Vidra  → bileşenler (yt-dlp, ffmpeg, deno), giriş oturumu, çerezler (makineye özel, büyük/gizli)
/// </summary>
public sealed class AppPaths
{
    public string DataDir { get; }
    public string LocalDir { get; }

    public AppPaths(string? dataDir = null, string? localDir = null)
    {
        DataDir = dataDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vidra");
        LocalDir = localDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vidra");
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LocalDir);
        Directory.CreateDirectory(BinDir);
    }

    public string BinDir => Path.Combine(LocalDir, "bin");
    public string LoginProfileDir => Path.Combine(LocalDir, "WebLogin");
    public string AppCookiesFile => Path.Combine(LocalDir, "youtube-cookies.txt");
    /// <summary>Her yt-dlp sürecine verilen geçici çerez kopyaları (bkz. ProcessRunner.WithPrivateCookies).</summary>
    public string CookieCopyDir => Path.Combine(LocalDir, "cookie-copies");

    public string SettingsFile => Path.Combine(DataDir, "settings.json");
    public string HistoryFile => Path.Combine(DataDir, "history.json");
    public string PendingFile => Path.Combine(DataDir, "pending.json");
    public string FollowsFile => Path.Combine(DataDir, "follows.json");
    public string ArchiveFile => Path.Combine(DataDir, "archive.txt");
    public string LogFile => Path.Combine(LocalDir, "vidra.log");

    public static string DefaultOutputDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Vidra");
}
