namespace Vidra.Core.Storage;

/// <summary>
/// yt-dlp'nin --download-archive dosyası ("youtube &lt;id&gt;" satırları).
/// Takip servisi ve playlist ekranı "zaten indirildi" bilgisini buradan okur.
/// </summary>
public sealed class ArchiveStore(string path)
{
    public string Path { get; } = path;

    public HashSet<string> ReadIds()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(Path)) return set;
        try
        {
            using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            while (sr.ReadLine() is { } line)
            {
                var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[0] == "youtube") set.Add(parts[1].Trim());
            }
        }
        catch (IOException) { /* yt-dlp o an yazıyor olabilir; bir sonraki okumada gelir */ }
        return set;
    }
}
