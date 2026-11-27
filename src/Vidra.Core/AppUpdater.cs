using System.Security.Cryptography;

namespace Vidra.Core;

/// <summary>
/// Güncellemeyi indirir ve doğrular; taşınabilir exe'yi yerinde değiştirir. Kurulumu çalıştırmak ve uygulamayı
/// kapatıp açmak arayüzün işi (Vidra.App): burada yalnızca Linux'ta da test edilebilen dosya işleri var.
/// </summary>
public static class AppUpdater
{
    /// <summary>Taşınabilir güncellemede çalışan exe bu adla kenara alınır; bir sonraki açılışta silinir.</summary>
    public const string OldExeSuffix = ".old";

    /// <summary>İndirilen dosyanın adı sürüm içerir (Vidra-Setup-0.7.0.exe): eski indirmeler ayırt edilip temizlenir.</summary>
    public static string FileName(ReleaseAsset asset, Version version) =>
        $"{Path.GetFileNameWithoutExtension(asset.Name)}-{version.ToString(3)}{Path.GetExtension(asset.Name)}";

    /// <summary>
    /// Dosyayı dir içine indirir, boyutunu ve sha256'sını doğrular. Daha önce indirilip doğrulanmış dosya yeniden indirilmez.
    /// Doğrulama tutmazsa dosya silinir ve InvalidDataException fırlar. Sürümde digest yoksa yalnızca boyut denetlenir.
    /// </summary>
    public static async Task<string> DownloadAsync(HttpClient http, ReleaseAsset asset, Version version, string dir,
        IProgress<double?>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, FileName(asset, version));
        if (File.Exists(dest) && await VerifyAsync(dest, asset, ct).ConfigureAwait(false)) return dest;

        var tmp = dest + ".download";
        try
        {
            using (var res = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                res.EnsureSuccessStatusCode();
                var total = asset.Size > 0 ? asset.Size : res.Content.Headers.ContentLength ?? 0;
                await using var src = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = File.Create(tmp);
                var buf = new byte[1 << 16];
                long read = 0, lastReport = 0;
                int n;
                progress?.Report(total > 0 ? 0 : null);
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    read += n;
                    if (read - lastReport >= 256 * 1024)
                    {
                        lastReport = read;
                        progress?.Report(total > 0 ? Math.Min(1, (double)read / total) : null);
                    }
                }
            }
            if (!await VerifyAsync(tmp, asset, ct).ConfigureAwait(false))
                throw new InvalidDataException($"{asset.Name} doğrulanamadı: boyut ya da sha256 sürümdekiyle uyuşmuyor");
            File.Move(tmp, dest, overwrite: true);
            progress?.Report(1);
            return dest;
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    public static async Task<bool> VerifyAsync(string path, ReleaseAsset asset, CancellationToken ct = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0) return false;
        if (asset.Size > 0 && info.Length != asset.Size) return false;
        if (asset.Sha256 is null) return true;
        await using var f = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(f, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash) == asset.Sha256;
    }

    /// <summary>Kurulmuş (sürümü şimdikinden büyük olmayan) ya da yarım kalmış indirmeleri siler.</summary>
    public static void CleanupDownloads(string dir, Version current)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileNameWithoutExtension(f);
            var dash = name.LastIndexOf('-');
            var v = dash >= 0 ? AppUpdate.ParseVersion(name[(dash + 1)..]) : null;
            if (f.EndsWith(".download", StringComparison.OrdinalIgnoreCase) || v is null || v <= new Version(current.Major, current.Minor, Math.Max(0, current.Build)))
                TryDelete(f);
        }
    }

    /// <summary>
    /// Taşınabilir exe'yi değiştirir: Windows çalışan exe'nin silinmesine değil ama yeniden adlandırılmasına izin verir.
    /// Çalışan exe "Vidra.exe.old" olur, yenisi onun yerine kopyalanır. Kopyalama başarısızsa eski exe geri konur.
    /// </summary>
    public static void SwapPortable(string currentExe, string newExe)
    {
        var old = currentExe + OldExeSuffix;
        TryDelete(old);
        File.Move(currentExe, old);
        try
        {
            File.Copy(newExe, currentExe, overwrite: false);
        }
        catch
        {
            TryDelete(currentExe);
            File.Move(old, currentExe);
            throw;
        }
    }

    /// <summary>Açılışta: önceki taşınabilir güncellemeden kalan exe'yi sil (hâlâ kapanıyorsa sonraki açılışa kalır).</summary>
    public static void CleanupPortable(string currentExe) => TryDelete(currentExe + OldExeSuffix);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
