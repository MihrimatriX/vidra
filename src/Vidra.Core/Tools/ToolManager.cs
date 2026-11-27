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
/// Kurulum klasörü yazılabilir olduğu için "yt-dlp -U" kendini güncelleyebilir;
/// uygulama kurulumundan bağımsız olduğu için güncelleme Program Files izni istemez.
/// </summary>
public sealed class ToolManager(AppPaths paths, IProcessRunner runner, HttpClient http)
{
    private const string Gh = "https://github.com";
    private static bool Win => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private static bool Arm => RuntimeInformation.OSArchitecture == Architecture.Arm64;
    private static string Exe(string name) => Win ? name + ".exe" : name;

    /// <summary>Test/geliştirme için: bileşenleri başka bir klasörden kullan (VIDRA_BIN_DIR).</summary>
    public string BinDir { get; } = Environment.GetEnvironmentVariable("VIDRA_BIN_DIR") is { Length: > 0 } d ? d : paths.BinDir;

    public string YtDlpPath => Path.Combine(BinDir, Exe("yt-dlp"));
    public string FfmpegPath => Path.Combine(BinDir, Exe("ffmpeg"));
    public string FfprobePath => Path.Combine(BinDir, Exe("ffprobe"));
    public string DenoPath => Path.Combine(BinDir, Exe("deno"));

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

    /// <summary>yt-dlp kendi kendini günceller (kararlı ya da nightly kanal).</summary>
    public async Task<ProcessResult> UpdateYtDlpAsync(UpdateChannel channel, CancellationToken ct = default)
    {
        string[] args = channel == UpdateChannel.Nightly ? ["--update-to", "nightly"] : ["--update-to", "stable"];
        return await runner.RunAsync(YtDlpPath, args, ct).ConfigureAwait(false);
    }

    private async Task InstallYtDlpAsync(IProgress<SetupProgress>? progress, CancellationToken ct)
    {
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
