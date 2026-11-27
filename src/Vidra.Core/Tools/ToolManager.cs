using System.IO.Compression;
using System.Runtime.InteropServices;
using Vidra.Core.Models;
using Vidra.Core.Process;
using Vidra.Core.Storage;
using Vidra.Core.YtDlp;

namespace Vidra.Core.Tools;

public enum ToolId { YtDlp, Ffmpeg, Deno, PoToken }

public sealed record ToolStatus(ToolId Id, string Name, bool Installed, string? Version, string Purpose);

public sealed record SetupProgress(ToolId Tool, string Message, double? Fraction);

/// <summary>
/// yt-dlp, ffmpeg/ffprobe ve Deno'yu %LOCALAPPDATA%\Vidra\bin altında tutar.
/// Kurulum klasörü kullanıcıya ait olduğu için güncelleme Program Files izni istemez.
/// Windows'ta yt-dlp onedir paketidir ve Vidra günceller; diğer platformlarda "yt-dlp -U" kendini günceller.
/// </summary>
public sealed class ToolManager(AppPaths paths, IProcessRunner runner, HttpClient http)
{
    private const string Gh = "https://github.com";
    private static bool Win => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private static bool Arm => RuntimeInformation.OSArchitecture == Architecture.Arm64;
    private static string Exe(string name) => Win ? name + ".exe" : name;

    /// <summary>Test/geliştirme için: bileşenleri başka bir klasörden kullan (VIDRA_BIN_DIR).</summary>
    public string BinDir { get; } = Environment.GetEnvironmentVariable("VIDRA_BIN_DIR") is { Length: > 0 } d ? d : paths.BinDir;

    /// <summary>
    /// Windows'ta yt-dlp "onedir" paketi (yt-dlp_win.zip) kullanılır: tek dosyalık yt-dlp.exe her başlatmada kendini
    /// %TEMP%'e açar (Defender taramasıyla iş başına 1-3 sn); onedir paketi doğrudan çalışır. Bu paket -U ile kendini
    /// güncelleyemediği için güncellemeyi Vidra yapar: her sürüm ayrı klasöre açılır, "current.txt" etkin klasörü gösterir.
    /// Böylece çalışan indirmelerin kullandığı dosyalara dokunulmadan yeni sürüme geçilir.
    /// </summary>
    public string YtDlpDirRoot => Path.Combine(BinDir, "yt-dlp-dir");

    private string LegacyYtDlpPath => Path.Combine(BinDir, Exe("yt-dlp"));
    private string? _onedirExe;
    private bool _onedirResolved;

    /// <summary>Onedir kurulumu varsa onun yt-dlp.exe'si, yoksa (eski kurulum, diğer platformlar) tek dosya.</summary>
    public string YtDlpPath
    {
        get
        {
            if (!Win) return LegacyYtDlpPath;
            if (!_onedirResolved)
            {
                _onedirExe = ResolveOnedirExe(YtDlpDirRoot);
                _onedirResolved = true;
            }
            return _onedirExe ?? LegacyYtDlpPath;
        }
    }

    /// <summary>Onedir kullanılıyor mu (ayarlarda ve güncellemede gösterilir).</summary>
    public bool UsesOnedir => Win && YtDlpPath != LegacyYtDlpPath;

    /// <summary>current.txt'nin gösterdiği klasördeki yt-dlp.exe; dosya ya da klasör yoksa null.</summary>
    public static string? ResolveOnedirExe(string root)
    {
        try
        {
            var pointer = Path.Combine(root, "current.txt");
            if (!File.Exists(pointer)) return null;
            var name = File.ReadAllText(pointer).Trim();
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            var exe = Path.Combine(root, name, "yt-dlp.exe");
            return File.Exists(exe) ? exe : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Mimarinin onedir paketi.</summary>
    public static string WinOnedirAsset(bool arm) => arm ? "yt-dlp_win_arm64.zip" : "yt-dlp_win.zip";

    /// <summary>Kanalın GitHub deposu: nightly ayrı depoda yayımlanır.</summary>
    public static string YtDlpRepo(UpdateChannel channel) => channel == UpdateChannel.Nightly ? "yt-dlp/yt-dlp-nightly-builds" : "yt-dlp/yt-dlp";
    public string FfmpegPath => Path.Combine(BinDir, Exe("ffmpeg"));
    public string FfprobePath => Path.Combine(BinDir, Exe("ffprobe"));
    public string DenoPath => Path.Combine(BinDir, Exe("deno"));

    /// <summary>Analizde YouTube kısıtına karşı işe yarayan yol; indirmeler de aynısını kullanır.</summary>
    public FormatFallback Fallback { get; } = new();

    public ToolPaths Paths => PotInstalled
        ? new(YtDlpPath, BinDir, DenoPath, paths.ArchiveFile, PotServerDir, PluginDir)
        : new(YtDlpPath, BinDir, DenoPath, paths.ArchiveFile);

    // ---- PO token sağlayıcısı (bgutil-ytdlp-pot-provider, betik modu, Deno ile)
    // Sürüm sabit: eklenti ile betik aynı sürümden olmalı; 2.0.0 öncesi uzaktan kod çalıştırma açığı taşır.
    public const string PotVersion = "2.0.2";
    private const string PotRepo = "Brainicism/bgutil-ytdlp-pot-provider";
    private const string PotZip = "bgutil-ytdlp-pot-provider.zip";

    /// <summary>yt-dlp'ye --plugin-dirs ile verilen klasör; eklenti zip'i doğrudan buraya konur.</summary>
    public string PluginDir => Path.Combine(BinDir, "yt-dlp-plugins");
    public string PotHome => Path.Combine(BinDir, "bgutil-pot");

    /// <summary>Eklentinin server_home'u: src/generate_once.ts ve node_modules burada.</summary>
    public string PotServerDir => Path.Combine(PotHome, "server");

    public bool PotInstalled =>
        File.Exists(Path.Combine(PluginDir, PotZip)) &&
        File.Exists(Path.Combine(PotServerDir, "src", "generate_once.ts")) &&
        Directory.Exists(Path.Combine(PotServerDir, "node_modules"));

    /// <summary>
    /// Eklenti zip'ini ve sağlayıcının betiğini (aynı sürüm) indirir, bağımlılıklarını Deno ile kurar.
    /// Betik modu ayrı bir sunucu süreci gerektirmez: yt-dlp her token için betiği Deno ile çalıştırır.
    /// </summary>
    public async Task InstallPotProviderAsync(IProgress<SetupProgress>? progress = null, CancellationToken ct = default)
    {
        if (!File.Exists(DenoPath)) await InstallDenoAsync(progress, ct).ConfigureAwait(false);
        Directory.CreateDirectory(PluginDir);
        await DownloadAsync(ToolId.PoToken, $"{Gh}/{PotRepo}/releases/download/{PotVersion}/{PotZip}", Path.Combine(PluginDir, PotZip), progress, ct).ConfigureAwait(false);

        var src = Path.Combine(Path.GetTempPath(), $"vidra-pot-{Guid.NewGuid():N}.zip");
        try
        {
            await DownloadAsync(ToolId.PoToken, $"{Gh}/{PotRepo}/archive/refs/tags/{PotVersion}.zip", src, progress, ct).ConfigureAwait(false);
            progress?.Report(new(ToolId.PoToken, "PO token betiği açılıyor…", null));
            if (Directory.Exists(PotHome)) Directory.Delete(PotHome, recursive: true);
            ExtractSubtree(src, "server/", PotServerDir);
        }
        finally { TryDelete(src); }

        progress?.Report(new(ToolId.PoToken, "PO token bağımlılıkları kuruluyor (Deno)…", null));
        // README'deki Deno kurulumu: canvas'ın kurulum betiğine yalnızca o pakete izin vererek
        var r = await runner.RunInAsync(PotServerDir, DenoPath, ["install", "--allow-scripts=npm:canvas", "--frozen"], ct).ConfigureAwait(false);
        if (r.ExitCode != 0 || !PotInstalled)
        {
            var why = r.StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(3).Select(l => l.Trim());
            throw new InvalidOperationException("PO token sağlayıcısı kurulamadı: " + string.Join(" ", why));
        }
        progress?.Report(new(ToolId.PoToken, $"PO token sağlayıcısı {PotVersion} hazır", 1));
    }

    public void RemovePotProvider()
    {
        TryDelete(Path.Combine(PluginDir, PotZip));
        try { if (Directory.Exists(PotHome)) Directory.Delete(PotHome, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* süreç tutuyorsa sonraki kaldırmada */ }
    }

    /// <summary>GitHub kaynak arşivinden "kök/prefix…" altını hedefe çıkarır; arşiv dışına yazan girdiler (zip slip) reddedilir.</summary>
    internal static void ExtractSubtree(string zip, string prefix, string dest)
    {
        using var archive = ZipFile.OpenRead(zip);
        var root = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
        foreach (var e in archive.Entries)
        {
            // GitHub arşivleri tek bir "repo-sürüm/" klasörüyle başlar
            var slash = e.FullName.IndexOf('/');
            if (slash < 0) continue;
            var rel = e.FullName[(slash + 1)..];
            if (!rel.StartsWith(prefix, StringComparison.Ordinal) || rel.Length == prefix.Length) continue;
            var target = Path.GetFullPath(Path.Combine(dest, rel[prefix.Length..]));
            if (!target.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException($"Geçersiz arşiv girdisi: {e.FullName}");
            if (e.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, overwrite: true);
        }
    }

    public bool AllInstalled => File.Exists(YtDlpPath) && File.Exists(FfmpegPath) && File.Exists(FfprobePath) && File.Exists(DenoPath);

    public async Task<IReadOnlyList<ToolStatus>> StatusAsync(CancellationToken ct = default)
    {
        async Task<string?> Ver(string exe, params string[] args)
        {
            if (!File.Exists(exe)) return null;
            try
            {
                var r = await runner.RunAsync(exe, args, ct).ConfigureAwait(false);
                return r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            }
            catch { return null; }
        }

        var y = Ver(YtDlpPath, "--version");
        var f = Ver(FfmpegPath, "-hide_banner", "-version");
        var d = Ver(DenoPath, "--version");
        await Task.WhenAll(y, f, d).ConfigureAwait(false);

        static string? Short(string? s, string prefix) =>
            s is null ? null : s.StartsWith(prefix, StringComparison.Ordinal) ? s[prefix.Length..].Split(' ')[0] : s;

        return
        [
            new(ToolId.YtDlp, "yt-dlp", y.Result is not null, y.Result, "İndirme motoru"),
            new(ToolId.Ffmpeg, "ffmpeg / ffprobe", f.Result is not null, Short(f.Result, "ffmpeg version "), "Birleştirme, dönüştürme, kesme"),
            new(ToolId.Deno, "Deno", d.Result is not null, Short(d.Result, "deno "), "YouTube JS doğrulamaları (≥ 2.3)"),
        ];
    }

    /// <summary>Eksik bileşenleri indirir. İlk açılış kurulum ekranı bunu çağırır.</summary>
    public async Task EnsureInstalledAsync(IProgress<SetupProgress>? progress = null, bool force = false, CancellationToken ct = default)
    {
        Directory.CreateDirectory(BinDir);
        if (force || !File.Exists(YtDlpPath)) await InstallYtDlpAsync(progress, ct).ConfigureAwait(false);
        if (force || !File.Exists(DenoPath)) await InstallDenoAsync(progress, ct).ConfigureAwait(false);
        if (force || !File.Exists(FfmpegPath) || !File.Exists(FfprobePath)) await InstallFfmpegAsync(progress, ct).ConfigureAwait(false);
    }

    public Task InstallAsync(ToolId id, IProgress<SetupProgress>? progress = null, CancellationToken ct = default) => id switch
    {
        ToolId.YtDlp => InstallYtDlpAsync(progress, ct),
        ToolId.Deno => InstallDenoAsync(progress, ct),
        ToolId.PoToken => InstallPotProviderAsync(progress, ct),
        _ => InstallFfmpegAsync(progress, ct),
    };

    /// <summary>
    /// yt-dlp'yi günceller (kararlı ya da nightly kanal). Windows'ta onedir paketini indirir (eski tek dosyalık kurulum da
    /// böylece onedir'e geçer); diğer platformlarda yt-dlp kendini günceller.
    /// </summary>
    public async Task<ProcessResult> UpdateYtDlpAsync(UpdateChannel channel, CancellationToken ct = default)
    {
        if (!Win)
        {
            string[] args = channel == UpdateChannel.Nightly ? ["--update-to", "nightly"] : ["--update-to", "stable"];
            return await runner.RunAsync(YtDlpPath, args, ct).ConfigureAwait(false);
        }
        var before = await VersionAsync(ct).ConfigureAwait(false);
        var latest = await LatestTagAsync(channel, ct).ConfigureAwait(false);
        if (UsesOnedir && latest is not null && before == latest)
            return new ProcessResult(0, $"yt-dlp güncel ({before}).", "");
        await InstallOnedirAsync(channel, latest, null, ct).ConfigureAwait(false);
        var after = await VersionAsync(ct).ConfigureAwait(false);
        return new ProcessResult(0, before == after ? $"yt-dlp yeniden kuruldu ({after})." : $"yt-dlp güncellendi: {before ?? "?"} → {after}.", "");
    }

    /// <summary>
    /// Eski tek dosyalık kurulumu arka planda onedir'e taşır ve kullanılmayan eski sürüm klasörlerini siler.
    /// Açılışta çağrılır; hata sessizdir (tek dosyalık yt-dlp çalışmaya devam eder).
    /// </summary>
    public async Task MigrateAndCleanupAsync(UpdateChannel channel, CancellationToken ct = default)
    {
        if (!Win) return;
        try
        {
            if (!UsesOnedir && File.Exists(LegacyYtDlpPath))
                await InstallOnedirAsync(channel, null, null, ct).ConfigureAwait(false);
            CleanupOnedir();
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or TaskCanceledException) { /* bir sonraki açılışta yeniden dener */ }
    }

    private async Task<string?> VersionAsync(CancellationToken ct)
    {
        if (!File.Exists(YtDlpPath)) return null;
        try
        {
            var r = await runner.RunAsync(YtDlpPath, ["--version"], ct).ConfigureAwait(false);
            return r.StdOut.Trim() is { Length: > 0 } v ? v : null;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    /// <summary>Kanalın en son sürüm etiketi (GitHub API; oran sınırında null).</summary>
    private async Task<string?> LatestTagAsync(UpdateChannel channel, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{YtDlpRepo(channel)}/releases/latest");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { return null; }
    }

    /// <summary>Paketi yeni bir klasöre açar ve current.txt'yi ona çevirir; çalışan süreçler eski klasörden devam eder.</summary>
    private async Task InstallOnedirAsync(UpdateChannel channel, string? tag, IProgress<SetupProgress>? progress, CancellationToken ct)
    {
        var asset = WinOnedirAsset(Arm);
        var url = tag is null
            ? $"{Gh}/{YtDlpRepo(channel)}/releases/latest/download/{asset}"
            : $"{Gh}/{YtDlpRepo(channel)}/releases/download/{tag}/{asset}";
        Directory.CreateDirectory(YtDlpDirRoot);
        var zip = Path.Combine(Path.GetTempPath(), $"vidra-ytdlp-{Guid.NewGuid():N}.zip");
        var name = $"v{DateTime.UtcNow:yyyyMMddHHmmss}";
        var target = Path.Combine(YtDlpDirRoot, name);
        try
        {
            await DownloadAsync(ToolId.YtDlp, url, zip, progress, ct).ConfigureAwait(false);
            progress?.Report(new(ToolId.YtDlp, "yt-dlp açılıyor…", null));
            ZipFile.ExtractToDirectory(zip, target, overwriteFiles: true);
            if (!File.Exists(Path.Combine(target, "yt-dlp.exe"))) throw new FileNotFoundException("Pakette yt-dlp.exe bulunamadı");
            var pointer = Path.Combine(YtDlpDirRoot, "current.txt");
            File.WriteAllText(pointer + ".tmp", name);
            File.Move(pointer + ".tmp", pointer, overwrite: true);
            _onedirResolved = false; // sonraki işler yeni sürümü kullanır
            progress?.Report(new(ToolId.YtDlp, "yt-dlp hazır", 1));
        }
        catch
        {
            TryDeleteDir(target);
            throw;
        }
        finally { TryDelete(zip); }
        CleanupOnedir();
    }

    /// <summary>Etkin olmayan sürüm klasörlerini ve eski tek dosyayı siler; kullanımda olanlar (çalışan iş) kalır.</summary>
    private void CleanupOnedir()
    {
        if (!UsesOnedir) return;
        var current = Path.GetDirectoryName(YtDlpPath);
        foreach (var d in Directory.GetDirectories(YtDlpDirRoot))
            if (!string.Equals(d, current, StringComparison.OrdinalIgnoreCase) && !InUse(Path.Combine(d, "yt-dlp.exe"))) TryDeleteDir(d);
        TryDelete(LegacyYtDlpPath); // çalışıyorsa Windows silmeye izin vermez; sonraki açılışta silinir
    }

    /// <summary>
    /// Çalışan bir sürecin görüntüsü Windows'ta yazmaya kilitlidir. Kilitliyse klasöre dokunma: yt-dlp modüllerini
    /// _internal'dan ihtiyaç anında yükler, yarım silinen klasör çalışan indirmeyi düşürür.
    /// </summary>
    private static bool InUse(string exe)
    {
        if (!File.Exists(exe)) return false;
        try
        {
            using var _ = new FileStream(exe, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* kullanımda; sonra silinir */ }
    }

    private async Task InstallYtDlpAsync(IProgress<SetupProgress>? progress, CancellationToken ct)
    {
        if (Win)
        {
            await InstallOnedirAsync(UpdateChannel.Stable, null, progress, ct).ConfigureAwait(false);
            return;
        }
        var asset = Win ? "yt-dlp.exe"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "yt-dlp_macos"
            : Arm ? "yt-dlp_linux_aarch64" : "yt-dlp_linux";
        await DownloadAsync(ToolId.YtDlp, $"{Gh}/yt-dlp/yt-dlp/releases/latest/download/{asset}", YtDlpPath, progress, ct).ConfigureAwait(false);
        MakeExecutable(YtDlpPath);
    }

    private async Task InstallDenoAsync(IProgress<SetupProgress>? progress, CancellationToken ct)
    {
        var triple = (Arm ? "aarch64" : "x86_64") + "-" +
            (Win ? "pc-windows-msvc" : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "apple-darwin" : "unknown-linux-gnu");
        var zip = Path.Combine(Path.GetTempPath(), $"vidra-deno-{Guid.NewGuid():N}.zip");
        try
        {
            await DownloadAsync(ToolId.Deno, $"{Gh}/denoland/deno/releases/latest/download/deno-{triple}.zip", zip, progress, ct).ConfigureAwait(false);
            progress?.Report(new(ToolId.Deno, "Deno açılıyor…", null));
            ExtractEntry(zip, Exe("deno"), DenoPath);
            MakeExecutable(DenoPath);
        }
        finally { TryDelete(zip); }
    }

    private async Task InstallFfmpegAsync(IProgress<SetupProgress>? progress, CancellationToken ct)
    {
        if (!Win)
        {
            // yt-dlp'nin ffmpeg derlemeleri Linux için .tar.xz; masaüstü uygulaması Windows hedefli olduğundan
            // diğer platformlarda sistem ffmpeg'i beklenir.
            throw new PlatformNotSupportedException("Bu platformda ffmpeg'i paket yöneticisiyle kur ve VIDRA_BIN_DIR'e kopyala.");
        }
        var zip = Path.Combine(Path.GetTempPath(), $"vidra-ffmpeg-{Guid.NewGuid():N}.zip");
        try
        {
            // yt-dlp ekibinin yamalı ffmpeg derlemeleri — yt-dlp ile en uyumlu olanlar
            var asset = Arm ? "ffmpeg-master-latest-winarm64-gpl.zip" : "ffmpeg-master-latest-win64-gpl.zip";
            await DownloadAsync(ToolId.Ffmpeg, $"{Gh}/yt-dlp/FFmpeg-Builds/releases/download/latest/{asset}", zip, progress, ct).ConfigureAwait(false);
            progress?.Report(new(ToolId.Ffmpeg, "ffmpeg açılıyor…", null));
            ExtractEntry(zip, "ffmpeg.exe", FfmpegPath);
            ExtractEntry(zip, "ffprobe.exe", FfprobePath);
        }
        finally { TryDelete(zip); }
    }

    private async Task DownloadAsync(ToolId id, string url, string dest, IProgress<SetupProgress>? progress, CancellationToken ct)
    {
        var name = id switch { ToolId.YtDlp => "yt-dlp", ToolId.Deno => "Deno", ToolId.PoToken => "PO token sağlayıcısı", _ => "ffmpeg" };
        progress?.Report(new(id, $"{name} indiriliyor…", 0));
        using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        var total = res.Content.Headers.ContentLength;
        var tmp = dest + ".download";
        await using (var src = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = File.Create(tmp))
        {
            var buf = new byte[1 << 16];
            long read = 0;
            int n;
            var lastReport = 0L;
            while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                read += n;
                if (read - lastReport > 512 * 1024)
                {
                    lastReport = read;
                    progress?.Report(new(id, $"{name} indiriliyor… {read / 1048576d:0.0} MB", total is > 0 ? (double)read / total : null));
                }
            }
        }
        File.Move(tmp, dest, overwrite: true);
        progress?.Report(new(id, $"{name} hazır", 1));
    }

    private static void ExtractEntry(string zip, string fileName, string dest)
    {
        using var archive = ZipFile.OpenRead(zip);
        var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new FileNotFoundException($"Arşivde {fileName} bulunamadı");
        entry.ExtractToFile(dest, overwrite: true);
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* yoksay */ }
    }
}
