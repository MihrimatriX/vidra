namespace Vidra.Core.YtDlp;

/// <summary>
/// Analiz (probe) sırasında yt-dlp'nin verdiği video bilgisi. İndirme bunu <c>--load-info-json</c> ile kullanır:
/// YouTube'dan sayfa, oynatıcı JS'si ve format listesi ikinci kez çekilmez (iş başına birkaç saniye ve daha az istek, 429 riski azalır).
/// Format adresleri YouTube'da ~6 saat geçerli; güvenli pay bırakmak için varsayılan ömür 2 saat.
/// Bayat ya da bir kez hata vermiş bilgi kullanılmaz: iş normal yoldan (adresle) çıkarım yapar.
/// Dosya yt-dlp'nin format başına eklediği çerezleri de içerebilir; bu yüzden çerez dosyasıyla aynı yerel klasörde durur,
/// indirme bitince silinir ve ömrü dolanlar açılışta temizlenir.
/// </summary>
public sealed class InfoJsonCache(string dir, TimeSpan? ttl = null)
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(2);

    private readonly TimeSpan _ttl = ttl ?? DefaultTtl;

    public string Dir { get; } = dir;

    public void Save(string videoId, string json)
    {
        if (!IsSafeId(videoId)) return;
        try
        {
            Directory.CreateDirectory(Dir);
            var path = PathFor(videoId);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* yazılamazsa indirme adresle yapılır */ }
    }

    /// <summary>Taze bilgi dosyasının yolu; yoksa ya da eskiyse null.</summary>
    public string? TryGetFresh(string? videoId)
    {
        if (videoId is null || !IsSafeId(videoId)) return null;
        var fi = new FileInfo(PathFor(videoId));
        if (!fi.Exists) return null;
        return DateTime.UtcNow - fi.LastWriteTimeUtc <= _ttl ? fi.FullName : null;
    }

    public void Invalidate(string? videoId)
    {
        if (videoId is null || !IsSafeId(videoId)) return;
        try { File.Delete(PathFor(videoId)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* sonraki Prune siler */ }
    }

    /// <summary>Ömrü geçmiş dosyaları siler (açılışta arka planda).</summary>
    public int Prune()
    {
        if (!Directory.Exists(Dir)) return 0;
        var n = 0;
        foreach (var f in new DirectoryInfo(Dir).GetFiles("*.json*"))
        {
            if (DateTime.UtcNow - f.LastWriteTimeUtc <= _ttl) continue;
            try { f.Delete(); n++; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* kullanımda */ }
        }
        return n;
    }

    private string PathFor(string videoId) => Path.Combine(Dir, videoId + ".json");

    // YouTube kimliği: harf, rakam, - ve _ (yol enjeksiyonuna karşı)
    private static bool IsSafeId(string id) => id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
