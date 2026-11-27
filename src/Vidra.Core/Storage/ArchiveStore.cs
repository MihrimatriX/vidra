namespace Vidra.Core.Storage;

/// <summary>
/// yt-dlp'nin --download-archive dosyası ("youtube &lt;id&gt;" satırları).
/// Takip servisi ve playlist ekranı "zaten indirildi" bilgisini buradan okur.
/// </summary>
public sealed class ArchiveStore(string path)
{
    public string Path { get; } = path;

    // Arşiv on binlerce satır olabilir ve her video analizinde, playlist açılışında okunuyordu.
    // Dosya değişmediyse (boyut + değişme zamanı) son okumanın kopyası döner.
    private static readonly Lock CacheGate = new();
    private static readonly Dictionary<string, (long Length, DateTime Written, HashSet<string> Ids)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Arşivdeki YouTube kimlikleri. Dönen küme çağıranındır (değiştirilebilir, önbelleği etkilemez).</summary>
    public HashSet<string> ReadIds()
    {
        var fi = new FileInfo(Path);
        if (!fi.Exists) return new HashSet<string>(StringComparer.Ordinal);
        lock (CacheGate)
            if (Cache.TryGetValue(Path, out var c) && c.Length == fi.Length && c.Written == fi.LastWriteTimeUtc)
                return new HashSet<string>(c.Ids, StringComparer.Ordinal);

        var (set, complete) = ReadFile();
        if (complete)
            lock (CacheGate) Cache[Path] = (fi.Length, fi.LastWriteTimeUtc, set);
        return new HashSet<string>(set, StringComparer.Ordinal);
    }

    private (HashSet<string> Ids, bool Complete) ReadFile()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            while (sr.ReadLine() is { } line)
            {
                var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[0] == "youtube") set.Add(parts[1].Trim());
            }
            return (set, true);
        }
        catch (IOException) { return (set, false); /* yt-dlp o an yazıyor olabilir; bir sonraki okumada gelir */ }
    }
}
